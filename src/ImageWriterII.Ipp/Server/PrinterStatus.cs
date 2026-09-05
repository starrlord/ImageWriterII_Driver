using ImageWriterII.Core.Ports;
using ImageWriterII.Ipp.Protocol;

namespace ImageWriterII.Ipp.Server;

/// <summary>Live state of the physical printer as seen by the spooler.</summary>
public sealed class PrinterStatus
{
    private readonly object _sync = new();
    private IppPrinterState _state = IppPrinterState.Idle;
    private string[] _reasons = ["none"];
    private string _message = "Ready";

    public DateTimeOffset StartedAt { get; } = DateTimeOffset.Now;
    public DateTimeOffset StateChangedAt { get; private set; } = DateTimeOffset.Now;
    public Iw2Identity? Identity { get; set; }
    public string PortDescription { get; set; } = "";
    public bool PortOpen { get; set; }
    public long BytesSentTotal { get; set; }

    public IppPrinterState State { get { lock (_sync) return _state; } }
    public string[] Reasons { get { lock (_sync) return _reasons; } }
    public string Message { get { lock (_sync) return _message; } }

    /// <summary>Seconds since the service started (IPP printer-up-time).</summary>
    public int UpTimeSeconds => (int)Math.Max(1, (DateTimeOffset.Now - StartedAt).TotalSeconds);

    public int SecondsSinceStart(DateTimeOffset t) => (int)Math.Max(0, (t - StartedAt).TotalSeconds);

    public void Set(IppPrinterState state, string message, params string[] reasons)
    {
        lock (_sync)
        {
            if (_state != state) StateChangedAt = DateTimeOffset.Now;
            _state = state;
            _message = message;
            _reasons = reasons.Length == 0 ? ["none"] : reasons;
        }
    }

    public event Action? Changed;
    public void NotifyChanged() => Changed?.Invoke();
}
