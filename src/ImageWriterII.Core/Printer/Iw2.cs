namespace ImageWriterII.Core.Printer;

/// <summary>
/// Apple ImageWriter II control codes. All facts here come from the
/// "Apple ImageWriter II Technical Reference Manual" (Addison-Wesley, 1987),
/// chapter 8 (Graphics and Color Printing) and appendix A (Command Summary).
/// </summary>
public static class Iw2
{
    public const byte ESC = 0x1B;
    public const byte CR = 0x0D;
    public const byte LF = 0x0A;
    public const byte FF = 0x0C;
    public const byte BEL = 0x07;
    public const byte BS = 0x08;
    public const byte TAB = 0x09;
    public const byte SO = 0x0E;   // double-width on
    public const byte SI = 0x0F;   // double-width off
    public const byte XON = 0x11;  // DC1, also "select printer"
    public const byte XOFF = 0x13; // DC3, also "deselect printer"
    public const byte US = 0x1F;   // multi line feed prefix (US n)

    /// <summary>Print width of the 10-inch carriage in inches (Table 8-2: 1280 dots at 160 dpi).</summary>
    public const double PrintWidthInches = 8.0;

    /// <summary>Vertical pin spacing / graphics row spacing per pass, in dots per inch.</summary>
    public const int PinDpi = 72;

    /// <summary>Paper motion resolution: line feeds are specified in 1/144 inch.</summary>
    public const int FeedUnitsPerInch = 144;

    /// <summary>Software reset (ESC c) takes up to three seconds; data sent during that time is lost.</summary>
    public static readonly TimeSpan ResetSettleTime = TimeSpan.FromSeconds(3.5);
}

/// <summary>Horizontal character pitch. The numeric value is the horizontal graphics density in dots per inch (Table 8-2).</summary>
public enum Iw2Pitch
{
    Extended = 72,          // ESC n,  9 cpi
    Pica = 80,              // ESC N, 10 cpi
    Elite = 96,             // ESC E, 12 cpi
    Semicondensed = 107,    // ESC e, 13.4 cpi
    Condensed = 120,        // ESC q, 15 cpi
    Ultracondensed = 136,   // ESC Q, 17 cpi
    PicaProportional = 144, // ESC p
    EliteProportional = 160 // ESC P
}

/// <summary>Ribbon colour selected with ESC K n (Table 8-6). Requires the four-colour ribbon.</summary>
public enum Iw2Color
{
    Black = 0,
    Yellow = 1,
    Magenta = 2,
    Cyan = 3,
    Orange = 4, // yellow + magenta (printer overprints)
    Green = 5,  // yellow + cyan
    Purple = 6  // magenta + cyan
}

/// <summary>Print quality / font selected with ESC a n (Table A-6).</summary>
public enum Iw2Font
{
    Correspondence = 0,   // ESC a 0
    Draft = 1,            // ESC a 1 (power-on default)
    NearLetterQuality = 2 // ESC a 2
}

public static class Iw2Pitches
{
    public static readonly Iw2Pitch[] All =
    [
        Iw2Pitch.Extended, Iw2Pitch.Pica, Iw2Pitch.Elite, Iw2Pitch.Semicondensed,
        Iw2Pitch.Condensed, Iw2Pitch.Ultracondensed, Iw2Pitch.PicaProportional, Iw2Pitch.EliteProportional
    ];

    public static bool TryFromDpi(int dpi, out Iw2Pitch pitch)
    {
        foreach (var p in All)
        {
            if ((int)p == dpi) { pitch = p; return true; }
        }
        pitch = default;
        return false;
    }

    public static Iw2Pitch FromDpi(int dpi) =>
        TryFromDpi(dpi, out var p) ? p : throw new ArgumentOutOfRangeException(nameof(dpi), dpi,
            "ImageWriter II horizontal densities are 72, 80, 96, 107, 120, 136, 144 and 160 dpi.");

    /// <summary>Command character for the pitch (ESC x).</summary>
    public static char Command(this Iw2Pitch pitch) => pitch switch
    {
        Iw2Pitch.Extended => 'n',
        Iw2Pitch.Pica => 'N',
        Iw2Pitch.Elite => 'E',
        Iw2Pitch.Semicondensed => 'e',
        Iw2Pitch.Condensed => 'q',
        Iw2Pitch.Ultracondensed => 'Q',
        Iw2Pitch.PicaProportional => 'p',
        Iw2Pitch.EliteProportional => 'P',
        _ => throw new ArgumentOutOfRangeException(nameof(pitch))
    };

    /// <summary>Maximum graphics columns per line for the pitch (Table 8-2): 8 inches times the density.</summary>
    public static int MaxColumns(this Iw2Pitch pitch) => (int)pitch * 8;

    public static int Dpi(this Iw2Pitch pitch) => (int)pitch;

    /// <summary>Vertical densities the printer can produce: 72 (one pass) or 144 (two interleaved passes 1/144" apart).</summary>
    public static bool IsSupportedVerticalDpi(int dpi) => dpi is 72 or 144;
}
