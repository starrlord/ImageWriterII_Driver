using ImageWriterII.Core.Ports;
using ImageWriterII.Ipp.Discovery;
using ImageWriterII.Ipp.Protocol;
using ImageWriterII.Ipp.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageWriterII.Tests;

public class IppAndDnsTests
{
    [Fact]
    public void IppMessageRoundTripsAllValueTypes()
    {
        var m = new IppMessage { VersionMajor = 2, VersionMinor = 0, OperationOrStatus = (ushort)IppOperation.PrintJob, RequestId = 42 };
        var op = m.AddGroup(IppTag.OperationAttributes);
        op.Add("attributes-charset", IppValue.Charset("utf-8"));
        op.Add("attributes-natural-language", IppValue.Language("en"));
        op.Add("printer-uri", IppValue.Uri("ipp://host:631/ipp/print"));
        op.Add("job-name", IppValue.Name("Tëst"));
        op.Add("requested-attributes", IppValue.Keyword("all"), IppValue.Keyword("media-col-database"));
        var job = m.AddGroup(IppTag.JobAttributes);
        job.Add("copies", IppValue.Integer(3));
        job.Add("printer-resolution", IppValue.Resolution(160, 144));
        job.Add("print-quality", IppValue.Enum(5));
        job.Add("ipp-attribute-fidelity", IppValue.Boolean(true));
        job.Add("copies-supported", IppValue.Range(1, 99));
        job.Add("date", IppValue.DateTime(new DateTimeOffset(2026, 9, 4, 12, 30, 15, 400, TimeSpan.FromHours(-5))));
        job.Add("blob", IppValue.Octets([1, 2, 3, 0, 255]));
        job.Add("nothing", IppValue.NoValue());
        job.Add("localized", new IppValue(IppTag.TextWithLanguage, new IppLocalizedString("de", "Hallo")));
        var col = new IppCollection()
            .Add("media-size", IppValue.Collection(new IppCollection().Add("x-dimension", IppValue.Integer(21590)).Add("y-dimension", IppValue.Integer(27940))))
            .Add("media-type", IppValue.Keyword("stationery"));
        var col2 = new IppCollection().Add("media-size-name", IppValue.Keyword("iso_a4_210x297mm"));
        job.Add("media-col", IppValue.Collection(col), IppValue.Collection(col2));

        var bytes = IppCodec.Write(m);
        var ms = new MemoryStream();
        ms.Write(bytes);
        ms.Write("DOCUMENT"u8); // trailing document data must be left untouched
        ms.Position = 0;
        var back = IppCodec.Read(ms);

        Assert.Equal(2, back.VersionMajor);
        Assert.Equal(IppOperation.PrintJob, back.Operation);
        Assert.Equal(42, back.RequestId);
        Assert.Equal("Tëst", back.GetString(IppTag.OperationAttributes, "job-name"));
        Assert.Equal(2, back.Find(IppTag.OperationAttributes, "requested-attributes")!.Values.Count);
        Assert.Equal(3, back.GetInt(IppTag.JobAttributes, "copies"));
        Assert.Equal(new IppResolution(160, 144), back.Find(IppTag.JobAttributes, "printer-resolution")!.First.Data);
        Assert.Equal(5, back.GetInt(IppTag.JobAttributes, "print-quality"));
        Assert.True(back.GetBool(IppTag.JobAttributes, "ipp-attribute-fidelity"));
        Assert.Equal(new IppRange(1, 99), back.Find(IppTag.JobAttributes, "copies-supported")!.First.Data);
        var dt = (DateTimeOffset)back.Find(IppTag.JobAttributes, "date")!.First.Data!;
        Assert.Equal(2026, dt.Year);
        Assert.Equal(400, dt.Millisecond);
        Assert.Equal(TimeSpan.FromHours(-5), dt.Offset);
        Assert.Equal(new byte[] { 1, 2, 3, 0, 255 }, (byte[])back.Find(IppTag.JobAttributes, "blob")!.First.Data!);
        Assert.Equal(IppTag.NoValue, back.Find(IppTag.JobAttributes, "nothing")!.Tag);
        Assert.Equal("Hallo", back.Find(IppTag.JobAttributes, "localized")!.First.AsString());
        var mediaCol = back.Find(IppTag.JobAttributes, "media-col")!;
        Assert.Equal(2, mediaCol.Values.Count);
        var size = mediaCol.Values[0].AsCollection().Find("media-size")!.First.AsCollection();
        Assert.Equal(21590, size.Find("x-dimension")!.First.AsInt());
        Assert.Equal("stationery", mediaCol.Values[0].AsCollection().Find("media-type")!.First.AsString());
        Assert.Equal("iso_a4_210x297mm", mediaCol.Values[1].AsCollection().Find("media-size-name")!.First.AsString());
        // document follows immediately after the end-of-attributes tag
        var rest = new byte[8];
        Assert.Equal(8, ms.Read(rest, 0, 8));
        Assert.Equal("DOCUMENT", System.Text.Encoding.ASCII.GetString(rest));
    }

