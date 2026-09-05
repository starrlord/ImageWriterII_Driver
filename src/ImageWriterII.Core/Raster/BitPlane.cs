namespace ImageWriterII.Core.Raster;

/// <summary>
/// One-bit-per-pixel plane, rows packed MSB-first (bit 7 of the first byte is pixel 0). A set bit means "ink".
/// This is the same layout as a PWG raster black_1 page, so those pages copy straight in.
/// </summary>
public sealed class BitPlane
{
    public BitPlane(int width, int height)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        Width = width;
        Height = height;
        Stride = (width + 7) >> 3;
        Data = new byte[Stride * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int Stride { get; }
    public byte[] Data { get; }

    public bool Get(int x, int y) => (Data[y * Stride + (x >> 3)] & (0x80 >> (x & 7))) != 0;

    public void Set(int x, int y) => Data[y * Stride + (x >> 3)] |= (byte)(0x80 >> (x & 7));

    public void Clear(int x, int y) => Data[y * Stride + (x >> 3)] &= (byte)~(0x80 >> (x & 7));

    public Span<byte> Row(int y) => Data.AsSpan(y * Stride, Stride);

    public bool IsRowBlank(int y)
    {
        var row = Row(y);
        return row.IndexOfAnyExcept((byte)0) < 0;
    }

    public bool IsBlank()
    {
        return Data.AsSpan().IndexOfAnyExcept((byte)0) < 0;
    }

    /// <summary>Returns a plane with the row order reversed (for FeedTransform = -1).</summary>
    public BitPlane FlipVertical()
    {
        var r = new BitPlane(Width, Height);
        for (int y = 0; y < Height; y++)
            Row(y).CopyTo(r.Row(Height - 1 - y));
        return r;
    }

    /// <summary>Returns a plane mirrored left-to-right (for CrossFeedTransform = -1).</summary>
    public BitPlane FlipHorizontal()
    {
        var r = new BitPlane(Width, Height);
        for (int y = 0; y < Height; y++)
            for (int x = 0; x < Width; x++)
                if (Get(x, y)) r.Set(Width - 1 - x, y);
        return r;
    }
}
