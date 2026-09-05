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

    /// <summary>
    /// The domain name embedded in this record's rdata (PTR target, SRV target), kept as text so the writer
    /// can emit it with compression pointers. <see cref="Data"/> holds the uncompressed encoding, which is
    /// what readers and equality comparisons use; <see cref="RdataNameOffset"/> says where in it the name
    /// starts. Null for records whose rdata has no name.
    /// </summary>
    public string? RdataName { get; init; }
    /// <summary>Byte offset of <see cref="RdataName"/> within <see cref="Data"/>; the bytes before it are copied verbatim.</summary>
    public int RdataNameOffset { get; init; }

    public DnsRecord WithTtl(uint ttl) => new()
    {
        Name = Name, Type = Type, Class = Class, Ttl = ttl, Data = Data, CacheFlush = CacheFlush,
        PtrTarget = PtrTarget, RdataName = RdataName, RdataNameOffset = RdataNameOffset
    };

    public static DnsRecord Ptr(string name, string target, uint ttl) =>
        new() { Name = name, Type = DnsType.PTR, Ttl = ttl, Data = DnsWire.EncodeName(target), PtrTarget = target, RdataName = target };

    public static DnsRecord Srv(string name, ushort port, string target, uint ttl)
    {
        var data = new List<byte> { 0, 0, 0, 0, (byte)(port >> 8), (byte)port };
        data.AddRange(DnsWire.EncodeName(target));
        return new DnsRecord
        {
            Name = name, Type = DnsType.SRV, Ttl = ttl, Data = data.ToArray(), CacheFlush = true,
            RdataName = target, RdataNameOffset = 6
        };
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

    /// <summary>
    /// NSEC "only these types exist" record (RFC 6762 6.1) so clients stop asking for AAAA. Its rdata name is
    /// deliberately left uncompressed (no RdataName): RFC 6762 18.14 lists PTR, NS, SOA, CNAME and SRV as the
    /// types whose rdata names may be compressed, and a resolver that does not know NSEC cannot decompress it.
    /// </summary>
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
    internal static List<string> SplitLabels(string name)
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
            // Re-escape so the text form round-trips through SplitLabels: a dot inside a label is data
            // (RFC 6763 4.3 allows them in instance names), a dot between labels is a separator. Without
            // this, a parsed name never compares equal to the escaped name we generated, which would make
            // conflict detection blind for any printer whose name contains a dot.
            foreach (char c in Encoding.UTF8.GetString(packet.Slice(pos, len)))
            {
                if (c is '.' or '\\') sb.Append('\\');
                sb.Append(c);
            }
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
            // Names inside PTR and SRV rdata may be compression pointers into the rest of the packet, which
            // only mean anything with the whole message to hand. Resolve them here and store the rdata in its
            // expanded form, so a record compares equal to the same record built locally regardless of how
            // the sender chose to compress it. Without this, comparing raw rdata gives false mismatches.
            string? ptrTarget = null, rdataName = null;
            int rdataNameOffset = 0;
            var rdata = p.Slice(offset, rdlen).ToArray();
            if (type == DnsType.PTR || type == DnsType.SRV)
            {
                rdataNameOffset = type == DnsType.SRV ? 6 : 0;
                if (rdlen >= rdataNameOffset)
                {
                    int o = offset + rdataNameOffset;
                    try
                    {
                        rdataName = ReadName(p, ref o);
                        if (type == DnsType.PTR) ptrTarget = rdataName;
                        var expanded = new List<byte>(rdata.AsSpan(0, rdataNameOffset).ToArray());
                        expanded.AddRange(EncodeName(rdataName));
                        rdata = expanded.ToArray();
                    }
                    catch (InvalidDataException) { rdataName = null; }
                }
            }
            packet.Answers.Add(new DnsRecord
            {
                Name = name, Type = type, Class = (ushort)(cls & 0x7FFF), Ttl = ttl, Data = rdata,
                PtrTarget = ptrTarget, RdataName = rdataName, RdataNameOffset = rdataNameOffset
            });
            offset += rdlen;
        }
        return packet;
    }

    /// <summary>
    /// Largest DNS payload we will put in one datagram. 1500-byte Ethernet MTU less 20 bytes of IPv4 header
    /// and 8 of UDP leaves 1472; stay under that so the datagram is never IP-fragmented.
    /// </summary>
    public const int MaxPayload = 1400;

    /// <summary>Builds a response packet. <paramref name="legacy"/> responses keep the query id, clear cache-flush bits and cap TTLs at 10 s.</summary>
    public static byte[] BuildResponse(ushort id, IReadOnlyList<DnsRecord> answers, IReadOnlyList<DnsRecord> additionals, bool legacy)
    {
        var buf = new List<byte>(512);
        // Compression offsets are relative to the start of THIS message, so the table lives and dies with it.
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void U16(ushort v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        void U32(uint v) { buf.Add((byte)(v >> 24)); buf.Add((byte)(v >> 16)); buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        void Record(DnsRecord r)
        {
            WriteName(buf, r.Name, offsets);
            U16(r.Type);
            U16((ushort)(r.Class | (r.CacheFlush && !legacy ? 0x8000 : 0)));
            U32(legacy ? Math.Min(r.Ttl, 10u) : r.Ttl);
            if (r.RdataName is null)
            {
                U16((ushort)r.Data.Length);
                buf.AddRange(r.Data);
            }
            else
            {
                // RFC 6762 18.14: compress the names inside PTR and SRV rdata too. RDLENGTH can only be
                // filled in once the compressed name has been written, so reserve it and patch it after.
                int lengthAt = buf.Count;
                U16(0);
                int start = buf.Count;
                for (int i = 0; i < r.RdataNameOffset; i++) buf.Add(r.Data[i]);
                WriteName(buf, r.RdataName, offsets);
                int len = buf.Count - start;
                buf[lengthAt] = (byte)(len >> 8);
                buf[lengthAt + 1] = (byte)len;
            }
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

    /// <summary>
    /// Builds one or more response packets, splitting the answer list so no datagram exceeds
    /// <paramref name="maxPayload"/>. RFC 6762 8.3 allows an announcement to be spread over several packets;
    /// a single record that will not fit on its own is still emitted alone rather than dropped.
    /// </summary>
    public static List<byte[]> BuildResponses(ushort id, IReadOnlyList<DnsRecord> answers, IReadOnlyList<DnsRecord> additionals,
                                              bool legacy, int maxPayload = MaxPayload)
    {
        var packets = new List<byte[]>();
        var all = new List<DnsRecord>(answers);
        all.AddRange(additionals);
        int answerCount = answers.Count;

        var batch = new List<DnsRecord>();
        int batchAnswers = 0;
        for (int i = 0; i < all.Count; i++)
        {
            batch.Add(all[i]);
            if (i < answerCount) batchAnswers++;
            var candidate = BuildResponse(id, batch.Take(batchAnswers).ToList(), batch.Skip(batchAnswers).ToList(), legacy);
            if (candidate.Length <= maxPayload) continue;

            if (batch.Count == 1)
            {
                // One oversized record: nothing to split, send it and let IP fragment it.
                packets.Add(candidate);
                batch.Clear();
                batchAnswers = 0;
                continue;
            }
            // Roll the last record back into the next packet.
            batch.RemoveAt(batch.Count - 1);
            int keptAnswers = i < answerCount ? batchAnswers - 1 : batchAnswers;
            packets.Add(BuildResponse(id, batch.Take(keptAnswers).ToList(), batch.Skip(keptAnswers).ToList(), legacy));
            batch.Clear();
            batch.Add(all[i]);
            batchAnswers = i < answerCount ? 1 : 0;
        }
        if (batch.Count > 0)
            packets.Add(BuildResponse(id, batch.Take(batchAnswers).ToList(), batch.Skip(batchAnswers).ToList(), legacy));
        return packets;
    }

    /// <summary>
    /// Builds RFC 6762 8.1 probe queries: one question per name with QTYPE ANY and the unicast-response bit
    /// set, and the records we propose to use in the authority section so a simultaneous prober can tie-break.
    /// Split across datagrams if the proposed set is large.
    /// </summary>
    public static List<byte[]> BuildProbe(IReadOnlyList<string> names, IReadOnlyList<DnsRecord> proposed, int maxPayload = MaxPayload)
    {
        var packets = new List<byte[]>();
        var buf = new List<byte>(512);
        var offsets = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        void U16(ushort v) { buf.Add((byte)(v >> 8)); buf.Add((byte)v); }
        void U32(uint v) { buf.Add((byte)(v >> 24)); buf.Add((byte)(v >> 16)); buf.Add((byte)(v >> 8)); buf.Add((byte)v); }

        U16(0);
        U16(0);                              // a query, not a response
        U16((ushort)names.Count);
        U16(0);
        U16((ushort)proposed.Count);         // authority: the proposed records
        U16(0);
        foreach (var n in names)
        {
            WriteName(buf, n, offsets);
            U16(DnsType.ANY);
            // QM, not QU. RFC 6762 8.1 only says a probe SHOULD set the unicast-response bit, and asking for
            // a multicast answer is markedly more reliable here: when a second responder shares port 5353 on
            // the same host (two instances of this service, or another mDNS stack), which socket receives a
            // unicast reply is undefined, so the conflicting answer can be delivered to the wrong process and
            // the conflict missed entirely. Three extra multicast packets at startup is a cheap trade.
            U16(0x0001);
        }
        foreach (var r in proposed)
        {
            WriteName(buf, r.Name, offsets);
            U16(r.Type);
            U16(r.Class);                    // no cache-flush bit on a proposed record
            U32(r.Ttl);
            U16((ushort)r.Data.Length);
            buf.AddRange(r.Data);            // authority rdata stays uncompressed: simplest and always safe
        }
        // A probe for one host plus a handful of services is far under the MTU; maxPayload is accepted for
        // symmetry with BuildResponses and to document the intent if the service list ever grows.
        _ = maxPayload;
        packets.Add(buf.ToArray());
        return packets;
    }

    /// <summary>
    /// Writes a domain name, reusing an earlier appearance of any matching suffix as a two-byte pointer
    /// (RFC 1035 4.1.4). Every suffix written literally is recorded so later names can point at it.
    /// </summary>
    private static void WriteName(List<byte> buf, string name, Dictionary<string, int> offsets)
    {
        var labels = SplitLabels(name);
        for (int i = 0; i < labels.Count; i++)
        {
            if (labels[i].Length == 0) continue;
            string suffix = string.Join('.', labels.Skip(i));
            if (offsets.TryGetValue(suffix, out int at))
            {
                buf.Add((byte)(0xC0 | (at >> 8)));
                buf.Add((byte)at);
                return;
            }
            // A pointer can only address the first 16383 bytes, so past that just keep writing literals.
            if (buf.Count <= 0x3FFF) offsets[suffix] = buf.Count;
            var bytes = Encoding.UTF8.GetBytes(labels[i]);
            if (bytes.Length > 63) throw new ArgumentException($"DNS label too long: {labels[i]}");
            buf.Add((byte)bytes.Length);
            buf.AddRange(bytes);
        }
        buf.Add(0);
    }
}
