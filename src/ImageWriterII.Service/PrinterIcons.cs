using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace ImageWriterII.Service;

/// <summary>Generates the printer-icons PNGs (48/128/512 px) at runtime: a platinum ImageWriter with a sheet of fanfold paper.</summary>
public static class PrinterIcons
{
    private static readonly Dictionary<int, byte[]> Cache = new();

    public static byte[] Get(int size)
    {
        size = size switch { <= 48 => 48, <= 128 => 128, _ => 512 };
        lock (Cache)
        {
            if (Cache.TryGetValue(size, out var cached)) return cached;
            byte[] png;
            try { png = Render(size); }
            catch (Exception) { png = Fallback(); }
            Cache[size] = png;
            return png;
        }
    }

    private static byte[] Render(int size)
    {
        using var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);
        float s = size / 64f;

        // paper
        using (var paper = new SolidBrush(Color.FromArgb(250, 250, 245)))
        using (var edge = new Pen(Color.FromArgb(150, 150, 150), 1f * s))
        {
            var sheet = new RectangleF(16 * s, 4 * s, 32 * s, 30 * s);
            g.FillRectangle(paper, sheet);
            g.DrawRectangle(edge, sheet.X, sheet.Y, sheet.Width, sheet.Height);
            using var text = new Pen(Color.FromArgb(90, 90, 90), 1.2f * s);
            for (int i = 0; i < 4; i++)
                g.DrawLine(text, 20 * s, (9 + i * 4) * s, (40 - (i % 2) * 6) * s, (9 + i * 4) * s);
            using var holes = new SolidBrush(Color.FromArgb(200, 200, 200));
            for (int i = 0; i < 5; i++)
            {
                g.FillEllipse(holes, 17.2f * s, (6 + i * 5.5f) * s, 1.6f * s, 1.6f * s);
                g.FillEllipse(holes, 45.2f * s, (6 + i * 5.5f) * s, 1.6f * s, 1.6f * s);
            }
        }

        // printer body (Apple platinum)
        using (var body = new LinearGradientBrush(new RectangleF(0, 28 * s, size, 30 * s), Color.FromArgb(232, 228, 218), Color.FromArgb(196, 192, 182), LinearGradientMode.Vertical))
        using (var outline = new Pen(Color.FromArgb(120, 118, 112), 1.2f * s))
        {
            using var path = RoundedRect(new RectangleF(4 * s, 30 * s, 56 * s, 26 * s), 4 * s);
            g.FillPath(body, path);
            g.DrawPath(outline, path);
        }
        // paper slot
        using (var slot = new SolidBrush(Color.FromArgb(70, 70, 70)))
            g.FillRectangle(slot, 12 * s, 33 * s, 40 * s, 2.2f * s);
        // control cluster
        using (var btn = new SolidBrush(Color.FromArgb(120, 120, 120)))
        {
            for (int i = 0; i < 3; i++) g.FillRectangle(btn, (10 + i * 5) * s, 46 * s, 3.5f * s, 2.5f * s);
        }
        using (var led = new SolidBrush(Color.FromArgb(40, 200, 60)))
            g.FillEllipse(led, 50 * s, 46 * s, 3 * s, 3 * s);

        using var ms = new MemoryStream();
        bmp.Save(ms, ImageFormat.Png);
        return ms.ToArray();
    }

    private static GraphicsPath RoundedRect(RectangleF r, float radius)
    {
        var p = new GraphicsPath();
        float d = radius * 2;
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    private static byte[] Fallback() => Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
}
