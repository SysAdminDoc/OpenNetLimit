using System.Diagnostics;
using System.Net;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using RuleAction = OpenNetLimit.Core.Models.RuleAction;
using OpenNetLimit.Engine.Monitoring;
using OpenNetLimit.Engine.RateLimiting;
using SharpDivert;

namespace OpenNetLimit.Engine.Interception;

public sealed class WinDivertInterceptor : IPacketInterceptor
{
    private readonly IFlowTracker _flowTracker;
    private readonly IRateLimiter _rateLimiter;
    private readonly IRuleEngine _ruleEngine;
    private readonly ITrafficMonitor _trafficMonitor;
    private readonly PacketScheduler _scheduler = new();
    private readonly ConnectionLogger _connectionLog = new();
    private readonly DnsDomainCache _dnsCache = new();
    private readonly SocketOwners _socketOwners;
    private Timer? _socketRefreshTimer;
    private long _totalBlocked;

    /// <summary>How often adopted flows are checked against the OS socket tables.</summary>
    private static readonly TimeSpan SocketRefreshInterval = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Windows' own processes. No rule blocks or slows them, since that would slow everything.
    /// Their TCP still counts toward the whole-PC limit, see <see cref="LimitsFor"/>.
    /// </summary>
    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "services", "lsass", "csrss", "wininit", "smss",
        "dns", "dhcp", "dnscache", "System", "ntoskrnl"
    };

    /// <summary>
    /// The Flow layer only reports events, so WinDivert requires it to be opened as a
    /// passive observer with both Sniff and RecvOnly. Any other combination makes
    /// WinDivertOpen fail with ERROR_INVALID_PARAMETER (87).
    /// </summary>
    internal const WinDivert.Flag FlowLayerFlags = WinDivert.Flag.Sniff | WinDivert.Flag.RecvOnly;

    /// <summary>
    /// The Network layer diverts packets so they can be delayed or dropped, then reinjected.
    /// </summary>
    internal const WinDivert.Flag NetworkLayerFlags = 0;

    /// <summary>
    /// The driver drops packets once more are waiting than its queue holds. The defaults are
    /// 4096 packets and 4 MB, which a fast download fills in a few milliseconds (with receive
    /// coalescing a 4 MB queue holds only about 64 packets of 64 KB). Those drops are silent, so
    /// TCP stalls on retransmits while the packet loop catches up. These are the largest values
    /// WinDivert allows; at the maximum a 12,000-datagram loopback burst arrives whole.
    /// </summary>
    internal const ulong NetworkQueueLength = WinDivert.QueueLengthMax;
    internal const ulong NetworkQueueBytes = WinDivert.QueueSizeMax;

    private WinDivert? _networkHandle;
    private WinDivert? _flowHandle;
    private CancellationTokenSource? _cts;
    private readonly DriverPreflight _preflight;
    private Task? _networkTask;
    private Task? _flowTask;

    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;

    /// <summary>
    /// From a start until the stop that closes the handles. The packet loops clear
    /// <see cref="_isRunning"/> when they give up after repeated errors, but the handles stay
    /// open, and the driver loaded, until a stop closes them.
    /// </summary>
    private volatile bool _opened;
    public PacketScheduler Scheduler => _scheduler;

    public long TotalBlocked => Volatile.Read(ref _totalBlocked);
    public long TotalDelayed => _scheduler.TotalDelayed;
    public long TotalDropped => _scheduler.TotalDropped;
    public long TotalSent => _scheduler.TotalSent;
    public ConnectionLogger ConnectionLog => _connectionLog;

    public IReadOnlyList<object> GetRecentConnectionLog(int maxCount) =>
        _connectionLog.GetRecent(maxCount).Cast<object>().ToList();

    public WinDivertInterceptor(
        IFlowTracker flowTracker,
        IRateLimiter rateLimiter,
        IRuleEngine ruleEngine,
        ITrafficMonitor trafficMonitor,
        IDriverService driverService)
    {
        _flowTracker = flowTracker;
        _rateLimiter = rateLimiter;
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
        _preflight = new DriverPreflight(driverService);
        _socketOwners = new SocketOwners(flowTracker, OsSocketTable.Read,
            processId => new ProcessIdentity(ResolveProcessName(processId), ResolveProcessPath(processId)));
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_opened) return Task.CompletedTask;

        // Set before launching tasks to prevent a racing StopAsync from returning early
        // while tasks are starting
        _opened = true;
        _isRunning = true;

        // The last stop awaited both loops, so nothing still uses the previous source.
        _cts?.Dispose();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            if (_preflight.WaitWhileDriverIsChanging(_cts.Token) is { } waited)
                Trace.TraceInformation(waited);
            _flowHandle = _preflight.OpenLoadingDriver(() => OpenHandle(WinDivert.Layer.Flow, FlowLayerFlags), DriverFolder);
            _networkHandle = OpenHandle(WinDivert.Layer.Network, NetworkLayerFlags);
            _networkHandle.QueueLength = NetworkQueueLength;
            _networkHandle.QueueSize = NetworkQueueBytes;
        }
        catch
        {
            _isRunning = false;
            _opened = false;
            _flowHandle?.Dispose();
            _flowHandle = null;
            _networkHandle?.Dispose();
            _networkHandle = null;
            throw;
        }

        _scheduler.SetHandle(_networkHandle);

        _flowTask = Task.Factory.StartNew(
            () => FlowLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        _networkTask = Task.Factory.StartNew(
            () => NetworkLoop(_cts.Token),
            _cts.Token,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);

        // The first read happens right away, after the flow handle is open, so every connection
        // is either reported by the flow layer or found in the tables.
        _socketRefreshTimer = new Timer(_ => RefreshSocketOwners(), null, TimeSpan.Zero, SocketRefreshInterval);

        return Task.CompletedTask;
    }

    private void RefreshSocketOwners()
    {
        try
        {
            _socketOwners.Refresh();
        }
        catch (Exception ex)
        {
            Trace.TraceWarning($"Reading the socket tables failed: {ex.Message}");
        }
    }

    private static WinDivert OpenHandle(WinDivert.Layer layer, WinDivert.Flag flags)
    {
        try
        {
            return new WinDivert("true", layer, 0, flags);
        }
        catch (WinDivertException ex)
        {
            throw new WinDivertOpenException(layer, flags, ex);
        }
    }

    /// <summary>
    /// The folder of the WinDivert.dll this process loaded. The DLL installs the driver from the
    /// WinDivert64.sys next to it, which in a framework-dependent build is under runtimes\.
    /// </summary>
    internal static string DriverFolder(IEnumerable<string> modulePaths) =>
        modulePaths
            .Where(path => Path.GetFileName(path).Equals("WinDivert.dll", StringComparison.OrdinalIgnoreCase))
            .Select(path => Path.GetDirectoryName(path))
            .FirstOrDefault(folder => !string.IsNullOrEmpty(folder))
        ?? AppContext.BaseDirectory;

    private static string DriverFolder()
    {
        var paths = new List<string>();
        try
        {
            using var process = Process.GetCurrentProcess();
            foreach (ProcessModule module in process.Modules)
            {
                using (module)
                    paths.Add(module.FileName);
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or NotSupportedException)
        {
            // The open's own error matters more than this lookup, so it carries on with the app folder.
            Trace.TraceWarning($"Listing the loaded modules failed, so the driver is looked for in the app folder: {ex.Message}");
        }
        return DriverFolder(paths);
    }

    public async Task StopAsync()
    {
        if (!_opened) return;

        _cts?.Cancel();
        _socketRefreshTimer?.Dispose();
        _socketRefreshTimer = null;

        // Both loops wait inside RecvEx, which never sees the cancellation. Without this a stop
        // waited for the next packet and the next flow event, often several seconds. Shutting
        // down receive makes a waiting RecvEx return once its queue is empty.
        ShutdownReceive(_networkHandle);
        ShutdownReceive(_flowHandle);

        try
        {
            if (_networkTask is not null)
                await _networkTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        try
        {
            if (_flowTask is not null)
                await _flowTask.ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }

        // Detached, not disposed, so a later start can attach the new handle.
        _scheduler.Detach();
        _networkHandle?.Dispose();
        _flowHandle?.Dispose();
        _networkHandle = null;
        _flowHandle = null;

        _isRunning = false;
        _opened = false;
    }

    private static void ShutdownReceive(WinDivert? handle)
    {
        try
        {
            handle?.ShutdownRecv();
        }
        catch (WinDivertException ex)
        {
            Trace.TraceWarning($"Stopping WinDivert receive failed: {ex.Message}");
        }
    }

    private void FlowLoop(CancellationToken ct)
    {
        var buffer = new Memory<byte>(new byte[65535]);
        var addrBuffer = new Memory<WinDivertAddress>(new WinDivertAddress[1]);
        int consecutiveErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (recvLen, _) = _flowHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                consecutiveErrors = 0;
                ref var addr = ref addrBuffer.Span[0];

                var flowData = addr.Flow;
                var flowKey = FlowAddress.ToFlowKey(flowData);
                var protocol = flowKey.Protocol;
                var localAddr = flowKey.LocalAddress;
                var remoteAddr = flowKey.RemoteAddress;

                if (addr.Event == WinDivert.Event.FlowEstablished)
                {
                    if (!_socketOwners.RegisterEstablished(flowKey, flowData.ProcessId, addr.Timestamp,
                            out var processId, out var identity))
                        continue;
                    _connectionLog.Log(new ConnectionLogEntry
                    {
                        ProcessId = processId,
                        ProcessName = identity.Name,
                        Action = "Established",
                        Protocol = protocol.ToString(),
                        LocalEndpoint = $"{localAddr}:{flowData.LocalPort}",
                        RemoteEndpoint = $"{remoteAddr}:{flowData.RemotePort}"
                    });
                }
                else if (addr.Event == WinDivert.Event.FlowDeleted)
                {
                    _flowTracker.UnregisterFlow(flowKey);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Trace.TraceError($"FlowLoop error ({consecutiveErrors}): {ex.Message}");
                if (consecutiveErrors >= 10)
                {
                    Trace.TraceError("FlowLoop: too many consecutive errors, stopping");
                    _isRunning = false;
                    break;
                }
                Thread.Sleep(Math.Min(consecutiveErrors * 100, 1000));
            }
        }
    }

    private void NetworkLoop(CancellationToken ct)
    {
        var buffer = new Memory<byte>(new byte[65535]);
        var addrBuffer = new Memory<WinDivertAddress>(new WinDivertAddress[1]);
        int consecutiveErrors = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (recvLen, _) = _networkHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                consecutiveErrors = 0;
                ref var addr = ref addrBuffer.Span[0];
                var packet = buffer[..(int)recvLen];

                var parsed = ParsePacket(packet);
                if (parsed is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var (flowKey, payloadLength, payloadData, closesFlow) = parsed.Value;
                bool isOutbound = addr.Outbound;

                // Responses are cached before attribution: lookups usually come back to the
                // DNS Client service, not the app, and domain rules need them either way.
                TryCacheDnsResponse(_dnsCache, flowKey, isOutbound, payloadData.Span);

                var processId = _flowTracker.LookupProcessId(flowKey);
                if (processId is null && _socketOwners.TryAdopt(flowKey, isOutbound))
                    processId = _flowTracker.LookupProcessId(flowKey);
                if (processId is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var connection = _flowTracker.LookupConnection(flowKey);
                string processName = connection?.ProcessName ?? "unknown";

                var endpoints = PacketEndpoints.From(flowKey, isOutbound);
                var remoteAddr = endpoints.RemoteAddress;
                var remotePort = endpoints.RemotePort;
                var protocolStr = flowKey.Protocol.ToString();
                var resolvedDomain = _dnsCache.LookupDomain(remoteAddr);
                var matchingRule = _ruleEngine.FindMatchingRule(processName, connection?.ProcessPath,
                    remoteAddr, remotePort, protocolStr, resolvedDomain: resolvedDomain);
                bool isProtected = ProtectedProcesses.Contains(processName);
                // A Block rule and a used-up quota that blocks both drop the packet here.
                bool blockedByRule = matchingRule?.Action == RuleAction.Block;
                if (!isProtected && (blockedByRule || _rateLimiter.IsBlocked(processId.Value)))
                {
                    Interlocked.Increment(ref _totalBlocked);
                    // Dropped bytes never reached the app, so they don't count as its traffic or
                    // fill its quota. Recording zero still lists the app.
                    _trafficMonitor.RecordBytes(processId.Value, processName, 0, isOutbound, connection?.ProcessPath);
                    _connectionLog.Log(new ConnectionLogEntry
                    {
                        ProcessId = processId.Value,
                        ProcessName = processName,
                        Action = "Blocked",
                        Protocol = flowKey.Protocol.ToString(),
                        LocalEndpoint = $"{endpoints.LocalAddress}:{endpoints.LocalPort}",
                        RemoteEndpoint = $"{endpoints.RemoteAddress}:{endpoints.RemotePort}",
                        RuleName = blockedByRule ? matchingRule!.Name : "Quota used up"
                    });
                    continue;
                }

                if (connection is not null)
                {
                    if (isOutbound)
                        connection.AddBytesSent(payloadLength);
                    else
                        connection.AddBytesReceived(payloadLength);
                }

                _trafficMonitor.RecordBytes(processId.Value, processName, payloadLength, isOutbound, connection?.ProcessPath);

                var limits = LimitsFor(isProtected, flowKey.Protocol, payloadLength, closesFlow,
                    PcLimitScope.Applies(_rateLimiter.PcLimit, isOutbound, remoteAddr, addr.Loopback),
                    _rateLimiter.HasLimit(processId.Value));
                if (limits.Any)
                {
                    // Every packet is charged, the delayed ones too, so a burst queues behind its
                    // own debt and leaves at the limit. A packet with nowhere to wait is dropped.
                    var reserved = false;
                    var delay = TimeSpan.Zero;
                    if (_scheduler.HasRoom(processId.Value))
                        reserved = limits.App
                            ? _rateLimiter.TryReserve(processId.Value, payloadLength, isOutbound, limits.WholePc, PacketScheduler.MaxDelay, out delay)
                            : _rateLimiter.TryReserveWholePc(payloadLength, isOutbound, PacketScheduler.MaxDelay, out delay);
                    if (!reserved)
                    {
                        _scheduler.RecordDrop();
                        continue;
                    }
                    if (delay > TimeSpan.Zero)
                    {
                        _scheduler.Enqueue(processId.Value, packet.Span, addrBuffer.Span, delay);
                        continue;
                    }
                }

                _networkHandle.SendEx(packet.Span, addrBuffer.Span);
            }
            catch (Exception) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                consecutiveErrors++;
                Trace.TraceError($"NetworkLoop error ({consecutiveErrors}): {ex.Message}");
                if (consecutiveErrors >= 10)
                {
                    Trace.TraceError("NetworkLoop: too many consecutive errors, stopping");
                    _isRunning = false;
                    break;
                }
                Thread.Sleep(Math.Min(consecutiveErrors * 100, 1000));
            }
        }
    }

    /// <summary>Which limits a packet waits for: the whole-PC limit, its app's own, both or neither.</summary>
    internal readonly record struct PacketLimits(bool WholePc, bool App)
    {
        public bool Any => WholePc || App;
    }

    /// <summary>
    /// Which limits a packet waits for. <paramref name="pcLimitCounts"/> says the whole-PC limit
    /// covers its direction and remote end, <paramref name="hasAppLimit"/> that its app has a
    /// limit of its own.
    /// </summary>
    internal static PacketLimits LimitsFor(bool isProtected, TransportProtocol protocol, int payloadLength,
        bool closesFlow, bool pcLimitCounts, bool hasAppLimit)
    {
        // A pure ACK or SYN carries no data, so it never waits behind the debt data ran up.
        // Holding an upload's ACKs behind a download cap's queue slowed the upload too. FIN and
        // RST still wait, or they'd get there before the data queued ahead of them.
        if (payloadLength == 0 && !closesFlow)
            return default;
        // Windows' own services carry Windows Update, BITS, Delivery Optimization and SMB file
        // copies over TCP, so that counts toward the whole-PC limit. Their UDP (DNS, DHCP, NTP)
        // doesn't, and no app limit applies to them.
        return new PacketLimits(
            WholePc: pcLimitCounts && (!isProtected || protocol == TransportProtocol.Tcp),
            App: hasAppLimit && !isProtected);
    }

    /// <summary>
    /// A packet key is (source, destination). Outbound packets leave from the local end and
    /// inbound packets arrive from the remote end, so the sides swap with direction.
    /// </summary>
    internal readonly record struct PacketEndpoints(IPAddress LocalAddress, int LocalPort, IPAddress RemoteAddress, int RemotePort)
    {
        public static PacketEndpoints From(in FlowKey packetKey, bool isOutbound) => isOutbound
            ? new(packetKey.LocalAddress, packetKey.LocalPort, packetKey.RemoteAddress, packetKey.RemotePort)
            : new(packetKey.RemoteAddress, packetKey.RemotePort, packetKey.LocalAddress, packetKey.LocalPort);
    }

    /// <summary>
    /// Caches the domain to address answers of an inbound DNS response (UDP from port 53).
    /// Returns true when the packet was a DNS response that yielded at least one record.
    /// </summary>
    internal static bool TryCacheDnsResponse(DnsDomainCache cache, in FlowKey packetKey, bool isOutbound, ReadOnlySpan<byte> payload)
    {
        if (isOutbound || packetKey.Protocol != TransportProtocol.Udp || payload.Length <= 12)
            return false;
        if (PacketEndpoints.From(packetKey, isOutbound).RemotePort != 53)
            return false;

        try
        {
            var records = DnsResponseParser.ParseResponse(payload);
            foreach (var record in records)
                cache.RecordMapping(record.Address, record.Domain, record.Ttl);
            return records.Count > 0;
        }
        catch (Exception ex)
        {
            // A malformed response must never disrupt packet flow.
            Trace.TraceWarning($"DNS response parse failed: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// The packet's flow, its payload, and whether it's a TCP FIN or RST, which has to keep its
    /// place behind the data sent before it. Null for anything but TCP or UDP over IP.
    /// </summary>
    internal static unsafe (FlowKey flowKey, int payloadLength, Memory<byte> payloadData, bool closesFlow)? ParsePacket(Memory<byte> packet)
    {
        var parser = new WinDivertPacketParser(packet);
        foreach (var result in parser)
        {
            IPAddress srcAddr, dstAddr;
            if (result.IPv4Hdr != null)
            {
                Span<byte> srcBytes = stackalloc byte[4];
                Span<byte> dstBytes = stackalloc byte[4];
                new ReadOnlySpan<byte>(&result.IPv4Hdr->SrcAddr, 4).CopyTo(srcBytes);
                new ReadOnlySpan<byte>(&result.IPv4Hdr->DstAddr, 4).CopyTo(dstBytes);
                srcAddr = new IPAddress(srcBytes);
                dstAddr = new IPAddress(dstBytes);
            }
            else if (result.IPv6Hdr != null)
            {
                Span<byte> srcBytes = stackalloc byte[16];
                Span<byte> dstBytes = stackalloc byte[16];
                new ReadOnlySpan<byte>(&result.IPv6Hdr->SrcAddr, 16).CopyTo(srcBytes);
                new ReadOnlySpan<byte>(&result.IPv6Hdr->DstAddr, 16).CopyTo(dstBytes);
                srcAddr = new IPAddress(srcBytes);
                dstAddr = new IPAddress(dstBytes);
            }
            else
            {
                return null;
            }

            TransportProtocol protocol;
            ushort srcPort, dstPort;
            var closesFlow = false;

            if (result.TCPHdr != null)
            {
                protocol = TransportProtocol.Tcp;
                srcPort = result.TCPHdr->SrcPort;
                dstPort = result.TCPHdr->DstPort;
                // The flags are the header's 14th byte: FIN is 0x01, RST 0x04.
                closesFlow = (((byte*)result.TCPHdr)[13] & 0x05) != 0;
            }
            else if (result.UDPHdr != null)
            {
                protocol = TransportProtocol.Udp;
                srcPort = result.UDPHdr->SrcPort;
                dstPort = result.UDPHdr->DstPort;
            }
            else
            {
                return null;
            }

            int payloadLength = result.Data.Length;
            var payloadData = result.Data;

            var flowKey = new FlowKey(protocol, srcAddr, srcPort, dstAddr, dstPort);
            return (flowKey, payloadLength, payloadData, closesFlow);
        }

        return null;
    }

    private static string ResolveProcessName(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.ProcessName;
        }
        catch
        {
            return $"PID-{processId}";
        }
    }

    private static string? ResolveProcessPath(uint processId)
    {
        try
        {
            using var process = Process.GetProcessById((int)processId);
            return process.MainModule?.FileName;
        }
        catch
        {
            return null;
        }
    }

    public void Dispose()
    {
        // Use ConfigureAwait(false) throughout StopAsync to avoid deadlock
        // when Dispose is called from a synchronization context
        var task = StopAsync();
        if (!task.IsCompleted)
            task.ConfigureAwait(false).GetAwaiter().GetResult();
        _cts?.Dispose();
        _scheduler.Dispose();
    }
}
