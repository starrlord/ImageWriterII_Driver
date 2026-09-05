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
}
