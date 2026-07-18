using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace CodexUsageTray;

internal static class TrayIconRenderer
{
    public static Icon Create(double? usedPercent)
    {
        const int size = 64;
        using var bitmap = new Bitmap(size, size, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Color.Transparent);

        var percent = Math.Clamp(usedPercent ?? 0, 0, 100);
        var accent = percent switch
        {
            >= 95 => Color.FromArgb(252, 129, 129),
            >= 80 => Color.FromArgb(246, 173, 85),
            _ => Color.FromArgb(104, 211, 145)
        };

        using var background = new SolidBrush(Color.FromArgb(255, 20, 25, 32));
        using var border = new Pen(accent, 6);
        graphics.FillEllipse(background, 4, 4, 56, 56);
        graphics.DrawEllipse(border, 7, 7, 50, 50);

        using var font = new Font("Segoe UI", 20, FontStyle.Bold, GraphicsUnit.Pixel);
        using var textBrush = new SolidBrush(Color.White);
        var text = percent.ToString("0");
        var measured = graphics.MeasureString(text, font);
        graphics.DrawString(text, font, textBrush, (size - measured.Width) / 2, (size - measured.Height) / 2 - 1);

        var handle = bitmap.GetHicon();
        try
        {
            using var temporary = Icon.FromHandle(handle);
            return (Icon)temporary.Clone();
        }
        finally
        {
            DestroyIcon(handle);
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool DestroyIcon(IntPtr handle);
}
