using System.Buffers.Binary;
using System.Text;

namespace ImageWriterII.Core.Raster;

/// <summary>Colour spaces as numbered in CUPS/PWG raster headers (cupsColorSpace).</summary>
public enum RasterColorSpace
{
    W = 0,
    Rgb = 1,
    Rgba = 2,
    K = 3,
    Cmy = 4,
    Ymc = 5,
    Cmyk = 6,
    Sw = 18,
    Srgb = 19,
    AdobeRgb = 20,
    Device1 = 48,
    Unknown = -1
}

/// <summary>Per-page header of a PWG raster (PWG 5102.4) or Apple raster (URF) stream, normalised.</summary>
public sealed class RasterPageHeader
{
    public int Width { get; init; }
    public int Height { get; init; }
    public int BitsPerColor { get; init; }
    public int BitsPerPixel { get; init; }
    public int BytesPerLine { get; init; }
    public RasterColorSpace ColorSpace { get; init; }
    public int NumColors { get; init; }
    public int HwResolutionX { get; init; }
    public int HwResolutionY { get; init; }
    public int PageWidthPoints { get; init; }
    public int PageHeightPoints { get; init; }
    public int NumCopies { get; init; }
    public int TotalPageCount { get; init; }
    public int CrossFeedTransform { get; init; } = 1;
    public int FeedTransform { get; init; } = 1;
    public int PrintQuality { get; init; }
    public string MediaSizeName { get; init; } = "";
    public string MediaType { get; init; } = "";
    public bool Compressed { get; init; } = true;

    /// <summary>Bytes per "pixel unit" for the run-length coder: max(1, BitsPerPixel/8).</summary>
    public int BytesPerPixel => Math.Max(1, BitsPerPixel / 8);

    public override string ToString() =>
        $"{Width}x{Height} px, {HwResolutionX}x{HwResolutionY} dpi, {ColorSpace}/{BitsPerPixel} bpp, media {MediaSizeName} {PageWidthPoints}x{PageHeightPoints} pt, copies {NumCopies}, quality {PrintQuality}";
}

/// <summary>
/// Streaming decoder for PWG raster ("RaS2", big-endian, PWG-compressed) and Apple raster ("UNIRAST") documents,
/// plus CUPS raster v2/v3 in either byte order for testing convenience.
/// </summary>
public sealed class RasterStreamReader
{
    private const int CupsHeaderSize = 1796;

    private readonly Stream _stream;
    private bool _syncRead;
    private bool _bigEndian = true;
    private bool _apple;
    private bool _compressedStream = true;
    private int _applePagesRemaining;

    public RasterStreamReader(Stream stream) => _stream = stream;

    public bool IsAppleRaster => _apple;

    /// <summary>Reads the next page header, or returns null at end of stream.</summary>
    public RasterPageHeader? ReadPageHeader()
    {
        if (!_syncRead)
        {
            if (!ReadSync()) return null;
            _syncRead = true;
        }

        if (_apple)
        {
            if (_applePagesRemaining == 0) return null;
            var hdr = new byte[32];
            if (!TryReadExactly(hdr)) return null;
            _applePagesRemaining--;
            return ParseAppleHeader(hdr);
        }
        else
        {
            var hdr = new byte[CupsHeaderSize];
            if (!TryReadExactly(hdr)) return null;
            return ParseCupsHeader(hdr);
        }
    }

    /// <summary>Decodes the whole page into <paramref name="dest"/> (Height * BytesPerLine bytes).</summary>
    public void ReadPagePixels(RasterPageHeader h, Span<byte> dest)
    {
        int lineBytes = h.BytesPerLine;
        if (dest.Length < lineBytes * h.Height) throw new ArgumentException("Destination too small.", nameof(dest));

        if (!h.Compressed)
        {
            if (!TryReadExactly(dest[..(lineBytes * h.Height)])) throw new EndOfStreamException("Truncated raster data.");
            return;
        }

        int bpp = h.BytesPerPixel;
        int y = 0;
        var pixel = new byte[bpp];
        while (y < h.Height)
        {
            int repeat = ReadByteOrThrow() + 1;
            var line = dest.Slice(y * lineBytes, lineBytes);
            int pos = 0;
            while (pos < lineBytes)
            {
                int code = ReadByteOrThrow();
                if (code > 128)
                {
                    // literal run of (257 - code) pixels
                    int count = (257 - code) * bpp;
                    if (count > lineBytes - pos) count = lineBytes - pos;
                    if (!TryReadExactly(line.Slice(pos, count))) throw new EndOfStreamException("Truncated raster data.");
                    pos += count;
                }
                else
                {
                    // repeat next pixel (code + 1) times; code 128 is a 129-literal in CUPS, keep that behaviour
                    int count = code == 128 ? 129 : code + 1;
                    if (code == 128)
                    {
                        int bytes = Math.Min(count * bpp, lineBytes - pos);
                        if (!TryReadExactly(line.Slice(pos, bytes))) throw new EndOfStreamException("Truncated raster data.");
                        pos += bytes;
                        continue;
                    }
                    if (!TryReadExactly(pixel)) throw new EndOfStreamException("Truncated raster data.");
                    for (int i = 0; i < count && pos < lineBytes; i++)
                    {
                        for (int b = 0; b < bpp && pos < lineBytes; b++) line[pos++] = pixel[b];
                    }
                }
            }
            y++;
            for (int r = 1; r < repeat && y < h.Height; r++, y++)
                line.CopyTo(dest.Slice(y * lineBytes, lineBytes));
        }
    }

