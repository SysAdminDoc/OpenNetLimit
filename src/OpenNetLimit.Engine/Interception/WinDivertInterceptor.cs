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
    private long _totalBlocked;

    private static readonly HashSet<string> ProtectedProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "svchost", "services", "lsass", "csrss", "wininit", "smss",
        "dns", "dhcp", "dnscache", "System", "ntoskrnl"
    };

    private WinDivert? _networkHandle;
    private WinDivert? _flowHandle;
    private CancellationTokenSource? _cts;
    private Task? _networkTask;
    private Task? _flowTask;

    private volatile bool _isRunning;
    public bool IsRunning => _isRunning;
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
        ITrafficMonitor trafficMonitor)
    {
        _flowTracker = flowTracker;
        _rateLimiter = rateLimiter;
        _ruleEngine = ruleEngine;
        _trafficMonitor = trafficMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;

        // Set _isRunning before launching tasks to prevent a racing StopAsync
        // from seeing IsRunning == false and returning early while tasks are starting
        _isRunning = true;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        try
        {
            _flowHandle = new WinDivert("true", WinDivert.Layer.Flow, 0, WinDivert.Flag.Sniff);
            _networkHandle = new WinDivert("true", WinDivert.Layer.Network, 0, default);
        }
        catch
        {
            _isRunning = false;
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

        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!IsRunning) return;

        _cts?.Cancel();

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

        _scheduler.Dispose();
        _networkHandle?.Dispose();
        _flowHandle?.Dispose();
        _networkHandle = null;
        _flowHandle = null;

        _isRunning = false;
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
                var protocol = flowData.Protocol == 6 ? TransportProtocol.Tcp :
                               flowData.Protocol == 17 ? TransportProtocol.Udp :
                               TransportProtocol.Other;

                var localAddr = ParseIPv6Addr(flowData.LocalAddr);
                var remoteAddr = ParseIPv6Addr(flowData.RemoteAddr);
                var flowKey = new FlowKey(
                    protocol,
                    localAddr,
                    flowData.LocalPort,
                    remoteAddr,
                    flowData.RemotePort);

                if (addr.Event == WinDivert.Event.FlowEstablished)
                {
                    string processName = ResolveProcessName(flowData.ProcessId);
                    string? processPath = ResolveProcessPath(flowData.ProcessId);
                    _flowTracker.RegisterFlow(flowKey, flowData.ProcessId, processName, processPath);
                    _connectionLog.Log(new ConnectionLogEntry
                    {
                        ProcessId = flowData.ProcessId,
                        ProcessName = processName,
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

                var (flowKey, payloadLength, _) = parsed.Value;
                bool isOutbound = addr.Outbound;

                var processId = _flowTracker.LookupProcessId(flowKey);
                if (processId is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var connection = _flowTracker.LookupConnection(flowKey);
                string processName = connection?.ProcessName ?? "unknown";

                if (connection is not null)
                {
                    if (isOutbound)
                        connection.AddBytesSent(payloadLength);
                    else
                        connection.AddBytesReceived(payloadLength);
                }

                _trafficMonitor.RecordBytes(processId.Value, processName, payloadLength, isOutbound, connection?.ProcessPath);

                // Detect DNS responses (UDP from port 53) and cache domain→IP mappings
                if (!isOutbound && flowKey.Protocol == TransportProtocol.Udp && flowKey.RemotePort == 53 && payloadLength > 12)
                {
                    try
                    {
                        var dnsRecords = DnsResponseParser.ParseResponse(parsed.Value.payloadData.Span);
                        foreach (var record in dnsRecords)
                            _dnsCache.RecordMapping(record.Address, record.Domain, record.Ttl);
                    }
                    catch
                    {
                        // Best-effort DNS parsing — don't disrupt packet flow
                    }
                }

                var remoteAddr = isOutbound ? flowKey.RemoteAddress : flowKey.LocalAddress;
                var remotePort = isOutbound ? (int)flowKey.RemotePort : (int)flowKey.LocalPort;
                var protocolStr = flowKey.Protocol.ToString();
                var resolvedDomain = _dnsCache.LookupDomain(remoteAddr);
                var matchingRule = _ruleEngine.FindMatchingRule(processName, connection?.ProcessPath,
                    remoteAddr, remotePort, protocolStr, resolvedDomain: resolvedDomain);
                if (matchingRule?.Action == RuleAction.Block && !ProtectedProcesses.Contains(processName))
                {
                    Interlocked.Increment(ref _totalBlocked);
                    _connectionLog.Log(new ConnectionLogEntry
                    {
                        ProcessId = processId.Value,
                        ProcessName = processName,
                        Action = "Blocked",
                        Protocol = flowKey.Protocol.ToString(),
                        LocalEndpoint = $"{flowKey.LocalAddress}:{flowKey.LocalPort}",
                        RemoteEndpoint = $"{flowKey.RemoteAddress}:{flowKey.RemotePort}",
                        RuleName = matchingRule.Name
                    });
                    continue;
                }

                if (_rateLimiter.HasLimit(processId.Value) && !ProtectedProcesses.Contains(processName))
                {
                    var delay = _rateLimiter.GetDelay(processId.Value, payloadLength, isOutbound);
                    if (delay > TimeSpan.Zero)
                    {
                        _rateLimiter.TryConsume(processId.Value, payloadLength, isOutbound);
                        _scheduler.Enqueue(processId.Value, packet.Span, addrBuffer.Span, delay);
                        continue;
                    }
                    _rateLimiter.TryConsume(processId.Value, payloadLength, isOutbound);
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

    private static unsafe (FlowKey flowKey, int payloadLength, Memory<byte> payloadData)? ParsePacket(Memory<byte> packet)
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

            if (result.TCPHdr != null)
            {
                protocol = TransportProtocol.Tcp;
                srcPort = result.TCPHdr->SrcPort;
                dstPort = result.TCPHdr->DstPort;
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
            return (flowKey, payloadLength, payloadData);
        }

        return null;
    }

    private static unsafe IPAddress ParseIPv6Addr(IPv6Addr addr)
    {
        Span<byte> bytes = stackalloc byte[16];
        byte* ptr = (byte*)&addr;
        for (int i = 0; i < 16; i++)
            bytes[i] = ptr[i];
        return new IPAddress(bytes);
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
    }
}
