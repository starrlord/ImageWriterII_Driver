using ImageWriterII.Core.Encoder;
using ImageWriterII.Core.Printer;
using ImageWriterII.Core.Raster;
using ImageWriterII.Core.Text;
using Xunit;

namespace ImageWriterII.Tests;

/// <summary>
/// Auto tear-off for tractor paper. Loading fresh paper and tearing a sheet off both leave the top of the
/// next sheet at the tear edge, which puts the print head that far *into* the page. Each job therefore winds
/// the paper back before claiming top of form and runs it forward again at the end, so the perforation ends
/// up at the tear edge and the next job still starts at the top of a sheet. Being symmetric, it needs no
/// state between jobs and nothing to reset when the service restarts.
/// </summary>
public class TearOffTests
{
    private const double TearOff = 4.0;
    private static int Units(double inches) => (int)Math.Round(inches * Iw2.FeedUnitsPerInch);

    private static byte[] EncodeJob(double tearOffInches, int pages = 1)
    {
        var ms = new MemoryStream();
        var page = new RasterPage
        {
            DpiX = 144,
            DpiY = 144,
            Black = new BitPlane(1224, 288),
            MediaWidthPoints = 612,
            MediaHeightPoints = 792
        };
        page.Black.Set(100, 10);
        var enc = new Iw2JobEncoder(ms, new Iw2EncoderOptions { TearOffInches = tearOffInches });
        enc.BeginJob();
        for (int i = 0; i < pages; i++) enc.EncodePage(page);
        enc.EndJob();
        return ms.ToArray();
    }

    /// <summary>Sums the ESC T nn distances that follow an ESC r, up to the ESC f that ends the reverse.</summary>
    private static int ReverseUnitsAfter(byte[] s, int from = 0)
    {
        int i = IndexOfEsc(s, (byte)'r', from);
        if (i < 0) return 0;
        int total = 0;
        for (int p = i + 2; p + 3 < s.Length; )
        {
            if (s[p] == Iw2.ESC && s[p + 1] == (byte)'f') break;
            if (s[p] == Iw2.ESC && s[p + 1] == (byte)'T')
            {
                total += (s[p + 2] - '0') * 10 + (s[p + 3] - '0');
                p += 4;
                continue;
            }
            p++;
        }
        return total;
    }

    private static int IndexOfEsc(byte[] s, byte code, int from = 0)
    {
        for (int i = from; i < s.Length - 1; i++)
            if (s[i] == Iw2.ESC && s[i + 1] == code) return i;
        return -1;
    }

    [Fact]
    public void DisabledByDefaultSoExistingSetupsAreUnchanged()
    {
        Assert.Equal(0.0, new Iw2EncoderOptions().TearOffInches);
        var bytes = EncodeJob(0);
        // No reverse feed anywhere: the only ESC r in a mono job would be a tear-off wind back.
        Assert.Equal(-1, IndexOfEsc(bytes, (byte)'r'));
    }

    /// <summary>
    /// The scenario that matters: fresh paper loaded with its top edge at the tear edge. The head is 4" into
    /// the sheet, so the job must wind back 4" BEFORE ESC v, or top of form lands a quarter of the way down.
    /// </summary>
    [Fact]
    public void WindsBackBeforeClaimingTopOfForm()
    {
        var bytes = EncodeJob(TearOff);

        int reverse = IndexOfEsc(bytes, (byte)'r');
        int topOfForm = IndexOfEsc(bytes, (byte)'v');
        Assert.True(reverse >= 0, "expected a reverse feed at job start");
        Assert.True(topOfForm >= 0, "expected ESC v");
        Assert.True(reverse < topOfForm, "the wind back must happen before top of form is claimed");
        Assert.Equal(Units(TearOff), ReverseUnitsAfter(bytes));
    }

    [Fact]
    public void RunsThePerforationOutToTheTearEdgeAtTheEnd()
    {
        var bytes = EncodeJob(TearOff);

        // After the final form feed the paper is run forward by the same distance it was wound back.
        int lastFf = Array.LastIndexOf(bytes, Iw2.FF);
        Assert.True(lastFf > 0);
        int forward = 0;
        for (int p = lastFf; p + 3 < bytes.Length; p++)
            if (bytes[p] == Iw2.ESC && bytes[p + 1] == (byte)'T')
                forward += (bytes[p + 2] - '0') * 10 + (bytes[p + 3] - '0');
        Assert.Equal(Units(TearOff), forward);
    }

