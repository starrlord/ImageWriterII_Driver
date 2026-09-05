namespace ImageWriterII.Core.Raster;

/// <summary>Turns decoded raster pixels of any supported colour space into <see cref="RasterPage"/> ink planes.</summary>
public static class RasterPageBuilder
{
    /// <summary>
    /// Build a page. <paramref name="wantColor"/> asks for four ink planes when the source has colour
    /// information; grayscale sources always produce a single black plane.
    /// </summary>
    public static RasterPage Build(RasterPageHeader h, ReadOnlySpan<byte> pixels, bool wantColor, HalftoneOptions halftone)
    {
        int w = h.Width, ht = h.Height, stride = h.BytesPerLine;
        BitPlane black;
        BitPlane? yellow = null, magenta = null, cyan = null;

        switch (h.ColorSpace)
        {
            case RasterColorSpace.K when h.BitsPerPixel == 1:
                black = CopyBilevel(pixels, w, ht, stride, invert: false);
                break;

            case RasterColorSpace.W when h.BitsPerPixel == 1:
            case RasterColorSpace.Sw when h.BitsPerPixel == 1:
                black = CopyBilevel(pixels, w, ht, stride, invert: true); // 1 = white in W spaces
                break;

            case RasterColorSpace.K when h.BitsPerPixel == 8:
                {
                    // 8-bit K: 0 = no ink, 255 = full ink. Convert to the gray convention (255 = white).
                    var gray = new byte[stride * ht];
                    for (int i = 0; i < gray.Length; i++) gray[i] = (byte)(255 - pixels[i]);
                    black = Halftone.DitherGray(gray, w, ht, stride, halftone);
                    break;
                }

            case RasterColorSpace.W when h.BitsPerPixel == 8:
            case RasterColorSpace.Sw when h.BitsPerPixel == 8:
                black = Halftone.DitherGray(pixels, w, ht, stride, halftone);
                break;

            case RasterColorSpace.Rgb when h.BitsPerPixel == 24:
            case RasterColorSpace.Srgb when h.BitsPerPixel == 24:
            case RasterColorSpace.AdobeRgb when h.BitsPerPixel == 24:
                if (wantColor)
                    (yellow, magenta, cyan, black) = Halftone.DitherRgbToYmck(pixels, w, ht, stride, halftone);
                else
                    black = Halftone.DitherRgbToGray(pixels, w, ht, stride, halftone);
                break;

            case RasterColorSpace.Cmyk when h.BitsPerPixel == 32:
                {
                    // Convert CMYK to RGB then reuse the RGB paths (rare; mostly for test tools).
                    var rgb = new byte[w * 3 * ht];
                    for (int y = 0; y < ht; y++)
                    {
                        var src = pixels.Slice(y * stride, w * 4);
                        for (int x = 0; x < w; x++)
                        {
                            int c = src[x * 4], m = src[x * 4 + 1], yy = src[x * 4 + 2], k = src[x * 4 + 3];
                            rgb[(y * w + x) * 3] = (byte)((255 - c) * (255 - k) / 255);
                            rgb[(y * w + x) * 3 + 1] = (byte)((255 - m) * (255 - k) / 255);
                            rgb[(y * w + x) * 3 + 2] = (byte)((255 - yy) * (255 - k) / 255);
                        }
                    }
                    if (wantColor)
                        (yellow, magenta, cyan, black) = Halftone.DitherRgbToYmck(rgb, w, ht, w * 3, halftone);
                    else
                        black = Halftone.DitherRgbToGray(rgb, w, ht, w * 3, halftone);
                    break;
                }

            default:
                throw new NotSupportedException($"Unsupported raster colour space {h.ColorSpace} at {h.BitsPerPixel} bpp.");
        }

        if (h.FeedTransform < 0)
        {
            black = black.FlipVertical();
            yellow = yellow?.FlipVertical();
            magenta = magenta?.FlipVertical();
            cyan = cyan?.FlipVertical();
        }
        if (h.CrossFeedTransform < 0)
        {
            black = black.FlipHorizontal();
            yellow = yellow?.FlipHorizontal();
            magenta = magenta?.FlipHorizontal();
            cyan = cyan?.FlipHorizontal();
        }

        int widthPts = h.PageWidthPoints > 0 ? h.PageWidthPoints : (int)Math.Round(w * 72.0 / Math.Max(1, h.HwResolutionX));
        int heightPts = h.PageHeightPoints > 0 ? h.PageHeightPoints : (int)Math.Round(ht * 72.0 / Math.Max(1, h.HwResolutionY));

        return new RasterPage
        {
            DpiX = h.HwResolutionX,
            DpiY = h.HwResolutionY,
            Black = black,
            Yellow = yellow,
            Magenta = magenta,
            Cyan = cyan,
            MediaWidthPoints = widthPts,
            MediaHeightPoints = heightPts,
            MediaSizeName = h.MediaSizeName,
            Copies = Math.Max(1, h.NumCopies),
            PrintQuality = h.PrintQuality
        };
    }

    private static BitPlane CopyBilevel(ReadOnlySpan<byte> pixels, int w, int ht, int stride, bool invert)
    {
        var plane = new BitPlane(w, ht);
        for (int y = 0; y < ht; y++)
        {
            var src = pixels.Slice(y * stride, plane.Stride);
            var dst = plane.Row(y);
            if (invert)
                for (int i = 0; i < src.Length; i++) dst[i] = (byte)~src[i];
            else
                src.CopyTo(dst);
            // mask padding bits beyond the width so they never print
            int extra = plane.Stride * 8 - w;
            if (extra > 0) dst[^1] &= (byte)(0xFF << extra);
        }
        return plane;
    }
}
