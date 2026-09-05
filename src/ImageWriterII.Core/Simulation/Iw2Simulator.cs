using ImageWriterII.Core.Printer;
using ImageWriterII.Core.Raster;

namespace ImageWriterII.Core.Simulation;

public sealed class SimulatorOptions
{
    /// <summary>Canvas resolution. 144x144 reproduces the printer's dot grid exactly; use 720 horizontally to also resolve 160 dpi.</summary>
    public int DpiX { get; set; } = 144;
    public int DpiY { get; set; } = 144;
    public double PageWidthInches { get; set; } = 8.5;
    public double PageHeightInches { get; set; } = 11.0;
    /// <summary>Where dot column 0 lands on the sheet.</summary>
    public double LeftEdgeOffsetInches { get; set; } = 0.25;
    /// <summary>Rendered dot diameter in inches; 0 marks a single canvas pixel per dot (for exact comparisons).</summary>
    public double DotDiameterInches { get; set; } = 0;
    /// <summary>Draw a placeholder block for every text character so text jobs are visible.</summary>
    public bool RenderTextPlaceholders { get; set; } = true;
}

public sealed class SimulatedPage
{
    public required int Width { get; init; }
    public required int Height { get; init; }
    public required BitPlane Black { get; init; }
    public required BitPlane Yellow { get; init; }
    public required BitPlane Magenta { get; init; }
    public required BitPlane Cyan { get; init; }
    public int Dots { get; set; }
    public List<string> Warnings { get; } = [];

    /// <summary>Composite as 24-bit RGB (top-down rows) with approximate ribbon colours.</summary>
    public byte[] ToRgb()
    {
        var rgb = new byte[Width * Height * 3];
        Array.Fill(rgb, (byte)255);
        for (int y = 0; y < Height; y++)
        {
            for (int x = 0; x < Width; x++)
            {
                int i = (y * Width + x) * 3;
                double r = 1, g = 1, b = 1;
                if (Yellow.Get(x, y)) { r *= 0.98; g *= 0.86; b *= 0.16; }
                if (Magenta.Get(x, y)) { r *= 0.80; g *= 0.12; b *= 0.40; }
                if (Cyan.Get(x, y)) { r *= 0.0; g *= 0.55; b *= 0.80; }
                if (Black.Get(x, y)) { r *= 0.12; g *= 0.12; b *= 0.12; }
                rgb[i] = (byte)(r * 255);
                rgb[i + 1] = (byte)(g * 255);
                rgb[i + 2] = (byte)(b * 255);
            }
        }
        return rgb;
    }
}

/// <summary>
/// Interprets an ImageWriter II escape-code stream and renders the dots that the printer would place.
/// Enough of the command set is implemented to verify graphics jobs produced by the encoder
/// (pitches, ESC G/S/g/V/F, ESC T, ESC A/B, forward/reverse feed, page length, colour, double width).
/// </summary>
public sealed class Iw2Simulator
{
    private readonly SimulatorOptions _o;
    private readonly List<SimulatedPage> _pages = [];

    // printer state
    private int _pitchDpi = 80;      // DIP default: pica
    private int _spacing144 = 24;    // 6 lpi
    private bool _reverse;
    private int _headCol;            // dot columns from left margin
    private int _y144;               // paper position below top of form, in 1/144"
    private int _pageLength144;
    private Iw2Color _color = Iw2Color.Black;
    private bool _doubleWidth;
    private int _leftMarginChars;
    private SimulatedPage? _page;
    private bool _pageDirty;

    public Iw2Simulator(SimulatorOptions? options = null)
    {
        _o = options ?? new SimulatorOptions();
        _pageLength144 = (int)Math.Round(_o.PageHeightInches * 144);
    }

    public IReadOnlyList<SimulatedPage> Pages => _pages;

    public List<string> Log { get; } = [];

    /// <summary>
    /// Total 1/144" units the stream tried to reverse-feed past the top of form. Always 0 for a correct
    /// stream; non-zero means the encoder rewound further than it had actually advanced the paper.
    /// </summary>
    public int OverReversedUnits { get; private set; }

