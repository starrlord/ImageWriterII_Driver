using System.Buffers;
using System.Text;

namespace ImageWriterII.Core.Printer;

/// <summary>
/// Emits ImageWriter II control sequences into a byte sink. Every method maps to a documented command;
/// numeric parameters are rendered as the fixed-width ASCII decimal strings the printer expects.
/// </summary>
public sealed class Iw2Writer
{
    private readonly IBufferWriter<byte> _out;

    public Iw2Writer(IBufferWriter<byte> output) => _out = output;

    public long BytesWritten { get; private set; }

    public void Raw(ReadOnlySpan<byte> bytes)
    {
        _out.Write(bytes);
        BytesWritten += bytes.Length;
    }

    public void Byte(byte b)
    {
        var span = _out.GetSpan(1);
        span[0] = b;
        _out.Advance(1);
        BytesWritten++;
    }

    public void Ascii(string s) => Raw(Encoding.ASCII.GetBytes(s));

    public void Esc(char command) { Byte(Iw2.ESC); Byte((byte)command); }

    private void Esc(char command, string decimalArg) { Esc(command); Ascii(decimalArg); }

    // ----- printing / paper motion -----

    public void CarriageReturn() => Byte(Iw2.CR);
    public void LineFeed() => Byte(Iw2.LF);
    public void FormFeed() => Byte(Iw2.FF);
    public void Bell() => Byte(Iw2.BEL);

    /// <summary>US n: feed 1..15 lines at the current line pitch.</summary>
    public void LineFeeds(int count)
    {
        if (count is < 1 or > 15) throw new ArgumentOutOfRangeException(nameof(count));
        Byte(Iw2.US);
        Byte((byte)(0x30 + count));
    }

    /// <summary>ESC T nn: distance between lines is nn/144 inch (01..99).</summary>
    public void LineSpacing144ths(int nn)
    {
        if (nn is < 1 or > 99) throw new ArgumentOutOfRangeException(nameof(nn));
        Esc('T', nn.ToString("00"));
    }

    public void SixLinesPerInch() => Esc('A');
    public void EightLinesPerInch() => Esc('B');
    public void ForwardLineFeed() => Esc('f');
    public void ReverseLineFeed() => Esc('r');

    /// <summary>ESC H nnnn: page length in 1/144 inch (0001..9999).</summary>
    public void PageLength144ths(int nnnn)
    {
        if (nnnn is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(nnnn));
        Esc('H', nnnn.ToString("0000"));
    }

    /// <summary>ESC v: set top-of-form to the current position (ignored when the SheetFeeder is installed).</summary>
    public void SetTopOfForm() => Esc('v');

    /// <summary>ESC L nnn: left margin at character column nnn (000 = leftmost).</summary>
    public void LeftMargin(int column)
    {
        if (column is < 0 or > 999) throw new ArgumentOutOfRangeException(nameof(column));
        Esc('L', column.ToString("000"));
    }

    // ----- head motion -----

    public void Unidirectional() => Esc('>');
    public void Bidirectional() => Esc('<');

    /// <summary>ESC F nnnn: place the print head nnnn dot columns (current pitch) from the left margin.</summary>
    public void HeadPosition(int dotColumn)
    {
        if (dotColumn is < 0 or > 9999) throw new ArgumentOutOfRangeException(nameof(dotColumn));
        Esc('F', dotColumn.ToString("0000"));
    }

    // ----- character pitch / attributes -----

    public void Pitch(Iw2Pitch pitch) => Esc(pitch.Command());
    public void DoubleWidth(bool on) => Byte(on ? Iw2.SO : Iw2.SI);
    public void Boldface(bool on) => Esc(on ? '!' : '"');
    public void Underline(bool on) => Esc(on ? 'X' : 'Y');
    public void HalfHeight(bool on) => Esc(on ? 'w' : 'W');
    public void SuperSubscriptOff() => Esc('z');
    public void StandardCharacterSet() => Esc('$');
    public void Font(Iw2Font font) => Esc('a', ((int)font).ToString());

    public void ProportionalDotSpacing(int n)
    {
        if (n is < 0 or > 9) throw new ArgumentOutOfRangeException(nameof(n));
        Esc('s', n.ToString());
    }

    // ----- colour -----

    public void Color(Iw2Color color) => Esc('K', ((int)color).ToString());

    // ----- software switches (ESC Z a b opens, ESC D a b closes; register A bit n-1 = SW A-n) -----

    public const byte SwA_SoftSelectResponse = 0x10;   // A-5
    public const byte SwA_LineFeedWhenLineFull = 0x20; // A-6
    public const byte SwA_OnlyCrCausesPrinting = 0x40; // A-7 (open = CR only, closed = CR, LF, FF)
    public const byte SwA_AutoLineFeedAfterCr = 0x80;  // A-8
    public const byte SwB_SlashZero = 0x01;            // B-1
    public const byte SwB_PerforationSkip = 0x04;      // B-3 (open = skip, closed = no skip)
    public const byte SwB_EightBitData = 0x20;         // B-6 (open = 8 bits, closed = 7 bits)

    public void OpenSwitches(byte registerA, byte registerB) { Esc('Z'); Byte(registerA); Byte(registerB); }
    public void CloseSwitches(byte registerA, byte registerB) { Esc('D'); Byte(registerA); Byte(registerB); }

    /// <summary>ESC l 0/1: whether the printer inserts a CR before LF and FF (default: inserts).</summary>
    public void CarriageReturnInsertion(bool insert) => Esc('l', insert ? "0" : "1");

    // ----- misc -----

    /// <summary>ESC c: software reset. Allow <see cref="Iw2.ResetSettleTime"/> before sending more data.</summary>
    public void Reset() => Esc('c');

    /// <summary>ESC ?: request the self-ID string ("IW10CF": ImageWriter, 10-inch carriage, colour ribbon, SheetFeeder).</summary>
    public void RequestIdentification() => Esc('?');

    public void PaperOutSensor(bool on) => Esc(on ? 'o' : 'O');

    // ----- graphics -----

    /// <summary>ESC G nnnn + data (or ESC g nnn when the count is a multiple of eight and <paramref name="preferGroups"/>).</summary>
    public void Graphics(ReadOnlySpan<byte> columns, bool preferGroups = true)
    {
        if (columns.IsEmpty) return;
        if (columns.Length > 9999) throw new ArgumentOutOfRangeException(nameof(columns), "At most 9999 columns per command.");
        if (preferGroups && columns.Length % 8 == 0 && columns.Length / 8 <= 999)
            Esc('g', (columns.Length / 8).ToString("000"));
        else
            Esc('G', columns.Length.ToString("0000"));
        Raw(columns);
    }

    /// <summary>ESC V nnnn c: print the column pattern c nnnn times.</summary>
    public void GraphicsRepeat(int count, byte column)
    {
        if (count is < 1 or > 9999) throw new ArgumentOutOfRangeException(nameof(count));
        Esc('V', count.ToString("0000"));
        Byte(column);
    }
}
