using System.Buffers.Binary;
using System.Net;
using System.Text;

namespace ImageWriterII.Ipp.Discovery;

public static class DnsType
{
    public const ushort A = 1;
    public const ushort PTR = 12;
    public const ushort TXT = 16;
    public const ushort AAAA = 28;
    public const ushort SRV = 33;
    public const ushort NSEC = 47;
    public const ushort ANY = 255;
}

public sealed record DnsQuestion(string Name, ushort Type, ushort Class, bool UnicastResponse);

public sealed class DnsRecord
{
    public required string Name { get; init; }
    public required ushort Type { get; init; }
    public ushort Class { get; init; } = 1;
    public required uint Ttl { get; init; }
    public required byte[] Data { get; init; }
    /// <summary>Set the mDNS cache-flush bit (unique records: SRV, TXT, A).</summary>
    public bool CacheFlush { get; init; }

    public string? PtrTarget { get; init; }

    public DnsRecord WithTtl(uint ttl) => new() { Name = Name, Type = Type, Class = Class, Ttl = ttl, Data = Data, CacheFlush = CacheFlush, PtrTarget = PtrTarget };

    public static DnsRecord Ptr(string name, string target, uint ttl) =>
        new() { Name = name, Type = DnsType.PTR, Ttl = ttl, Data = DnsWire.EncodeName(target), PtrTarget = target };

    public static DnsRecord Srv(string name, ushort port, string target, uint ttl)
    {
        var data = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
        data.AddRange(DnsWire.EncodeName(target));
        return new DnsRecord { Name = name, Type = DnsType.SRV, Ttl = ttl, Data = data.ToArray(), CacheFlush = true };
    }

    public static DnsRecord Txt(string name, IEnumerable<KeyValuePair<string, string>> entries, uint ttl)
    {
        var data = new List<byte>();
        foreach (var kv in entries)
        {
            var bytes = Encoding.UTF8.GetBytes(kv.Value.Length == 0 ? kv.Key : $"{kv.Key}={kv.Value}");
            if (bytes.Length > 255) bytes = bytes[..255];
            data.Add((byte)bytes.Length);
            data.AddRange(bytes);
        }
        if (data.Count == 0) data.Add(0);
        return new DnsRecord { Name = name, Type = DnsType.TXT, Ttl = ttl, Data = data.ToArray(), CacheFlush = true };
    }

    public static DnsRecord AddressV4(string name, IPAddress address, uint ttl) =>
        new() { Name = name, Type = DnsType.A, Ttl = ttl, Data = address.GetAddressBytes(), CacheFlush = true };

    /// <summary>NSEC "only these types exist" record (RFC 6762 6.1) so clients stop asking for AAAA.</summary>
    public static DnsRecord Nsec(string name, uint ttl, params ushort[] types)
    {
        var data = new List<byte>(DnsWire.EncodeName(name));
        var bitmap = new byte[32];
        int max = 0;
        foreach (var t in types)
        {
            if (t >= 256) continue;
            bitmap[t >> 3] |= (byte)(0x80 >> (t & 7));
            max = Math.Max(max, (t >> 3) + 1);
        }
        data.Add(0);
        data.Add((byte)max);
        data.AddRange(bitmap.AsSpan(0, max).ToArray());
        return new DnsRecord { Name = name, Type = DnsType.NSEC, Ttl = ttl, Data = data.ToArray(), CacheFlush = true };
    }
}

public sealed class DnsPacket
{
    public ushort Id { get; init; }
    public ushort Flags { get; init; }
    public bool IsQuery => (Flags & 0x8000) == 0;
    public List<DnsQuestion> Questions { get; } = [];
    public List<DnsRecord> Answers { get; } = [];
}

/// <summary>DNS message encoding for mDNS: names, questions, resource records (RFC 1035 / RFC 6762).</summary>
public static class DnsWire
{
    public static byte[] EncodeName(string name)
    {
        var list = new List<byte>();
        foreach (var label in SplitLabels(name))
        {
            if (label.Length == 0) continue;
            var bytes = Encoding.UTF8.GetBytes(label);
            if (bytes.Length > 63) throw new ArgumentException($"DNS label too long: {label}");
            list.Add((byte)bytes.Length);
            list.AddRange(bytes);
        }
        list.Add(0);
        return list.ToArray();
    }

