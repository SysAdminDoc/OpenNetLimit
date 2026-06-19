using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using OpenNetLimit.Core.Interfaces;
using OpenNetLimit.Core.Models;
using SharpDivert;

namespace OpenNetLimit.Engine.Interception;

public sealed class WinDivertInterceptor : IPacketInterceptor
{
    private readonly IFlowTracker _flowTracker;
    private readonly IRateLimiter _rateLimiter;
    private readonly ITrafficMonitor _trafficMonitor;

    private WinDivert? _networkHandle;
    private WinDivert? _flowHandle;
    private CancellationTokenSource? _cts;
    private Task? _networkTask;
    private Task? _flowTask;

    public bool IsRunning { get; private set; }

    public WinDivertInterceptor(
        IFlowTracker flowTracker,
        IRateLimiter rateLimiter,
        ITrafficMonitor trafficMonitor)
    {
        _flowTracker = flowTracker;
        _rateLimiter = rateLimiter;
        _trafficMonitor = trafficMonitor;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (IsRunning) return Task.CompletedTask;

        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        _flowHandle = new WinDivert("true", WinDivertLayer.Flow, 0, WinDivertOpenFlags.Sniff);
        _networkHandle = new WinDivert("true", WinDivertLayer.Network, 0, WinDivertOpenFlags.None);

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
                var recvLen = _flowHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                ref var addr = ref addrBuffer.Span[0];

                var flowData = addr.Flow;
                var protocol = flowData.Protocol == 6 ? TransportProtocol.Tcp :
                               flowData.Protocol == 17 ? TransportProtocol.Udp :
                               TransportProtocol.Other;

                var localAddr = ParseAddress(flowData.LocalAddr);
                var remoteAddr = ParseAddress(flowData.RemoteAddr);
                var flowKey = new FlowKey(
                    protocol,
                    localAddr,
                    flowData.LocalPort,
                    remoteAddr,
                    flowData.RemotePort);

                if (addr.Event == WinDivertEvent.FlowEstablished)
                {
                    string processName = ResolveProcessName(addr.ProcessId);
                    string? processPath = ResolveProcessPath(addr.ProcessId);
                    _flowTracker.RegisterFlow(flowKey, addr.ProcessId, processName, processPath);
                }
                else if (addr.Event == WinDivertEvent.FlowDeleted)
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
                var recvLen = _networkHandle!.RecvEx(buffer.Span, addrBuffer.Span);
                ref var addr = ref addrBuffer.Span[0];
                var packet = buffer[..recvLen];

                var parsed = ParsePacket(packet.Span);
                if (parsed is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var (flowKey, payloadLength) = parsed.Value;
                bool isOutbound = addr.Flags.HasFlag(WinDivertAddressFlags.Outbound);

                var processId = _flowTracker.LookupProcessId(flowKey);
                if (processId is null)
                {
                    _networkHandle.SendEx(packet.Span, addrBuffer.Span);
                    continue;
                }

                var connection = _flowTracker.LookupConnection(flowKey);
                string processName = connection?.ProcessName ?? "unknown";

                _trafficMonitor.RecordBytes(processId.Value, processName, payloadLength, isOutbound);

                if (_rateLimiter.HasLimit(processId.Value))
                {
                    var delay = _rateLimiter.GetDelay(processId.Value, payloadLength, isOutbound);
                    if (delay > TimeSpan.Zero && delay < TimeSpan.FromSeconds(2))
                    {
                        Thread.Sleep(delay);
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

    private static (FlowKey flowKey, int payloadLength)? ParsePacket(ReadOnlySpan<byte> packet)
    {
        var result = WinDivertParser.Parse(packet);
        if (result.IPv4Header == null && result.IPv6Header == null)
            return null;

        IPAddress srcAddr, dstAddr;
        if (result.IPv4Header != null)
        {
            srcAddr = new IPAddress(result.IPv4Header.Value.SrcAddr);
            dstAddr = new IPAddress(result.IPv4Header.Value.DstAddr);
        }
        else
        {
            srcAddr = new IPAddress(result.IPv6Header!.Value.SrcAddr);
            dstAddr = new IPAddress(result.IPv6Header!.Value.DstAddr);
        }

        TransportProtocol protocol;
        ushort srcPort, dstPort;
        int payloadLength = packet.Length;

        if (result.TcpHeader != null)
        {
            protocol = TransportProtocol.Tcp;
            srcPort = result.TcpHeader.Value.SrcPort;
            dstPort = result.TcpHeader.Value.DstPort;
        }
        else if (result.UdpHeader != null)
        {
            protocol = TransportProtocol.Udp;
            srcPort = result.UdpHeader.Value.SrcPort;
            dstPort = result.UdpHeader.Value.DstPort;
        }
        else
        {
            return null;
        }

        var flowKey = new FlowKey(protocol, srcAddr, srcPort, dstAddr, dstPort);
        return (flowKey, payloadLength);
    }

    private static IPAddress ParseAddress(ReadOnlySpan<byte> addr)
    {
        if (addr.Length == 16)
        {
            bool isIPv4Mapped = addr[..10].SequenceEqual(stackalloc byte[10])
                && addr[10] == 0xFF && addr[11] == 0xFF;
            if (isIPv4Mapped)
                return new IPAddress(addr[12..16]);
            return new IPAddress(addr);
        }
        return new IPAddress(addr);
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
