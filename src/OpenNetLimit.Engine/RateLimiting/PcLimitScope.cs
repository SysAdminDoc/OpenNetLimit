using System.Net;
using OpenNetLimit.Core.Models;

namespace OpenNetLimit.Engine.RateLimiting;

/// <summary>Which packets the whole-PC limit covers.</summary>
public static class PcLimitScope
{
    /// <summary>
    /// Whether a packet with this remote end counts toward the limit in its direction. Traffic
    /// within the PC never does, and the local network only when the limit includes it.
    /// </summary>
    public static bool Applies(WholePcLimit? limit, bool isUpload, IPAddress remote)
    {
        if (limit is null)
            return false;
        long rate = isUpload ? limit.UploadBytesPerSecond : limit.DownloadBytesPerSecond;
        if (rate <= 0)
            return false;
        if (remote.IsIPv4MappedToIPv6)
            remote = remote.MapToIPv4();
        if (IPAddress.IsLoopback(remote))
            return false;
        return limit.IncludeLan || !IsLocalNetwork(remote);
    }

    /// <summary>
    /// Private (RFC 1918), link-local, unique local (fc00::/7), the old IPv6 site-local
    /// (fec0::/10) and multicast or broadcast addresses. Carrier-grade NAT (100.64.0.0/10) is the provider's side, so it isn't local.
    /// </summary>
    public static bool IsLocalNetwork(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
            address = address.MapToIPv4();
        Span<byte> b = stackalloc byte[16];
        if (!address.TryWriteBytes(b, out int length))
            return false;
        if (length == 4)
            return b[0] == 10
                || (b[0] == 172 && (b[1] & 0xF0) == 16)
                || (b[0] == 192 && b[1] == 168)
                || (b[0] == 169 && b[1] == 254)
                || b[0] == 0
                || b[0] >= 224;
        return (b[0] & 0xFE) == 0xFC
            || (b[0] == 0xFE && (b[1] & 0x80) == 0x80)
            || b[0] == 0xFF;
    }
}
