using System.Drawing.Drawing2D;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace GpuGuard;

/// <summary>Draws the temperature into a tray icon; green ring when actively cooling.</summary>
public static class TrayIconRenderer
{
    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);

    public static Icon Render(int? tempC, bool throttling, bool autoOn, bool error, int targetC = 70, int criticalC = 76)
    {
        const int size = 32;
        using var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            g.Clear(Color.Transparent);

            // Background disc: black when auto-cool is off, green when on; hot temps override.
            var bg = error ? Color.FromArgb(120, 120, 120)
                   : tempC is null ? Color.FromArgb(70, 70, 70)
                   : tempC >= criticalC ? Color.FromArgb(200, 40, 40)
                   : tempC > targetC ? Color.FromArgb(230, 140, 20)
                   : autoOn ? Color.FromArgb(30, 150, 70)
                   : Color.FromArgb(20, 20, 20);
            using (var b = new SolidBrush(bg)) g.FillEllipse(b, 3, 3, size - 6, size - 6);

            if (throttling)
            {
                // Actively throttling: bright green ring.
                using var pen = new Pen(Color.FromArgb(90, 255, 130), 4);
                g.DrawEllipse(pen, 2, 2, size - 4, size - 4);
            }

            var text = error ? "!" : tempC?.ToString() ?? "--";
            using var font = new Font("Segoe UI", text.Length > 2 ? 12 : 17, FontStyle.Bold, GraphicsUnit.Pixel);
            var sz = g.MeasureString(text, font);
            using var fb = new SolidBrush(Color.White);
            g.DrawString(text, font, fb, (size - sz.Width) / 2f, (size - sz.Height) / 2f);
        }
        var h = bmp.GetHicon();
        try { return (Icon)Icon.FromHandle(h).Clone(); }
        finally { DestroyIcon(h); }
    }
}
