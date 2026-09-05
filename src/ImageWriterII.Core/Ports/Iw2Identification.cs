using System.Buffers;
using System.Text;
using ImageWriterII.Core.Printer;

namespace ImageWriterII.Core.Ports;

/// <summary>Result of the ESC ? self-identification query (manual, Table 6-7): e.g. "IW10CF".</summary>
public sealed record Iw2Identity(string Raw, bool IsImageWriter, int CarriageInches, bool ColorRibbon, bool SheetFeeder)
{
    public static Iw2Identity Parse(string raw)
    {
        raw = raw.Trim();
        bool iw = raw.StartsWith("IW", StringComparison.OrdinalIgnoreCase);
        int carriage = 0;
        if (raw.Length >= 4 && int.TryParse(raw.AsSpan(2, 2), out var c)) carriage = c;
        bool color = raw.Length > 4 && raw[4..].Contains('C');
        bool feeder = raw.Length > 4 && raw[4..].Contains('F');
        return new Iw2Identity(raw, iw, carriage, color, feeder);
    }

    public override string ToString() =>
        $"{Raw}: {(IsImageWriter ? "ImageWriter" : "unknown")}, {CarriageInches}-inch carriage, {(ColorRibbon ? "colour ribbon" : "black ribbon")}{(SheetFeeder ? ", SheetFeeder" : "")}";
}

public static class Iw2Identification
{
    /// <summary>
    /// Sends ESC ? and collects the reply. The printer answers only when idle, with the high bit clear.
    /// Returns null when nothing arrives within <paramref name="timeout"/> (no cable return line, printer off, or busy).
    /// </summary>
    public static Iw2Identity? Query(IPrinterPort port, TimeSpan timeout)
    {
        port.DiscardInput();
        var buf = new ArrayBufferWriter<byte>();
        var w = new Iw2Writer(buf);
        w.RequestIdentification();
        w.CarriageReturn(); // the command executes when its line is "printed"
        port.Write(buf.WrittenSpan);
        port.Flush();

        var sb = new StringBuilder();
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            int b = port.ReadByte(deadline - DateTime.UtcNow);
            if (b < 0) break;
            char ch = (char)(b & 0x7F);
            if (ch is '\r' or '\n') { if (sb.Length > 0) break; continue; }
            if (ch >= ' ') sb.Append(ch);
            if (sb.Length >= 6 && sb.ToString().StartsWith("IW", StringComparison.OrdinalIgnoreCase))
            {
                // Give any trailing option letters a moment to arrive.
                int extra = port.ReadByte(TimeSpan.FromMilliseconds(150));
                if (extra >= 0) { char e = (char)(extra & 0x7F); if (e >= ' ') sb.Append(e); }
                break;
            }
        }
        return sb.Length == 0 ? null : Iw2Identity.Parse(sb.ToString());
    }
}
