using ImageWriterII.Core.Ports;
using ImageWriterII.Ipp.Protocol;
using ImageWriterII.Ipp.Server;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace ImageWriterII.Tests;

/// <summary>
/// The multi-request job path: Create-Job, Send-Document, Close-Job, and the cancel and identify operations.
/// This is what iOS, macOS and CUPS actually use - Print-Job in one shot is the Windows path - so it is worth
/// pinning down separately.
/// </summary>
public class IppJobLifecycleTests : IDisposable
{
    private readonly string _spoolDir = Path.Combine(Path.GetTempPath(), "iw2-lifecycle-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_spoolDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        GC.SuppressFinalize(this);
    }

    private (IppPrinter printer, PrinterConfig cfg) NewPrinter()
    {
        var cfg = new PrinterConfig { Uuid = Guid.NewGuid().ToString(), SpoolDirectory = _spoolDir };
        cfg.Validate();
        Directory.CreateDirectory(_spoolDir);
        var status = new PrinterStatus();
        var spooler = new PrintSpooler(cfg, () => new StreamPrinterPort(Stream.Null, "null"), status, NullLogger<PrintSpooler>.Instance);
        return (new IppPrinter(cfg, new JobStore(10), spooler, status, NullLogger<IppPrinter>.Instance), cfg);
    }

    private static IppMessage Request(IppOperation op, Action<IppAttributeGroup>? extra = null)
    {
        var m = new IppMessage { VersionMajor = 2, VersionMinor = 0, OperationOrStatus = (ushort)op, RequestId = 1 };
        var g = m.AddGroup(IppTag.OperationAttributes);
        g.Add("attributes-charset", IppValue.Charset("utf-8"));
        g.Add("attributes-natural-language", IppValue.Language("en"));
        g.Add("printer-uri", IppValue.Uri("ipp://pc.local:631/ipp/print"));
        g.Add("requesting-user-name", IppValue.Name("mobile"));
        extra?.Invoke(g);
        return m;
    }

    private static readonly IppRequestContext Ctx = new() { Host = "pc.local:631", ResourcePath = "/ipp/print" };

    /// <summary>Writes a spool file whose document part is a minimal Apple Raster stream, as iOS would send.</summary>
    private IppRequestContext DocumentContext(out string path)
    {
        path = Path.Combine(_spoolDir, Guid.NewGuid() + ".spool");
        var doc = new List<byte>("IPPHDR"u8.ToArray());
        doc.AddRange("UNIRAST\0"u8.ToArray());
        doc.AddRange([0, 0, 0, 1]);
        File.WriteAllBytes(path, doc.ToArray());
        return new IppRequestContext
        {
            Host = "pc.local:631",
            ResourcePath = "/ipp/print",
            SpoolPath = path,
            DocumentOffset = 6,
            DocumentLength = doc.Count - 6
        };
    }

    [Fact]
    public void CreateJobSendDocumentAndCloseJobRunTheWholeLifecycle()
    {
        var (printer, _) = NewPrinter();

        var created = printer.Handle(Request(IppOperation.CreateJob, g => g.Add("job-name", IppValue.Name("Claire New Address"))), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, created.Status);
        int id = created.Group(IppTag.JobAttributes)!.Find("job-id")!.First.AsInt();
        // RFC 8011: a job created without its document waits in pending-held, not pending.
        Assert.Equal(IppJobState.PendingHeld, printer.Jobs.Get(id)!.State);

        // The document arrives in a second request, flagged as the last one.
        var ctx = DocumentContext(out _);
        var sent = printer.Handle(Request(IppOperation.SendDocument, g =>
        {
            g.Add("job-id", IppValue.Integer(id));
            g.Add("document-format", IppValue.MimeType("image/urf"));
            g.Add("last-document", IppValue.Boolean(true));
        }), ctx);

        Assert.Equal(IppStatus.SuccessfulOk, sent.Status);
        Assert.True(ctx.SpoolConsumed);
        var job = printer.Jobs.Get(id)!;
        Assert.Equal(JobDocumentKind.AppleRaster, job.Kind);   // sniffed from the UNIRAST sync word
        Assert.Equal("image/urf", job.Format);
        Assert.True(job.DocumentsComplete);

        // Close-Job on an already-complete job is accepted rather than rejected.
        var closed = printer.Handle(Request(IppOperation.CloseJob, g => g.Add("job-id", IppValue.Integer(id))), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, closed.Status);
    }