    public IReadOnlyList<SimulatedPage> Run(ReadOnlySpan<byte> data)
    {
        int i = 0;
        while (i < data.Length)
        {
            byte b = data[i++];
            switch (b)
            {
                case Iw2.ESC:
                    if (i >= data.Length) break;
                    i = Escape(data, i);
                    break;
                case Iw2.CR:
                    _headCol = 0;
                    break;
                case Iw2.LF:
                    Feed(_spacing144);
                    break;
                case Iw2.FF:
                    FormFeed();
                    break;
                case Iw2.US:
                    if (i < data.Length)
                    {
                        int n = data[i++] - 0x30;
                        if (n is >= 1 and <= 15) Feed(_spacing144 * n);
                    }
                    break;
                case Iw2.SO: _doubleWidth = true; break;
                case Iw2.SI: _doubleWidth = false; break;
                case Iw2.BEL:
                case Iw2.XON:
                case Iw2.XOFF:
                case Iw2.BS:
                case Iw2.TAB:
                case 0:
                    break;
                default:
                    if (b >= 0x20) PrintCharacter(b);
                    break;
            }
        }
        if (_pageDirty) EmitPage();
        return _pages;
    }

    private int Escape(ReadOnlySpan<byte> data, int i)
    {
        char c = (char)data[i++];
        switch (c)
        {
            case 'n': _pitchDpi = 72; break;
            case 'N': _pitchDpi = 80; break;
            case 'E': _pitchDpi = 96; break;
            case 'e': _pitchDpi = 107; break;
            case 'q': _pitchDpi = 120; break;
            case 'Q': _pitchDpi = 136; break;
            case 'p': _pitchDpi = 144; break;
            case 'P': _pitchDpi = 160; break;
            case 'A': _spacing144 = 24; break;
            case 'B': _spacing144 = 18; break;
            case 'f': _reverse = false; break;
            case 'r': _reverse = true; break;
            case 'v': _y144 = 0; break;
            case '>': Log.Add("unidirectional"); break;
            case '<': Log.Add("bidirectional"); break;
            case 'c':
                _pitchDpi = 80; _spacing144 = 24; _reverse = false; _color = Iw2Color.Black; _doubleWidth = false; _leftMarginChars = 0;
                break;
            case 'T':
                {
                    int nn = ReadNumber(data, ref i, 2);
                    if (nn is >= 1 and <= 99) _spacing144 = nn; else Warn($"ESC T out of range: {nn}");
                    break;
                }
            case 'H':
                {
                    int n = ReadNumber(data, ref i, 4);
                    if (n >= 1) _pageLength144 = n;
                    break;
                }
            case 'L':
                _leftMarginChars = ReadNumber(data, ref i, 3);
                break;
            case 'K':
                {
                    int n = ReadNumber(data, ref i, 1);
                    if (n is >= 0 and <= 6) _color = (Iw2Color)n; else Warn($"ESC K out of range: {n}");
                    break;
                }
            case 'F':
                {
                    int n = ReadNumber(data, ref i, 4);
                    if (n < _headCol) Warn($"ESC F moved backwards ({_headCol} -> {n})");
                    _headCol = n;
                    break;
                }
            case 'G':
            case 'S':
                {
                    int n = ReadNumber(data, ref i, 4);
                    for (int k = 0; k < n && i < data.Length; k++) PlotColumn(data[i++]);
                    break;
                }
            case 'g':
                {
                    int n = ReadNumber(data, ref i, 3) * 8;
                    for (int k = 0; k < n && i < data.Length; k++) PlotColumn(data[i++]);
                    break;
                }
            case 'V':
                {
                    int n = ReadNumber(data, ref i, 4);
                    if (i < data.Length)
                    {
                        byte pattern = data[i++];
                        for (int k = 0; k < n; k++) PlotColumn(pattern);
                    }
                    break;
                }
            case 'Z':
            case 'D':
                i += 2; // software switches: two data bytes
                break;
            case 'a':
            case 's':
            case 'l':
                i += 1;
                break;
            case '!': case '"': case 'X': case 'Y': case 'w': case 'W': case 'x': case 'y': case 'z':
            case '$': case '&': case '-': case '+': case '\'': case '*': case '0': case 'o': case 'O': case '?': case 'm': case 'M':
                break;
            case '(':
            case ')':
                while (i < data.Length && data[i] != (byte)'.') i++;
                i++;
                break;
            case 'u':
                i += 3;
                break;
            case 'I':
                while (i < data.Length && data[i] != 0x04) i++;
                i++;
                break;
            default:
                Warn($"unknown escape ESC {c} (0x{(byte)c:X2})");
                break;
        }
        return i;
    }

    private static int ReadNumber(ReadOnlySpan<byte> data, ref int i, int digits)
    {
        int v = 0;
        for (int k = 0; k < digits && i < data.Length; k++)
        {
            byte b = data[i++];
            if (b >= (byte)'0' && b <= (byte)'9') v = v * 10 + (b - '0');
            else if (b != (byte)' ') return v; // malformed
        }
        return v;
    }

