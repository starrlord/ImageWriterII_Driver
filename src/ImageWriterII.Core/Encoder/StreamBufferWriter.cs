using System.Buffers;

namespace ImageWriterII.Core.Encoder;

/// <summary>Small IBufferWriter that flushes to a stream when its buffer fills or on demand.</summary>
public sealed class StreamBufferWriter : IBufferWriter<byte>, IDisposable
{
    private readonly Stream _stream;
    private byte[] _buffer;
    private int _count;

    public StreamBufferWriter(Stream stream, int bufferSize = 16 * 1024)
    {
        _stream = stream;
        _buffer = new byte[Math.Max(256, bufferSize)];
    }

    public long TotalWritten { get; private set; }

    public void Advance(int count)
    {
        _count += count;
        TotalWritten += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsMemory(_count);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        Ensure(sizeHint);
        return _buffer.AsSpan(_count);
    }

    private void Ensure(int sizeHint)
    {
        if (sizeHint < 1) sizeHint = 1;
        if (_buffer.Length - _count >= sizeHint) return;
        Flush();
        if (_buffer.Length < sizeHint) _buffer = new byte[sizeHint];
    }

    public void Flush()
    {
        if (_count > 0)
        {
            _stream.Write(_buffer, 0, _count);
            _count = 0;
        }
        _stream.Flush();
    }

    public void Dispose() => Flush();
}