    private bool ReadSync()
    {
        var sync = new byte[4];
        if (!TryReadExactly(sync)) return false;
        string s = Encoding.ASCII.GetString(sync);
        switch (s)
        {
            case "RaS2": _bigEndian = true; _compressedStream = true; return true;
            case "2SaR": _bigEndian = false; _compressedStream = true; return true;
            case "RaS3": _bigEndian = true; _compressedStream = false; return true;
            case "3SaR": _bigEndian = false; _compressedStream = false; return true;
            case "RaSt": _bigEndian = true; _compressedStream = false; return true;
            case "tSaR": _bigEndian = false; _compressedStream = false; return true;
            case "UNIR":
                {
                    var rest = new byte[8]; // "AST\0" + page count
                    if (!TryReadExactly(rest)) return false;
                    if (rest[0] != (byte)'A' || rest[1] != (byte)'S' || rest[2] != (byte)'T') return false;
                    _apple = true;
                    _applePagesRemaining = (int)BinaryPrimitives.ReadUInt32BigEndian(rest.AsSpan(4));
                    return true;
                }
            default:
                throw new InvalidDataException($"Not a PWG/URF raster stream (sync word {s}).");
        }
    }

    private uint U32(ReadOnlySpan<byte> b, int offset) =>
        _bigEndian ? BinaryPrimitives.ReadUInt32BigEndian(b.Slice(offset, 4)) : BinaryPrimitives.ReadUInt32LittleEndian(b.Slice(offset, 4));

    private static string Str(ReadOnlySpan<byte> b, int offset, int len)
    {
        var s = b.Slice(offset, len);
        int nul = s.IndexOf((byte)0);
        if (nul >= 0) s = s[..nul];
        return Encoding.ASCII.GetString(s);
    }

    private RasterPageHeader ParseCupsHeader(ReadOnlySpan<byte> b)
    {
        int width = (int)U32(b, 372);
        int height = (int)U32(b, 376);
        int bitsPerColor = (int)U32(b, 384);
        int bitsPerPixel = (int)U32(b, 388);
        int bytesPerLine = (int)U32(b, 392);
        int colorSpace = (int)U32(b, 400);
        int numColors = (int)U32(b, 420);
        if (bytesPerLine == 0) bytesPerLine = (width * bitsPerPixel + 7) / 8;
        return new RasterPageHeader
        {
            Width = width,
            Height = height,
            BitsPerColor = bitsPerColor,
            BitsPerPixel = bitsPerPixel,
            BytesPerLine = bytesPerLine,
            ColorSpace = Enum.IsDefined(typeof(RasterColorSpace), colorSpace) ? (RasterColorSpace)colorSpace : RasterColorSpace.Unknown,
            NumColors = numColors == 0 ? 1 : numColors,
            HwResolutionX = (int)U32(b, 276),
            HwResolutionY = (int)U32(b, 280),
            PageWidthPoints = (int)U32(b, 352),
            PageHeightPoints = (int)U32(b, 356),
            NumCopies = (int)U32(b, 340),
            TotalPageCount = (int)U32(b, 452),
            CrossFeedTransform = (int)U32(b, 456) == unchecked((int)0xFFFFFFFF) ? -1 : 1,
            FeedTransform = (int)U32(b, 460) == unchecked((int)0xFFFFFFFF) ? -1 : 1,
            PrintQuality = (int)U32(b, 484),
            MediaSizeName = Str(b, 1732, 64),
            MediaType = Str(b, 128, 64),
            Compressed = _compressedStream
        };
    }

    private RasterPageHeader ParseAppleHeader(ReadOnlySpan<byte> b)
    {
        int bpp = b[0];
        int cs = b[1];
        RasterColorSpace space = cs switch
        {
            0 => RasterColorSpace.Sw,
            1 => RasterColorSpace.Srgb,
            3 => RasterColorSpace.AdobeRgb,
            4 => RasterColorSpace.W,
            5 => RasterColorSpace.Rgb,
            6 => RasterColorSpace.Cmyk,
            _ => RasterColorSpace.Unknown
        };
        int numColors = space switch { RasterColorSpace.Cmyk => 4, RasterColorSpace.Sw or RasterColorSpace.W => 1, _ => 3 };
        int width = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(12, 4));
        int height = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(16, 4));
        int dpi = (int)BinaryPrimitives.ReadUInt32BigEndian(b.Slice(20, 4));
        return new RasterPageHeader
        {
            Width = width,
            Height = height,
            BitsPerPixel = bpp,
            BitsPerColor = numColors > 0 ? bpp / numColors : bpp,
            BytesPerLine = width * bpp / 8,
            ColorSpace = space,
            NumColors = numColors,
            HwResolutionX = dpi,
            HwResolutionY = dpi,
            PageWidthPoints = dpi > 0 ? width * 72 / dpi : 0,
            PageHeightPoints = dpi > 0 ? height * 72 / dpi : 0,
            NumCopies = 1,
            TotalPageCount = _applePagesRemaining + 1,
            PrintQuality = b[3],
            Compressed = true
        };
    }

    private int ReadByteOrThrow()
    {
        int b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException("Truncated raster data.");
        return b;
    }

    private bool TryReadExactly(Span<byte> buffer)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = _stream.Read(buffer[total..]);
            if (n <= 0) return false;
            total += n;
        }
        return true;
    }
}
