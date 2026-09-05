using System.Diagnostics;
using System.IO.Ports;

namespace ImageWriterII.Core.Ports;

public enum Iw2Handshake
{
    /// <summary>Look at CTS, DSR and DCD when the port opens and use whichever carries the printer's ready signal.</summary>
    Auto,
    /// <summary>No flow control. Only safe with a large external buffer or MaxBytesPerSecond pacing.</summary>
    None,
    /// <summary>Printer DTR arrives on the PC's CTS (hardware flow control in the adapter).</summary>
    RequestToSend,
    /// <summary>Printer DTR arrives on the PC's DSR (hardware flow control; typical for Mac-style DIN-8 cables).</summary>
    DataSetReady,
    /// <summary>Printer DTR arrives on the PC's DCD. No adapter supports this in hardware, so output is gated and paced in software.</summary>
    DataCarrierDetect,
    /// <summary>XON/XOFF characters from the printer (DIP switch SW2-3 closed). Needs the printer-to-PC data line.</summary>
    XOnXOff,
    /// <summary>CTS hardware handshake plus XON/XOFF.</summary>
    RequestToSendXOnXOff
}

public sealed class SerialPortSettings
{
    public string PortName { get; set; } = "COM1";
    /// <summary>300, 1200, 2400 or 9600 (DIP switches SW2-1/2-2; factory setting 9600).</summary>
    public int BaudRate { get; set; } = 9600;
    public Iw2Handshake Handshake { get; set; } = Iw2Handshake.Auto;
    /// <summary>Assert DTR/RTS towards the printer (it ignores DSR, but some cables loop signals).</summary>
    public bool AssertDtr { get; set; } = true;
    public bool AssertRts { get; set; } = true;
    /// <summary>Write timeout in milliseconds; 0 or negative means wait forever (recommended: a busy or paper-out printer just pauses).</summary>
    public int WriteTimeoutMs { get; set; } = 0;
    /// <summary>Optional pacing in bytes per second; 0 disables (software-gated modes default to 480).</summary>
    public int MaxBytesPerSecond { get; set; } = 0;
    /// <summary>NUL bytes appended after each job to push the last real bytes out of USB adapters that hold them (0 disables).</summary>
    public int TrailingNulPadding { get; set; } = 0;

    public override string ToString() => $"{PortName} {BaudRate} 8N1 {Handshake}";
}

/// <summary>Which modem control line the printer's DTR (ready/busy) signal is arriving on.</summary>
public enum ReadyLine
{
    Unknown,
    Cts,
    Dsr,
    Dcd,
    /// <summary>Flow control does not use a modem line (XON/XOFF or none).</summary>
    NotUsed
}

/// <summary>Serial connection to the ImageWriter II (8 data bits, 1 stop bit, no parity; the printer allows nothing else).</summary>
public sealed class SerialPrinterPort : IPrinterPort
{
    private readonly SerialPortSettings _s;
    private SerialPort? _port;
    private readonly Stopwatch _pace = new();
    private long _pacedBytes;
    private ReadyLine _readyLine = ReadyLine.Unknown;
    private bool _softwareGated;      // gate + pace writes on _readyLine in software
    private bool _hardwareApplied;    // adapter does the flow control itself (diagnostics only)
    private string _modeDescription = "";

    public SerialPrinterPort(SerialPortSettings settings) => _s = settings;

    public string Description => $"{_s.PortName} {_s.BaudRate} 8N1 {_s.Handshake}{(_modeDescription.Length > 0 ? " (" + _modeDescription + ")" : "")}";

    public bool IsOpen => _port?.IsOpen == true;

    public int BytesToWrite => _port?.IsOpen == true ? _port.BytesToWrite : 0;

    /// <summary>The line currently used as the printer's ready signal.</summary>
    public ReadyLine ReadyLine => _readyLine;

