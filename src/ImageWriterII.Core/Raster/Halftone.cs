namespace ImageWriterII.Core.Raster;

public enum DitherMode
{
    /// <summary>Floyd–Steinberg error diffusion with serpentine scanning (best for photos).</summary>
    FloydSteinberg,
    /// <summary>8x8 Bayer ordered dither (regular pattern, fastest, stable for line art).</summary>
    Ordered,
    /// <summary>Simple 50% threshold (text and line art that is already black and white).</summary>
    Threshold
}

public sealed class HalftoneOptions
{
    public DitherMode Mode { get; set; } = DitherMode.FloydSteinberg;

    /// <summary>
    /// Dot-gain compensation exponent applied to ink coverage (coverage' = coverage^Gamma).
    /// The ImageWriter's dots are much larger than the 1/144" grid pitch, so mid-tones print far darker
    /// than requested; values above 1 lighten them. 1.0 disables compensation.
    /// </summary>
    public double Gamma { get; set; } = 1.8;

    /// <summary>Coverage below this (0..1) is never printed; suppresses stray dots in near-white areas.</summary>
    public double WhiteClip { get; set; } = 0.03;

    /// <summary>Coverage above this (0..1) is always printed solid.</summary>
    public double BlackClip { get; set; } = 0.97;
}

/// <summary>Converts continuous-tone rasters into the one-bit ink planes the printer needs.</summary>
public static class Halftone
{
    private static readonly byte[] Bayer8 =
    [
         0, 32,  8, 40,  2, 34, 10, 42,
        48, 16, 56, 24, 50, 18, 58, 26,
        12, 44,  4, 36, 14, 46,  6, 38,
        60, 28, 52, 20, 62, 30, 54, 22,
         3, 35, 11, 43,  1, 33,  9, 41,
        51, 19, 59, 27, 49, 17, 57, 25,
        15, 47,  7, 39, 13, 45,  5, 37,
        63, 31, 55, 23, 61, 29, 53, 21
    ];

    /// <summary>Builds a lookup from 8-bit gray (0 = black, 255 = white) to ink coverage 0..1.</summary>
    public static float[] CoverageLut(HalftoneOptions o)
    {
        var lut = new float[256];
        for (int v = 0; v < 256; v++)
        {
            double coverage = 1.0 - v / 255.0;
            coverage = Math.Pow(coverage, o.Gamma);
            if (coverage <= o.WhiteClip) coverage = 0;
            else if (coverage >= o.BlackClip) coverage = 1;
            lut[v] = (float)coverage;
        }
        return lut;
    }

    /// <summary>Dither an 8-bit gray page (0 = black, 255 = white, <paramref name="stride"/> bytes per row) into a plane.</summary>
    public static BitPlane DitherGray(ReadOnlySpan<byte> gray, int width, int height, int stride, HalftoneOptions options)
    {
        var lut = CoverageLut(options);
        var coverage = new float[width];
        var plane = new BitPlane(width, height);
        var ctx = new DitherContext(width, options.Mode);
        for (int y = 0; y < height; y++)
        {
            var row = gray.Slice(y * stride, width);
            for (int x = 0; x < width; x++) coverage[x] = lut[row[x]];
            ctx.DitherRow(coverage, y, plane);
        }
        return plane;
    }

    /// <summary>Dither an 8-bit RGB page (3 bytes per pixel) to a single black plane using luminance.</summary>
    public static BitPlane DitherRgbToGray(ReadOnlySpan<byte> rgb, int width, int height, int stride, HalftoneOptions options)
    {
        var lut = CoverageLut(options);
        var coverage = new float[width];
        var plane = new BitPlane(width, height);
        var ctx = new DitherContext(width, options.Mode);
        for (int y = 0; y < height; y++)
        {
            var row = rgb.Slice(y * stride, width * 3);
            for (int x = 0; x < width; x++)
            {
                int r = row[x * 3], g = row[x * 3 + 1], b = row[x * 3 + 2];
                int lum = (r * 299 + g * 587 + b * 114 + 500) / 1000;
                coverage[x] = lut[lum];
            }
            ctx.DitherRow(coverage, y, plane);
        }
        return plane;
    }

