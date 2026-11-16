using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace BatteryCarePortal;

/// <summary>Draws the tray battery glyph at the tray's pixel size: outline, fill to the level, and a tick at the charge limit when one is set.</summary>
internal static class BatteryTrayIconRenderer
{
    public static Icon Render(int levelPercent, int? limitPercent, int size, bool lightTaskbar)
    {
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.Clear(Color.Transparent);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            Color ink = lightTaskbar ? Color.FromArgb(255, 32, 32, 32) : Color.White;
            Color fill = Color.FromArgb(255, 46, 184, 120);
            float stroke = Math.Max(1.4f, size / 12f);
            float margin = stroke / 2f + 0.5f;
            float capWidth = Math.Max(1.5f, size * 0.09f);
            var body = new RectangleF(margin, size * 0.26f, size - 2 * margin - capWidth, size * 0.48f);

            using (var pen = new Pen(ink, stroke))
                g.DrawRectangle(pen, body.X, body.Y, body.Width, body.Height);
            using (var ink2 = new SolidBrush(ink))
                g.FillRectangle(ink2, body.Right + 0.5f, body.Y + body.Height * 0.3f, capWidth, body.Height * 0.4f);

            float inner = stroke / 2f + Math.Max(0.5f, size / 32f);
            var well = new RectangleF(body.X + inner, body.Y + inner, body.Width - 2 * inner, body.Height - 2 * inner);
            float level = Math.Clamp(levelPercent, 0, 100) / 100f;
            using (var fillBrush = new SolidBrush(fill))
                g.FillRectangle(fillBrush, well.X, well.Y, well.Width * level, well.Height);

            // The limit tick sticks out above and below the glyph so it stays visible over the fill.
            if (limitPercent is int limit and < 100)
            {
                float tickX = well.X + well.Width * Math.Clamp(limit, 0, 100) / 100f;
                using var tickPen = new Pen(Color.FromArgb(255, 255, 176, 0), Math.Max(1f, size / 16f));
                g.DrawLine(tickPen, tickX, body.Y - size * 0.12f, tickX, body.Bottom + size * 0.12f);
            }
        }

        IntPtr handle = bitmap.GetHicon();
        try
        {
            using Icon borrowed = Icon.FromHandle(handle);
            return (Icon)borrowed.Clone();
        }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}

