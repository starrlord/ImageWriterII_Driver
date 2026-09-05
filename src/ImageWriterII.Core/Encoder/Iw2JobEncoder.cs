using ImageWriterII.Core.Printer;
using ImageWriterII.Core.Raster;

namespace ImageWriterII.Core.Encoder;

/// <summary>
/// Converts <see cref="RasterPage"/>s into the ImageWriter II's native escape-code stream.
///
/// Geometry: raster pixel (0,0) is the top-left corner of the sheet. The printer's top-of-form is
/// assumed to be the top edge of the sheet and its dot column 0 sits <see cref="Iw2EncoderOptions.LeftEdgeOffsetInches"/>
/// from the left edge, so the leading columns of every row are simply dropped (they are in the unprintable margin).
///
/// Vertical: each pass prints eight pins 1/72" apart. At 144 dpi two passes are interleaved: even rows,
/// a 1/144" feed, odd rows, then a 15/144" feed to the next band (manual, chapter 8 "Dot Spacing").
/// Blank bands are skipped with line feeds; blank stretches within a line are skipped with ESC F,
/// and runs of identical columns are compressed with ESC V.
/// </summary>
public sealed class Iw2JobEncoder
{
    private readonly Stream _output;
    private readonly StreamBufferWriter _buffer;
    private readonly Iw2Writer _w;
    private readonly Iw2EncoderOptions _o;

    private int _currentSpacing = -1;
    private int _pageLength144 = -1;
    private Iw2Pitch? _pitch;
    private Iw2Color _color = Iw2Color.Black;
    private int _pendingFeed;          // 1/144" not yet sent
    private int _advancedSincePageTop; // 1/144" physically fed since the page's top of form
    private bool _jobStarted;

    public Iw2JobEncoder(Stream output, Iw2EncoderOptions options)
    {
        _output = output;
        _o = options;
        _buffer = new StreamBufferWriter(output);
        _w = new Iw2Writer(_buffer);
    }

    /// <summary>Invoked after each band with (bandsDone, bandsTotal) so callers can report progress.</summary>
    public Action<int, int>? Progress { get; set; }

    public long BytesWritten => _buffer.TotalWritten;

    /// <summary>Puts every printer setting that affects graphics into a known state. No ESC c: a software reset needs a three second pause.</summary>
    public void BeginJob()
    {
        if (_jobStarted) return;
        _jobStarted = true;

        _w.StandardCharacterSet();
        _w.DoubleWidth(false);
        _w.Boldface(false);
        _w.Underline(false);
        _w.HalfHeight(false);
        _w.SuperSubscriptOff();
        // A-8 open: no automatic LF after CR (we colour-overprint lines with bare CRs).
        // A-6 open: no LF when the line is full. B-6 open: 8-bit data.
        _w.OpenSwitches((byte)(Iw2Writer.SwA_AutoLineFeedAfterCr | Iw2Writer.SwA_LineFeedWhenLineFull), Iw2Writer.SwB_EightBitData);
        // A-7 closed: CR, LF and FF all cause printing. B-3 closed: no perforation skip (it would move paper mid-image).
        _w.CloseSwitches(Iw2Writer.SwA_OnlyCrCausesPrinting, Iw2Writer.SwB_PerforationSkip);
        _w.CarriageReturnInsertion(true);
        _w.ForwardLineFeed();
        _w.LeftMargin(0);
        if (_o.Bidirectional) _w.Bidirectional(); else _w.Unidirectional();
        if (_o.ColorRibbon) SelectColor(Iw2Color.Black);
        if (_o.SetTopOfFormAtJobStart) _w.SetTopOfForm();
        _buffer.Flush();
    }

    /// <summary>Leaves the printer in a friendly state for whatever prints next (black ribbon, 6 lpi, bidirectional).</summary>
    public void EndJob()
    {
        if (!_jobStarted) return;
        if (_o.ColorRibbon) SelectColor(Iw2Color.Black);
        _w.SixLinesPerInch();
        _currentSpacing = -1;
        _w.Bidirectional();
        _buffer.Flush();
        _jobStarted = false;
    }

