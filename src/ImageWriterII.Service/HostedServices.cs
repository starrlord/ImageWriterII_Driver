using ImageWriterII.Core.Ports;
using ImageWriterII.Ipp.Discovery;
using ImageWriterII.Ipp.Server;

namespace ImageWriterII.Service;

/// <summary>Asks the printer for its self-ID at startup (ESC ?) to learn whether the colour ribbon is installed.</summary>
public sealed class RibbonDetector : IHostedService
{
    private readonly PrinterConfig _cfg;
    private readonly PrinterStatus _status;
    private readonly ILogger<RibbonDetector> _log;

    public RibbonDetector(PrinterConfig cfg, PrinterStatus status, ILogger<RibbonDetector> log)
    {
        _cfg = cfg;
        _status = status;
        _log = log;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        switch (_cfg.Ribbon)
        {
            case RibbonSetting.Black:
                _cfg.ColorRibbon = false;
                _log.LogInformation("Ribbon: black (configured)");
                return Task.CompletedTask;
            case RibbonSetting.Color:
                _cfg.ColorRibbon = true;
                _log.LogInformation("Ribbon: colour (configured)");
                return Task.CompletedTask;
        }

        try
        {
            using var port = new SerialPrinterPort(_cfg.Serial);
            port.Open();
            _log.LogInformation("Serial port {Port}: {Lines}; {Recommendation}", port.Description, port.LineStatus, port.Recommendation());
            if (!port.IsReady)
            {
                _cfg.ColorRibbon = false;
                _log.LogWarning("Ribbon auto-detect skipped: the printer is not signalling ready ({Lines}). Assuming a black ribbon; set Ribbon to Color to override.", port.LineStatus);
                return Task.CompletedTask;
            }
            var id = Iw2Identification.Query(port, TimeSpan.FromSeconds(3));
            if (id is null)
            {
                _cfg.ColorRibbon = false;
                _log.LogWarning("Ribbon auto-detect: no answer to ESC ? on {Port} (printer off, busy, or the cable has no printer-to-PC data line). Assuming a black ribbon; set Ribbon to Color to override.", _cfg.Serial.PortName);
            }
            else
            {
                _status.Identity = id;
                _cfg.ColorRibbon = id.ColorRibbon;
                _log.LogInformation("Printer identified: {Identity}", id);
            }
        }
        catch (Exception ex)
        {
            _cfg.ColorRibbon = false;
            _log.LogWarning("Ribbon auto-detect failed on {Port}: {Message}. Assuming a black ribbon.", _cfg.Serial.PortName, ex.Message);
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>Advertises the printer with DNS-SD (_ipp._tcp and the raw port) for the lifetime of the service.</summary>
public sealed class MdnsAdvertiser : IHostedService
{
    private readonly PrinterConfig _cfg;
    private readonly ILogger<MdnsAdvertiser> _log;
    private readonly ILoggerFactory _loggers;
    private MdnsResponder? _responder;

    public MdnsAdvertiser(PrinterConfig cfg, ILogger<MdnsAdvertiser> log, ILoggerFactory loggers)
    {
        _cfg = cfg;
        _log = log;
        _loggers = loggers;
    }

    public MdnsResponder? Responder => _responder;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (!_cfg.MdnsEnabled)
        {
            _log.LogInformation("mDNS advertising disabled by configuration");
            return Task.CompletedTask;
        }
        try
        {
            string adminUrl = $"http://{_cfg.HostName}.local:{_cfg.HttpPort}/";
            var txt = new List<KeyValuePair<string, string>>
            {
                new("txtvers", "1"),
                new("qtotal", "1"),
                new("rp", "ipp/print"),
                new("ty", _cfg.MakeAndModel),
                new("adminurl", adminUrl),
                new("note", _cfg.Location),
                new("pdl", "image/pwg-raster,image/urf,application/octet-stream,text/plain"),
                new("product", "(ImageWriter II)"),
                new("Color", _cfg.ColorRibbon ? "T" : "F"),
                new("Duplex", "F"),
                new("Fax", "F"),
                new("Scan", "F"),
                new("UUID", _cfg.Uuid),
                new("URF", string.Join(",", IppPrinter.UrfSupported(_cfg.ResolutionList(), _cfg.ColorRibbon))),
                new("kind", "document"),
                new("priority", "0"),
                new("PaperMax", "legal-A4"),
                new("mopria-certified", "1.3"),
                new("usb_MFG", "Apple"),
                new("usb_MDL", "ImageWriter II"),
                new("usb_CMD", "PWGRaster,URF")
            };
            var services = new List<MdnsService>
            {
                new() { Type = "_ipp._tcp", Port = (ushort)_cfg.HttpPort, Subtypes = ["_universal", "_print"], Txt = txt },
                new() { Type = "_http._tcp", Port = (ushort)_cfg.HttpPort, Subtypes = ["_printer"], Txt = [new("path", "/")] }
            };
            if (_cfg.RawPortEnabled)
            {
                services.Add(new MdnsService
                {
                    Type = "_pdl-datastream._tcp",
                    Port = (ushort)_cfg.RawPort,
                    Txt = [new("txtvers", "1"), new("qtotal", "1"), new("ty", _cfg.MakeAndModel), new("pdl", "image/pwg-raster,application/octet-stream,text/plain"), new("product", "(ImageWriter II)"), new("Color", _cfg.ColorRibbon ? "T" : "F"), new("Duplex", "F"), new("UUID", _cfg.Uuid)]
                });
            }
            _responder = new MdnsResponder(new MdnsOptions
            {
                InstanceName = _cfg.PrinterName,
                HostLabel = _cfg.HostName,
                Services = services,
                InterfaceFilter = _cfg.MdnsInterfaces
            }, _loggers.CreateLogger<MdnsResponder>());
            _responder.Start();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "mDNS responder could not start (port 5353 in use by another responder?). The printer can still be added by URL: http://<host>:{Port}/ipp/print", _cfg.HttpPort);
            _responder = null;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _responder?.Stop();
        _responder = null;
        return Task.CompletedTask;
    }
}