    private void Warn(string message)
    {
        Log.Add(message);
        _page?.Warnings.Add(message);
    }

    private void EnsurePage()
    {
        if (_page is not null) return;
        int w = (int)Math.Round(_o.PageWidthInches * _o.DpiX);
        int h = (int)Math.Round(Math.Max(_o.PageHeightInches, _pageLength144 / 144.0) * _o.DpiY);
        _page = new SimulatedPage
        {
            Width = w,
            Height = h,
            Black = new BitPlane(w, h),
            Yellow = new BitPlane(w, h),
            Magenta = new BitPlane(w, h),
            Cyan = new BitPlane(w, h)
        };
    }

    private void Feed(int units)
    {
        _y144 += _reverse ? -units : units;
        if (_y144 < 0)
        {
            // A real printer cannot reverse past the paper it has fed, so a stream that asks it to has an
            // arithmetic bug (this is how the PerPage colour rewind used to misregister). Record it rather
            // than clamping silently, so round-trip tests can assert on it.
            OverReversedUnits += -_y144;
            _y144 = 0;
        }
        if (_y144 >= _pageLength144)
        {
            // Ran off the bottom of the form: the printer keeps going onto the next sheet.
            _y144 -= _pageLength144;
            EmitPage();
        }
    }

    private void FormFeed()
    {
        EmitPage();
        _y144 = 0;
        _headCol = 0;
    }

    private void EmitPage()
    {
        if (_page is null)
        {
            EnsurePage();
        }
        _pages.Add(_page!);
        _page = null;
        _pageDirty = false;
    }

    private void PrintCharacter(byte ch)
    {
        int charWidth = _pitchDpi switch { 144 => 12, 160 => 13, _ => 8 };
        if (_o.RenderTextPlaceholders && ch != (byte)' ')
        {
            // 5x7 block so text jobs are visible
            for (int cx = 0; cx < 5; cx++) PlotDots(_headCol + cx + (_leftMarginChars * charWidth), 0x7F);
        }
        _headCol += charWidth * (_doubleWidth ? 2 : 1);
    }

    private void PlotColumn(byte pattern)
    {
        int col = _headCol + _leftMarginChars * 8;
        PlotDots(col, pattern);
        if (_doubleWidth) { PlotDots(col + 1, pattern); _headCol += 2; }
        else _headCol += 1;
    }

    private void PlotDots(int dotColumn, byte pattern)
    {
        if (pattern == 0) return;
        EnsurePage();
        _pageDirty = true;
        var page = _page!;
        double xIn = _o.LeftEdgeOffsetInches + (double)dotColumn / _pitchDpi;
        int px = (int)Math.Round(xIn * _o.DpiX);
        double rIn = _o.DotDiameterInches / 2;
        int rx = (int)Math.Round(rIn * _o.DpiX);
        int ry = (int)Math.Round(rIn * _o.DpiY);
        for (int k = 0; k < 8; k++)
        {
            if ((pattern & (1 << k)) == 0) continue;
            double yIn = _y144 / 144.0 + k / 72.0;
            int py = (int)Math.Round(yIn * _o.DpiY);
            page.Dots++;
            if (rx == 0 && ry == 0)
            {
                Mark(page, px, py);
            }
            else
            {
                for (int dy = -ry; dy <= ry; dy++)
                    for (int dx = -rx; dx <= rx; dx++)
                    {
                        double nx = rx == 0 ? 0 : (double)dx / rx, ny = ry == 0 ? 0 : (double)dy / ry;
                        if (nx * nx + ny * ny <= 1.0) Mark(page, px + dx, py + dy);
                    }
            }
        }
    }

    private void Mark(SimulatedPage page, int x, int y)
    {
        if (x < 0 || y < 0 || x >= page.Width || y >= page.Height) return;
        switch (_color)
        {
            case Iw2Color.Black: page.Black.Set(x, y); break;
            case Iw2Color.Yellow: page.Yellow.Set(x, y); break;
            case Iw2Color.Magenta: page.Magenta.Set(x, y); break;
            case Iw2Color.Cyan: page.Cyan.Set(x, y); break;
            case Iw2Color.Orange: page.Yellow.Set(x, y); page.Magenta.Set(x, y); break;
            case Iw2Color.Green: page.Yellow.Set(x, y); page.Cyan.Set(x, y); break;
            case Iw2Color.Purple: page.Magenta.Set(x, y); page.Cyan.Set(x, y); break;
        }
    }
}