    public void Flush() => _buffer.Flush();

    public void EncodePage(RasterPage page, CancellationToken ct = default)
    {
        if (!_jobStarted) BeginJob();
        if (!Iw2Pitches.TryFromDpi(page.DpiX, out var pitch))
            throw new NotSupportedException($"Horizontal resolution {page.DpiX} dpi is not an ImageWriter II density.");
        if (!Iw2Pitches.IsSupportedVerticalDpi(page.DpiY))
            throw new NotSupportedException($"Vertical resolution {page.DpiY} dpi is not supported (72 or 144).");

        // Page length in 1/144": points * 2. Clamp to the printer's range.
        int pageLen = Math.Clamp(page.MediaHeightPoints * 2, 1, 9999);
        if (pageLen != _pageLength144)
        {
            _w.PageLength144ths(pageLen);
            _pageLength144 = pageLen;
        }
        if (_pitch != pitch)
        {
            _w.Pitch(pitch);
            _pitch = pitch;
        }

        _pendingFeed = 0;
        _advancedSincePageTop = 0;
        if (_o.TopEdgeOffsetInches > 0)
            _pendingFeed += (int)Math.Round(_o.TopEdgeOffsetInches * Iw2.FeedUnitsPerInch);

        int passes = page.DpiY == 144 ? 2 : 1;
        int bandRows = 8 * passes;
        int colOffset = (int)Math.Round(_o.LeftEdgeOffsetInches * page.DpiX);
        int columns = Math.Min(page.Width - colOffset, pitch.MaxColumns());
        int bands = (page.Height + bandRows - 1) / bandRows;

        List<(InkPlane ink, BitPlane plane)> inks;
        if (page.IsColor && _o.ColorRibbon)
            inks = page.InksInPrintOrder().ToList();
        else if (page.IsColor)
            inks = [(InkPlane.Black, MergePlanes(page))];
        else
            inks = [(InkPlane.Black, page.Black)];

        if (columns <= 0)
        {
            // Nothing printable (raster narrower than the margin); still eject the page.
            _w.FormFeed();
            _buffer.Flush();
            return;
        }

        var cols = new byte[columns];

        if (inks.Count > 1 && _o.ColorStrategy == ColorStrategy.PerPage)
        {
            for (int i = 0; i < inks.Count; i++)
            {
                EncodeBands(page, [inks[i]], cols, colOffset, columns, passes, bandRows, bands, ct);
                if (i < inks.Count - 1)
                {
                    // Rewind to the top of the page for the next ink.
                    _pendingFeed = 0;
                    ReverseFeed(_advancedSincePageTop);
                    _advancedSincePageTop = 0;
                }
            }
        }
        else
        {
            EncodeBands(page, inks, cols, colOffset, columns, passes, bandRows, bands, ct);
        }

        _pendingFeed = 0;
        _w.FormFeed();
        _buffer.Flush();
    }

    private void EncodeBands(RasterPage page, List<(InkPlane ink, BitPlane plane)> inks, byte[] cols, int colOffset, int columns,
        int passes, int bandRows, int bands, CancellationToken ct)
    {
        for (int band = 0; band < bands; band++)
        {
            ct.ThrowIfCancellationRequested();
            int y0 = band * bandRows;
            int y1 = Math.Min(page.Height, y0 + bandRows);

            bool blank = true;
            foreach (var (_, plane) in inks)
            {
                for (int y = y0; y < y1 && blank; y++)
                    if (!plane.IsRowBlank(y)) blank = false;
                if (!blank) break;
            }

            if (blank)
            {
                _pendingFeed += 16;
                _advancedSincePageTop += 16;
                Progress?.Invoke(band + 1, bands);
                continue;
            }

            FlushFeed();
            for (int pass = 0; pass < passes; pass++)
            {
                foreach (var (ink, plane) in inks)
                {
                    int end = FillColumns(plane, y0, pass, passes, colOffset, columns, cols);
                    if (end == 0) continue;
                    if (inks.Count > 1 || _o.ColorRibbon) SelectColor(InkToColor(ink));
                    EmitLine(cols.AsSpan(0, end));
                    _w.CarriageReturn();
                }
                if (pass < passes - 1)
                {
                    SetSpacing(1);
                    _w.LineFeed();
                    _advancedSincePageTop += 1;
                }
            }
            int rest = passes == 2 ? 15 : 16;
            _pendingFeed += rest;
            _advancedSincePageTop += rest;
            _buffer.Flush();
            Progress?.Invoke(band + 1, bands);
        }
    }

