using ImageWriterII.Core.Raster;
using Xunit;

namespace ImageWriterII.Tests;

public class RasterCodecTests
{
    private static byte[] RandomBytes(int n, int seed, int alphabet = 256)
    {
        var rng = new Random(seed);
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)rng.Next(alphabet);
        return b;
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(24)]
    public void PwgRasterRoundTrips(int bpp)
    {
        int w = 301, h = 45;
        var header = bpp switch
        {
            1 => PwgRasterWriter.BilevelHeader(w, h, 144, 144, 612, 792),
            8 => PwgRasterWriter.GrayHeader(w, h, 144, 144, 612, 792),
            _ => PwgRasterWriter.RgbHeader(w, h, 160, 144, 612, 792)
        };
        var pixels = RandomBytes(header.BytesPerLine * h, bpp, alphabet: 3); // small alphabet => many runs
        // make some identical rows to exercise the line-repeat code
        for (int y = 10; y < 20; y++) Array.Copy(pixels, 9 * header.BytesPerLine, pixels, y * header.BytesPerLine, header.BytesPerLine);

        var ms = new MemoryStream();
        using (var wtr = new PwgRasterWriter(ms))
        {
            wtr.WritePage(header, pixels);
            wtr.WritePage(header, pixels);
        }
        ms.Position = 0;

        var rdr = new RasterStreamReader(ms);
        for (int page = 0; page < 2; page++)
        {
            var hdr = rdr.ReadPageHeader();
            Assert.NotNull(hdr);
            Assert.Equal(w, hdr!.Width);
            Assert.Equal(h, hdr.Height);
            Assert.Equal(bpp, hdr.BitsPerPixel);
            Assert.Equal(header.HwResolutionX, hdr.HwResolutionX);
            Assert.Equal(header.HwResolutionY, hdr.HwResolutionY);
            Assert.Equal("na_letter_8.5x11in", hdr.MediaSizeName);
            var dest = new byte[hdr.BytesPerLine * hdr.Height];
            rdr.ReadPagePixels(hdr, dest);
            Assert.Equal(pixels, dest);
        }
        Assert.Null(rdr.ReadPageHeader());
    }

    [Fact]
    public void AppleRasterHeaderIsParsed()
    {
        var ms = new MemoryStream();
        ms.Write("UNIRAST\0"u8);
        ms.Write(new byte[] { 0, 0, 0, 1 }); // one page
        var hdr = new byte[32];
        hdr[0] = 8; hdr[1] = 0; hdr[2] = 1; hdr[3] = 4;
        hdr[12] = 0; hdr[13] = 0; hdr[14] = 0; hdr[15] = 16; // width 16
        hdr[16] = 0; hdr[17] = 0; hdr[18] = 0; hdr[19] = 2;  // height 2
        hdr[20] = 0; hdr[21] = 0; hdr[22] = 0; hdr[23] = 72; // 72 dpi
        ms.Write(hdr);
        // two identical lines of 16 gray pixels: repeat byte 1, then run of 16 x 0x40
        ms.Write(new byte[] { 1, 15, 0x40 });
        ms.Position = 0;

        var rdr = new RasterStreamReader(ms);
        var h = rdr.ReadPageHeader();
        Assert.NotNull(h);
        Assert.True(rdr.IsAppleRaster);
        Assert.Equal(RasterColorSpace.Sw, h!.ColorSpace);
        Assert.Equal(16, h.Width);
        Assert.Equal(2, h.Height);
        Assert.Equal(72, h.HwResolutionX);
        Assert.Equal(4, h.PrintQuality);
        var dest = new byte[32];
        rdr.ReadPagePixels(h, dest);
        Assert.All(dest, b => Assert.Equal(0x40, b));
        Assert.Null(rdr.ReadPageHeader());
    }

    [Fact]
    public void BilevelPagesCopyWithoutDithering()
    {
        var header = PwgRasterWriter.BilevelHeader(20, 3, 72, 72, 612, 792);
        var pixels = new byte[header.BytesPerLine * 3];
        pixels[0] = 0b1010_0000; // row 0: pixels 0 and 2
        pixels[3] = 0b0000_0001; // row 1: pixel 7
        pixels[8] = 0b1111_1111; // row 2 last byte: only pixels 16..19 are inside the width
        var page = RasterPageBuilder.Build(header, pixels, wantColor: false, new HalftoneOptions());
        Assert.True(page.Black.Get(0, 0));
        Assert.False(page.Black.Get(1, 0));
        Assert.True(page.Black.Get(2, 0));
        Assert.True(page.Black.Get(7, 1));
        Assert.True(page.Black.Get(19, 2));
        Assert.Equal(0xF0, page.Black.Row(2)[2]); // padding bits masked off
        Assert.False(page.IsColor);
    }

    [Fact]
    public void GrayPagesAreDitheredWithPlausibleCoverage()
    {
        int w = 256, h = 64;
        var header = PwgRasterWriter.GrayHeader(w, h, 144, 144, 612, 792);
        var pixels = new byte[w * h];
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) pixels[y * w + x] = (byte)(y < 32 ? 0 : 255);
        var page = RasterPageBuilder.Build(header, pixels, false, new HalftoneOptions());
        int top = 0, bottom = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) { if (page.Black.Get(x, y)) { if (y < 32) top++; else bottom++; } }
        Assert.Equal(w * 32, top);
        Assert.Equal(0, bottom);

        // mid gray with gamma 1.0 should be roughly 50% coverage
        Array.Fill(pixels, (byte)128);
        page = RasterPageBuilder.Build(header, pixels, false, new HalftoneOptions { Gamma = 1.0 });
        int dots = 0;
        for (int y = 0; y < h; y++) for (int x = 0; x < w; x++) if (page.Black.Get(x, y)) dots++;
        double coverage = dots / (double)(w * h);
        Assert.InRange(coverage, 0.45, 0.55);
    }

    [Fact]
    public void RgbPagesSeparateIntoInkPlanes()
    {
        int w = 32, h = 8;
        var header = PwgRasterWriter.RgbHeader(w, h, 144, 144, 612, 792);
        var pixels = new byte[w * h * 3];
        for (int i = 0; i < w * h; i++)
        {
            // left half pure yellow, right half pure black
            bool left = (i % w) < 16;
            pixels[i * 3] = (byte)(left ? 255 : 0);
            pixels[i * 3 + 1] = (byte)(left ? 255 : 0);
            pixels[i * 3 + 2] = 0;
        }
        var page = RasterPageBuilder.Build(header, pixels, wantColor: true, new HalftoneOptions());
        Assert.True(page.IsColor);
        for (int y = 0; y < h; y++)
        {
            Assert.True(page.Yellow!.Get(3, y));
            Assert.False(page.Black.Get(3, y));
            Assert.True(page.Black.Get(20, y));
            Assert.False(page.Yellow.Get(20, y));
            Assert.False(page.Cyan!.Get(3, y));
            Assert.False(page.Magenta!.Get(3, y));
        }
    }

    /// <summary>
    /// The raw port on 9100 is unauthenticated, so a page header is attacker-controlled: ~1.8 KB of it must
    /// not be able to make the service allocate a gigabyte. Bad geometry has to be rejected as a bad
    /// document, before anything sizes a buffer from it.
    /// </summary>
    [Theory]
    [InlineData(0, 100, 8, 0, "zero width")]
    [InlineData(100, 0, 8, 0, "zero height")]
    [InlineData(-1, 100, 8, 0, "negative width")]
    [InlineData(2_000_000, 2_000_000, 24, 0, "absurd dimensions")]
    [InlineData(100, 100, 7, 0, "unsupported depth")]
    [InlineData(1000, 100, 24, 10, "stride smaller than the row needs")]
    [InlineData(40_000, 40_000, 32, 0, "page over the size cap")]
    public void BadPageGeometryIsRejected(int width, int height, int bitsPerPixel, int bytesPerLine, string because)
    {
        var header = new RasterPageHeader
        {
            Width = width,
            Height = height,
            BitsPerPixel = bitsPerPixel,
            BitsPerColor = bitsPerPixel,
            BytesPerLine = bytesPerLine > 0 ? bytesPerLine : Math.Max(1, (width * bitsPerPixel + 7) / 8),
            ColorSpace = RasterColorSpace.Srgb,
            NumColors = 3
        };
        Assert.Throws<InvalidDataException>(() => header.Validated());
        Assert.False(string.IsNullOrEmpty(because));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    [InlineData(24)]
    public void RealPageGeometryIsAccepted(int bpp)
    {
        var header = bpp switch
        {
            1 => PwgRasterWriter.BilevelHeader(1224, 1584, 144, 144, 612, 792),
            8 => PwgRasterWriter.GrayHeader(1224, 1584, 144, 144, 612, 792),
            _ => PwgRasterWriter.RgbHeader(1360, 1584, 160, 144, 612, 792)
        };
        Assert.Same(header, header.Validated());
    }

    /// <summary>A hostile header must not survive the parser either, whatever the transport.</summary>
    [Fact]
    public void OversizedHeaderFromTheWireAbortsTheJob()
    {
        var header = PwgRasterWriter.GrayHeader(64, 64, 144, 144, 612, 792);
        var ms = new MemoryStream();
        using (var wtr = new PwgRasterWriter(ms)) wtr.WritePage(header, new byte[header.BytesPerLine * 64]);
        var bytes = ms.ToArray();

        // Overwrite the width and height fields of the page header (PWG header starts after the 4-byte sync word).
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + 372, 4), 1_000_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(4 + 376, 4), 1_000_000);

        var rdr = new RasterStreamReader(new MemoryStream(bytes));
        Assert.Throws<InvalidDataException>(() => rdr.ReadPageHeader());
    }

    // ---------------------------------------------------------------- Apple Raster (URF)

    /// <summary>Builds a one-page URF stream: "UNIRAST\0" + page count, a 32-byte page header, then lines.</summary>
    private static byte[] BuildUrf(int width, int height, int bitsPerPixel, int dpi, byte[] lineData)
    {
        var ms = new MemoryStream();
        ms.Write("UNIRAST\0"u8);
        var be = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(be, 1);
        ms.Write(be);                                   // one page

        var hdr = new byte[32];
        hdr[0] = (byte)bitsPerPixel;
        hdr[1] = 0;                                     // colorspace 0 => sGray
        hdr[3] = 4;                                     // quality
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(12, 4), (uint)width);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(16, 4), (uint)height);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(20, 4), (uint)dpi);
        ms.Write(hdr);

        ms.Write(lineData);
        return ms.ToArray();
    }

    /// <summary>
    /// In Apple Raster 0x80 means "fill the rest of the line with white and stop"; no pixel byte follows it.
    /// PWG raster gives the identical byte the opposite meaning (a 129-pixel literal run), and reading URF the
    /// PWG way swallows 129 bytes that were never sent - which desynchronised and then killed every real
    /// AirPrint job from iOS and macOS. Regression for that.
    /// </summary>
    [Fact]
    public void AppleRasterFillToEndOfLineCode0x80()
    {
        const int width = 16, height = 4;
        var lines = new List<byte>();
        for (int y = 0; y < height; y++)
        {
            lines.Add(0);            // line repeat count 0 => this line appears once
            lines.Add(3);            // repeat the next pixel 4 times
            lines.Add((byte)(0x10 * (y + 1)));
            lines.Add(0x80);         // ... then white to the end of the line, and NOTHING follows
        }
        var urf = BuildUrf(width, height, 8, 144, lines.ToArray());

        var rdr = new RasterStreamReader(new MemoryStream(urf));
        var h = rdr.ReadPageHeader();
        Assert.NotNull(h);
        Assert.True(rdr.IsAppleRaster);
        Assert.Equal(width, h!.Width);
        Assert.Equal(height, h.Height);
        Assert.Equal(width, h.BytesPerLine);

        var dest = new byte[h.BytesPerLine * h.Height];
        rdr.ReadPagePixels(h, dest);                      // used to throw EndOfStreamException

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < 4; x++) Assert.Equal((byte)(0x10 * (y + 1)), dest[y * width + x]);
            for (int x = 4; x < width; x++) Assert.Equal(0xFF, dest[y * width + x]);
        }
    }

    /// <summary>The same byte in a PWG raster stream still means a 129-pixel literal run, not a line fill.</summary>
    [Fact]
    public void PwgRasterCode0x80IsStillA129PixelLiteral()
    {
        const int width = 200, height = 1;
        var body = new List<byte> { 0, 0x80 };            // line repeat 0, then 129 literal pixels
        for (int i = 0; i < 129; i++) body.Add((byte)i);
        body.Add(184);                                    // 257-184 = 73 more literal pixels fills the row
        for (int i = 0; i < 71; i++) body.Add(0xAA);

        var ms = new MemoryStream();
        ms.Write("RaS2"u8);
        var hdr = new byte[1796];
        void Put(int off, uint v) => System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(hdr.AsSpan(off, 4), v);
        Put(372, width); Put(376, height); Put(384, 8); Put(388, 8); Put(392, width);
        Put(400, 18); Put(420, 1); Put(276, 144); Put(280, 144); Put(352, 612); Put(356, 792);
        ms.Write(hdr);
        ms.Write(body.ToArray());

        var rdr = new RasterStreamReader(new MemoryStream(ms.ToArray()));
        var h = rdr.ReadPageHeader();
        Assert.NotNull(h);
        Assert.False(rdr.IsAppleRaster);
        var dest = new byte[h!.BytesPerLine * h.Height];
        rdr.ReadPagePixels(h, dest);

        for (int i = 0; i < 129; i++) Assert.Equal((byte)i, dest[i]);
        Assert.Equal(0xAA, dest[129]);
    }

    /// <summary>A line-repeat count in URF repeats the decoded line, so a mostly-blank page stays tiny.</summary>
    [Fact]
    public void AppleRasterLineRepeatFillsTheWholePage()
    {
        const int width = 8, height = 10;
        // one record: repeat this line 10 times; the line is entirely white via the 0x80 fill
        var lines = new byte[] { (byte)(height - 1), 0x80 };
        var urf = BuildUrf(width, height, 8, 144, lines);

        var rdr = new RasterStreamReader(new MemoryStream(urf));
        var h = rdr.ReadPageHeader()!;
        var dest = new byte[h.BytesPerLine * h.Height];
        rdr.ReadPagePixels(h, dest);

        Assert.All(dest.ToArray(), b => Assert.Equal(0xFF, b));
    }
}
