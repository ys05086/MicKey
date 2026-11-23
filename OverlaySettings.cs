using System.Drawing;

namespace MicPtt;

public sealed class OverlaySettings
{
    public bool Enabled { get; set; }
    public int OpacityPercent { get; set; } = 85;
    public int ScalePercent { get; set; } = 100;
    public bool ShowWhenInactive { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }

    public void Normalize()
    {
        OpacityPercent = Math.Clamp(OpacityPercent, 35, 100);
        ScalePercent = Math.Clamp(ScalePercent, 75, 175);
        if (X.HasValue != Y.HasValue) X = Y = null;
    }
}

/// <summary>Placement policy independent of the current desktop, for deterministic tests.</summary>
internal static class OverlayPlacement
{
    public static Point Clamp(Point requested, Size size, IReadOnlyList<Rectangle> workingAreas)
    {
        var candidates = workingAreas.Where(area => area.Width > 0 && area.Height > 0).ToArray();
        if (candidates.Length == 0) return requested;

        // Prefer the monitor containing most of the overlay. If an old monitor was
        // disconnected, choose the nearest remaining work area instead.
        var chosen = candidates[0];
        double bestOverlap = -1;
        double bestDistance = double.MaxValue;
        double width = Math.Max(1, size.Width);
        double height = Math.Max(1, size.Height);
        double centerX = requested.X + width / 2;
        double centerY = requested.Y + height / 2;
        foreach (var area in candidates)
        {
            double overlapWidth = Math.Max(0, Math.Min(requested.X + width, (double)area.X + area.Width) - Math.Max(requested.X, area.X));
            double overlapHeight = Math.Max(0, Math.Min(requested.Y + height, (double)area.Y + area.Height) - Math.Max(requested.Y, area.Y));
            double overlap = overlapWidth * overlapHeight;
            double dx = centerX - Math.Clamp(centerX, area.X, (double)area.X + area.Width);
            double dy = centerY - Math.Clamp(centerY, area.Y, (double)area.Y + area.Height);
            double distance = dx * dx + dy * dy;
            if (overlap > bestOverlap || (overlap == bestOverlap && distance < bestDistance))
            {
                chosen = area;
                bestOverlap = overlap;
                bestDistance = distance;
            }
        }

        int maxX = (int)Math.Max(chosen.Left, (long)chosen.Left + chosen.Width - Math.Max(1, size.Width));
        int maxY = (int)Math.Max(chosen.Top, (long)chosen.Top + chosen.Height - Math.Max(1, size.Height));
        return new Point(Math.Clamp(requested.X, chosen.Left, maxX), Math.Clamp(requested.Y, chosen.Top, maxY));
    }
}