    private static Iw2Color InkToColor(InkPlane ink) => ink switch
    {
        InkPlane.Yellow => Iw2Color.Yellow,
        InkPlane.Magenta => Iw2Color.Magenta,
        InkPlane.Cyan => Iw2Color.Cyan,
        _ => Iw2Color.Black
    };

    private static BitPlane MergePlanes(RasterPage page)
    {
        var merged = new BitPlane(page.Width, page.Height);
        foreach (var (_, plane) in page.InksInPrintOrder())
        {
            var src = plane.Data;
            var dst = merged.Data;
            for (int i = 0; i < dst.Length; i++) dst[i] |= src[i];
        }
        return merged;
    }

    /// <summary>
    /// Builds one head pass: column x gets bit k (k = 0 top pin .. 7 bottom pin) from raster row
    /// y0 + k*passes + pass. Returns the count of columns up to and including the last non-blank one.
    /// </summary>
    internal static int FillColumns(BitPlane plane, int y0, int pass, int passes, int colOffset, int columns, Span<byte> cols)
    {
        cols.Clear();
        int end = 0;
        for (int k = 0; k < 8; k++)
        {
            int row = y0 + k * passes + pass;
            if (row >= plane.Height) break;
            var bytes = plane.Row(row);
            byte bit = (byte)(1 << k);
            for (int x = 0; x < columns; x++)
            {
                int sx = x + colOffset;
                if ((bytes[sx >> 3] & (0x80 >> (sx & 7))) != 0)
                {
                    cols[x] |= bit;
                    if (x + 1 > end) end = x + 1;
                }
            }
        }
        return end;
    }

    /// <summary>Emit one pass (already trimmed of trailing blanks) using skip, repeat and literal segments.</summary>
    private void EmitLine(ReadOnlySpan<byte> cols)
    {
        int end = cols.Length;
        int i = 0;
        int litStart = -1;
        while (i < end)
        {
            byte v = cols[i];
            int r = 1;
            while (i + r < end && cols[i + r] == v && r < 9999) r++;

            bool skip = v == 0 && _o.UseHeadPositioning && r >= _o.MinSkipRun;
            bool repeat = !skip && r >= _o.MinRepeatRun;
            if (skip || repeat)
            {
                if (litStart >= 0) { _w.Graphics(cols[litStart..i], _o.PreferGroupedGraphics); litStart = -1; }
                if (skip) _w.HeadPosition(i + r);
                else _w.GraphicsRepeat(r, v);
                i += r;
            }
            else
            {
                if (litStart < 0) litStart = i;
                i += r;
            }
        }
        if (litStart >= 0) _w.Graphics(cols[litStart..end], _o.PreferGroupedGraphics);
    }

    private bool _colorEverSet;

    private void SelectColor(Iw2Color color)
    {
        if (_colorEverSet && _color == color) return;
        _w.Color(color);
        _color = color;
        _colorEverSet = true;
    }

    private void SetSpacing(int units)
    {
        if (_currentSpacing == units) return;
        _w.LineSpacing144ths(units);
        _currentSpacing = units;
    }

    private void FlushFeed()
    {
        while (_pendingFeed > 0)
        {
            int step = Math.Min(99, _pendingFeed);
            SetSpacing(step);
            _w.LineFeed();
            _pendingFeed -= step;
        }
    }

    private void ReverseFeed(int units)
    {
        if (units <= 0) return;
        _w.ReverseLineFeed();
        while (units > 0)
        {
            int step = Math.Min(99, units);
            SetSpacing(step);
            _w.LineFeed();
            units -= step;
        }
        _w.ForwardLineFeed();
    }
}
