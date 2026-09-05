using System.Diagnostics;
using ImageWriterII.Core.Ports;
using ImageWriterII.Ipp.Protocol;
using ImageWriterII.Ipp.Server;
using ImageWriterII.Service;
using Microsoft.Extensions.Logging.EventLog;

string contentRoot = AppContext.BaseDirectory;
var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = contentRoot });

// ---- configuration ----
var cfg = new PrinterConfig();
builder.Configuration.GetSection("ImageWriter").Bind(cfg);
cfg.SpoolDirectory = Path.IsPathRooted(cfg.SpoolDirectory) ? cfg.SpoolDirectory : Path.Combine(contentRoot, cfg.SpoolDirectory);
Directory.CreateDirectory(cfg.SpoolDirectory);
foreach (var stale in Directory.GetFiles(cfg.SpoolDirectory)) { try { File.Delete(stale); } catch (Exception) { /* ignore */ } }
if (string.IsNullOrWhiteSpace(cfg.Uuid))
{
    string uuidFile = Path.Combine(contentRoot, "printer-uuid.txt");
    if (File.Exists(uuidFile) && Guid.TryParse(File.ReadAllText(uuidFile).Trim(), out var stored)) cfg.Uuid = stored.ToString();
    else
    {
        cfg.Uuid = Guid.NewGuid().ToString();
        try { File.WriteAllText(uuidFile, cfg.Uuid); } catch (Exception) { /* read-only install dir: fine, uuid regenerates */ }
    }
}
cfg.Validate();

// ---- logging ----
builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(o => { o.SingleLine = true; o.TimestampFormat = "HH:mm:ss "; });
builder.Logging.AddProvider(new FileLoggerProvider(Path.Combine(contentRoot, "logs")));
builder.Logging.AddFilter<EventLogLoggerProvider>(null, LogLevel.Warning);
builder.Host.UseWindowsService(o => o.ServiceName = "ImageWriterII");

// ---- HTTP server ----
builder.WebHost.ConfigureKestrel(k =>
{
    k.ListenAnyIP(cfg.HttpPort);
    k.Limits.MaxRequestBodySize = null;          // multi-page colour rasters can be large
    k.Limits.MinRequestBodyDataRate = null;      // slow clients are fine
    k.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(5);
    k.Limits.RequestHeadersTimeout = TimeSpan.FromMinutes(1);
    k.AddServerHeader = false;
});

// ---- services ----
builder.Services.AddSingleton(cfg);
builder.Services.AddSingleton<PrinterStatus>();
builder.Services.AddSingleton(new JobStore(cfg.KeepCompletedJobs));
// "file:<path>" as the port name appends the printer stream to a file instead (testing / debugging).
builder.Services.AddSingleton<Func<IPrinterPort>>(_ => () =>
    cfg.Serial.PortName.StartsWith("file:", StringComparison.OrdinalIgnoreCase)
        ? new FilePrinterPort(Path.IsPathRooted(cfg.Serial.PortName[5..]) ? cfg.Serial.PortName[5..] : Path.Combine(contentRoot, cfg.Serial.PortName[5..]))
        : new SerialPrinterPort(cfg.Serial));
builder.Services.AddSingleton<PrintSpooler>();
builder.Services.AddSingleton<IppPrinter>();
builder.Services.AddSingleton<MdnsAdvertiser>();
builder.Services.AddHostedService<RibbonDetector>();                                   // 1. learn about the ribbon
builder.Services.AddHostedService(sp => sp.GetRequiredService<PrintSpooler>());         // 2. own the port
builder.Services.AddHostedService<RawPortListener>();                                   // 3. raw socket
builder.Services.AddHostedService(sp => sp.GetRequiredService<MdnsAdvertiser>());       // 4. announce (needs ribbon info)

var app = builder.Build();
var log = app.Logger;
log.LogInformation("ImageWriter II IPP service starting: printer '{Name}' on {Port}, HTTP port {Http}, spool {Spool}", cfg.PrinterName, cfg.Serial, cfg.HttpPort, cfg.SpoolDirectory);

