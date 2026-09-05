namespace ImageWriterII.Core.Raster;

/// <summary>
/// A page ready for the ImageWriter encoder: one black plane, or four ink planes
/// (yellow, magenta, cyan, black) when printing with the colour ribbon.
/// Pixel (0,0) is the top-left corner of the physical sheet at the page resolution.
/// </summary>
public sealed class RasterPage
{
    public required int DpiX { get; init; }
    public required int DpiY { get; init; }
    public required BitPlane Black { get; init; }
    public BitPlane? Yellow { get; init; }
    public BitPlane? Magenta { get; init; }
    public BitPlane? Cyan { get; init; }

    /// <summary>Physical media size in PostScript points (1/72 inch); used for the printer's page length.</summary>
    public required int MediaWidthPoints { get; init; }
    public required int MediaHeightPoints { get; init; }

    public string MediaSizeName { get; init; } = "";
    public int Copies { get; init; } = 1;

    /// <summary>IPP print-quality (3 draft, 4 normal, 5 high) or 0 when unknown.</summary>
    public int PrintQuality { get; init; }

    public int Width => Black.Width;
    public int Height => Black.Height;
    public bool IsColor => Yellow is not null && Magenta is not null && Cyan is not null;

    public IEnumerable<(InkPlane ink, BitPlane plane)> InksInPrintOrder()
    {
        // Manual, chapter 8: print yellow first to avoid contaminating the yellow ribbon band.
        if (IsColor)
        {
            yield return (InkPlane.Yellow, Yellow!);
            yield return (InkPlane.Magenta, Magenta!);
            yield return (InkPlane.Cyan, Cyan!);
        }
        yield return (InkPlane.Black, Black);
    }
}

public enum InkPlane
{
    Black,
    Yellow,
    Magenta,
    Cyan
}
