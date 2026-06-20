using Microsoft.Data.Sqlite;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Service.Storage;

public sealed class TrafficStatsDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly object _dbLock = new();

    public TrafficStatsDb(string dbPath)
    {
        var dir = Path.GetDirectoryName(dbPath);
        if (dir is not null)
            Directory.CreateDirectory(dir);

        _connection = new SqliteConnection($"Data Source={dbPath}");
        _connection.Open();
        InitializeSchema();
    }

    private void InitializeSchema()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS traffic_hourly (
                process_name TEXT NOT NULL,
                hour_utc TEXT NOT NULL,
                bytes_received INTEGER NOT NULL DEFAULT 0,
                bytes_sent INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (process_name, hour_utc)
            );
            CREATE TABLE IF NOT EXISTS traffic_daily (
                process_name TEXT NOT NULL,
                date_utc TEXT NOT NULL,
                bytes_received INTEGER NOT NULL DEFAULT 0,
                bytes_sent INTEGER NOT NULL DEFAULT 0,
                PRIMARY KEY (process_name, date_utc)
            );
            CREATE INDEX IF NOT EXISTS idx_hourly_hour ON traffic_hourly(hour_utc);
            CREATE INDEX IF NOT EXISTS idx_daily_date ON traffic_daily(date_utc);
            """;
        cmd.ExecuteNonQuery();

        using var walCmd = _connection.CreateCommand();
        walCmd.CommandText = "PRAGMA journal_mode=WAL;";
        walCmd.ExecuteNonQuery();
    }

    public void RecordSnapshot(TrafficSnapshot snapshot)
    {
        var hourKey = DateTime.UtcNow.ToString("yyyy-MM-ddTHH");
        var dateKey = DateTime.UtcNow.ToString("yyyy-MM-dd");

        lock (_dbLock)
        {
            using var transaction = _connection.BeginTransaction();
            try
            {
                foreach (var proc in snapshot.Processes)
                {
                    if (proc.CurrentDownloadBytesPerSecond == 0 && proc.CurrentUploadBytesPerSecond == 0)
                        continue;

                    UpsertHourly(proc.ProcessName, hourKey,
                        proc.CurrentDownloadBytesPerSecond, proc.CurrentUploadBytesPerSecond);
                    UpsertDaily(proc.ProcessName, dateKey,
                        proc.CurrentDownloadBytesPerSecond, proc.CurrentUploadBytesPerSecond);
                }
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }
    }

    private void UpsertHourly(string processName, string hourKey, long bytesReceived, long bytesSent)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO traffic_hourly (process_name, hour_utc, bytes_received, bytes_sent)
            VALUES (@name, @hour, @recv, @sent)
            ON CONFLICT(process_name, hour_utc) DO UPDATE SET
                bytes_received = bytes_received + @recv,
                bytes_sent = bytes_sent + @sent;
            """;
        cmd.Parameters.AddWithValue("@name", processName);
        cmd.Parameters.AddWithValue("@hour", hourKey);
        cmd.Parameters.AddWithValue("@recv", bytesReceived);
        cmd.Parameters.AddWithValue("@sent", bytesSent);
        cmd.ExecuteNonQuery();
    }

    private void UpsertDaily(string processName, string dateKey, long bytesReceived, long bytesSent)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO traffic_daily (process_name, date_utc, bytes_received, bytes_sent)
            VALUES (@name, @date, @recv, @sent)
            ON CONFLICT(process_name, date_utc) DO UPDATE SET
                bytes_received = bytes_received + @recv,
                bytes_sent = bytes_sent + @sent;
            """;
        cmd.Parameters.AddWithValue("@name", processName);
        cmd.Parameters.AddWithValue("@date", dateKey);
        cmd.Parameters.AddWithValue("@recv", bytesReceived);
        cmd.Parameters.AddWithValue("@sent", bytesSent);
        cmd.ExecuteNonQuery();
    }

    public List<TrafficStatEntry> GetHourlyStats(string? processName, int hours = 24)
    {
        lock (_dbLock)
        {
            var since = DateTime.UtcNow.AddHours(-hours).ToString("yyyy-MM-ddTHH");
            using var cmd = _connection.CreateCommand();

            if (processName is not null)
            {
                cmd.CommandText = """
                    SELECT process_name, hour_utc, bytes_received, bytes_sent
                    FROM traffic_hourly
                    WHERE process_name = @name AND hour_utc >= @since
                    ORDER BY hour_utc;
                    """;
                cmd.Parameters.AddWithValue("@name", processName);
            }
            else
            {
                cmd.CommandText = """
                    SELECT 'ALL' as process_name, hour_utc,
                        SUM(bytes_received) as bytes_received,
                        SUM(bytes_sent) as bytes_sent
                    FROM traffic_hourly
                    WHERE hour_utc >= @since
                    GROUP BY hour_utc
                    ORDER BY hour_utc;
                    """;
            }
            cmd.Parameters.AddWithValue("@since", since);

            return ExecuteStatQuery(cmd);
        }
    }

    public List<TrafficStatEntry> GetDailyStats(string? processName, int days = 30)
    {
        lock (_dbLock)
        {
            var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
            using var cmd = _connection.CreateCommand();

            if (processName is not null)
            {
                cmd.CommandText = """
                    SELECT process_name, date_utc as period, bytes_received, bytes_sent
                    FROM traffic_daily
                    WHERE process_name = @name AND date_utc >= @since
                    ORDER BY date_utc;
                    """;
                cmd.Parameters.AddWithValue("@name", processName);
            }
            else
            {
                cmd.CommandText = """
                    SELECT 'ALL' as process_name, date_utc as period,
                        SUM(bytes_received) as bytes_received,
                        SUM(bytes_sent) as bytes_sent
                    FROM traffic_daily
                    WHERE date_utc >= @since
                    GROUP BY date_utc
                    ORDER BY date_utc;
                    """;
            }
            cmd.Parameters.AddWithValue("@since", since);

            return ExecuteStatQuery(cmd);
        }
    }

    public List<ProcessTotalEntry> GetTopProcesses(int days = 7, int limit = 20)
    {
        lock (_dbLock)
        {
            var since = DateTime.UtcNow.AddDays(-days).ToString("yyyy-MM-dd");
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                SELECT process_name,
                    SUM(bytes_received) as total_received,
                    SUM(bytes_sent) as total_sent
                FROM traffic_daily
                WHERE date_utc >= @since
                GROUP BY process_name
                ORDER BY (total_received + total_sent) DESC
                LIMIT @limit;
                """;
            cmd.Parameters.AddWithValue("@since", since);
            cmd.Parameters.AddWithValue("@limit", limit);

            var results = new List<ProcessTotalEntry>();
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                results.Add(new ProcessTotalEntry
                {
                    ProcessName = reader.GetString(0),
                    TotalBytesReceived = reader.GetInt64(1),
                    TotalBytesSent = reader.GetInt64(2)
                });
            }
            return results;
        }
    }

    public void PurgeOlderThan(int days = 90)
    {
        lock (_dbLock)
        {
            var cutoff = DateTime.UtcNow.AddDays(-days);
            var hourCutoff = cutoff.ToString("yyyy-MM-ddTHH");
            var dateCutoff = cutoff.ToString("yyyy-MM-dd");

            using var cmd = _connection.CreateCommand();
            cmd.CommandText = """
                DELETE FROM traffic_hourly WHERE hour_utc < @hourCutoff;
                DELETE FROM traffic_daily WHERE date_utc < @dateCutoff;
                """;
            cmd.Parameters.AddWithValue("@hourCutoff", hourCutoff);
            cmd.Parameters.AddWithValue("@dateCutoff", dateCutoff);
            cmd.ExecuteNonQuery();
        }
    }

    private static List<TrafficStatEntry> ExecuteStatQuery(SqliteCommand cmd)
    {
        var results = new List<TrafficStatEntry>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new TrafficStatEntry
            {
                ProcessName = reader.GetString(0),
                Period = reader.GetString(1),
                BytesReceived = reader.GetInt64(2),
                BytesSent = reader.GetInt64(3)
            });
        }
        return results;
    }

    public void Dispose()
    {
        _connection.Dispose();
    }
}

public class TrafficStatEntry
{
    public string ProcessName { get; set; } = string.Empty;
    public string Period { get; set; } = string.Empty;
    public long BytesReceived { get; set; }
    public long BytesSent { get; set; }
}

public class ProcessTotalEntry
{
    public string ProcessName { get; set; } = string.Empty;
    public long TotalBytesReceived { get; set; }
    public long TotalBytesSent { get; set; }
}