    /// <summary>
    /// Splits a name into labels on unescaped dots and unescapes "\." within a label. DNS-SD instance names
    /// are allowed to contain dots ("Joe's 3.5 Printer") and RFC 6763 section 4.3 escapes them this way; a
    /// plain Split('.') would silently turn one instance into two labels and break the service name.
    /// </summary>
    private static List<string> SplitLabels(string name)
    {
        var labels = new List<string>();
        var current = new StringBuilder();
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '\\' && i + 1 < name.Length) { current.Append(name[++i]); continue; }
            if (c == '.') { labels.Add(current.ToString()); current.Clear(); continue; }
            current.Append(c);
        }
        if (current.Length > 0) labels.Add(current.ToString());
        return labels;
    }

    public static string ReadName(ReadOnlySpan<byte> packet, ref int offset)
    {
        var sb = new StringBuilder();
        int pos = offset;
        int jumped = -1;
        int hops = 0;
        while (true)
        {
            if (pos >= packet.Length) throw new InvalidDataException("Truncated DNS name.");
            int len = packet[pos];
            if (len == 0) { pos++; break; }
            if ((len & 0xC0) == 0xC0)
            {
                if (pos + 1 >= packet.Length) throw new InvalidDataException("Truncated DNS pointer.");
                int ptr = ((len & 0x3F) << 8) | packet[pos + 1];
                if (jumped < 0) jumped = pos + 2;
                pos = ptr;
                if (++hops > 64) throw new InvalidDataException("DNS pointer loop.");
                continue;
            }
            pos++;
            if (pos + len > packet.Length) throw new InvalidDataException("Truncated DNS label.");
            if (sb.Length > 0) sb.Append('.');
            sb.Append(Encoding.UTF8.GetString(packet.Slice(pos, len)));
            pos += len;
        }
        offset = jumped >= 0 ? jumped : pos;
        return sb.ToString();
    }

    public static DnsPacket Parse(ReadOnlySpan<byte> p)
    {
        if (p.Length < 12) throw new InvalidDataException("DNS packet too short.");
        var packet = new DnsPacket { Id = BinaryPrimitives.ReadUInt16BigEndian(p), Flags = BinaryPrimitives.ReadUInt16BigEndian(p[2..]) };
        int qd = BinaryPrimitives.ReadUInt16BigEndian(p[4..]);
        int an = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
        int offset = 12;
        for (int i = 0; i < qd; i++)
        {
            string name = ReadName(p, ref offset);
            if (offset + 4 > p.Length) throw new InvalidDataException("Truncated question.");
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[offset..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 2)..]);
            offset += 4;
            packet.Questions.Add(new DnsQuestion(name, type, (ushort)(cls & 0x7FFF), (cls & 0x8000) != 0));
        }
        for (int i = 0; i < an; i++)
        {
            if (offset >= p.Length) break;
            string name;
            try { name = ReadName(p, ref offset); } catch (InvalidDataException) { break; }
            if (offset + 10 > p.Length) break;
            ushort type = BinaryPrimitives.ReadUInt16BigEndian(p[offset..]);
            ushort cls = BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 2)..]);
            uint ttl = BinaryPrimitives.ReadUInt32BigEndian(p[(offset + 4)..]);
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 8)..]);
            offset += 10;
            if (offset + rdlen > p.Length) break;
            string? ptrTarget = null;
            if (type == DnsType.PTR)
            {
                int o = offset;
                try { ptrTarget = ReadName(p, ref o); } catch (InvalidDataException) { }
            }
            packet.Answers.Add(new DnsRecord { Name = name, Type = type, Class = (ushort)(cls & 0x7FFF), Ttl = ttl, Data = p.Slice(offset, rdlen).ToArray(), PtrTarget = ptrTarget });
            offset += rdlen;
        }
        return packet;
    }

    /// <summary>Builds a response packet. <paramref name="legacy"/> responses keep the query id, clear cache-flush bits and cap TTLs at 10 s.</summary>
    public static byte[] BuildResponse(ushort id, IReadOnlyList<DnsRecord> answers, IReadOnlyList<DnsRecord> additionals, bool legacy)
    {
        var buf = new List<byte>(512);
        void U16(ushort v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        void U32(uint v) { buf.Add((byte)(v >> 24)); buf.Add((byte)(v >> 16)); buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        void Record(DnsRecord r)
        {
            buf.AddRange(EncodeName(r.Name));
            U16(r.Type);
            U16((ushort)(r.Class | (r.CacheFlush && !legacy ? 0x8000 : 0)));
            U32(legacy ? Math.Min(r.Ttl, 10u) : r.Ttl);
            U16((ushort)r.Data.Length);
            buf.AddRange(r.Data);
        }
        U16(legacy ? id : (ushort)0);
        U16(0x8400); // response, authoritative
        U16(0);
        U16((ushort)answers.Count);
        U16(0);
        U16((ushort)additionals.Count);
        foreach (var r in answers) Record(r);
        foreach (var r in additionals) Record(r);
        return buf.ToArray();
    }
}
