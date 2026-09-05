using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using ImageWriterII.Ipp.Protocol;

namespace ImageWriterII.Cli;

/// <summary>
/// Draws an alignment and quality test page in page-inch coordinates so it is correct at any (non-square) resolution:
/// inch grid, diagonals, circles (aspect check), single-dot line pairs (144 dpi interleave check), gray ramp, text sizes, colour bars.
/// </summary>
public static class TestPageRenderer
{
    public static Bitmap Render(double pw, double ph, IppResolution res, bool color, string note)
    {
        int W = (int)Math.Round(pw * res.X), H = (int)Math.Round(ph * res.Y);
        var bmp = new Bitmap(W, H, PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.White);
        g.SmoothingMode = SmoothingMode.None;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.PixelOffsetMode = PixelOffsetMode.Half;
        g.ScaleTransform(res.X, res.Y); // draw in inches from here on

        double left = 0.25, top = 0.25, right = pw - 0.25, bottom = ph - 0.25;
        float hair = 1f / res.X; // one dot wide

        using var thin = new Pen(Color.Black, hair);
        using var medium = new Pen(Color.Black, 0.02f);
        using var thick = new Pen(Color.Black, 0.04f);
        using var black = new SolidBrush(Color.Black);

        // printable border
        g.DrawRectangle(thin, (float)left, (float)top, (float)(right - left), (float)(bottom - top));

        // inch grid (ticks along edges) and half-inch minor ticks
        for (double x = 1; x < pw; x += 0.5)
        {
            float len = x % 1 == 0 ? 0.15f : 0.07f;
            g.DrawLine(thin, (float)x, (float)top, (float)x, (float)top + len);
            g.DrawLine(thin, (float)x, (float)bottom, (float)x, (float)bottom - len);
        }
        for (double y = 1; y < ph; y += 0.5)
        {
            float len = y % 1 == 0 ? 0.15f : 0.07f;
            g.DrawLine(thin, (float)left, (float)y, (float)left + len, (float)y);
            g.DrawLine(thin, (float)right, (float)y, (float)right - len, (float)y);
        }

        // title block
        using (var title = new Font("Arial", 18f / 72f, FontStyle.Bold, GraphicsUnit.World))
        using (var body = new Font("Arial", 10f / 72f, GraphicsUnit.World))
        using (var small = new Font("Arial", 7f / 72f, GraphicsUnit.World))
        {
            g.DrawString("Apple ImageWriter II  -  IPP Everywhere test page", title, black, 0.6f, 0.45f);
            g.DrawString($"{res.X} x {res.Y} dpi   {pw}x{ph} in   {(color ? "colour ribbon" : "black ribbon")}   {DateTime.Now:yyyy-MM-dd HH:mm}   {note}", body, black, 0.6f, 0.82f);
            g.DrawString("Border = printable area (1/4\" margins). Ticks every 1/2\". Circles must be round; the fine line pairs below must show separate lines at 144 dpi vertical.", small, black, 0.6f, 1.02f);
        }

        // text samples at several sizes (checks legibility per resolution)
        float ty = 1.35f;
        foreach (var pt in new[] { 6f, 8f, 10f, 12f, 14f, 18f, 24f })
        {
            using var f = new Font("Times New Roman", pt / 72f, GraphicsUnit.World);
            g.DrawString($"{pt:0} pt  The quick brown fox jumps over the lazy dog 0123456789", f, black, 0.6f, ty);
            ty += pt / 72f * 1.35f;
        }

        // circles and diagonals (aspect ratio check)
        float cx = 2.0f, cy = 5.3f;
        foreach (var r in new[] { 0.25f, 0.5f, 0.75f, 1.0f })
            g.DrawEllipse(medium, cx - r, cy - r, 2 * r, 2 * r);
        g.DrawLine(thin, cx - 1.1f, cy - 1.1f, cx + 1.1f, cy + 1.1f);
        g.DrawLine(thin, cx - 1.1f, cy + 1.1f, cx + 1.1f, cy - 1.1f);
        g.DrawLine(medium, cx - 1.1f, cy, cx + 1.1f, cy);
        g.DrawLine(medium, cx, cy - 1.1f, cx, cy + 1.1f);

        // 1-inch square for scale
        g.DrawRectangle(thick, 3.5f, 4.3f, 1f, 1f);
        using (var f = new Font("Arial", 8f / 72f, GraphicsUnit.World)) g.DrawString("1 inch", f, black, 3.55f, 5.35f);

        // fine line pairs: horizontal single-dot lines with 1-dot gaps (interleave test) and vertical ones
        g.ResetTransform();
        int px0 = (int)(5.0 * res.X), py0 = (int)(4.3 * res.Y);
        for (int i = 0; i < 12; i++)
        {
            int y = py0 + i * 2;
            for (int x = px0; x < px0 + (int)(1.2 * res.X); x++) bmp.SetPixel(x, y, Color.Black);
        }
        for (int i = 0; i < 24; i++)
        {
            int x = px0 + (int)(1.4 * res.X) + i * 2;
            for (int y = py0; y < py0 + (int)(0.5 * res.Y); y++) bmp.SetPixel(x, y, Color.Black);
        }
        // isolated single dots
        for (int i = 0; i < 8; i++) bmp.SetPixel(px0 + (int)(1.4 * res.X) + i * 8, py0 + (int)(0.7 * res.Y), Color.Black);
        g.ScaleTransform(res.X, res.Y);
        using (var f = new Font("Arial", 7f / 72f, GraphicsUnit.World))
            g.DrawString("single-dot lines / dots", f, black, 5.0f, 5.15f);

        // gray ramp
        float gy = 6.7f;
        for (int i = 0; i <= 10; i++)
        {
            int v = 255 - i * 255 / 10;
            using var b = new SolidBrush(Color.FromArgb(v, v, v));
            g.FillRectangle(b, 0.6f + i * 0.7f, gy, 0.68f, 0.6f);
            using var f = new Font("Arial", 7f / 72f, GraphicsUnit.World);
            g.DrawString($"{i * 10}%", f, black, 0.6f + i * 0.7f, gy + 0.65f);
        }
        // continuous gradient
        using (var grad = new LinearGradientBrush(new RectangleF(0.6f, gy + 0.95f, 7.4f, 0.4f), Color.White, Color.Black, LinearGradientMode.Horizontal))
            g.FillRectangle(grad, 0.6f, gy + 0.95f, 7.4f, 0.4f);

        // colour bars (or hatch patterns in mono)
        float by = 8.3f;
        var bars = new[] { Color.Yellow, Color.Magenta, Color.Cyan, Color.Red, Color.Green, Color.Blue, Color.Black };
        var names = new[] { "yellow", "magenta", "cyan", "red", "green", "blue", "black" };
        for (int i = 0; i < bars.Length; i++)
        {
            using var b = new SolidBrush(color ? bars[i] : Color.FromArgb(40 + i * 30, 40 + i * 30, 40 + i * 30));
            g.FillRectangle(b, 0.6f + i * 1.05f, by, 1.0f, 0.8f);
            using var f = new Font("Arial", 7f / 72f, GraphicsUnit.World);
            g.DrawString(names[i], f, black, 0.6f + i * 1.05f, by + 0.85f);
        }

        // bottom-right registration target
        g.DrawLine(thin, (float)right - 0.6f, (float)bottom - 0.3f, (float)right - 0.1f, (float)bottom - 0.3f);
        g.DrawLine(thin, (float)right - 0.35f, (float)bottom - 0.55f, (float)right - 0.35f, (float)bottom - 0.05f);
        g.DrawEllipse(thin, (float)right - 0.5f, (float)bottom - 0.45f, 0.3f, 0.3f);
        return bmp;
    }
}
