using ImageWriterII.Core.Encoder;
using ImageWriterII.Core.Raster;
using ImageWriterII.Core.Simulation;
using Xunit;

namespace ImageWriterII.Tests;

/// <summary>
/// Encode synthetic pages and feed the escape stream through the simulator; every dot must land
/// exactly where the raster had it, for each resolution, colour mode and compression path.
/// </summary>
public class EncoderRoundTripTests
{
    private const double LeftOffset = 0.25;

    private static BitPlane RandomPlane(int w, int h, int seed, double density, bool withRuns)
    {
        var rng = new Random(seed);
        var p = new BitPlane(w, h);
        for (int y = 0; y < h; y++)
        {
            if (withRuns && y % 37 == 0) continue; // blank rows
            if (withRuns && y % 53 == 1)
            {
                for (int x = 40; x < w - 40; x++) p.Set(x, y); // long solid run
                continue;
            }
            for (int x = 0; x < w; x++)
                if (rng.NextDouble() < density) p.Set(x, y);
        }
        return p;
    }

    private static byte[] Encode(RasterPage page, Iw2EncoderOptions o)
    {
        var ms = new MemoryStream();
        var enc = new Iw2JobEncoder(ms, o);
        enc.BeginJob();
        enc.EncodePage(page);
        enc.EndJob();
        return ms.ToArray();
    }

