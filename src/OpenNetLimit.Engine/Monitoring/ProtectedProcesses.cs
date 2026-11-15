using System.Collections.Frozen;
using OpenNetLimit.Core;

namespace OpenNetLimit.Engine.Monitoring;

/// <summary>How far a process is kept out of rules and limits.</summary>
public enum ProcessProtection
{
    /// <summary>Rules and limits apply.</summary>
    None,

    /// <summary>
    /// Windows' own, so no rule blocks or slows it. Its TCP still counts toward the whole-PC
    /// limit, since it can carry Windows Update, BITS, Delivery Optimization or file copies.
    /// </summary>
    Windows,

    /// <summary>Only Windows networking runs in it (DNS, DHCP and the like), which nothing slows or counts.</summary>
    Networking
}

/// <summary>
/// Which processes rules leave alone. Slowing one of these would slow everything: DNS lookups,
/// address renewals, sign-ins, certificate checks.
/// </summary>
public static class ProtectedProcesses
{
    private static readonly FrozenSet<string> Names = new[]
    {
        "services", "lsass", "csrss", "wininit", "smss", "System", "ntoskrnl"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Processes that send nothing but name resolution and addressing, when they run from
    /// System32. The Windows DNS Server answers TCP queries from local port 53, which the
    /// remote-port DNS check never sees. A copy elsewhere is anyone's program under that name.
    /// </summary>
    private static readonly FrozenSet<string> NetworkingNames = new[]
    {
        "dns", "dhcp", "dnscache"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Services that keep the svchost they run in out of every rule: name resolution and
    /// addressing, the network stack's own bookkeeping, the firewall and IPsec, VPN and Wi-Fi
    /// sign-in, domain sign-in, time, certificate checks, and RPC, which everything above uses.
    /// Connection sharing too: SharedAccess answers DNS and DHCP for Mobile Hotspot and WSL
    /// clients, which icssvc and the Host Network Service (hns) set up.
    /// </summary>
    public static readonly FrozenSet<string> NetworkingServices = new[]
    {
        "Dnscache", "Dhcp", "nsi", "NlaSvc", "netprofm", "Wcmsvc", "NcbService", "iphlpsvc",
        "WinHttpAutoProxySvc", "BFE", "mpssvc", "IKEEXT", "PolicyAgent", "RasMan", "SstpSvc",
        "Wlansvc", "dot3svc", "EapHost", "Netlogon", "W32Time", "CryptSvc",
        "RpcSs", "RpcEptMapper", "DcomLaunch", "LSM",
        "SharedAccess", "icssvc", "hns"
    }.ToFrozenSet(StringComparer.OrdinalIgnoreCase);

    /// <param name="services">The services running in the process, null when there are none or they aren't known.</param>
    /// <param name="processPath">
    /// The program's full path, null when it couldn't be read. A Windows name counts only from
    /// System32 or SysWOW64: a copy anywhere else is anyone's program under that name. With no
    /// path the name is all there is to go on, and the kernel (System) never has one.
    /// </param>
    public static ProcessProtection Of(string processName, IReadOnlyList<string>? services, string? processPath = null)
    {
        bool networkingName = NetworkingNames.Contains(processName);
        bool serviceHost = ServiceHosts.IsServiceHost(processName);
        if (!networkingName && !serviceHost && !Names.Contains(processName))
            return ProcessProtection.None;
        if (processPath is not null && !WindowsFolders.Contain(processPath))
            return ProcessProtection.None;
        if (networkingName)
            return processPath is null ? ProcessProtection.Windows : ProcessProtection.Networking;
        if (!serviceHost)
            return ProcessProtection.Windows;
        // A svchost whose services haven't been read yet is kept safe until they are.
        if (services is not { Count: > 0 })
            return ProcessProtection.Windows;
        int networking = 0;
        foreach (var service in services)
        {
            if (NetworkingServices.Contains(service))
                networking++;
        }
        if (networking == 0)
            return ProcessProtection.None;
        return networking == services.Count ? ProcessProtection.Networking : ProcessProtection.Windows;
    }

    /// <summary>The services in the list that keep a svchost out of every rule.</summary>
    public static string[] NetworkingIn(IReadOnlyList<string>? services) =>
        services?.Where(NetworkingServices.Contains).ToArray() ?? [];
}