    public void Open()
    {
        if (IsOpen) return;
        bool dotnetHandshake = _s.Handshake is Iw2Handshake.RequestToSend or Iw2Handshake.XOnXOff or Iw2Handshake.RequestToSendXOnXOff;
        var p = new SerialPort(_s.PortName, _s.BaudRate, Parity.None, 8, StopBits.One)
        {
            Handshake = _s.Handshake switch
            {
                Iw2Handshake.RequestToSend => Handshake.RequestToSend,
                Iw2Handshake.XOnXOff => Handshake.XOnXOff,
                Iw2Handshake.RequestToSendXOnXOff => Handshake.RequestToSendXOnXOff,
                _ => Handshake.None
            },
            WriteTimeout = _s.WriteTimeoutMs > 0 ? _s.WriteTimeoutMs : SerialPort.InfiniteTimeout,
            ReadTimeout = 500,
            WriteBufferSize = 4096,
            ReadBufferSize = 4096,
            Encoding = System.Text.Encoding.Latin1
        };
        p.Open();
        // Modem lines first: their setters rewrite the DCB, which would undo a later DSR-flow patch.
        try { p.DtrEnable = _s.AssertDtr; } catch (Exception) { /* not every adapter exposes DTR */ }
        if (!dotnetHandshake || p.Handshake == Handshake.XOnXOff)
        {
            try { p.RtsEnable = _s.AssertRts; } catch (Exception) { /* ignore */ }
        }
        _port = p;
        _pace.Restart();
        _pacedBytes = 0;
        _softwareGated = false;
        _hardwareApplied = false;
        _readyLine = ReadyLine.Unknown;
        _modeDescription = "";

        switch (_s.Handshake)
        {
            case Iw2Handshake.RequestToSend:
            case Iw2Handshake.RequestToSendXOnXOff:
                _readyLine = ReadyLine.Cts;
                _hardwareApplied = true;
                _modeDescription = "CTS hardware handshake";
                break;
            case Iw2Handshake.XOnXOff:
                _readyLine = ReadyLine.NotUsed;
                _hardwareApplied = true;
                _modeDescription = "XON/XOFF";
                break;
            case Iw2Handshake.None:
                _readyLine = ReadyLine.NotUsed;
                _modeDescription = "no flow control" + (_s.MaxBytesPerSecond > 0 ? $", paced {_s.MaxBytesPerSecond} B/s" : "");
                break;
            case Iw2Handshake.DataSetReady:
                ApplyDsrMode();
                break;
            case Iw2Handshake.DataCarrierDetect:
                _readyLine = ReadyLine.Dcd;
                _softwareGated = true;
                _modeDescription = "DCD software gating";
                break;
            case Iw2Handshake.Auto:
                Thread.Sleep(100); // let the lines settle after DTR/RTS come up
                TryAutoSelect();
                break;
        }
    }

    /// <summary>In Auto mode, pick the first line that is asserted. Returns true once a line has been chosen.</summary>
    private bool TryAutoSelect()
    {
        var p = _port;
        if (p is null || !p.IsOpen || _readyLine != ReadyLine.Unknown) return _readyLine != ReadyLine.Unknown;
        bool cts = SafeLine(() => p.CtsHolding), dsr = SafeLine(() => p.DsrHolding), dcd = SafeLine(() => p.CDHolding);
        if (cts)
        {
            try
            {
                p.Handshake = Handshake.RequestToSend; // adapter handles it from here
                _readyLine = ReadyLine.Cts;
                _hardwareApplied = true;
                _modeDescription = "auto: CTS hardware handshake";
                return true;
            }
            catch (Exception)
            {
                _readyLine = ReadyLine.Cts;
                _softwareGated = true;
                _modeDescription = "auto: CTS software gating";
                return true;
            }
        }
        if (dsr) { ApplyDsrMode(); _modeDescription = "auto: " + _modeDescription; return true; }
        if (dcd)
        {
            _readyLine = ReadyLine.Dcd;
            _softwareGated = true;
            _modeDescription = "auto: DCD software gating";
            return true;
        }
        return false;
    }

