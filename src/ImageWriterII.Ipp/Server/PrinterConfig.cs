using ImageWriterII.Core.Encoder;
using ImageWriterII.Core.Ports;
using ImageWriterII.Core.Raster;
using ImageWriterII.Core.Text;
using ImageWriterII.Ipp.Protocol;

namespace ImageWriterII.Ipp.Server;

public enum RibbonSetting
{
    /// <summary>
    /// Ask the printer with ESC ? at startup; fall back to Black when it does not answer.
    /// The query needs the printer-to-PC data line (Mini-DIN-8 pin 5), which many cables omit,
    /// so this is not the default: see <see cref="PrinterConfig.Ribbon"/>.
    /// </summary>
    Auto,
    Black,
    Color
}

/// <summary>Everything the printer service needs; bound from appsettings.json "ImageWriter" section.</summary>
public sealed class PrinterConfig
{
    public string PrinterName { get; set; } = "ImageWriter II";
    public string MakeAndModel { get; set; } = "Apple ImageWriter II";
    public string Location { get; set; } = "";
    public string Organization { get; set; } = "";

    /// <summary>DNS-SD host label; the printer is reachable as &lt;HostName&gt;.local.</summary>
    public string HostName { get; set; } = "imagewriter-ii";
    public int HttpPort { get; set; } = 631;
    public bool MdnsEnabled { get; set; } = true;
    /// <summary>Only advertise on interfaces whose name or description contains one of these (empty = all).</summary>
    public List<string> MdnsInterfaces { get; set; } = [];
    public bool RawPortEnabled { get; set; } = true;
    public int RawPort { get; set; } = 9100;

    public SerialPortSettings Serial { get; set; } = new();
    /// <summary>
    /// Which ribbon is fitted. Colour is the default: the four-colour ribbon is what makes this printer
    /// interesting, a colour job on a black ribbon still prints (just in black), and <see cref="RibbonSetting.Auto"/>
    /// silently degrades to black on the many cables that have no printer-to-PC data line.
    /// </summary>
    public RibbonSetting Ribbon { get; set; } = RibbonSetting.Color;

    public Iw2EncoderOptions Encoder { get; set; } = new();
    public HalftoneOptions Halftone { get; set; } = new();
    public TextJobOptions Text { get; set; } = new();

    /// <summary>Resolutions offered to clients, "WxH" in dpi. Horizontal must be an ImageWriter density, vertical 72 or 144.</summary>
    public List<string> Resolutions { get; set; } = ["72x72", "144x144", "160x144"];
    public string DefaultResolution { get; set; } = "144x144";

    /// <summary>PWG self-describing media names. Custom sizes are accepted between MediaMin and MediaMax.</summary>
    public List<string> MediaSupported { get; set; } =
    [
        "na_letter_8.5x11in", "na_legal_8.5x14in", "iso_a4_210x297mm", "na_executive_7.25x10.5in",
        "na_invoice_5.5x8.5in", "iso_a5_148x210mm", "na_index-4x6_4x6in"
    ];
    public string MediaDefault { get; set; } = "na_letter_8.5x11in";
    public string MediaMin { get; set; } = "custom_min_2x2in";
    public string MediaMax { get; set; } = "custom_max_8.5x17in";

    /// <summary>Unprintable margins reported to clients, in inches. Left must cover Encoder.LeftEdgeOffsetInches.</summary>
    public double MarginLeftInches { get; set; } = 0.25;
    public double MarginRightInches { get; set; } = 0.25;
    public double MarginTopInches { get; set; } = 0.25;
    public double MarginBottomInches { get; set; } = 0.25;

    public string SpoolDirectory { get; set; } = "spool";
    public int KeepCompletedJobs { get; set; } = 50;
    /// <summary>Persisted printer UUID (generated on first start when empty).</summary>
    public string Uuid { get; set; } = "";
    /// <summary>Emit a bell to the printer for Identify-Printer.</summary>
    public bool IdentifyWithBell { get; set; } = true;

    // ---- derived, set at runtime ----

    /// <summary>Effective ribbon after auto-detection.</summary>
    public bool ColorRibbon { get; set; }