    private static void AssertPlaneMatches(BitPlane expected, BitPlane simulated, int dpiX, int simDpiX, int colOffset, int maxColumns)
    {
        // simulated canvas: pixel x_sim = round((LeftOffset + col/dpiX) * simDpiX) for printer column col = x - colOffset
        int mismatches = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                int col = x - colOffset;
                bool printable = col >= 0 && col < maxColumns;
                bool exp = expected.Get(x, y) && printable;
                int sx = (int)Math.Round((LeftOffset + (double)col / dpiX) * simDpiX);
                bool got = sx >= 0 && sx < simulated.Width && simulated.Get(sx, y);
                if (exp != got) mismatches++;
            }
        }
        Assert.Equal(0, mismatches);
    }

    [Theory]
    [InlineData(72, 72, false)]
    [InlineData(144, 144, false)]
    [InlineData(144, 144, true)]
    [InlineData(160, 144, false)]
    [InlineData(80, 72, false)]
    [InlineData(120, 144, false)]
    public void MonochromePageRoundTrips(int dpiX, int dpiY, bool bidirectional)
    {
        int w = (int)(8.5 * dpiX), h = (int)(3.0 * dpiY); // three inches of a letter-width page
        var plane = RandomPlane(w, h, seed: dpiX * 7 + dpiY, density: 0.15, withRuns: true);
        var page = new RasterPage { DpiX = dpiX, DpiY = dpiY, Black = plane, MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var o = new Iw2EncoderOptions { Bidirectional = bidirectional, LeftEdgeOffsetInches = LeftOffset };
        var bytes = Encode(page, o);

        var sim = new Iw2Simulator(new SimulatorOptions { DpiX = dpiX, DpiY = dpiY, LeftEdgeOffsetInches = LeftOffset, PageHeightInches = 11 });
        var pages = sim.Run(bytes);
        Assert.Single(pages);
        Assert.Empty(pages[0].Warnings);
        int colOffset = (int)Math.Round(LeftOffset * dpiX);
        AssertPlaneMatches(plane, pages[0].Black, dpiX, dpiX, colOffset, dpiX * 8);
    }

    [Theory]
    [InlineData(ColorStrategy.PerBand)]
    [InlineData(ColorStrategy.PerPage)]
    public void ColorPageRoundTrips(ColorStrategy strategy)
    {
        int dpi = 144;
        int w = (int)(8.5 * dpi), h = (int)(2.0 * dpi);
        var y = RandomPlane(w, h, 1, 0.05, true);
        var m = RandomPlane(w, h, 2, 0.05, false);
        var c = RandomPlane(w, h, 3, 0.05, true);
        var k = RandomPlane(w, h, 4, 0.05, false);
        var page = new RasterPage { DpiX = dpi, DpiY = dpi, Black = k, Yellow = y, Magenta = m, Cyan = c, MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var o = new Iw2EncoderOptions { ColorRibbon = true, ColorStrategy = strategy, LeftEdgeOffsetInches = LeftOffset };
        var bytes = Encode(page, o);

        var sim = new Iw2Simulator(new SimulatorOptions { DpiX = dpi, DpiY = dpi, LeftEdgeOffsetInches = LeftOffset });
        var pages = sim.Run(bytes);
        Assert.Single(pages);
        Assert.Empty(pages[0].Warnings);
        int colOffset = (int)Math.Round(LeftOffset * dpi);
        AssertPlaneMatches(y, pages[0].Yellow, dpi, dpi, colOffset, dpi * 8);
        AssertPlaneMatches(m, pages[0].Magenta, dpi, dpi, colOffset, dpi * 8);
        AssertPlaneMatches(c, pages[0].Cyan, dpi, dpi, colOffset, dpi * 8);
        AssertPlaneMatches(k, pages[0].Black, dpi, dpi, colOffset, dpi * 8);
    }

    [Fact]
    public void ColorPageWithoutColorRibbonPrintsEverythingInBlack()
    {
        int dpi = 72;
        int w = 8 * dpi + 36, h = dpi;
        var y = RandomPlane(w, h, 11, 0.1, false);
        var k = RandomPlane(w, h, 12, 0.1, false);
        var empty = new BitPlane(w, h);
        var page = new RasterPage { DpiX = dpi, DpiY = dpi, Black = k, Yellow = y, Magenta = empty, Cyan = empty, MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var bytes = Encode(page, new Iw2EncoderOptions { ColorRibbon = false, LeftEdgeOffsetInches = LeftOffset });
        var sim = new Iw2Simulator(new SimulatorOptions { DpiX = dpi, DpiY = dpi, LeftEdgeOffsetInches = LeftOffset });
        var pages = sim.Run(bytes);
        var expected = new BitPlane(w, h);
        for (int i = 0; i < expected.Data.Length; i++) expected.Data[i] = (byte)(y.Data[i] | k.Data[i]);
        int colOffset = (int)Math.Round(LeftOffset * dpi);
        AssertPlaneMatches(expected, pages[0].Black, dpi, dpi, colOffset, dpi * 8);
        Assert.True(pages[0].Yellow.IsBlank());
    }

    [Fact]
    public void BlankPageStillEjects()
    {
        var page = new RasterPage { DpiX = 144, DpiY = 144, Black = new BitPlane(1224, 1584), MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var bytes = Encode(page, new Iw2EncoderOptions());
        Assert.Contains((byte)0x0C, bytes);
        var sim = new Iw2Simulator();
        var pages = sim.Run(bytes);
        Assert.Single(pages);
        Assert.Equal(0, pages[0].Dots);
    }

    [Fact]
    public void MultiPageJobProducesOnePageEach()
    {
        var ms = new MemoryStream();
        var enc = new Iw2JobEncoder(ms, new Iw2EncoderOptions());
        enc.BeginJob();
        for (int p = 0; p < 3; p++)
        {
            var plane = new BitPlane(1224, 1584);
            for (int x = 100; x < 200; x++) plane.Set(x, 100 + p * 300);
            enc.EncodePage(new RasterPage { DpiX = 144, DpiY = 144, Black = plane, MediaWidthPoints = 612, MediaHeightPoints = 792 });
        }
        enc.EndJob();
        var pages = new Iw2Simulator().Run(ms.ToArray());
        Assert.Equal(3, pages.Count);
        Assert.All(pages, pg => Assert.Equal(100, pg.Dots));
    }

    [Fact]
    public void CompressionIsUsedForRunsAndSkips()
    {
        int dpi = 144;
        var plane = new BitPlane(1224, 16);
        for (int x = 400; x < 1000; x++) for (int y = 0; y < 16; y++) plane.Set(x, y); // solid block after a gap
        var page = new RasterPage { DpiX = dpi, DpiY = dpi, Black = plane, MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var bytes = Encode(page, new Iw2EncoderOptions());
        string s = System.Text.Encoding.Latin1.GetString(bytes);
        Assert.Contains("F0364", s);   // 400 - 36 columns of margin: jump over the blank stretch
        Assert.Contains("V0600ÿ", s); // 600 identical columns of 0xFF
        Assert.DoesNotContain("G", s);   // no literal data needed at all
        Assert.True(bytes.Length < 200);
    }

    [Fact]
    public void FeedsAccumulateAcrossBlankBandsWithoutExceedingEscTRange()
    {
        var plane = new BitPlane(1224, 1584);
        plane.Set(500, 1500); // single dot near the bottom of a letter page
        var page = new RasterPage { DpiX = 144, DpiY = 144, Black = plane, MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var bytes = Encode(page, new Iw2EncoderOptions());
        var sim = new Iw2Simulator();
        var pages = sim.Run(bytes);
        Assert.Single(pages);
        Assert.Equal(1, pages[0].Dots);
        Assert.True(pages[0].Black.Get((int)Math.Round((0.25 + (500 - 36) / 144.0) * 144), 1500));
    }

    /// <summary>
    /// PerPage colour rewinds the paper between inks. The rewind must match what was actually fed, not what
    /// the encoder intended: feed for the last band and for every trailing blank band is still pending and
    /// was never emitted, so counting it would drag magenta, cyan and black progressively back past the top
    /// of form. A page with a blank lower half makes that remainder large enough to be unmistakable.
    /// </summary>
    [Fact]
    public void ColorPerPageWithBlankLowerHalfKeepsInksRegistered()
    {
        int dpi = 144;
        int w = (int)(8.5 * dpi), h = (int)(6.0 * dpi);
        int inked = (int)(1.5 * dpi);   // only the top 1.5" carries dots; the rest is blank bands

        BitPlane TopOnly(int seed)
        {
            var plane = new BitPlane(w, h);
            var rng = new Random(seed);
            for (int y = 0; y < inked; y++)
                for (int x = 0; x < w; x++)
                    if (rng.NextDouble() < 0.08) plane.Set(x, y);
            return plane;
        }

        var y0 = TopOnly(11);
        var m0 = TopOnly(12);
        var c0 = TopOnly(13);
        var k0 = TopOnly(14);
        var page = new RasterPage { DpiX = dpi, DpiY = dpi, Black = k0, Yellow = y0, Magenta = m0, Cyan = c0, MediaWidthPoints = 612, MediaHeightPoints = 792 };
        var o = new Iw2EncoderOptions { ColorRibbon = true, ColorStrategy = ColorStrategy.PerPage, LeftEdgeOffsetInches = LeftOffset };
        var bytes = Encode(page, o);

        var sim = new Iw2Simulator(new SimulatorOptions { DpiX = dpi, DpiY = dpi, LeftEdgeOffsetInches = LeftOffset, PageHeightInches = 11 });
        var pages = sim.Run(bytes);

        // The old code rewound by the intended feed and asked the printer to reverse past top of form.
        Assert.Equal(0, sim.OverReversedUnits);
        Assert.Single(pages);
        Assert.Empty(pages[0].Warnings);
        int colOffset = (int)Math.Round(LeftOffset * dpi);
        AssertPlaneMatches(y0, pages[0].Yellow, dpi, dpi, colOffset, dpi * 8);
        AssertPlaneMatches(m0, pages[0].Magenta, dpi, dpi, colOffset, dpi * 8);
        AssertPlaneMatches(c0, pages[0].Cyan, dpi, dpi, colOffset, dpi * 8);
        AssertPlaneMatches(k0, pages[0].Black, dpi, dpi, colOffset, dpi * 8);
    }

    /// <summary>An all-blank ink plane must not make the rewind go backwards past the top of form either.</summary>
    [Fact]
    public void ColorPerPageWithAnEmptyInkPlaneDoesNotOverReverse()
    {
        int dpi = 144;
        int w = (int)(8.5 * dpi), h = (int)(3.0 * dpi);
        var k0 = RandomPlane(w, h, 21, 0.10, false);
        var page = new RasterPage
        {
            DpiX = dpi,
            DpiY = dpi,
            Black = k0,
            Yellow = new BitPlane(w, h),      // empty
            Magenta = new BitPlane(w, h),     // empty
            Cyan = new BitPlane(w, h),        // empty
            MediaWidthPoints = 612,
            MediaHeightPoints = 792
        };
        var o = new Iw2EncoderOptions { ColorRibbon = true, ColorStrategy = ColorStrategy.PerPage, LeftEdgeOffsetInches = LeftOffset };

        var sim = new Iw2Simulator(new SimulatorOptions { DpiX = dpi, DpiY = dpi, LeftEdgeOffsetInches = LeftOffset, PageHeightInches = 11 });
        var pages = sim.Run(Encode(page, o));

        Assert.Equal(0, sim.OverReversedUnits);
        Assert.Single(pages);
        AssertPlaneMatches(k0, pages[0].Black, dpi, dpi, (int)Math.Round(LeftOffset * dpi), dpi * 8);
    }
}
