using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace OpenNetLimit.Engine.Monitoring;

public static class DnsResponseParser
{
    public static List<DnsRecord> ParseResponse(ReadOnlySpan<byte> payload)
    {
        var records = new List<DnsRecord>();

        if (payload.Length < 12)
            return records;

        var flags = BinaryPrimitives.ReadUInt16BigEndian(payload[2..]);
        bool isResponse = (flags & 0x8000) != 0;
        if (!isResponse)
            return records;

        var qdCount = BinaryPrimitives.ReadUInt16BigEndian(payload[4..]);
        var anCount = BinaryPrimitives.ReadUInt16BigEndian(payload[6..]);

        if (anCount == 0)
            return records;

        int offset = 12;

        // Skip question section
        for (int i = 0; i < qdCount && offset < payload.Length; i++)
        {
            offset = SkipName(payload, offset);
            if (offset < 0 || offset + 4 > payload.Length) return records;
            offset += 4; // QTYPE + QCLASS
        }

        // Parse answer section
        for (int i = 0; i < anCount && offset < payload.Length; i++)
        {
            var nameResult = ReadName(payload, offset);
            if (nameResult is null) break;
            var (name, newOffset) = nameResult.Value;
            offset = newOffset;

            if (offset + 10 > payload.Length) break;

            var type = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;
            offset += 2; // CLASS
            var ttl = BinaryPrimitives.ReadUInt32BigEndian(payload[offset..]);
            offset += 4;
            var rdLength = BinaryPrimitives.ReadUInt16BigEndian(payload[offset..]);
            offset += 2;

            if (offset + rdLength > payload.Length) break;

            if (type == 1 && rdLength == 4) // A record
            {
                var ip = new IPAddress(payload.Slice(offset, 4));
                records.Add(new DnsRecord(name, ip, TimeSpan.FromSeconds(Math.Max(ttl, 60))));
            }
            else if (type == 28 && rdLength == 16) // AAAA record
            {
                var ip = new IPAddress(payload.Slice(offset, 16));
                records.Add(new DnsRecord(name, ip, TimeSpan.FromSeconds(Math.Max(ttl, 60))));
            }

            offset += rdLength;
        }

        return records;
    }

    private static int SkipName(ReadOnlySpan<byte> data, int offset)
    {
        while (offset < data.Length)
        {
            var len = data[offset];
            if (len == 0) return offset + 1;
            if ((len & 0xC0) == 0xC0) return offset + 2; // Pointer
            offset += len + 1;
        }
        return -1;
    }

    private static (string name, int newOffset)? ReadName(ReadOnlySpan<byte> data, int offset)
    {
        var sb = new StringBuilder(64);
        int maxJumps = 20;
        int jumps = 0;
        int savedOffset = -1;
        bool first = true;

        while (offset < data.Length && jumps < maxJumps)
        {
            var len = data[offset];
            if (len == 0)
            {
                offset++;
                break;
            }

            if ((len & 0xC0) == 0xC0)
            {
                if (offset + 1 >= data.Length) return null;
                if (savedOffset < 0) savedOffset = offset + 2;
                offset = ((len & 0x3F) << 8) | data[offset + 1];
                if (offset >= data.Length) return null;
                jumps++;
                continue;
            }

            offset++;
            if (offset + len > data.Length) return null;

            if (!first) sb.Append('.');
            first = false;

            for (int i = 0; i < len; i++)
                sb.Append((char)data[offset + i]);

            offset += len;
        }

        var finalOffset = savedOffset >= 0 ? savedOffset : offset;
        return (sb.ToString(), finalOffset);
    }
}

public readonly record struct DnsRecord(string Domain, IPAddress Address, TimeSpan Ttl);
