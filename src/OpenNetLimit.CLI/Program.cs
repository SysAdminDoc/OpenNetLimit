using System.Globalization;
using System.Text;
using System.Text.Json;

namespace OpenNetLimit.CLI;

public static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static string BaseUrl = "http://127.0.0.1:47719";
    private static string? ApiKey;

    public static async Task<int> Main(string[] args)
    {
        BaseUrl = Environment.GetEnvironmentVariable("OPENNETLIMIT_API_URL") ?? BaseUrl;
        ApiKey = Environment.GetEnvironmentVariable("OPENNETLIMIT_API_KEY");

        if (args.Length == 0)
            return PrintUsage();

        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "status" => await GetJson("/api/v1/status"),
                "snapshot" => await GetJson("/api/v1/snapshot"),
                "processes" => await GetJson("/api/v1/processes"),
                "rules" => await HandleRules(args[1..]),
                "stats" => await HandleStats(args[1..]),
                "groups" => await HandleGroups(args[1..]),
                "quotas" => await GetJson("/api/v1/quotas"),
                "connections" => await GetJson("/api/v1/connections"),
                "alerts" => await GetJson("/api/v1/alerts/events"),
                "health" => await GetJson("/health"),
                "selftest" => await RunSelfTest(args[1..]),
                "pause" => await PauseEngine(args[1..]),
                "resume" => await ResumeEngine(),
                "pc-limit" => await HandlePcLimit(args[1..]),
                "help" or "--help" or "-h" => PrintUsage(),
                _ => PrintUsage()
            };
        }
        catch (HttpRequestException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            Console.Error.WriteLine("Is the OpenNetLimit service running?");
            return 1;
        }
        catch (SelfTestException ex)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunSelfTest(string[] args)
    {
        var limit = GetArg(args, "--limit", "256");
        if (!long.TryParse(limit, out var limitKb) || limitKb <= 0)
        {
            Console.Error.WriteLine($"Error: --limit value '{limit}' is not a positive number");
            return 1;
        }

        // Ctrl+C cancels the test instead of killing the process, so the test rule still gets removed.
        using var cancel = new CancellationTokenSource();
        ConsoleCancelEventHandler onCancel = (_, e) =>
        {
            e.Cancel = true;
            cancel.Cancel();
        };
        Console.CancelKeyPress += onCancel;
        try
        {
            using var client = CreateClient();
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            var test = new SelfTest(
                new RestSelfTestService(client, BaseUrl),
                new LoopbackProbe(warmup: TimeSpan.FromSeconds(2), window: TimeSpan.FromSeconds(3)),
                Console.Out,
                self.ProcessName);
            return await test.RunAsync(limitKb * 1024, cancel.Token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested)
        {
            Console.Error.WriteLine("Self test cancelled.");
            return 1;
        }
        finally
        {
            Console.CancelKeyPress -= onCancel;
        }
    }

    private static async Task<int> PauseEngine(string[] args)
    {
        bool stay = args.Contains("--stay-paused", StringComparer.OrdinalIgnoreCase);
        var (succeeded, body) = await PostEmpty($"/api/v1/engine/pause?stay={stay}");
        PrintFormatted(body);
        return succeeded ? PauseOutcome(body, Console.Error, stay) : 1;
    }

    private static async Task<int> ResumeEngine()
    {
        var (succeeded, body) = await PostEmpty("/api/v1/engine/resume");
        PrintFormatted(body);
        return succeeded ? ResumeOutcome(body, Console.Error) : 1;
    }

    /// <summary>
    /// 0 when paused with the driver unloaded, 2 when paused but the driver is still loaded
    /// (why goes to stderr), 1 when the engine isn't paused. A stay-paused choice the service
    /// couldn't save or clear gets a warning on stderr.
    /// </summary>
    internal static int PauseOutcome(string statusJson, TextWriter error, bool stayRequested = false)
    {
        if (!TryParseStatus(statusJson, error, out var status))
            return 1;
        if (!IsTrue(status, "paused"))
        {
            error.WriteLine($"Error: the engine isn't paused. {Text(status, "engineError")}".TrimEnd());
            return 1;
        }
        bool stayPaused = IsTrue(status, "stayPaused");
        if (stayRequested && !stayPaused)
            error.WriteLine("Warning: paused, but the choice to stay paused couldn't be saved, so the pause ends when the service restarts.");
        else if (!stayRequested && stayPaused)
            error.WriteLine("Warning: paused, but an earlier choice to stay paused couldn't be cleared, so the engine stays paused after a restart until you run `onl resume`.");
        if (Text(status, "driverLoadedReason") is { } reason)
        {
            error.WriteLine(reason);
            return 2;
        }
        return 0;
    }

    /// <summary>
    /// 0 when the engine runs again, 1 (with why on stderr) when it doesn't. A stay-paused choice
    /// the service couldn't clear gets a warning on stderr.
    /// </summary>
    internal static int ResumeOutcome(string statusJson, TextWriter error)
    {
        if (!TryParseStatus(statusJson, error, out var status))
            return 1;
        if (IsTrue(status, "running"))
        {
            if (IsTrue(status, "stayPaused"))
                error.WriteLine("Warning: resumed, but the choice to stay paused couldn't be cleared, so the engine comes up paused again after a restart.");
            return 0;
        }
        error.WriteLine($"Error: the engine didn't start. {Text(status, "engineError")}".TrimEnd());
        return 1;
    }

    private static bool TryParseStatus(string json, TextWriter error, out JsonElement status)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            status = doc.RootElement.Clone();
            return status.ValueKind == JsonValueKind.Object;
        }
        catch (JsonException)
        {
            error.WriteLine("Error: the service's answer wasn't a status.");
            status = default;
            return false;
        }
    }

    private static bool IsTrue(JsonElement status, string name) =>
        status.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.True;

    private static string? Text(JsonElement status, string name) =>
        status.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static async Task<int> HandlePcLimit(string[] args)
    {
        if (args.Length == 0 || args[0].Equals("show", StringComparison.OrdinalIgnoreCase))
            return await GetJson("/api/v1/pc-limit");

        switch (args[0].ToLowerInvariant())
        {
            case "set":
                return PcLimitBody(args[1..], Console.Error) is { } body
                    ? await PutJson("/api/v1/pc-limit", body)
                    : 1;
            case "off":
                return await DeleteJson("/api/v1/pc-limit");
            default:
                return PrintUsage();
        }
    }

    /// <summary>
    /// The whole-PC limit that <c>pc-limit set</c> asks for, from KB/s on the command line. A
    /// direction left out has no limit. Null, with why on <paramref name="error"/>, when the
    /// arguments don't make a limit.
    /// </summary>
    internal static Dictionary<string, object>? PcLimitBody(string[] args, TextWriter error)
    {
        if (!TryKilobytes(args, "--download", error, out long download)
            || !TryKilobytes(args, "--upload", error, out long upload))
            return null;
        if (download == 0 && upload == 0)
        {
            error.WriteLine("Error: give --download, --upload or both. To turn the limit off, run `onl pc-limit off`.");
            return null;
        }
        return new Dictionary<string, object>
        {
            ["downloadBytesPerSecond"] = download * 1024,
            ["uploadBytesPerSecond"] = upload * 1024,
            ["includeLan"] = args.Contains("--include-lan", StringComparer.OrdinalIgnoreCase)
        };
    }

    private static bool TryKilobytes(string[] args, string flag, TextWriter error, out long kilobytes)
    {
        kilobytes = 0;
        var value = GetArg(args, flag, null);
        if (value is null)
            return true;
        if (long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out kilobytes)
            && kilobytes > 0 && kilobytes <= long.MaxValue / 1024)
            return true;
        error.WriteLine($"Error: {flag} value '{value}' is not a whole number of KB/s above 0");
        return false;
    }

    private static async Task<int> HandleRules(string[] args)
    {
        if (args.Length == 0 || args[0] == "list")
            return await GetJson("/api/v1/rules");

        return args[0].ToLowerInvariant() switch
        {
            "add" => await AddRule(args[1..]),
            "remove" or "delete" => await RemoveRule(args[1..]),
            "export" => await GetJson("/api/v1/rules"),
            "import" => await ImportRules(args[1..]),
            _ => PrintUsage()
        };
    }

    private static async Task<int> HandleStats(string[] args)
    {
        if (args.Length == 0) return PrintUsage();

        return args[0].ToLowerInvariant() switch
        {
            "top" => await GetJson($"/api/v1/stats/top?days={GetArg(args, "--days", "7")}&limit={GetArg(args, "--limit", "20")}"),
            "hourly" => await GetJson($"/api/v1/stats/hourly?processName={GetArg(args, "--process", "")}&hours={GetArg(args, "--hours", "24")}"),
            "daily" => await GetJson($"/api/v1/stats/daily?processName={GetArg(args, "--process", "")}&days={GetArg(args, "--days", "30")}"),
            _ => PrintUsage()
        };
    }

    private static async Task<int> HandleGroups(string[] args)
    {
        if (args.Length == 0 || args[0] == "list")
            return await GetJson("/api/v1/groups");

        return await GetJson($"/api/v1/groups/{Uri.EscapeDataString(args[0])}");
    }

    private static async Task<int> AddRule(string[] args)
    {
        var process = GetArg(args, "--process", null);
        if (process is null)
        {
            Console.Error.WriteLine("Error: --process is required");
            return 1;
        }

        var body = new Dictionary<string, object?> { ["processName"] = process };

        var download = GetArg(args, "--download", null);
        if (download is not null)
        {
            if (!long.TryParse(download, out var dlValue))
            {
                Console.Error.WriteLine($"Error: --download value '{download}' is not a valid number");
                return 1;
            }
            body["downloadBytesPerSecond"] = dlValue * 1024;
        }

        var upload = GetArg(args, "--upload", null);
        if (upload is not null)
        {
            if (!long.TryParse(upload, out var ulValue))
            {
                Console.Error.WriteLine($"Error: --upload value '{upload}' is not a valid number");
                return 1;
            }
            body["uploadBytesPerSecond"] = ulValue * 1024;
        }

        body["action"] = GetArg(args, "--action", "Limit");

        var group = GetArg(args, "--group", null);
        if (group is not null) body["groupName"] = group;

        var name = GetArg(args, "--name", null);
        if (name is not null) body["name"] = name;

        var protocol = GetArg(args, "--protocol", null);
        if (protocol is not null) body["protocolFilter"] = protocol;

        var ip = GetArg(args, "--ip", null);
        if (ip is not null) body["remoteAddressFilter"] = ip;

        var port = GetArg(args, "--port", null);
        if (port is not null)
        {
            if (!int.TryParse(port, out var portValue))
            {
                Console.Error.WriteLine($"Error: --port value '{port}' is not a valid number");
                return 1;
            }
            body["remotePortFilter"] = portValue;
        }

        return await PostJson("/api/v1/rules", body);
    }

    private static async Task<int> RemoveRule(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("Error: rule ID is required");
            return 1;
        }
        return await DeleteJson($"/api/v1/rules/{args[0]}");
    }

    private static async Task<int> ImportRules(string[] args)
    {
        const int MaxImportBytes = 1024 * 1024; // 1 MB limit matching REST API
        string json;
        if (args.Length > 0 && File.Exists(args[0]))
        {
            var fileInfo = new FileInfo(args[0]);
            if (fileInfo.Length > MaxImportBytes)
            {
                Console.Error.WriteLine($"Error: import file exceeds {MaxImportBytes / 1024}KB limit");
                return 1;
            }
            json = await File.ReadAllTextAsync(args[0]);
        }
        else
        {
            var buffer = new char[MaxImportBytes + 1];
            int totalRead = await Console.In.ReadBlockAsync(buffer, 0, buffer.Length);
            if (totalRead > MaxImportBytes)
            {
                Console.Error.WriteLine($"Error: stdin input exceeds {MaxImportBytes / 1024}KB limit");
                return 1;
            }
            json = new string(buffer, 0, totalRead);
        }

        var replace = args.Contains("--replace");
        return await PostJson($"/api/v1/rules/import?replace={replace}", json);
    }

    private static async Task<int> GetJson(string path)
    {
        using var client = CreateClient();
        var response = await client.GetAsync(BaseUrl + path);
        var body = await response.Content.ReadAsStringAsync();
        PrintFormatted(body);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<int> PostJson(string path, object body)
    {
        using var client = CreateClient();
        var json = body is string s ? s : JsonSerializer.Serialize(body, JsonOptions);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(BaseUrl + path, content);
        var responseBody = await response.Content.ReadAsStringAsync();
        PrintFormatted(responseBody);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<int> PutJson(string path, object body)
    {
        using var client = CreateClient();
        using var content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json");
        var response = await client.PutAsync(BaseUrl + path, content);
        PrintFormatted(await response.Content.ReadAsStringAsync());
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static async Task<(bool Succeeded, string Body)> PostEmpty(string path)
    {
        using var client = CreateClient();
        using var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
        var response = await client.PostAsync(BaseUrl + path, content);
        return (response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    }

    private static async Task<int> DeleteJson(string path)
    {
        using var client = CreateClient();
        var response = await client.DeleteAsync(BaseUrl + path);
        var body = await response.Content.ReadAsStringAsync();
        PrintFormatted(body);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient();
        if (ApiKey is not null)
            client.DefaultRequestHeaders.Add("X-OpenNetLimit-Key", ApiKey);
        return client;
    }

    private static void PrintFormatted(string json)
    {
        try
        {
            var doc = JsonDocument.Parse(json);
            Console.WriteLine(JsonSerializer.Serialize(doc, JsonOptions));
        }
        catch
        {
            Console.WriteLine(json);
        }
    }

    private static string? GetArg(string[] args, string name, string? defaultValue)
    {
        for (int i = 0; i < args.Length - 1; i++)
        {
            if (args[i].Equals(name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        }
        return defaultValue;
    }

    private static int PrintUsage()
    {
        Console.WriteLine("""
            OpenNetLimit CLI: scriptable bandwidth rule management

            Usage: onl <command> [options]

            Commands:
              status                          Service status and diagnostics
              snapshot                        Current per-process traffic snapshot
              processes                       Tracked processes
              health                          Liveness check
              selftest [--limit <KB/s>]       Limit this client, measure loopback traffic,
                                              then remove the limit (default 256 KB/s)
              pause [--stay-paused]           Stop limiting and unload the driver, for games
                                              whose anti-cheat won't start beside it. Exits 2
                                              if the driver is still loaded, and says why.
                                              --stay-paused keeps it paused after a restart
              resume                          Start limiting again

              pc-limit                        Show the whole-PC limit
              pc-limit set                    Cap the whole PC's internet traffic, shared
                [--download <KB/s>]             by every app. A direction left out has no
                [--upload <KB/s>]               cap. Apps with their own limit stay under it
                [--include-lan]                 Count the local network too
              pc-limit off                    Turn the whole-PC limit off

              rules list                      List all bandwidth rules
              rules add --process <name>      Add a bandwidth rule
                [--download <KB/s>]             Download limit in KB/s
                [--upload <KB/s>]               Upload limit in KB/s
                [--action Limit|Block|Allow]     Rule action (default: Limit)
                [--group <name>]                Assign to a rule group
                [--name <label>]                Human-readable rule name
                [--protocol Tcp|Udp]            Protocol filter
                [--ip <addr or CIDR>]           Remote IP/subnet filter
                [--port <port>]                 Remote port filter
              rules remove <id>               Remove a rule by ID
              rules export                    Export rules as JSON
              rules import <file> [--replace] Import rules from JSON file or stdin

              stats top [--days N] [--limit N] Top processes by bandwidth
              stats hourly [--process <name>]  Hourly stats
              stats daily [--process <name>]   Daily stats

              groups list                     List rule group names
              groups <name>                   Show rules in a group

              quotas                          Quota states
              connections                     Recent connection log
              alerts                          Recent alert events

            Environment:
              OPENNETLIMIT_API_URL            REST API base URL (default: http://127.0.0.1:47719)
              OPENNETLIMIT_API_KEY            API key for mutations and remote access
            """);
        return 1;
    }
}
