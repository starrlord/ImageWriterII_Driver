using System.Buffers;
using ImageWriterII.Core.Printer;
using ImageWriterII.Core.Simulation;
using ImageWriterII.Core.Text;
using Xunit;

namespace ImageWriterII.Tests;

public class CommandAndTextTests
{
    private static string Latin1(ReadOnlySpan<byte> b) => System.Text.Encoding.Latin1.GetString(b);

    [Fact]
    public void GraphicsCommandsUseFourDigitCounts()
    {
        var buf = new ArrayBufferWriter<byte>();
        var w = new Iw2Writer(buf);
        w.Graphics(new byte[] { 1, 2, 3 }, preferGroups: true);
        w.Graphics(new byte[16], preferGroups: true);
        w.Graphics(new byte[16], preferGroups: false);
        w.GraphicsRepeat(1280, 0x5A);
        w.HeadPosition(27);
        w.LineSpacing144ths(1);
        w.PageLength144ths(1584);
        w.Pitch(Iw2Pitch.EliteProportional);
        w.Color(Iw2Color.Cyan);
        string s = Latin1(buf.WrittenSpan);
        Assert.StartsWith("G0003g002", s);
        Assert.Contains("G0016", s);
        Assert.Contains("V1280Z", s);
        Assert.Contains("F0027", s);
        Assert.Contains("T01", s);
        Assert.Contains("H1584", s);
        Assert.Contains("P", s);
        Assert.EndsWith("K3", s);
    }

    [Fact]
    public void PitchTableMatchesManual()
    {
        Assert.Equal('n', Iw2Pitch.Extended.Command());
        Assert.Equal('p', Iw2Pitch.PicaProportional.Command());
        Assert.Equal(1280, Iw2Pitch.EliteProportional.MaxColumns());
        Assert.Equal(576, Iw2Pitch.Extended.MaxColumns());
        Assert.True(Iw2Pitches.TryFromDpi(107, out var p));
        Assert.Equal(Iw2Pitch.Semicondensed, p);
        Assert.False(Iw2Pitches.TryFromDpi(100, out _));
    }

    [Fact]
    public void TextJobNormalisesLineEndingsAndTabs()
    {
        var buf = new ArrayBufferWriter<byte>();
        TextJobEncoder.Encode("a\tb\nc\r\nd\re"u8, buf, new TextJobOptions { TabWidth = 4 });
        string s = Latin1(buf.WrittenSpan);
        Assert.Contains("a   b\r\nc\r\nd\r\ne\r\n\f", s);
        Assert.Single(s.Split('\f').Skip(1)); // exactly one form feed at the end
    }

    [Fact]
    public void RawDetectionSeesEscapes()
    {
        Assert.True(TextJobEncoder.LooksLikeRawPrinterData("G0001x"u8));
        Assert.False(TextJobEncoder.LooksLikeRawPrinterData("Hello, world\r\n"u8));
    }

    [Fact]
    public void SimulatorParsesSpacePaddedNumbers()
    {
        // The manual allows leading zeros to be replaced by spaces.
        var bytes = "nT16G   3\r\n"u8.ToArray();
        var sim = new Iw2Simulator(new SimulatorOptions { DpiX = 72, DpiY = 72, LeftEdgeOffsetInches = 0 });
        var pages = sim.Run(bytes);
        Assert.Single(pages);
        Assert.Equal(3, pages[0].Dots);
        Assert.True(pages[0].Black.Get(0, 0));
        Assert.True(pages[0].Black.Get(1, 1));
        Assert.True(pages[0].Black.Get(2, 2));
    }

    [Fact]
    public void IdentityStringIsParsed()
    {
        var id = ImageWriterII.Core.Ports.Iw2Identity.Parse("IW10CF");
        Assert.True(id.IsImageWriter);
        Assert.Equal(10, id.CarriageInches);
        Assert.True(id.ColorRibbon);
        Assert.True(id.SheetFeeder);
        var plain = ImageWriterII.Core.Ports.Iw2Identity.Parse("IW10");
        Assert.False(plain.ColorRibbon);
    }
}
