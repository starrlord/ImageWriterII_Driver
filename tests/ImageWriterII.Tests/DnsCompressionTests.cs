using System.Buffers.Binary;
using System.Net;
using ImageWriterII.Ipp.Discovery;
using Xunit;

namespace ImageWriterII.Tests;

/// <summary>
/// Name compression (RFC 1035 4.1.4) in the packet writer, and the datagram splitting that keeps an
/// announcement inside the Ethernet MTU (RFC 6762 8.3).
/// </summary>
public class DnsCompressionTests
{
    private const string Host = "imagewriter-ii.local";
    private const string Type = "_ipp._tcp.local";
    private const string Instance = "ImageWriter II._ipp._tcp.local";

    private static List<DnsRecord> TypicalAnnouncement()
    {
        var txt = new List<KeyValuePair<string, string>>
        {
            new("txtvers", "1"), new("rp", "ipp/print"), new("ty", "Apple ImageWriter II"),
            new("pdl", "image/pwg-raster,image/urf,application/octet-stream,text/plain"),
            new("URF", "V1.4,W8,SRGB24,CP1,IS1,MT1-2,OB9,PQ3-4-5,RS72-144"), new("Color", "T")
        };
        return
        [
            DnsRecord.Ptr("_services._dns-sd._udp.local", Type, 4500),
            DnsRecord.Ptr(Type, Instance, 4500),
            DnsRecord.Ptr("_universal._sub." + Type, Instance, 4500),
            DnsRecord.Ptr("_print._sub." + Type, Instance, 4500),
            DnsRecord.Srv(Instance, 631, Host, 120),
            DnsRecord.Txt(Instance, txt, 120),
            DnsRecord.AddressV4(Host, IPAddress.Parse("192.168.1.20"), 120),
        ];
    }

    /// <summary>Counts owner names written as a two-byte pointer rather than in full.</summary>
    private static int CountPointers(byte[] packet)
    {
        var p = packet.AsSpan();
        int an = BinaryPrimitives.ReadUInt16BigEndian(p[6..]);
        int ar = BinaryPrimitives.ReadUInt16BigEndian(p[10..]);
        int offset = 12, pointers = 0;
        for (int i = 0; i < an + ar; i++)
        {
            if ((packet[offset] & 0xC0) == 0xC0) pointers++;
            _ = DnsWire.ReadName(p, ref offset);
            int rdlen = BinaryPrimitives.ReadUInt16BigEndian(p[(offset + 8)..]);
            offset += 10 + rdlen;
        }
        return pointers;
    }

    [Fact]
    public void RepeatedNamesAreWrittenAsPointers()
    {
        var packet = DnsWire.BuildResponse(0, TypicalAnnouncement(), [], legacy: false);
        Assert.True(CountPointers(packet) >= 4, "repeated owner names should be compressed");
    }

    [Fact]
    public void CompressionShrinksTheAnnouncementAndItStillParses()
    {
        var records = TypicalAnnouncement();
        var packet = DnsWire.BuildResponse(0, records, [], legacy: false);

        // Every name still round-trips, including the ones inside PTR and SRV rdata.
        var parsed = DnsWire.Parse(packet);
        Assert.Equal(records.Count, parsed.Answers.Count);
        Assert.Equal(Instance, parsed.Answers.Single(a => a.Type == DnsType.SRV).Name);
        Assert.Equal(Host, parsed.Answers.Single(a => a.Type == DnsType.SRV).RdataName);
        Assert.Equal(Instance, parsed.Answers.First(a => a.Type == DnsType.PTR && a.Name == Type).PtrTarget);

        // An uncompressed encoding of the same names is measurably bigger.
        int uncompressed = records.Sum(r => DnsWire.EncodeName(r.Name).Length + 10 + r.Data.Length) + 12;
        Assert.True(packet.Length < uncompressed, $"expected compression to help: {packet.Length} vs {uncompressed}");
    }