    private static (IppPrinter printer, PrinterConfig cfg) NewPrinter(bool color = false)
    {
        var cfg = new PrinterConfig { Uuid = Guid.NewGuid().ToString(), ColorRibbon = color, SpoolDirectory = Path.Combine(Path.GetTempPath(), "iw2test-spool") };
        cfg.Validate();
        var status = new PrinterStatus();
        var spooler = new PrintSpooler(cfg, () => new StreamPrinterPort(Stream.Null, "null"), status, NullLogger<PrintSpooler>.Instance);
        var printer = new IppPrinter(cfg, new JobStore(10), spooler, status, NullLogger<IppPrinter>.Instance);
        return (printer, cfg);
    }

    private static IppMessage Request(IppOperation op, Action<IppAttributeGroup>? extra = null)
    {
        var m = new IppMessage { VersionMajor = 2, VersionMinor = 0, OperationOrStatus = (ushort)op, RequestId = 1 };
        var g = m.AddGroup(IppTag.OperationAttributes);
        g.Add("attributes-charset", IppValue.Charset("utf-8"));
        g.Add("attributes-natural-language", IppValue.Language("en"));
        g.Add("printer-uri", IppValue.Uri("ipp://pc.local:631/ipp/print"));
        g.Add("requesting-user-name", IppValue.Name("tester"));
        extra?.Invoke(g);
        return m;
    }

    private static readonly IppRequestContext Ctx = new() { Host = "pc.local:631", ResourcePath = "/ipp/print" };

    [Fact]
    public void GetPrinterAttributesDescribesAnIppEverywherePrinter()
    {
        var (printer, cfg) = NewPrinter();
        var resp = printer.Handle(Request(IppOperation.GetPrinterAttributes, g => g.Add("requested-attributes", IppValue.Keyword("all"), IppValue.Keyword("media-col-database"))), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, resp.Status);
        var p = resp.Group(IppTag.PrinterAttributes)!;
        Assert.Equal("ipp://pc.local:631/ipp/print", p.Find("printer-uri-supported")!.First.AsString());
        Assert.Contains(p.Find("document-format-supported")!.Values, v => v.AsString() == "image/pwg-raster");
        Assert.Contains(p.Find("pwg-raster-document-resolution-supported")!.Values, v => v.Data!.Equals(new IppResolution(160, 144)));
        Assert.Equal(new IppResolution(144, 144), p.Find("printer-resolution-default")!.First.Data);
        Assert.Contains(p.Find("pwg-raster-document-type-supported")!.Values, v => v.AsString() == "sgray_8");
        Assert.DoesNotContain(p.Find("pwg-raster-document-type-supported")!.Values, v => v.AsString() == "srgb_8");
        Assert.Contains(p.Find("print-color-mode-supported")!.Values, v => v.AsString() == "monochrome");
        Assert.Equal("na_letter_8.5x11in", p.Find("media-default")!.First.AsString());
        Assert.True(p.Find("media-col-database")!.Values.Count >= cfg.MediaSupported.Count);
        Assert.Equal(635, p.Find("media-left-margin-supported")!.First.AsInt());
        Assert.Contains(p.Find("urf-supported")!.Values, v => v.AsString() == "RS72-144");
        Assert.Equal(3, p.Find("printer-state")!.First.AsInt());
        Assert.Contains(p.Find("operations-supported")!.Values, v => v.AsInt() == (int)IppOperation.PrintJob);

        // media-col-database must not be returned unless explicitly requested
        var resp2 = printer.Handle(Request(IppOperation.GetPrinterAttributes), Ctx);
        Assert.Null(resp2.Group(IppTag.PrinterAttributes)!.Find("media-col-database"));
        Assert.NotNull(resp2.Group(IppTag.PrinterAttributes)!.Find("media-supported"));
    }

