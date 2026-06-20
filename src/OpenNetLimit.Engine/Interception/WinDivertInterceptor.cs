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
    private long _totalBlocked;

    private WinDivert? _networkHandle;
    private WinDivert? _flowHandle;
    private CancellationTokenSource? _cts;
    private Task? _networkTask;
    private Task? _flowTask;

    public bool IsRunning { get; private set; }
    public PacketScheduler Scheduler => _scheduler;

    public long TotalBlocked => Volatile.Read(ref _totalBlocked);
    public ConnectionLogger ConnectionLog => _connectionLog;

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

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _flowHandle = new WinDivert("true", WinDivert.Layer.Flow, 0, WinDivert.Flag.Sniff);
        _networkHandle = new WinDivert("true", WinDivert.Layer.Network, 0, default);
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

        IsRunning = true;
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

        IsRunning = false;
    }

    private void FlowLoop(CancellationToken ct)
    {
        var buffer = new Memory<byte>(new byte[65535]);
        var addrBuffer = new Memory<WinDivertAddress>(new WinDivertAddress[1]);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (recvLen, _) = _flowHandle!.RecvEx(buffer.Span, addrBuffer.Span);
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
        }
    }

    private void NetworkLoop(CancellationToken ct)
    {
        var buffer = new Memory<byte>(new byte[65535]);
        var addrBuffer = new Memory<WinDivertAddress>(new WinDivertAddress[1]);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (recvLen, _) = _networkHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                ref var addr = ref addrBuffer.Span[0];
                var packet = buffer[..(int)recvLen];

                var parsed = ParsePacket(packet);
                if (parsed is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var (flowKey, payloadLength) = parsed.Value;
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

                var matchingRule = _ruleEngine.FindMatchingRule(processName, connection?.ProcessPath);
                if (matchingRule?.Action == RuleAction.Block)
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

                if (_rateLimiter.HasLimit(processId.Value))
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
        }
    }

    private static unsafe (FlowKey flowKey, int payloadLength)? ParsePacket(Memory<byte> packet)
    {
        var parser = new WinDivertPacketParser(packet);
        foreach (var result in parser)
        {
            IPAddress srcAddr, dstAddr;
            if (result.IPv4Hdr != null)
            {
                srcAddr = IPAddress.Parse(result.IPv4Hdr->SrcAddr.ToString());
                dstAddr = IPAddress.Parse(result.IPv4Hdr->DstAddr.ToString());
            }
            else if (result.IPv6Hdr != null)
            {
                srcAddr = IPAddress.Parse(result.IPv6Hdr->SrcAddr.ToString());
                dstAddr = IPAddress.Parse(result.IPv6Hdr->DstAddr.ToString());
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

            var flowKey = new FlowKey(protocol, srcAddr, srcPort, dstAddr, dstPort);
            return (flowKey, payloadLength);
        }

        return null;
    }

    private static IPAddress ParseIPv6Addr(IPv6Addr addr)
    {
        return IPAddress.Parse(addr.ToString());
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
        StopAsync().GetAwaiter().GetResult();
        _cts?.Dispose();
    }
}