    /// <summary>Wind back and run out must cancel exactly, or successive jobs would creep down the paper.</summary>
    [Fact]
    public void TheTwoMovementsAreEqualAndOpposite()
    {
        var bytes = EncodeJob(TearOff);
        int lastFf = Array.LastIndexOf(bytes, Iw2.FF);
        int back = ReverseUnitsAfter(bytes);
        int forward = 0;
        for (int p = lastFf; p + 3 < bytes.Length; p++)
            if (bytes[p] == Iw2.ESC && bytes[p + 1] == (byte)'T')
                forward += (bytes[p + 2] - '0') * 10 + (bytes[p + 3] - '0');
        Assert.Equal(back, forward);
    }

    /// <summary>One wind back per job, not one per page, however many pages the job has.</summary>
    [Fact]
    public void AMultiPageJobWindsBackOnlyOnce()
    {
        var bytes = EncodeJob(TearOff, pages: 3);
        int count = 0;
        for (int i = 0; i < bytes.Length - 1; i++)
            if (bytes[i] == Iw2.ESC && bytes[i + 1] == (byte)'r') count++;
        Assert.Equal(1, count);
    }

    [Fact]
    public void DistanceIsClampedSoThePaperCannotLeaveTheSensor()
    {
        var bytes = EncodeJob(Iw2.MaxTearOffInches + 50);
        Assert.Equal(Units(Iw2.MaxTearOffInches), ReverseUnitsAfter(bytes));
    }

    [Fact]
    public void TextJobsGetTheSameTreatment()
    {
        var buf = new System.Buffers.ArrayBufferWriter<byte>();
        TextJobEncoder.Encode("hello"u8, buf, new TextJobOptions { TearOffInches = TearOff });
        var bytes = buf.WrittenSpan.ToArray();

        Assert.Equal(Units(TearOff), ReverseUnitsAfter(bytes));
        int lastFf = Array.LastIndexOf(bytes, Iw2.FF);
        int forward = 0;
        for (int p = lastFf; p + 3 < bytes.Length; p++)
            if (bytes[p] == Iw2.ESC && bytes[p + 1] == (byte)'T')
                forward += (bytes[p + 2] - '0') * 10 + (bytes[p + 3] - '0');
        Assert.Equal(Units(TearOff), forward);
    }

    /// <summary>
    /// ESC K must go out even when the service is configured for a black ribbon. It drives the ribbon shift,
    /// so skipping it leaves a four-colour ribbon parked on whatever band it last used; a printer sitting on
    /// yellow would print an entire job in near-invisible yellow and look like a faded ribbon.
    /// </summary>
    [Fact]
    public void BlackRibbonStillSelectsTheBlackBand()
    {
        var ms = new MemoryStream();
        var page = new RasterPage
        {
            DpiX = 144, DpiY = 144, Black = new BitPlane(1224, 144),
            MediaWidthPoints = 612, MediaHeightPoints = 792
        };
        page.Black.Set(50, 5);
        var enc = new Iw2JobEncoder(ms, new Iw2EncoderOptions { ColorRibbon = false });
        enc.BeginJob();
        enc.EncodePage(page);
        enc.EndJob();
        var bytes = ms.ToArray();

        int k = IndexOfEsc(bytes, (byte)'K');
        Assert.True(k >= 0, "a mono job must still select the black band");
        Assert.Equal((byte)'0', bytes[k + 2]);          // ESC K 0 is black
        int firstGraphics = IndexOfEsc(bytes, (byte)'G');
        if (firstGraphics < 0) firstGraphics = IndexOfEsc(bytes, (byte)'g');
        Assert.True(k < firstGraphics, "the band must be selected before any dots are printed");
    }

    /// <summary>ESC T only carries two digits, so 4" (576 units) has to go out as several feeds.</summary>
    [Fact]
    public void LongDistancesAreSplitAcrossSeveralFeedCommands()
    {
        var bytes = EncodeJob(TearOff);
        int reverse = IndexOfEsc(bytes, (byte)'r');
        int feeds = 0;
        for (int p = reverse + 2; p + 3 < bytes.Length; p++)
        {
            if (bytes[p] == Iw2.ESC && bytes[p + 1] == (byte)'f') break;
            if (bytes[p] == Iw2.ESC && bytes[p + 1] == (byte)'T') feeds++;
        }
        Assert.True(feeds >= Units(TearOff) / 99, $"576 units needs at least 6 feeds, saw {feeds}");
        // and the direction is restored afterwards
        Assert.True(IndexOfEsc(bytes, (byte)'f', reverse) > reverse, "reverse feed must be turned off again");
    }
}
