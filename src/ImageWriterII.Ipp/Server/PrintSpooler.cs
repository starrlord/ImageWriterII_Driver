using System.Buffers;
using System.Threading.Channels;
using ImageWriterII.Core.Encoder;
using ImageWriterII.Core.Ports;
using ImageWriterII.Core.Printer;
using ImageWriterII.Core.Raster;
using ImageWriterII.Core.Text;
using ImageWriterII.Ipp.Protocol;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImageWriterII.Ipp.Server;

/// <summary>
/// Owns the serial port and prints jobs one at a time. Jobs are decoded page by page and streamed
/// to the printer; a busy or offline printer simply blocks the write (hardware handshake) until it is ready.
/// </summary>
public sealed class PrintSpooler : BackgroundService
{
    private readonly PrinterConfig _cfg;
    private readonly Func<IPrinterPort> _portFactory;
    private readonly ILogger _log;
    private readonly Channel<PrintJob> _queue = Channel.CreateUnbounded<PrintJob>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<Func<IPrinterPort, Task>> _actions = Channel.CreateUnbounded<Func<IPrinterPort, Task>>();
    private IPrinterPort? _port;
    private PrintJob? _current;

    public PrintSpooler(PrinterConfig config, Func<IPrinterPort> portFactory, PrinterStatus status, ILogger<PrintSpooler> logger)
    {
        _cfg = config;
        _portFactory = portFactory;
        Status = status;
        _log = logger;
    }

    public PrinterStatus Status { get; }

    public PrintJob? CurrentJob => _current;

    public int QueuedCount => _queue.Reader.Count;

    public void Enqueue(PrintJob job)
    {
        job.SetState(IppJobState.Pending, "none");
        _queue.Writer.TryWrite(job);
        _log.LogInformation("Queued {Job}", job);
    }

    /// <summary>Identify-Printer: ring the bell when the printer is next idle.</summary>
    public void Identify()
    {
        _actions.Writer.TryWrite(port =>
        {
            if (!_cfg.IdentifyWithBell) return Task.CompletedTask;
            var buf = new ArrayBufferWriter<byte>();
            var w = new Iw2Writer(buf);
            w.Bell();
            w.CarriageReturn();
            port.Write(buf.WrittenSpan);
            port.Flush();
            return Task.CompletedTask;
        });
    }

    /// <summary>Runs an arbitrary action against the open port between jobs (used for the startup identity query).</summary>
    public void RunOnPort(Func<IPrinterPort, Task> action) => _actions.Writer.TryWrite(action);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // The printer work is blocking I/O; keep it off the thread pool's async machinery.
        await Task.Yield();
        Status.PortDescription = _cfg.Serial.ToString();
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                // Drain pending port actions (identify, identity query) before waiting on jobs.
                while (_actions.Reader.TryRead(out var action))
                {
                    var port = await EnsurePortAsync(stoppingToken);
                    if (port is null) break;
                    try { await action(port); }
                    catch (Exception ex) { _log.LogWarning(ex, "Port action failed"); ResetPort(); }
                }

                var readTask = _queue.Reader.ReadAsync(stoppingToken).AsTask();
                var actionTask = _actions.Reader.WaitToReadAsync(stoppingToken).AsTask();
                var done = await Task.WhenAny(readTask, actionTask);
                if (done == actionTask) { await actionTask; continue; }
                var job = await readTask;