    [Fact]
    public void ColorRibbonAdvertisesColor()
    {
        var (printer, _) = NewPrinter(color: true);
        var resp = printer.Handle(Request(IppOperation.GetPrinterAttributes), Ctx);
        var p = resp.Group(IppTag.PrinterAttributes)!;
        Assert.True(p.Find("color-supported")!.First.AsBool());
        Assert.Contains(p.Find("pwg-raster-document-type-supported")!.Values, v => v.AsString() == "srgb_8");
        Assert.Contains(p.Find("urf-supported")!.Values, v => v.AsString() == "SRGB24");
    }

    [Fact]
    public void ValidateJobReportsUnsupportedAttributesWithoutFailing()
    {
        var (printer, _) = NewPrinter();
        var req = Request(IppOperation.ValidateJob, g => g.Add("document-format", IppValue.MimeType("image/pwg-raster")));
        var jg = req.AddGroup(IppTag.JobAttributes);
        jg.Add("copies", IppValue.Integer(2));
        jg.Add("media", IppValue.Keyword("na_legal_8.5x14in"));
        jg.Add("sides", IppValue.Keyword("two-sided-long-edge"));
        var resp = printer.Handle(req, Ctx);
        Assert.Equal(IppStatus.SuccessfulOkIgnoredOrSubstitutedAttributes, resp.Status);
        Assert.NotNull(resp.Group(IppTag.UnsupportedAttributes)!.Find("sides"));

        var bad = printer.Handle(Request(IppOperation.ValidateJob, g => g.Add("document-format", IppValue.MimeType("application/pdf"))), Ctx);
        Assert.Equal(IppStatus.ClientErrorDocumentFormatNotSupported, bad.Status);
    }

    [Fact]
    public void PrintJobCreatesAJobAndGetJobsListsIt()
    {
        var (printer, cfg) = NewPrinter();
        Directory.CreateDirectory(cfg.SpoolDirectory);
        string spool = Path.Combine(cfg.SpoolDirectory, Guid.NewGuid() + ".spool");
        File.WriteAllBytes(spool, "HEADERRaS2xxxx"u8.ToArray());
        var ctx = new IppRequestContext { Host = "pc.local:631", ResourcePath = "/ipp/print", SpoolPath = spool, DocumentOffset = 6, DocumentLength = 8 };
        var req = Request(IppOperation.PrintJob, g => { g.Add("job-name", IppValue.Name("hello")); g.Add("document-format", IppValue.MimeType("application/octet-stream")); });
        req.AddGroup(IppTag.JobAttributes).Add("printer-resolution", IppValue.Resolution(72, 72));
        var resp = printer.Handle(req, ctx);
        Assert.Equal(IppStatus.SuccessfulOk, resp.Status);
        Assert.True(ctx.SpoolConsumed);
        var jg = resp.Group(IppTag.JobAttributes)!;
        int id = jg.Find("job-id")!.First.AsInt();
        Assert.True(id >= 1);
        Assert.Equal("ipp://pc.local:631/ipp/print/job/" + id, jg.Find("job-uri")!.First.AsString());
        Assert.Equal(JobDocumentKind.PwgRaster, printer.Jobs.Get(id)!.Kind); // sniffed from the RaS2 sync word

        var jobs = printer.Handle(Request(IppOperation.GetJobs, g => { g.Add("which-jobs", IppValue.Keyword("all")); g.Add("requested-attributes", IppValue.Keyword("all")); }), Ctx);
        Assert.Contains(jobs.GroupsOf(IppTag.JobAttributes), g => g.Find("job-id")!.First.AsInt() == id);

        var attrs = printer.Handle(Request(IppOperation.GetJobAttributes, g => g.Add("job-id", IppValue.Integer(id))), Ctx);
        Assert.Equal("hello", attrs.Group(IppTag.JobAttributes)!.Find("job-name")!.First.AsString());

        var missing = printer.Handle(Request(IppOperation.GetJobAttributes, g => g.Add("job-id", IppValue.Integer(999))), Ctx);
        Assert.Equal(IppStatus.ClientErrorNotFound, missing.Status);
        try { File.Delete(spool); } catch (IOException) { }
    }

