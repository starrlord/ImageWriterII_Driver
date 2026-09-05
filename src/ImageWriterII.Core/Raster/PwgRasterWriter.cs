using System.Buffers.Binary;
using System.Text;

namespace ImageWriterII.Core.Raster;

/// <summary>
/// Minimal PWG raster (PWG 5102.4) writer used by tests and the command-line tool to create sample jobs.
/// Supports black_1 (1-bit K), sgray_8 and srgb_8 pages with PWG run-length compression.
/// </summary>
public sealed class PwgRasterWriter : IDisposable
{
    private readonly Stream _stream;
    private bool _syncWritten;

    public PwgRasterWriter(Stream stream) => _stream = stream;

    public void WritePage(RasterPageHeader h, ReadOnlySpan<byte> pixels)
    {
        if (!_syncWritten)
        {
            _stream.Write(Encoding.ASCII.GetBytes("RaS2"));
            _syncWritten = true;
        }
        var hdr = new byte[1796];
        Encoding.ASCII.GetBytes("PwgRaster").CopyTo(hdr, 0);
        Encoding.ASCII.GetBytes(h.MediaType.Length > 63 ? h.MediaType[..63] : h.MediaType).CopyTo(hdr, 128);
        Put(hdr, 276, h.HwResolutionX);
        Put(hdr, 280, h.HwResolutionY);
        Put(hdr, 340, Math.Max(1, h.NumCopies));
        Put(hdr, 344, 0);
        Put(hdr, 352, h.PageWidthPoints);
        Put(hdr, 356, h.PageHeightPoints);
        Put(hdr, 372, h.Width);
        Put(hdr, 376, h.Height);
        Put(hdr, 384, h.BitsPerColor);
        Put(hdr, 388, h.BitsPerPixel);
        Put(hdr, 392, h.BytesPerLine);
        Put(hdr, 396, 0); // chunked
        Put(hdr, 400, (int)h.ColorSpace);
        Put(hdr, 420, h.NumColors);
        Put(hdr, 452, h.TotalPageCount);
        Put(hdr, 456, h.CrossFeedTransform < 0 ? -1 : 1);
        Put(hdr, 460, h.FeedTransform < 0 ? -1 : 1);
        Put(hdr, 464, 0); Put(hdr, 468, 0); Put(hdr, 472, h.Width); Put(hdr, 476, h.Height);
        Put(hdr, 480, unchecked((int)0xFFFFFF));
        Put(hdr, 484, h.PrintQuality);
        var name = Encoding.ASCII.GetBytes(h.MediaSizeName.Length > 63 ? h.MediaSizeName[..63] : h.MediaSizeName);
        name.CopyTo(hdr, 1732);
        _stream.Write(hdr);

        int bpp = h.BytesPerPixel;
        int lineBytes = h.BytesPerLine;
        int y = 0;
        var outBuf = new MemoryStream(lineBytes + lineBytes / 64 + 16);
        while (y < h.Height)
        {
            var line = pixels.Slice(y * lineBytes, lineBytes);
            int repeat = 1;
            while (y + repeat < h.Height && repeat < 256 && pixels.Slice((y + repeat) * lineBytes, lineBytes).SequenceEqual(line)) repeat++;
            outBuf.SetLength(0);
            outBuf.WriteByte((byte)(repeat - 1));
            EncodeLine(line, bpp, outBuf);
            outBuf.WriteTo(_stream);
            y += repeat;
        }
    }

    private static void EncodeLine(ReadOnlySpan<byte> line, int bpp, MemoryStream o)
    {
        int pixels = line.Length / bpp;
        int i = 0;
        while (i < pixels)
        {
            // count repeats of pixel i
            int run = 1;
            while (i + run < pixels && run < 128 && line.Slice((i + run) * bpp, bpp).SequenceEqual(line.Slice(i * bpp, bpp))) run++;
            if (run >= 2)
            {
                o.WriteByte((byte)(run - 1));
                o.Write(line.Slice(i * bpp, bpp));
                i += run;
                continue;
            }
            // literal run until the next repeat of length >= 2 (or 128 pixels)
            int lit = 1;
            while (i + lit < pixels && lit < 128)
            {
                if (i + lit + 1 < pixels && line.Slice((i + lit) * bpp, bpp).SequenceEqual(line.Slice((i + lit + 1) * bpp, bpp))) break;
                lit++;
            }
            o.WriteByte((byte)(257 - lit));
            o.Write(line.Slice(i * bpp, lit * bpp));
            i += lit;
        }
    }

    private static void Put(byte[] b, int offset, int value) => BinaryPrimitives.WriteInt32BigEndian(b.AsSpan(offset, 4), value);

    public void Dispose() => _stream.Flush();

    /// <summary>Convenience: header for a 1-bit black page of the given size.</summary>
    public static RasterPageHeader BilevelHeader(int width, int height, int dpiX, int dpiY, int widthPts, int heightPts, string mediaName = "na_letter_8.5x11in") =>
        new()
        {
            Width = width, Height = height, BitsPerColor = 1, BitsPerPixel = 1, BytesPerLine = (width + 7) / 8,
            ColorSpace = RasterColorSpace.K, NumColors = 1, HwResolutionX = dpiX, HwResolutionY = dpiY,
            PageWidthPoints = widthPts, PageHeightPoints = heightPts, NumCopies = 1, TotalPageCount = 1, MediaSizeName = mediaName
        };

    public static RasterPageHeader GrayHeader(int width, int height, int dpiX, int dpiY, int widthPts, int heightPts, string mediaName = "na_letter_8.5x11in") =>
        new()
        {
            Width = width, Height = height, BitsPerColor = 8, BitsPerPixel = 8, BytesPerLine = width,
            ColorSpace = RasterColorSpace.Sw, NumColors = 1, HwResolutionX = dpiX, HwResolutionY = dpiY,
            PageWidthPoints = widthPts, PageHeightPoints = heightPts, NumCopies = 1, TotalPageCount = 1, MediaSizeName = mediaName
        };

    public static RasterPageHeader RgbHeader(int width, int height, int dpiX, int dpiY, int widthPts, int heightPts, string mediaName = "na_letter_8.5x11in") =>
        new()
        {
            Width = width, Height = height, BitsPerColor = 8, BitsPerPixel = 24, BytesPerLine = width * 3,
            ColorSpace = RasterColorSpace.Srgb, NumColors = 3, HwResolutionX = dpiX, HwResolutionY = dpiY,
            PageWidthPoints = widthPts, PageHeightPoints = heightPts, NumCopies = 1, TotalPageCount = 1, MediaSizeName = mediaName
        };
}