                if (job.IsFinished) continue; // cancelled while queued
                await ProcessAsync(job, stoppingToken);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        finally
        {
            ResetPort();
        }
    }

    private async Task<IPrinterPort?> EnsurePortAsync(CancellationToken ct)
    {
        int attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            if (_port is { IsOpen: true }) return _port;
            try
            {
                _port?.Dispose();
                _port = _portFactory();
                _port.Open();
                Status.PortOpen = true;
                Status.PortDescription = _port.Description;
                if (Status.State == IppPrinterState.Stopped) Status.Set(IppPrinterState.Idle, "Ready");
                Status.NotifyChanged();
                _log.LogInformation("Opened printer port {Port}", _port.Description);
                return _port;
            }
            catch (Exception ex)
            {
                Status.PortOpen = false;
                Status.Set(IppPrinterState.Stopped, $"Cannot open {_cfg.Serial.PortName}: {ex.Message}", "other", "connecting-to-device");
                Status.NotifyChanged();
                if (attempt++ % 12 == 0) _log.LogError("Cannot open printer port {Port}: {Message}", _cfg.Serial.PortName, ex.Message);
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
            }
        }
        return null;
    }

    private void ResetPort()
    {
        try { _port?.Dispose(); } catch (Exception) { /* ignore */ }
        _port = null;
        Status.PortOpen = false;
    }

    private async Task ProcessAsync(PrintJob job, CancellationToken stoppingToken)
    {
        _current = job;
        job.SetState(IppJobState.Processing, "job-printing");
        Status.Set(IppPrinterState.Processing, $"Printing job {job.Id}: {job.Name}");
        Status.NotifyChanged();
        _log.LogInformation("Printing {Job}", job);

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, job.Cancellation.Token);
        var ct = linked.Token;
        try
        {
            var port = await EnsurePortAsync(ct);
            if (port is null) throw new OperationCanceledException(ct);

            await WaitForPrinterReadyAsync(port, job, ct);

            // Blocking serial writes happen on a dedicated thread so the service stays responsive.
            var printTask = Task.Factory.StartNew(() => PrintJobBlocking(job, port, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            await MonitorProgressAsync(printTask, port, job, ct);
            await printTask;

            job.SetState(IppJobState.Completed, "job-completed-successfully");
            Status.Set(IppPrinterState.Idle, "Ready");
            _log.LogInformation("Completed {Job}: {Pages} page(s), {Bytes} bytes sent", job, job.ImpressionsCompleted, job.DocumentBytes);
        }
        catch (OperationCanceledException) when (job.Cancellation.IsCancellationRequested)
        {
            _log.LogInformation("Cancelled {Job}", job);
            job.SetState(IppJobState.Canceled, "job-canceled-by-user");
            RecoverPrinter("cancel");
            Status.Set(IppPrinterState.Idle, "Ready");
        }
        catch (OperationCanceledException)
        {
            // Shutdown cut the job mid-page: the printer is very likely counting down a graphics run, and
            // nothing later clears that (BeginJob deliberately never sends ESC c), so recover here too.
            job.SetState(IppJobState.Aborted, "job-aborted-by-system", "printer-stopped");
            RecoverPrinter("shutdown");
            Status.Set(IppPrinterState.Stopped, "Service stopping", "shutdown");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Job {Job} failed", job);
            job.StateMessage = ex.Message;
            // "unsupported-document-format" is only right when the document really was undecodable; an I/O
            // failure on the port is a device error and clients act on the difference.
            bool badDocument = ex is InvalidDataException or NotSupportedException or FormatException or EndOfStreamException;
            job.SetState(IppJobState.Aborted, "job-aborted-by-system",
                badDocument ? "unsupported-document-format" : "printer-stopped");
            RecoverPrinter($"job {job.Id} failure");
            Status.Set(IppPrinterState.Stopped, $"Job {job.Id} failed: {ex.Message}", "other");
        }
        finally
        {
            _current = null;
            Status.NotifyChanged();
            if (job.SpoolPath is not null)
            {
                // Keep the document of a job that failed to decode: without it there is nothing left to
                // diagnose a client whose raster we cannot read. Program.cs clears the spool dir at startup.
                if (job.State == IppJobState.Aborted && job.StateReasons.Contains("unsupported-document-format"))
                    KeepFailedDocument(job);
                try { if (job.SpoolPath is not null) File.Delete(job.SpoolPath); } catch (Exception) { /* ignore */ }
                job.SpoolPath = null;
            }
        }
    }

    /// <summary>How many undecodable documents to keep before the oldest is discarded.</summary>
    private const int KeepFailedDocuments = 5;
    private const string FailedDocumentPrefix = "failed-job";

    private void KeepFailedDocument(PrintJob job)
    {
        try
        {
            string ext = job.Kind switch
            {
                JobDocumentKind.AppleRaster => "urf",
                JobDocumentKind.PwgRaster => "pwg",
                JobDocumentKind.Text => "txt",
                _ => "bin"
            };
            string kept = Path.Combine(_cfg.SpoolDirectory, $"{FailedDocumentPrefix}{job.Id}.{ext}");
            using (var src = File.OpenRead(job.SpoolPath!))
            using (var dst = File.Create(kept))
            {
                src.Position = job.DocumentOffset;   // skip the IPP request that precedes the document
                src.CopyTo(dst);
            }
            _log.LogWarning("Kept the undecodable document of job {Id} at {Path} ({Bytes} bytes) for diagnosis", job.Id, kept, job.DocumentBytes);
            PruneFailedDocuments();
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not keep the failed document of job {Id}", job.Id);
        }
    }

    /// <summary>
    /// Keeps only the most recent <see cref="KeepFailedDocuments"/> retained documents. A client that loops
    /// on a document we cannot decode would otherwise fill the spool directory until the next service start.
    /// </summary>
    private void PruneFailedDocuments()
    {
        try
        {
            var kept = new DirectoryInfo(_cfg.SpoolDirectory)
                .GetFiles(FailedDocumentPrefix + "*")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .Skip(KeepFailedDocuments)
                .ToList();
            foreach (var f in kept)
            {
                try { f.Delete(); } catch (Exception) { /* ignore */ }
            }
            if (kept.Count > 0) _log.LogDebug("Pruned {Count} old retained document(s) from the spool directory", kept.Count);
        }
        catch (Exception ex)
        {
            _log.LogDebug(ex, "Could not prune retained documents");
        }
    }

    /// <summary>Holds the job (with a clear printer-state message) until the printer raises its ready line.</summary>
    private async Task WaitForPrinterReadyAsync(IPrinterPort port, PrintJob job, CancellationToken ct)
    {
        if (port.IsReady) return;
        _log.LogWarning("Printer not ready before job {Id}: {Lines} on {Port}. Waiting for it to come on-line.", job.Id, port.LineStatus, port.Description);
        int n = 0;
        while (!port.IsReady)
        {
            ct.ThrowIfCancellationRequested();
            Status.Set(IppPrinterState.Stopped, $"Printer not ready ({port.LineStatus}). Check power, SELECT, paper and the cable's DTR line.", "other");
            if (n++ == 0) Status.NotifyChanged();
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        Status.Set(IppPrinterState.Processing, $"Printing job {job.Id}: {job.Name}");
        Status.NotifyChanged();
        _log.LogInformation("Printer ready ({Lines}); starting job {Id}", port.LineStatus, job.Id);
    }

    /// <summary>While a job prints, report stalls (printer off-line / out of paper) through the printer state.</summary>
    private async Task MonitorProgressAsync(Task printTask, IPrinterPort port, PrintJob job, CancellationToken ct)
    {
        int lastBands = -1;
        DateTime lastChange = DateTime.UtcNow;
        bool stalled = false;
        while (!printTask.IsCompleted)
        {
            // Not ct: once cancelled, Task.Delay(ct) completes instantly and Task.WhenAny never throws, so
            // passing ct here spins a core until the print thread notices. Check the token explicitly instead.
            if (ct.IsCancellationRequested) break;
            await Task.WhenAny(printTask, Task.Delay(TimeSpan.FromSeconds(2), CancellationToken.None));
            if (printTask.IsCompleted) break;
            if (job.BandsDone != lastBands) { lastBands = job.BandsDone; lastChange = DateTime.UtcNow; if (stalled) { stalled = false; Status.Set(IppPrinterState.Processing, $"Printing job {job.Id}: {job.Name}"); Status.NotifyChanged(); } continue; }
            if (!stalled && (DateTime.UtcNow - lastChange).TotalSeconds > 20 && !port.IsReady)
            {
                stalled = true;
                Status.Set(IppPrinterState.Stopped, $"Printer stopped accepting data ({port.LineStatus}): out of paper, deselected, or off.", "other");
                Status.NotifyChanged();
                _log.LogWarning("Job {Id} stalled: printer not ready ({Lines})", job.Id, port.LineStatus);
            }
        }
    }

    /// <summary>
    /// The longest literal graphics run the encoder can emit (ESC G nnnn is capped by the pitch's column
    /// count, 160 dpi x 8" = 1280). A job cut short mid-run leaves the printer counting down that many
    /// data bytes, so the reset has to be preceded by at least this much filler or it is eaten as image data.
    /// </summary>
    private const int GraphicsFillerBytes = 1400;

    /// <summary>
    /// Puts the printer back in a known state after a job was cancelled, failed, or was cut off by shutdown.
    /// Bounded and non-blocking by construction: a printer that is off-line will be reset by its own power
    /// cycle anyway, and blocking here would wedge the single-threaded spooler loop for as long as it stays off.
    /// </summary>
    private void RecoverPrinter(string reason)
    {
        ResetPort();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IPrinterPort? port = null;
        try
        {
            port = _portFactory();
            port.Open();
            port.WriteCancellation = cts.Token;
            if (!port.IsReady)
            {
                // Nothing can be sent to a printer that is not raising its ready line; do not park the
                // spooler waiting for it. Closing the port is the best we can do.
                _log.LogWarning("Printer not ready after {Reason}; skipping the reset ({Lines})", reason, port.LineStatus);
                port.Dispose();
                return;
            }
            _port = port;
            Status.PortOpen = true;

            var buf = new ArrayBufferWriter<byte>();
            var w = new Iw2Writer(buf);
            // Satisfy any outstanding ESC G / ESC V byte count first, otherwise ESC c is consumed as graphics data.
            port.Write(new byte[GraphicsFillerBytes]);
            w.CarriageReturn();
            w.Reset();
            port.Write(buf.WrittenSpan);
            port.Flush();
            cts.Token.WaitHandle.WaitOne(Iw2.ResetSettleTime);
            cts.Token.ThrowIfCancellationRequested();
            buf.Clear();
            w.FormFeed();
            port.Write(buf.WrittenSpan);
            port.Flush();
        }
        catch (OperationCanceledException)
        {
            _log.LogWarning("Printer reset after {Reason} timed out; closing the port", reason);
            ResetPort();
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not reset the printer after {Reason}", reason);
            ResetPort();
        }
        finally
        {
            if (_port is not null) _port.WriteCancellation = CancellationToken.None;
        }
    }

    private void PrintJobBlocking(PrintJob job, IPrinterPort port, CancellationToken ct)
    {
        using var output = new PrinterPortStream(port, ct);
        if (job.SpoolPath is null || job.DocumentBytes == 0)
        {
            _log.LogWarning("Job {Job} has no document data", job);
            return;
        }
        using var file = new FileStream(job.SpoolPath, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        file.Position = job.DocumentOffset;
        Stream doc = job.Compression switch
        {
            "gzip" => new System.IO.Compression.GZipStream(file, System.IO.Compression.CompressionMode.Decompress),
            "deflate" => new System.IO.Compression.ZLibStream(file, System.IO.Compression.CompressionMode.Decompress),
            _ => file
        };

        switch (job.Kind)
        {
            case JobDocumentKind.PwgRaster:
            case JobDocumentKind.AppleRaster:
                PrintRaster(job, doc, file, output, ct);
                break;
            case JobDocumentKind.Text:
                PrintText(job, doc, output, raw: false);
                break;
            default:
                PrintText(job, doc, output, raw: true);
                break;
        }
        output.Flush();
        port.Flush();
        Status.BytesSentTotal += output.TotalBytes;
    }

    private void PrintRaster(PrintJob job, Stream doc, FileStream file, Stream output, CancellationToken ct)
    {
        var encOptions = CloneEncoderOptions();
        if (job.PrintQuality == 3) encOptions.Bidirectional = true; // draft: speed over registration
        encOptions.ColorRibbon = _cfg.ColorRibbon;
        bool wantColor = _cfg.ColorRibbon && !IsMonochromeMode(job.ColorMode);

        var enc = new Iw2JobEncoder(output, encOptions)
        {
            Progress = (done, total) => { job.BandsDone = done; job.BandsTotal = total; }
        };
        enc.BeginJob();

        // Collated copies re-read the spool file once per copy; uncollated copies (or compressed
        // documents, which cannot be rewound cheaply) repeat each page as it is decoded.
        int copies = Math.Max(1, job.Copies);
        bool canRewind = doc == file;
        bool collated = job.Collate && canRewind;
        int passes = collated ? copies : 1;
        int repeatsPerPage = collated ? 1 : copies;

        for (int pass = 0; pass < passes; pass++)
        {
            if (pass > 0) file.Position = job.DocumentOffset;
            var reader = new RasterStreamReader(doc);
            int pageIndex = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var header = reader.ReadPageHeader();
                if (header is null) break;
                pageIndex++;
                if (pass == 0 && pageIndex == 1)
                {
                    job.Impressions = header.TotalPageCount > 0 ? header.TotalPageCount * copies : 0;
                    _log.LogInformation("Job {Id} page 1: {Header}", job.Id, header);
                }
                var pixels = new byte[(long)header.BytesPerLine * header.Height];
                reader.ReadPagePixels(header, pixels);
                var page = RasterPageBuilder.Build(header, pixels, wantColor, _cfg.Halftone);
                for (int r = 0; r < repeatsPerPage; r++)
                {
                    enc.EncodePage(page, ct);
                    job.ImpressionsCompleted++;
                }
            }
        }
        enc.EndJob();
    }

    private static bool IsMonochromeMode(string mode) =>
        mode is "monochrome" or "process-monochrome" or "auto-monochrome" or "bi-level";

    private void PrintText(PrintJob job, Stream doc, Stream output, bool raw)
    {
        using var ms = new MemoryStream();
        doc.CopyTo(ms);
        var data = ms.GetBuffer().AsSpan(0, (int)ms.Length);
        var buf = new ArrayBufferWriter<byte>();
        var o = new TextJobOptions
        {
            Font = _cfg.Text.Font,
            Pitch = _cfg.Text.Pitch,
            LinesPerInch = _cfg.Text.LinesPerInch,
            TabWidth = _cfg.Text.TabWidth,
            PerforationSkip = _cfg.Text.PerforationSkip,
            FormFeedAtEnd = _cfg.Text.FormFeedAtEnd,
            Bidirectional = _cfg.Text.Bidirectional,
            WrapColumn = _cfg.Text.WrapColumn,
            // One setting for both job kinds: tear-off is a property of the paper path, not of the encoder.
            TearOffInches = _cfg.Encoder.TearOffInches,
            Raw = raw
        };
        if (job.PrintQuality == 5) o.Font = Iw2Font.NearLetterQuality;
        else if (job.PrintQuality == 3) o.Font = Iw2Font.Draft;
        for (int c = 0; c < Math.Max(1, job.Copies); c++)
        {
            buf.Clear();
            TextJobEncoder.Encode(data, buf, o);
            output.Write(buf.WrittenSpan);
            job.ImpressionsCompleted++;
        }
    }

    private Iw2EncoderOptions CloneEncoderOptions() => new()
    {
        Bidirectional = _cfg.Encoder.Bidirectional,
        LeftEdgeOffsetInches = _cfg.Encoder.LeftEdgeOffsetInches,
        TopEdgeOffsetInches = _cfg.Encoder.TopEdgeOffsetInches,
        UseHeadPositioning = _cfg.Encoder.UseHeadPositioning,
        MinRepeatRun = _cfg.Encoder.MinRepeatRun,
        MinSkipRun = _cfg.Encoder.MinSkipRun,
        PreferGroupedGraphics = _cfg.Encoder.PreferGroupedGraphics,
        ColorRibbon = _cfg.ColorRibbon,
        ColorStrategy = _cfg.Encoder.ColorStrategy,
        TearOffInches = _cfg.Encoder.TearOffInches,
        SetTopOfFormAtJobStart = _cfg.Encoder.SetTopOfFormAtJobStart,
        FormFeedAfterPage = _cfg.Encoder.FormFeedAfterPage
    };
}