    [Fact]
    public void UnknownOperationIsRejectedGracefully()
    {
        var (printer, _) = NewPrinter();
        var resp = printer.Handle(Request(IppOperation.PausePrinter), Ctx);
        Assert.Equal(IppStatus.ServerErrorOperationNotSupported, resp.Status);
        Assert.NotNull(resp.Group(IppTag.OperationAttributes)!.Find("status-message"));
    }

    [Fact]
    public void PwgMediaNamesParse()
    {
        var letter = PwgMedia.Parse("na_letter_8.5x11in");
        Assert.Equal(21590, letter.WidthHundredthsMm);
        Assert.Equal(27940, letter.HeightHundredthsMm);
        Assert.Equal(612, letter.WidthPoints);
        Assert.Equal(792, letter.HeightPoints);
        var a4 = PwgMedia.Parse("iso_a4_210x297mm");
        Assert.Equal(21000, a4.WidthHundredthsMm);
        Assert.False(PwgMedia.TryParse("letter", out _));
        Assert.Equal("na_letter_8.5x11in", PwgMedia.NameFor(21590, 27940, ["iso_a4_210x297mm", "na_letter_8.5x11in"]));
    }

    [Fact]
    public void DnsNamesAndPacketsRoundTrip()
    {
        var q = new List<byte>();
        q.AddRange(new byte[] { 0x12, 0x34, 0x00, 0x00, 0x00, 0x02, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00 });
        q.AddRange(DnsWire.EncodeName("_ipp._tcp.local"));
        q.AddRange(new byte[] { 0x00, 0x0C, 0x80, 0x01 }); // PTR, QU bit
        q.AddRange(new byte[] { 0xC0, 0x0C, 0x00, 0x21, 0x00, 0x01 }); // compressed pointer to the same name, SRV
        var packet = DnsWire.Parse(q.ToArray());
        Assert.Equal(0x1234, packet.Id);
        Assert.True(packet.IsQuery);
        Assert.Equal(2, packet.Questions.Count);
        Assert.Equal("_ipp._tcp.local", packet.Questions[0].Name);
        Assert.True(packet.Questions[0].UnicastResponse);
        Assert.Equal("_ipp._tcp.local", packet.Questions[1].Name);
        Assert.Equal(DnsType.SRV, packet.Questions[1].Type);

        var ptr = DnsRecord.Ptr("_ipp._tcp.local", "ImageWriter II._ipp._tcp.local", 4500);
        var srv = DnsRecord.Srv("ImageWriter II._ipp._tcp.local", 631, "imagewriter-ii.local", 120);
        var txt = DnsRecord.Txt("ImageWriter II._ipp._tcp.local", [new("rp", "ipp/print"), new("ty", "Apple ImageWriter II")], 120);
        var a = DnsRecord.AddressV4("imagewriter-ii.local", System.Net.IPAddress.Parse("192.168.1.50"), 120);
        var response = DnsWire.BuildResponse(0, [ptr], [srv, txt, a], legacy: false);
        var parsed = DnsWire.Parse(response);
        Assert.False(parsed.IsQuery);
        Assert.Single(parsed.Answers);
        Assert.Equal("ImageWriter II._ipp._tcp.local", parsed.Answers[0].PtrTarget);
        Assert.Equal(4500u, parsed.Answers[0].Ttl);
        Assert.Equal(6 + srv.Data.Length - 6, srv.Data.Length);
        Assert.Equal(0x02, srv.Data[4]); Assert.Equal(0x77, srv.Data[5]); // port 631
        Assert.Equal((byte)"rp=ipp/print".Length, txt.Data[0]);

        var legacy = DnsWire.BuildResponse(0x1234, [srv], [], legacy: true);
        Assert.Equal(0x12, legacy[0]); Assert.Equal(0x34, legacy[1]);
        Assert.Equal(10u, DnsWire.Parse(legacy).Answers[0].Ttl); // capped for legacy unicast
    }

    [Fact]
    public void UrfKeywordsListSquareResolutionsOnly()
    {
        var urf = IppPrinter.UrfSupported([new IppResolution(72, 72), new IppResolution(160, 144), new IppResolution(144, 144)], color: true).ToList();
        Assert.Contains("RS72-144", urf);
        Assert.Contains("SRGB24", urf);
        Assert.Contains("W8", urf);
    }
}
