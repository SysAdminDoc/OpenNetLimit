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
        if (download is not null) body["downloadBytesPerSecond"] = long.Parse(download) * 1024;

        var upload = GetArg(args, "--upload", null);
        if (upload is not null) body["uploadBytesPerSecond"] = long.Parse(upload) * 1024;

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
        if (port is not null) body["remotePortFilter"] = int.Parse(port);

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
        string json;
        if (args.Length > 0 && File.Exists(args[0]))
            json = await File.ReadAllTextAsync(args[0]);
        else
            json = await Console.In.ReadToEndAsync();

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
            OpenNetLimit CLI - scriptable bandwidth rule management

            Usage: onl <command> [options]

            Commands:
              status                          Service status and diagnostics
              snapshot                        Current per-process traffic snapshot
              processes                       Tracked processes
              health                          Liveness check

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