    public IppResolution ParseResolution(string s)
    {
        var parts = s.ToLowerInvariant().Replace("dpi", "").Split('x');
        if (parts.Length == 1 && int.TryParse(parts[0], out var sq)) return new IppResolution(sq, sq);
        if (parts.Length == 2 && int.TryParse(parts[0], out var x) && int.TryParse(parts[1], out var y)) return new IppResolution(x, y);
        throw new FormatException($"Bad resolution '{s}'.");
    }

    public List<IppResolution> ResolutionList()
    {
        var list = Resolutions.Select(ParseResolution).Distinct().ToList();
        var def = ParseResolution(DefaultResolution);
        if (!list.Contains(def)) list.Insert(0, def);
        return list;
    }

    public IppResolution DefaultResolutionValue() => ParseResolution(DefaultResolution);

    public void Validate()
    {
        // The configuration binder appends to pre-populated lists; collapse duplicates from defaults + appsettings.
        Resolutions = Resolutions.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MediaSupported = MediaSupported.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        MdnsInterfaces = MdnsInterfaces.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var r in ResolutionList())
        {
            if (!Core.Printer.Iw2Pitches.TryFromDpi(r.X, out _))
                throw new InvalidOperationException($"Resolution {r}: horizontal density must be 72, 80, 96, 107, 120, 136, 144 or 160 dpi.");
            if (!Core.Printer.Iw2Pitches.IsSupportedVerticalDpi(r.Y))
                throw new InvalidOperationException($"Resolution {r}: vertical density must be 72 or 144 dpi.");
        }
        foreach (var m in MediaSupported) _ = PwgMedia.Parse(m);
        _ = PwgMedia.Parse(MediaDefault);
        if (!MediaSupported.Contains(MediaDefault)) MediaSupported.Insert(0, MediaDefault);
        if (MarginLeftInches < Encoder.LeftEdgeOffsetInches) MarginLeftInches = Encoder.LeftEdgeOffsetInches;
        // Winding back further than this can pull the sheet's edge off the paper-out sensor.
        if (Encoder.TearOffInches < 0) Encoder.TearOffInches = 0;
        if (Encoder.TearOffInches > Core.Printer.Iw2.MaxTearOffInches) Encoder.TearOffInches = Core.Printer.Iw2.MaxTearOffInches;
        if (HttpPort is <= 0 or > 65535) throw new InvalidOperationException("HttpPort out of range.");
    }
}

/// <summary>PWG 5101.1 self-describing media size names ("na_letter_8.5x11in", "iso_a4_210x297mm").</summary>
public sealed record PwgMedia(string Name, int WidthHundredthsMm, int HeightHundredthsMm)
{
    public double WidthInches => WidthHundredthsMm / 2540.0;
    public double HeightInches => HeightHundredthsMm / 2540.0;
    public int WidthPoints => (int)Math.Round(WidthHundredthsMm * 72 / 2540.0);
    public int HeightPoints => (int)Math.Round(HeightHundredthsMm * 72 / 2540.0);

    public static PwgMedia Parse(string name)
    {
        if (!TryParse(name, out var m)) throw new FormatException($"Not a PWG self-describing media name: '{name}'.");
        return m;
    }

    public static bool TryParse(string name, out PwgMedia media)
    {
        media = null!;
        int us = name.LastIndexOf('_');
        if (us < 0 || us == name.Length - 1) return false;
        string dims = name[(us + 1)..];
        double scale;
        if (dims.EndsWith("in", StringComparison.Ordinal)) { dims = dims[..^2]; scale = 2540; }
        else if (dims.EndsWith("mm", StringComparison.Ordinal)) { dims = dims[..^2]; scale = 100; }
        else return false;
        var parts = dims.Split('x');
        if (parts.Length != 2) return false;
        if (!double.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var w)) return false;
        if (!double.TryParse(parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var h)) return false;
        media = new PwgMedia(name, (int)Math.Round(w * scale), (int)Math.Round(h * scale));
        return true;
    }

    /// <summary>Finds a supported name for the given dimensions (tolerance 0.5 mm) or builds a custom name.</summary>
    public static string NameFor(int widthHundredths, int heightHundredths, IEnumerable<string> supported)
    {
        foreach (var s in supported)
        {
            if (TryParse(s, out var m) && Math.Abs(m.WidthHundredthsMm - widthHundredths) <= 50 && Math.Abs(m.HeightHundredthsMm - heightHundredths) <= 50)
                return s;
        }
        return $"custom_{widthHundredths / 100.0:0.##}x{heightHundredths / 100.0:0.##}mm";
    }
}