    [Fact]
    public void CloseJobEndsAJobWhoseDocumentWasNotFlaggedLast()
    {
        var (printer, _) = NewPrinter();
        int id = printer.Handle(Request(IppOperation.CreateJob), Ctx).Group(IppTag.JobAttributes)!.Find("job-id")!.First.AsInt();

        var ctx = DocumentContext(out _);
        printer.Handle(Request(IppOperation.SendDocument, g =>
        {
            g.Add("job-id", IppValue.Integer(id));
            g.Add("document-format", IppValue.MimeType("image/urf"));
            g.Add("last-document", IppValue.Boolean(false));
        }), ctx);

        var closed = printer.Handle(Request(IppOperation.CloseJob, g => g.Add("job-id", IppValue.Integer(id))), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, closed.Status);
        Assert.True(printer.Jobs.Get(id)!.DocumentsComplete);
    }

    [Fact]
    public void SendDocumentForAnUnknownJobIsRejected()
    {
        var (printer, _) = NewPrinter();
        var ctx = DocumentContext(out _);
        var resp = printer.Handle(Request(IppOperation.SendDocument, g =>
        {
            g.Add("job-id", IppValue.Integer(4242));
            g.Add("document-format", IppValue.MimeType("image/urf"));
        }), ctx);
        Assert.Equal(IppStatus.ClientErrorNotFound, resp.Status);
    }

    [Fact]
    public void CancelJobMovesAPendingJobToCanceled()
    {
        var (printer, _) = NewPrinter();
        int id = printer.Handle(Request(IppOperation.CreateJob), Ctx).Group(IppTag.JobAttributes)!.Find("job-id")!.First.AsInt();

        var resp = printer.Handle(Request(IppOperation.CancelJob, g => g.Add("job-id", IppValue.Integer(id))), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, resp.Status);
        var job = printer.Jobs.Get(id)!;
        Assert.Equal(IppJobState.Canceled, job.State);
        Assert.Contains("job-canceled-by-user", job.StateReasons);
    }

    [Fact]
    public void CancelJobOnAnUnknownJobIsNotFound()
    {
        var (printer, _) = NewPrinter();
        var resp = printer.Handle(Request(IppOperation.CancelJob, g => g.Add("job-id", IppValue.Integer(999))), Ctx);
        Assert.Equal(IppStatus.ClientErrorNotFound, resp.Status);
    }

    [Fact]
    public void CancelMyJobsCancelsEveryUnfinishedJob()
    {
        var (printer, _) = NewPrinter();
        int a = printer.Handle(Request(IppOperation.CreateJob), Ctx).Group(IppTag.JobAttributes)!.Find("job-id")!.First.AsInt();
        int b = printer.Handle(Request(IppOperation.CreateJob), Ctx).Group(IppTag.JobAttributes)!.Find("job-id")!.First.AsInt();

        var resp = printer.Handle(Request(IppOperation.CancelMyJobs), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, resp.Status);
        Assert.Equal(IppJobState.Canceled, printer.Jobs.Get(a)!.State);
        Assert.Equal(IppJobState.Canceled, printer.Jobs.Get(b)!.State);
    }

    [Fact]
    public void IdentifyPrinterIsAccepted()
    {
        var (printer, _) = NewPrinter();
        var resp = printer.Handle(Request(IppOperation.IdentifyPrinter, g => g.Add("identify-actions", IppValue.Keyword("sound"))), Ctx);
        Assert.Equal(IppStatus.SuccessfulOk, resp.Status);
    }

    /// <summary>A cancelled job must not then be printable by sending it a document.</summary>
    [Fact]
    public void SendDocumentAfterCancelDoesNotResurrectTheJob()
    {
        var (printer, _) = NewPrinter();
        int id = printer.Handle(Request(IppOperation.CreateJob), Ctx).Group(IppTag.JobAttributes)!.Find("job-id")!.First.AsInt();
        printer.Handle(Request(IppOperation.CancelJob, g => g.Add("job-id", IppValue.Integer(id))), Ctx);

        var ctx = DocumentContext(out _);
        var resp = printer.Handle(Request(IppOperation.SendDocument, g =>
        {
            g.Add("job-id", IppValue.Integer(id));
            g.Add("document-format", IppValue.MimeType("image/urf"));
            g.Add("last-document", IppValue.Boolean(true));
        }), ctx);

        Assert.Equal(IppStatus.ClientErrorNotPossible, resp.Status);
        Assert.Equal(IppJobState.Canceled, printer.Jobs.Get(id)!.State);
    }
}
