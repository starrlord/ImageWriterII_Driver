namespace ImageWriterII.Core.Ports;

/// <summary>A byte pipe to the printer. Writes block until the bytes have been accepted by the transport.</summary>
public interface IPrinterPort : IDisposable
{
    string Description { get; }
    bool IsOpen { get; }
    void Open();
    void Close();
    void Write(ReadOnlySpan<byte> data);
    void Flush();
    /// <summary>Reads one byte from the printer, or returns -1 when nothing arrives within the timeout.</summary>
    int ReadByte(TimeSpan timeout);
    void DiscardInput();
    /// <summary>Bytes handed to the transport that have not been transmitted yet (0 when unknown).</summary>
    int BytesToWrite { get; }
    /// <summary>True when the printer signals it can accept data (always true for transports without a ready line).</summary>
    bool IsReady { get; }
    /// <summary>Modem control line states for diagnostics, e.g. "CTS=low DSR=high DCD=high".</summary>
    string LineStatus { get; }
}

/// <summary>Adapts an <see cref="IPrinterPort"/> to a write-only <see cref="Stream"/>.</summary>
public sealed class PrinterPortStream : Stream
{
    private readonly IPrinterPort _port;
    private readonly CancellationToken _ct;

    public PrinterPortStream(IPrinterPort port, CancellationToken cancellationToken = default)
    {
        _port = port;
        _ct = cancellationToken;
    }

    public long TotalBytes { get; private set; }

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => TotalBytes; set => throw new NotSupportedException(); }
    public override void Flush() => _port.Flush();
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        // Write in modest chunks so cancellation is honoured while the printer is busy.
        const int chunk = 256;
        int pos = 0;
        while (pos < buffer.Length)
        {
            _ct.ThrowIfCancellationRequested();
            int n = Math.Min(chunk, buffer.Length - pos);
            _port.Write(buffer.Slice(pos, n));
            pos += n;
            TotalBytes += n;
        }
    }
}