    /// <summary>
    /// Separate an 8-bit RGB page into yellow, magenta, cyan and black planes.
    /// Full grey-component replacement: neutral density goes entirely to the black ribbon band,
    /// which is what a fabric-ribbon dot-matrix printer renders best.
    /// </summary>
    public static (BitPlane yellow, BitPlane magenta, BitPlane cyan, BitPlane black) DitherRgbToYmck(
        ReadOnlySpan<byte> rgb, int width, int height, int stride, HalftoneOptions options)
    {
        var yellow = new BitPlane(width, height);
        var magenta = new BitPlane(width, height);
        var cyan = new BitPlane(width, height);
        var black = new BitPlane(width, height);
        var cy = new float[width];
        var cm = new float[width];
        var cc = new float[width];
        var ck = new float[width];
        var ctxY = new DitherContext(width, options.Mode);
        var ctxM = new DitherContext(width, options.Mode);
        var ctxC = new DitherContext(width, options.Mode);
        var ctxK = new DitherContext(width, options.Mode);
        double gamma = options.Gamma;

        for (int y = 0; y < height; y++)
        {
            var row = rgb.Slice(y * stride, width * 3);
            for (int x = 0; x < width; x++)
            {
                double r = row[x * 3] / 255.0, g = row[x * 3 + 1] / 255.0, b = row[x * 3 + 2] / 255.0;
                double c = 1 - r, m = 1 - g, yy = 1 - b;
                double k = Math.Min(c, Math.Min(m, yy));
                double c2, m2, y2;
                if (k >= 0.999) { c2 = m2 = y2 = 0; }
                else { c2 = (c - k) / (1 - k); m2 = (m - k) / (1 - k); y2 = (yy - k) / (1 - k); }
                cy[x] = Clip(Math.Pow(y2, gamma), options);
                cm[x] = Clip(Math.Pow(m2, gamma), options);
                cc[x] = Clip(Math.Pow(c2, gamma), options);
                ck[x] = Clip(Math.Pow(k, gamma), options);
            }
            ctxY.DitherRow(cy, y, yellow);
            ctxM.DitherRow(cm, y, magenta);
            ctxC.DitherRow(cc, y, cyan);
            ctxK.DitherRow(ck, y, black);
        }
        return (yellow, magenta, cyan, black);
    }

    private static float Clip(double coverage, HalftoneOptions o)
    {
        if (coverage <= o.WhiteClip) return 0;
        if (coverage >= o.BlackClip) return 1;
        return (float)coverage;
    }

    /// <summary>Per-plane error diffusion state.</summary>
    private sealed class DitherContext
    {
        private readonly DitherMode _mode;
        private float[] _errCur;
        private float[] _errNext;
        private readonly int _width;

        public DitherContext(int width, DitherMode mode)
        {
            _mode = mode;
            _width = width;
            _errCur = new float[width + 2];
            _errNext = new float[width + 2];
        }

        public void DitherRow(float[] coverage, int y, BitPlane plane)
        {
            switch (_mode)
            {
                case DitherMode.Threshold:
                    for (int x = 0; x < _width; x++)
                        if (coverage[x] >= 0.5f) plane.Set(x, y);
                    return;

                case DitherMode.Ordered:
                    for (int x = 0; x < _width; x++)
                    {
                        float threshold = (Bayer8[((y & 7) << 3) | (x & 7)] + 0.5f) / 64f;
                        if (coverage[x] > threshold) plane.Set(x, y);
                    }
                    return;

                default:
                    FloydSteinbergRow(coverage, y, plane);
                    return;
            }
        }

        private void FloydSteinbergRow(float[] coverage, int y, BitPlane plane)
        {
            Array.Clear(_errNext);
            bool leftToRight = (y & 1) == 0;
            int start = leftToRight ? 0 : _width - 1;
            int end = leftToRight ? _width : -1;
            int step = leftToRight ? 1 : -1;
            for (int x = start; x != end; x += step)
            {
                float v = coverage[x] + _errCur[x + 1];
                float outv;
                if (v >= 0.5f) { plane.Set(x, y); outv = 1f; }
                else outv = 0f;
                // Pure white / pure black requests carry no error so edges stay crisp.
                float err = coverage[x] is 0f or 1f ? 0f : v - outv;
                if (err == 0f) continue;
                int i = x + 1;
                _errCur[i + step] += err * (7f / 16f);
                _errNext[i - step] += err * (3f / 16f);
                _errNext[i] += err * (5f / 16f);
                _errNext[i + step] += err * (1f / 16f);
            }
            // The next row's accumulated error becomes current; the old current buffer is cleared on the next call.
            (_errCur, _errNext) = (_errNext, _errCur);
        }
    }
}
