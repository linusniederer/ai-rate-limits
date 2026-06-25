using System.Drawing;
using AiRateLimits.Models;

namespace AiRateLimits;

/// <summary>
/// Generates simple colored tray icons per health state. The blueprint's two-bar Codex gauge
/// is a later enhancement; this scaffold draws a solid status dot so the tray is functional.
/// </summary>
public static class TrayIconFactory
{
    public static Color ColorFor(LimitHealth health) => health switch
    {
        LimitHealth.Healthy => Color.FromArgb(0x2E, 0xCC, 0x71),
        LimitHealth.Warning => Color.FromArgb(0xE6, 0x7E, 0x22),
        LimitHealth.Critical => Color.FromArgb(0xE7, 0x4C, 0x3C),
        _ => Color.FromArgb(0x95, 0xA5, 0xA6)
    };

    public static Icon Create(LimitHealth health)
    {
        const int size = 32;
        using var bitmap = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var brush = new SolidBrush(ColorFor(health));
            g.FillEllipse(brush, 3, 3, size - 6, size - 6);
        }

        var handle = bitmap.GetHicon();
        return Icon.FromHandle(handle);
    }
}
