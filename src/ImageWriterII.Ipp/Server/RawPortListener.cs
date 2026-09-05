using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ImageWriterII.Ipp.Server;

/// <summary>
/// "JetDirect"-style raw socket (default TCP 9100). Anything sent is spooled as one job: PWG/URF raster
/// streams are rasterised, ImageWriter escape streams are passed through, plain text is formatted.
/// Lets old software (and Windows "Standard TCP/IP port" + Generic/Text Only) talk to the printer directly.
/// </summary>
public sealed class RawPortListener : BackgroundService
{
    private readonly PrinterConfig _cfg;
    private readonly JobStore _jobs;
    private readonly PrintSpooler _spooler;
    private readonly ILogger _log;

    public RawPortListener(PrinterConfig cfg, JobStore jobs, PrintSpooler spooler, ILogger<RawPortListener> log)
    {
        _cfg = cfg;
        _jobs = jobs;
        _spooler = spooler;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_cfg.RawPortEnabled) return;
        var listener = new TcpListener(IPAddress.IPv6Any, _cfg.RawPort);
        listener.Server.DualMode = true;
        try
        {
            listener.Start();
        }
        catch (SocketException ex)
        {
            _log.LogError("Raw port {Port} unavailable: {Message}", _cfg.RawPort, ex.Message);
            return;
        }
        _log.LogInformation("Raw print port listening on TCP {Port}", _cfg.RawPort);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(stoppingToken);
                _ = Task.Run(() => HandleClientAsync(client, stoppingToken), stoppingToken);
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        string remote = client.Client.RemoteEndPoint?.ToString() ?? "?";
        try
        {
            using (client)
            {
                Directory.CreateDirectory(_cfg.SpoolDirectory);
                string path = Path.Combine(_cfg.SpoolDirectory, $"raw-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bin");
                long length;
                await using (var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true))
                await using (var net = client.GetStream())
                {
                    await net.CopyToAsync(file, ct);
                    length = file.Length;
                }
                if (length == 0)
                {
                    File.Delete(path);
                    return;
                }
                var job = new PrintJob
                {
                    Id = _jobs.NextId(),
                    Name = $"Raw job from {remote}",
                    UserName = remote,
                    Source = "raw",
                    Format = "application/octet-stream",
                    SpoolPath = path,
                    DocumentOffset = 0,
                    DocumentBytes = length,
                    DocumentsComplete = true
                };
                job.Kind = DetectKind(path, length);
                _jobs.Add(job);
                _spooler.Enqueue(job);
                _log.LogInformation("Raw port job {Id} from {Remote}: {Bytes} bytes ({Kind})", job.Id, remote, length, job.Kind);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _log.LogWarning(ex, "Raw port connection from {Remote} failed", remote);
        }
    }

    private static JobDocumentKind DetectKind(string path, long length)
    {
        var head = new byte[Math.Min(4096, length)];
        using (var f = File.OpenRead(path))
        {
            int total = 0;
            while (total < head.Length) { int r = f.Read(head, total, head.Length - total); if (r <= 0) break; total += r; }
        }
        if (head.Length >= 4 && (head.AsSpan(0, 4).SequenceEqual("RaS2"u8) || head.AsSpan(0, 4).SequenceEqual("2SaR"u8))) return JobDocumentKind.PwgRaster;
        if (head.Length >= 7 && head.AsSpan(0, 7).SequenceEqual("UNIRAST"u8)) return JobDocumentKind.AppleRaster;
        return Core.Text.TextJobEncoder.LooksLikeRawPrinterData(head) ? JobDocumentKind.Raw : JobDocumentKind.Text;
    }
}