    /// <summary>A pointer must reference an earlier occurrence, never a later one, or a reader loops.</summary>
    [Fact]
    public void PointersOnlyEverReferenceEarlierOffsets()
    {
        var packet = DnsWire.BuildResponse(0, TypicalAnnouncement(), [], legacy: false);
        for (int i = 0; i < packet.Length - 1; i++)
        {
            if ((packet[i] & 0xC0) != 0xC0) continue;
            int target = ((packet[i] & 0x3F) << 8) | packet[i + 1];
            Assert.True(target < i, $"pointer at {i} references {target}, which is not earlier");
        }
    }

    [Fact]
    public void ARealisticAnnouncementFitsInOneDatagram()
    {
        var packets = DnsWire.BuildResponses(0, TypicalAnnouncement(), [], legacy: false);
        Assert.Single(packets);
        Assert.True(packets[0].Length <= DnsWire.MaxPayload);
    }

    [Fact]
    public void AnOversizedRecordSetIsSplitAcrossDatagrams()
    {
        // Enough TXT bulk that the set cannot fit in one datagram.
        // Each record is comfortably under the cap on its own, but twenty of them are not.
        var big = new List<DnsRecord>();
        for (int i = 0; i < 20; i++)
        {
            var txt = Enumerable.Range(0, 2).Select(k => new KeyValuePair<string, string>($"k{k}", new string('x', 120))).ToList();
            big.Add(DnsRecord.Txt($"instance-{i}._ipp._tcp.local", txt, 120));
        }
        var packets = DnsWire.BuildResponses(0, big, [], legacy: false);

        Assert.True(packets.Count > 1, "the set should have been split");
        Assert.All(packets, p => Assert.True(p.Length <= DnsWire.MaxPayload, $"{p.Length} bytes exceeds the cap"));
        // Nothing is lost in the split.
        Assert.Equal(big.Count, packets.Sum(p => DnsWire.Parse(p).Answers.Count));
    }

    [Fact]
    public void LegacyResponsesCapTheTtlAndClearCacheFlush()
    {
        var packet = DnsWire.BuildResponse(0x1234, TypicalAnnouncement(), [], legacy: true);
        Assert.Equal(0x1234, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan()));
        foreach (var a in DnsWire.Parse(packet).Answers)
        {
            Assert.True(a.Ttl <= 10, $"{a.Name} kept TTL {a.Ttl}");
            Assert.Equal(1, a.Class);          // the cache-flush bit is masked off by Parse; class stays IN
        }
    }

    [Fact]
    public void ProbeIsAQueryForEveryNameWithTheProposedRecordsAttached()
    {
        var proposed = new List<DnsRecord> { DnsRecord.Srv(Instance, 631, Host, 120) };
        var packets = DnsWire.BuildProbe([Host, Instance], proposed);
        var packet = Assert.Single(packets);

        ushort flags = BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(2));
        Assert.Equal(0, flags & 0x8000);                                       // a query
        Assert.Equal(2, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(4)));  // two questions
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(8)));  // one authority record

        var parsed = DnsWire.Parse(packet);
        Assert.True(parsed.IsQuery);
        Assert.Equal(2, parsed.Questions.Count);
        Assert.All(parsed.Questions, q => Assert.Equal(DnsType.ANY, q.Type));
        // Multicast response requested: a unicast reply can be delivered to the wrong socket when another
        // responder shares port 5353 on this host, which would hide the conflict we are probing for.
        Assert.All(parsed.Questions, q => Assert.False(q.UnicastResponse));
    }

    [Fact]
    public void InstanceNamesContainingDotsSurviveAsOneLabel()
    {
        // RFC 6763 4.3: a dot in an instance name is escaped, not a label separator. The escaped form has to
        // survive a write/read round trip unchanged, or the name we generate never compares equal to the one
        // we parse back - which is what conflict detection relies on.
        const string name = @"Joe's 3\.5 Printer._ipp._tcp.local";
        var record = DnsRecord.Ptr(Type, name, 4500);
        var parsed = DnsWire.Parse(DnsWire.BuildResponse(0, [record], [], legacy: false));
        Assert.Equal(name, parsed.Answers[0].PtrTarget);

        // ... and the dot really is data: the instance is one label, so "<instance>._ipp._tcp.local" is four.
        Assert.Equal(4, DnsWire.SplitLabels(name).Count);
        Assert.Equal("Joe's 3.5 Printer", DnsWire.SplitLabels(name)[0]);
    }
}