    private void ApplyDsrMode()
    {
        _readyLine = ReadyLine.Dsr;
        string reason = "port not open";
        if (_port is not null && Win32SerialFlow.TryEnableDsrFlowControl(_port, out reason))
        {
            _hardwareApplied = true;
            _modeDescription = "DSR hardware handshake";
        }
        else
        {
            _softwareGated = true;
            _modeDescription = "DSR software gating" + (string.IsNullOrEmpty(reason) ? "" : $" ({reason})");
        }
    }

    private static bool SafeLine(Func<bool> read)
    {
        try { return read(); } catch (Exception) { return false; }
    }

    /// <summary>"CTS=high DSR=low DCD=low" for logs and status pages.</summary>
    public string LineStatus
    {
        get
        {
            var p = _port;
            if (p is null || !p.IsOpen) return "port closed";
            return $"CTS={(SafeLine(() => p.CtsHolding) ? "high" : "low")} DSR={(SafeLine(() => p.DsrHolding) ? "high" : "low")} DCD={(SafeLine(() => p.CDHolding) ? "high" : "low")}";
        }
    }

    /// <summary>True when the printer is signalling ready on the selected line (always true for XON/XOFF and none).</summary>
    public bool IsReady
    {
        get
        {
            var p = _port;
            if (p is null || !p.IsOpen) return false;
            if (_readyLine == ReadyLine.Unknown && !TryAutoSelect()) return false;
            return _readyLine switch
            {
                ReadyLine.Cts => SafeLine(() => p.CtsHolding),
                ReadyLine.Dsr => SafeLine(() => p.DsrHolding),
                ReadyLine.Dcd => SafeLine(() => p.CDHolding),
                _ => true
            };
        }
    }

    public void Close()
    {
        if (_port is null) return;
        try { _port.Close(); } catch (Exception) { /* ignore */ }
        _port.Dispose();
        _port = null;
    }

    public CancellationToken WriteCancellation { get; set; }

    public bool HardwareFlowControl => _hardwareApplied;

    public void Write(ReadOnlySpan<byte> data)
    {
        var p = _port ?? throw new InvalidOperationException("Port not open.");
        var ct = WriteCancellation;
        int pace = _s.MaxBytesPerSecond;
        if (_softwareGated && pace <= 0) pace = 480; // half the line rate: the 2K buffer never fills, so the gate only matters when the printer is off-line

        // Always write in small chunks and check the ready line before each one, even when the adapter does
        // the flow control itself. A single large BaseStream.Write to an off-line printer blocks in WriteFile
        // with an infinite timeout and cannot be cancelled; a chunk that starts with the line high completes.
        int chunk = _softwareGated ? 16 : 64;
        int pos = 0;
        while (pos < data.Length)
        {
            WaitUntilReady(p, ct);
            int n = Math.Min(chunk, data.Length - pos);
            p.BaseStream.Write(data.Slice(pos, n));
            pos += n;
            if (pace > 0)
            {
                _pacedBytes += n;
                double ahead = (double)_pacedBytes / pace - _pace.Elapsed.TotalSeconds;
                if (ahead > 0.005) Sleep(TimeSpan.FromSeconds(ahead), ct);
                // Idle time must not bank burst credit: after a long pause the deficit would let a whole
                // job go out at line rate with no pacing at all, which is exactly what pacing is there to stop.
                else if (ahead < -1.0) { _pace.Restart(); _pacedBytes = 0; }
            }
        }
    }

