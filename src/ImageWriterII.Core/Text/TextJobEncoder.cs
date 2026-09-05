using System.Buffers;
using ImageWriterII.Core.Printer;

namespace ImageWriterII.Core.Text;

public sealed class TextJobOptions
{
    public Iw2Font Font { get; set; } = Iw2Font.Draft;
    public Iw2Pitch Pitch { get; set; } = Iw2Pitch.Pica;
    /// <summary>6 or 8 lines per inch.</summary>
    public int LinesPerInch { get; set; } = 6;
    public int TabWidth { get; set; } = 8;
    public bool PerforationSkip { get; set; } = true;
    public bool FormFeedAtEnd { get; set; } = true;
    public bool Bidirectional { get; set; } = true;
    /// <summary>Pass ESC sequences and 8-bit bytes through untouched (for pre-formatted ImageWriter output).</summary>
    public bool Raw { get; set; }
    /// <summary>Maximum characters per line before an automatic wrap; 0 = let the printer wrap.</summary>
    public int WrapColumn { get; set; } = 0;

    /// <summary>Head-to-tear-edge distance; see <see cref="Encoder.Iw2EncoderOptions.TearOffInches"/>. 0 disables.</summary>
    public double TearOffInches { get; set; } = 0.0;
}

/// <summary>Prints plain text with the printer's built-in fonts (line ending normalisation, tab expansion, form feed at end).</summary>
public static class TextJobEncoder
{
    /// <summary>Tear-off distance in 1/144", clamped like the raster encoder's.</summary>
    private static int TearOffUnits(TextJobOptions o) => o.TearOffInches <= 0
        ? 0
        : (int)System.Math.Round(System.Math.Min(o.TearOffInches, Iw2.MaxTearOffInches) * Iw2.FeedUnitsPerInch);

    /// <summary>Runs the paper on so the perforation sits at the tear edge; the mirror of the prologue's wind back.</summary>
    public static void WriteEpilogue(Iw2Writer w, TextJobOptions o) => w.Feed144ths(TearOffUnits(o));

    public static void WritePrologue(Iw2Writer w, TextJobOptions o)
    {
        w.StandardCharacterSet();
        w.DoubleWidth(false);
        w.Boldface(false);
        w.Underline(false);
        w.HalfHeight(false);
        w.SuperSubscriptOff();
        w.OpenSwitches((byte)(Iw2Writer.SwA_AutoLineFeedAfterCr | Iw2Writer.SwA_LineFeedWhenLineFull), Iw2Writer.SwB_EightBitData);
        w.CloseSwitches(Iw2Writer.SwA_OnlyCrCausesPrinting, 0);
        if (o.PerforationSkip) w.OpenSwitches(0, Iw2Writer.SwB_PerforationSkip);
        else w.CloseSwitches(0, Iw2Writer.SwB_PerforationSkip);
        w.CarriageReturnInsertion(true);
        w.ForwardLineFeed();
        w.LeftMargin(0);
        w.Color(Iw2Color.Black);
        // Same tear-off handling as raster jobs: wind back to the top of the sheet before printing.
        w.Feed144ths(TearOffUnits(o), reverse: true);
        if (o.LinesPerInch == 8) w.EightLinesPerInch(); else w.SixLinesPerInch();
        w.Pitch(o.Pitch);
        w.Font(o.Font);
        if (o.Bidirectional) w.Bidirectional(); else w.Unidirectional();
    }

    public static void Encode(ReadOnlySpan<byte> text, IBufferWriter<byte> output, TextJobOptions o)
    {
        var w = new Iw2Writer(output);
        WritePrologue(w, o);

        if (o.Raw)
        {
            w.Raw(text);
            if (o.FormFeedAtEnd && (text.IsEmpty || text[^1] != Iw2.FF)) w.FormFeed();
            WriteEpilogue(w, o);
            return;
        }

        int column = 0;
        bool lastWasFf = false;
        for (int i = 0; i < text.Length; i++)
        {
            byte b = text[i];
            lastWasFf = false;
            switch (b)
            {
                case (byte)'\r':
                    if (i + 1 < text.Length && text[i + 1] == (byte)'\n') i++;
                    NewLine(w, ref column);
                    break;
                case (byte)'\n':
                    NewLine(w, ref column);
                    break;
                case Iw2.FF:
                    w.FormFeed();
                    column = 0;
                    lastWasFf = true;
                    break;
                case Iw2.TAB:
                    {
                        int spaces = o.TabWidth - (column % o.TabWidth);
                        for (int s = 0; s < spaces; s++) { w.Byte((byte)' '); column++; }
                        break;
                    }
                case 0xEF when i + 2 < text.Length && text[i + 1] == 0xBB && text[i + 2] == 0xBF:
                    i += 2; // UTF-8 BOM
                    break;
                default:
                    if (b < 0x20 || b == 0x7F) break; // drop other control characters
                    if (b >= 0x80)
                    {
                        // UTF-8 multi-byte sequence: emit one '?' per code point
                        int extra = b >= 0xF0 ? 3 : b >= 0xE0 ? 2 : b >= 0xC0 ? 1 : 0;
                        i += extra;
                        b = (byte)'?';
                    }
                    if (o.WrapColumn > 0 && column >= o.WrapColumn) NewLine(w, ref column);
                    w.Byte(b);
                    column++;
                    break;
            }
        }
        if (column > 0) NewLine(w, ref column);
        if (o.FormFeedAtEnd && !lastWasFf) w.FormFeed();
        WriteEpilogue(w, o);
    }

    private static void NewLine(Iw2Writer w, ref int column)
    {
        w.CarriageReturn();
        w.LineFeed();
        column = 0;
    }

    /// <summary>True when the data looks like a pre-encoded ImageWriter stream rather than plain text.</summary>
    public static bool LooksLikeRawPrinterData(ReadOnlySpan<byte> data)
    {
        int sample = Math.Min(data.Length, 4096);
        int esc = 0, high = 0;
        for (int i = 0; i < sample; i++)
        {
            byte b = data[i];
            if (b == Iw2.ESC) esc++;
            else if (b >= 0x80) high++;
        }
        return esc > 0 || high > sample / 50;
    }
}