// ---- IPP endpoint ----
async Task HandleIpp(HttpContext http)
{
    var printer = http.RequestServices.GetRequiredService<IppPrinter>();
    var ct = http.RequestAborted;
    string? contentType = http.Request.ContentType;
    if (contentType is null || !contentType.StartsWith("application/ipp", StringComparison.OrdinalIgnoreCase))
    {
        http.Response.StatusCode = StatusCodes.Status400BadRequest;
        await http.Response.WriteAsync("Expected Content-Type: application/ipp", ct);
        return;
    }

    var sw = Stopwatch.StartNew();
    string spoolPath = Path.Combine(cfg.SpoolDirectory, $"ipp-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.spool");
    IppMessage request;
    long docOffset, docLength;
    // Buffer the whole request (IPP message + document) to the spool file, then parse the IPP part.
    await using (var file = new FileStream(spoolPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.Read, 1 << 16, useAsync: true))
    {
        await http.Request.Body.CopyToAsync(file, ct);
        file.Position = 0;
        try
        {
            request = IppCodec.Read(file);
        }
        catch (IppException ex)
        {
            log.LogWarning("Malformed IPP request from {Remote}: {Message}", http.Connection.RemoteIpAddress, ex.Message);
            http.Response.StatusCode = StatusCodes.Status400BadRequest;
            await http.Response.WriteAsync(ex.Message, ct);
            file.Close();
            File.Delete(spoolPath);
            return;
        }
        docOffset = file.Position;
        docLength = file.Length - docOffset;
    }

    var ctx = new IppRequestContext
    {
        Host = http.Request.Host.HasValue ? http.Request.Host.Value! : $"localhost:{cfg.HttpPort}",
        ResourcePath = http.Request.Path.Value ?? IppPrinter.ResourcePath,
        RemoteAddress = http.Connection.RemoteIpAddress?.ToString() ?? "",
        SpoolPath = docLength > 0 ? spoolPath : null,
        DocumentOffset = docOffset,
        DocumentLength = docLength
    };
    var response = printer.Handle(request, ctx);
    if (!ctx.SpoolConsumed) { try { File.Delete(spoolPath); } catch (Exception) { /* ignore */ } }

    var bytes = IppCodec.Write(response);
    http.Response.StatusCode = StatusCodes.Status200OK;
    http.Response.ContentType = "application/ipp";
    http.Response.ContentLength = bytes.Length;
    await http.Response.Body.WriteAsync(bytes, ct);
    log.LogInformation("IPP {Operation} from {Remote} ({User}) -> {Status} in {Ms} ms{Doc}",
        request.Operation, ctx.RemoteAddress, request.GetString(IppTag.OperationAttributes, "requesting-user-name") ?? "-",
        response.Status, sw.ElapsedMilliseconds, docLength > 0 ? $", {docLength:N0} bytes of document data" : "");
}

app.MapPost("/ipp/print", HandleIpp);
app.MapPost("/ipp/print/{**rest}", HandleIpp);
app.MapPost("/ipp", HandleIpp);
app.MapPost("/", HandleIpp);
app.MapGet("/ipp/print", () => Results.Text("ImageWriter II IPP printer. POST application/ipp requests here.", "text/plain"));

// ---- web UI / API ----
app.MapGet("/", (HttpContext http, PrinterStatus status, JobStore jobs, PrintSpooler spooler) =>
    Results.Content(StatusPage.Render(cfg, status, jobs, spooler, http.Request.Host.Value ?? "localhost"), "text/html; charset=utf-8"));
app.MapGet("/icon-{size:int}.png", (int size) => Results.Bytes(PrinterIcons.Get(size), "image/png"));
app.MapGet("/api/status", (PrinterStatus status, JobStore jobs, PrintSpooler spooler) => Results.Json(new
{
    printer = cfg.PrinterName,
    state = status.State.ToString(),
    reasons = status.Reasons,
    message = status.Message,
    port = status.PortDescription,
    portOpen = status.PortOpen,
    identity = status.Identity?.Raw,
    colorRibbon = cfg.ColorRibbon,
    resolutions = cfg.ResolutionList().Select(r => r.ToString()),
    uptimeSeconds = status.UpTimeSeconds,
    bytesSent = status.BytesSentTotal,
    currentJob = spooler.CurrentJob?.Id,
    jobs = jobs.All().Select(j => new { j.Id, j.Name, j.UserName, j.Format, kind = j.Kind.ToString(), state = j.State.ToString(), reasons = j.StateReasons, pages = j.ImpressionsCompleted, created = j.CreatedAt })
}));
app.MapPost("/api/jobs/{id:int}/cancel", (int id, IppPrinter printer, JobStore jobs) =>
{
    var job = jobs.Get(id);
    if (job is null) return Results.NotFound();
    try { printer.Cancel(job); } catch (IppException ex) { return Results.BadRequest(ex.Message); }
    return Results.Redirect("/");
});

app.Run();