    /// <summary>Blocks until the printer's ready line is high, honouring <see cref="WriteCancellation"/>.</summary>
    private void WaitUntilReady(SerialPort p, CancellationToken ct)
    {
        if (IsReady) return;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (!p.IsOpen) throw new IOException("Serial port closed while waiting for the printer.");
            if (IsReady) return;
            Sleep(TimeSpan.FromMilliseconds(20), ct);
        }
    }

    private static void Sleep(TimeSpan delay, CancellationToken ct)
    {
        if (ct.CanBeCanceled) ct.WaitHandle.WaitOne(delay);
        else Thread.Sleep(delay);
    }

    public void Flush()
    {
        var p = _port;
        if (p is null || !p.IsOpen) return;
        p.BaseStream.Flush();
        if (_s.TrailingNulPadding > 0)
        {
            var pad = new byte[Math.Min(_s.TrailingNulPadding, 4096)];
            int remaining = _s.TrailingNulPadding;
            while (remaining > 0)
            {
                int n = Math.Min(pad.Length, remaining);
                Write(pad.AsSpan(0, n));
                remaining -= n;
            }
        }
    }

    public int ReadByte(TimeSpan timeout)
    {
        var p = _port ?? throw new InvalidOperationException("Port not open.");
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (p.BytesToRead > 0) return p.ReadByte();
            Thread.Sleep(10);
        }
        return -1;
    }

    public void DiscardInput()
    {
        try { _port?.DiscardInBuffer(); } catch (Exception) { /* ignore */ }
    }

    public void Dispose() => Close();

    public static string[] AvailablePorts() => SerialPort.GetPortNames();

    /// <summary>Human-readable recommendation from the current line states, for diagnostics.</summary>
    public string Recommendation()
    {
        var p = _port;
        if (p is null || !p.IsOpen) return "port closed";
        bool cts = SafeLine(() => p.CtsHolding), dsr = SafeLine(() => p.DsrHolding), dcd = SafeLine(() => p.CDHolding);
        if (cts) return "CTS is high: the printer's DTR reaches CTS. Handshake RequestToSend (or Auto) will work.";
        if (dsr) return "DSR is high but CTS is low: the cable puts the printer's DTR on DSR. Use Handshake DataSetReady (or Auto).";
        if (dcd) return "Only DCD is high: the printer's DTR reaches DCD. Use Handshake DataCarrierDetect (or Auto); output is gated in software.";
        return "No control line is high: printer off or deselected, or the cable does not connect the printer's DTR (Mini-DIN pin 2) to the PC. Check power/SELECT, or use XON/XOFF (SW2-3 closed) / None with pacing.";
    }
}

/// <summary>Appends everything to a file instead of a printer (configure PortName as "file:path"). Reads never return data.</summary>
public sealed class FilePrinterPort : IPrinterPort
{
    private readonly string _path;
    private FileStream? _file;

    public FilePrinterPort(string path) => _path = path;

    public string Description => "file:" + _path;
    public bool IsOpen => _file is not null;
    public int BytesToWrite => 0;
    public bool IsReady => true;
    public string LineStatus => "file";
    public bool HardwareFlowControl => false;
    public CancellationToken WriteCancellation { get; set; }

    public void Open()
    {
        if (_file is not null) return;
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_path)) ?? ".");
        _file = new FileStream(_path, FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    public void Close() { _file?.Dispose(); _file = null; }
    public void Write(ReadOnlySpan<byte> data) => (_file ?? throw new InvalidOperationException("Port not open.")).Write(data);
    public void Flush() => _file?.Flush();
    public int ReadByte(TimeSpan timeout) => -1;
    public void DiscardInput() { }
    public void Dispose() => Close();
}

/// <summary>Writes the job to a stream (file, stdout) instead of a printer. Reads never return data.</summary>
public sealed class StreamPrinterPort : IPrinterPort
{
    private readonly Stream _stream;
    private readonly bool _leaveOpen;

    public StreamPrinterPort(Stream stream, string description, bool leaveOpen = false)
    {
        _stream = stream;
        Description = description;
        _leaveOpen = leaveOpen;
        IsOpen = true;
    }

    public string Description { get; }
    public bool IsOpen { get; private set; }
    public int BytesToWrite => 0;
    public bool IsReady => true;
    public string LineStatus => "stream";
    public bool HardwareFlowControl => false;
    public CancellationToken WriteCancellation { get; set; }
    public void Open() => IsOpen = true;
    public void Close() { IsOpen = false; if (!_leaveOpen) _stream.Dispose(); }
    public void Write(ReadOnlySpan<byte> data) => _stream.Write(data);
    public void Flush() => _stream.Flush();
    public int ReadByte(TimeSpan timeout) => -1;
    public void DiscardInput() { }
    public void Dispose() => Close();
}
