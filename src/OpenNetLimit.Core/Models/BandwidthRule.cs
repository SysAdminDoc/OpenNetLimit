using System.Net;
using OpenNetLimit.Core;

namespace OpenNetLimit.Core.Models;

public enum RuleAction
{
    Limit,
    Block,
    Allow
}

public enum RuleDirection
{
    Both,
    Download,
    Upload
}

public class BandwidthRule
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;

    private string? _processName;
    public string? ProcessName
    {
        get => _processName;
        set => _processName = ProcessNames.Normalize(value);
    }
    public string? ProcessPath { get; set; }

    /// <summary>The process Windows runs shared services in.</summary>
    public const string ServiceHostName = "svchost";

    /// <summary>
    /// The rule is on svchost itself, by name or by the file its path ends in, rather than on a
    /// service. Such a rule never matches, since a svchost is reached only through a service in
    /// it. A wildcard path that takes svchost in among other programs isn't one, and neither is
    /// a path to a svchost.exe outside Windows' folders, which is some other program's copy.
    /// </summary>
    public bool TargetsServiceHost()
    {
        if (ServiceName is not null)
            return false;
        if (string.Equals(ProcessName, ServiceHostName, StringComparison.OrdinalIgnoreCase))
            return true;
        if (string.IsNullOrWhiteSpace(ProcessPath))
            return false;
        var path = ProcessPath.Trim().Trim('"');
        var file = Path.GetFileName(path);
        if (file.IndexOfAny(['*', '?']) >= 0
            || !string.Equals(ProcessNames.Normalize(file), ServiceHostName, StringComparison.OrdinalIgnoreCase))
            return false;
        var folder = Path.GetDirectoryName(path);
        return string.IsNullOrEmpty(folder) || folder.IndexOfAny(['*', '?']) >= 0 || WindowsFolders.Contain(path);
    }

    /// <summary>
    /// Windows' own svchost: one by that name with no path to go on, or one in System32 or SysWOW64.
    /// A copy anywhere else is some other program under its name, which rules reach like any other.
    /// </summary>
    public static bool IsServiceHost(string processName, string? processPath) =>
        processName.Equals(ServiceHostName, StringComparison.OrdinalIgnoreCase)
        && (processPath is null || WindowsFolders.Contain(processPath));

    private string? _serviceName;

    /// <summary>
    /// A Windows service, by its short name ("DoSvc", "BITS"). The rule then covers the process
    /// the service runs in, which for most services is a svchost of their own.
    /// </summary>
    public string? ServiceName
    {
        get => _serviceName;
        set => _serviceName = string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    public RuleAction Action { get; set; } = RuleAction.Limit;
    public RuleDirection Direction { get; set; } = RuleDirection.Both;

    public long DownloadBytesPerSecond { get; set; }
    public long UploadBytesPerSecond { get; set; }

    public string? RemoteAddressFilter { get; set; }
    public int? RemotePortFilter { get; set; }
    public string? ProtocolFilter { get; set; }
    public string[]? CountryFilter { get; set; }
    public string? DnsDomainFilter { get; set; }

    public DateTime? ActiveFrom { get; set; }
    public DateTime? ActiveUntil { get; set; }

    public RuleSchedule? Schedule { get; set; }

    public QuotaConfig? Quota { get; set; }

    public string? ProfileName { get; set; }

    public string? GroupName { get; set; }

    public int Priority { get; set; }

    public bool MatchesProcess(string processName, string? processPath) =>
        MatchesProcess(processName, processPath, services: null);

    /// <param name="services">The services running in the process, null when there are none or they aren't known.</param>
    public bool MatchesProcess(string processName, string? processPath, IReadOnlyList<string>? services)
    {
        if (ServiceName is not null)
        {
            if (services is null)
                return false;
            foreach (var service in services)
            {
                if (service.Equals(ServiceName, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        // A svchost runs Windows services, often several at once. Only a rule on one of them by
        // name covers it, so a rule on svchost or a path that takes it in can't slow them all.
        if (IsServiceHost(processName, processPath))
            return false;

        if (ProcessPath is not null && processPath is not null)
        {
            if (ProcessPath.Contains('*') || ProcessPath.Contains('?'))
                return WildcardMatcher.IsMatch(processPath, ProcessPath);
            return processPath.Equals(ProcessPath, StringComparison.OrdinalIgnoreCase);
        }

        if (ProcessName is not null)
            return processName.Equals(ProcessName, StringComparison.OrdinalIgnoreCase);

        return false;
    }

    public bool MatchesConnection(IPAddress? remoteAddress, int? remotePort, string? protocol)
    {
        if (ProtocolFilter is not null && protocol is not null &&
            !ProtocolFilter.Equals(protocol, StringComparison.OrdinalIgnoreCase))
            return false;

        if (RemotePortFilter.HasValue && remotePort.HasValue && RemotePortFilter.Value != remotePort.Value)
            return false;

        if (RemoteAddressFilter is not null && remoteAddress is not null)
        {
            if (RemoteAddressFilter.Contains('/'))
            {
                if (!MatchesCidr(remoteAddress, RemoteAddressFilter))
                    return false;
            }
            else
            {
                if (!IPAddress.TryParse(RemoteAddressFilter, out var filterIp) ||
                    !filterIp.Equals(remoteAddress))
                    return false;
            }
        }

        return true;
    }

    public bool HasConnectionFilters =>
        RemoteAddressFilter is not null || RemotePortFilter.HasValue ||
        ProtocolFilter is not null || CountryFilter is { Length: > 0 } ||
        DnsDomainFilter is not null;

    public bool MatchesDomain(string? resolvedDomain)
    {
        if (DnsDomainFilter is null)
            return true;
        if (resolvedDomain is null)
            return false;

        if (DnsDomainFilter.StartsWith("*."))
        {
            var suffix = DnsDomainFilter[1..]; // ".example.com"
            return resolvedDomain.EndsWith(suffix, StringComparison.OrdinalIgnoreCase) ||
                   resolvedDomain.Equals(DnsDomainFilter[2..], StringComparison.OrdinalIgnoreCase);
        }

        return resolvedDomain.Equals(DnsDomainFilter, StringComparison.OrdinalIgnoreCase);
    }

    public bool MatchesCountry(string? countryCode)
    {
        if (CountryFilter is not { Length: > 0 })
            return true;
        if (countryCode is null)
            return false;
        return CountryFilter.Any(c => c.Equals(countryCode, StringComparison.OrdinalIgnoreCase));
    }

    private static bool MatchesCidr(IPAddress address, string cidr)
    {
        var parts = cidr.Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var network) || !int.TryParse(parts[1], out var prefixLen))
            return false;

        var networkBytes = network.GetAddressBytes();
        var addressBytes = address.GetAddressBytes();
        if (networkBytes.Length != addressBytes.Length)
            return false;

        var fullBytes = prefixLen / 8;
        var remainingBits = prefixLen % 8;

        for (int i = 0; i < fullBytes && i < networkBytes.Length; i++)
        {
            if (networkBytes[i] != addressBytes[i])
                return false;
        }

        if (remainingBits > 0 && fullBytes < networkBytes.Length)
        {
            var mask = (byte)(0xFF << (8 - remainingBits));
            if ((networkBytes[fullBytes] & mask) != (addressBytes[fullBytes] & mask))
                return false;
        }

        return true;
    }

    public BandwidthRule Clone() => new()
    {
        Id = Id,
        Name = Name,
        Enabled = Enabled,
        ProcessName = ProcessName,
        ProcessPath = ProcessPath,
        ServiceName = ServiceName,
        Action = Action,
        Direction = Direction,
        DownloadBytesPerSecond = DownloadBytesPerSecond,
        UploadBytesPerSecond = UploadBytesPerSecond,
        RemoteAddressFilter = RemoteAddressFilter,
        RemotePortFilter = RemotePortFilter,
        ProtocolFilter = ProtocolFilter,
        CountryFilter = CountryFilter?.ToArray(),
        DnsDomainFilter = DnsDomainFilter,
        ActiveFrom = ActiveFrom,
        ActiveUntil = ActiveUntil,
        Schedule = Schedule is null ? null : new RuleSchedule
        {
            StartTime = Schedule.StartTime,
            EndTime = Schedule.EndTime,
            ActiveDays = Schedule.ActiveDays?.ToArray()
        },
        Quota = Quota is null ? null : new QuotaConfig
        {
            LimitBytes = Quota.LimitBytes,
            Period = Quota.Period,
            OnExceeded = Quota.OnExceeded,
            ThrottleBytesPerSecond = Quota.ThrottleBytesPerSecond,
            WarningPercent = Quota.WarningPercent
        },
        ProfileName = ProfileName,
        GroupName = GroupName,
        Priority = Priority
    };

    public bool IsActiveNow()
    {
        if (!Enabled) return false;
        var now = DateTime.UtcNow;
        if (ActiveFrom.HasValue && now < ActiveFrom.Value) return false;
        if (ActiveUntil.HasValue && now > ActiveUntil.Value) return false;
        if (Schedule is not null && !Schedule.IsActiveAt(now)) return false;
        return true;
    }
}

public enum QuotaPeriod
{
    Daily,
    Weekly,
    Monthly
}

public enum QuotaAction
{
    Throttle,
    Block,
    WarnOnly
}

public class QuotaConfig
{
    public long LimitBytes { get; set; }
    public QuotaPeriod Period { get; set; } = QuotaPeriod.Daily;
    public QuotaAction OnExceeded { get; set; } = QuotaAction.Throttle;
    public long ThrottleBytesPerSecond { get; set; } = 10 * 1024;
    public int WarningPercent { get; set; } = 80;
}

public class RuleSchedule
{
    public TimeOnly? StartTime { get; set; }
    public TimeOnly? EndTime { get; set; }
    public DayOfWeek[]? ActiveDays { get; set; }

    public bool IsActiveAt(DateTime utcNow)
    {
        var localNow = utcNow.Kind == DateTimeKind.Utc ? utcNow.ToLocalTime() : utcNow;

        if (ActiveDays is { Length: > 0 } && !ActiveDays.Contains(localNow.DayOfWeek))
            return false;

        if (StartTime.HasValue && EndTime.HasValue)
        {
            var timeNow = TimeOnly.FromDateTime(localNow);
            if (StartTime.Value <= EndTime.Value)
                return timeNow >= StartTime.Value && timeNow <= EndTime.Value;
            return timeNow >= StartTime.Value || timeNow <= EndTime.Value;
        }

        return true;
    }
}
