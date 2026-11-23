using MicPtt;
using System.Drawing;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
}

var settings = new OverlaySettings();
Check(!settings.Enabled && settings.OpacityPercent == 85 && settings.ScalePercent == 100 && !settings.ShowWhenInactive,
    "Defaults keep the overlay opt-in.");
settings.OpacityPercent = -10;
settings.ScalePercent = 900;
settings.X = 17;
settings.Normalize();
Check(settings.OpacityPercent == 35 && settings.ScalePercent == 175, "Low opacity and excessive scale are clamped.");
Check(settings.X is null && settings.Y is null, "An incomplete saved point resets to the default placement.");
settings.OpacityPercent = 200;
settings.ScalePercent = -1;
settings.X = -1200;
settings.Y = -300;
settings.Normalize();
Check(settings.OpacityPercent == 100 && settings.ScalePercent == 75, "High opacity and low scale are clamped.");
Check(settings.X == -1200 && settings.Y == -300, "Valid negative monitor coordinates are preserved.");

var primary = new Rectangle(0, 0, 1920, 1040);
var left = new Rectangle(-1920, 0, 1920, 1040);
var above = new Rectangle(0, -1080, 1920, 1080);
var size = new Size(282, 72);
Check(OverlayPlacement.Clamp(new(100, 150), size, [primary]) == new Point(100, 150), "On-screen placement stays unchanged.");
Check(OverlayPlacement.Clamp(new(1900, 1000), size, [primary]) == new Point(1638, 968), "Bottom-right overflow is clamped.");
Check(OverlayPlacement.Clamp(new(-100, -50), size, [primary]) == Point.Empty, "Top-left overflow is clamped.");
Check(OverlayPlacement.Clamp(new(-1500, 100), size, [left, primary]) == new Point(-1500, 100), "Left monitor placement remains negative.");
Check(OverlayPlacement.Clamp(new(200, -800), size, [above, primary]) == new Point(200, -800), "Above-primary monitor placement remains negative.");
Check(OverlayPlacement.Clamp(new(-1500, 100), size, [primary]) == new Point(0, 100), "Disconnected left monitor is recovered onto primary.");
Check(OverlayPlacement.Clamp(new(4000, 3000), size, [left, primary]) == new Point(1638, 968), "Far-away saved placement selects nearest screen.");
Check(OverlayPlacement.Clamp(new(-200, 100), size, [left, primary]) == new Point(-282, 100), "Straddling placement selects greatest monitor overlap.");
Check(OverlayPlacement.Clamp(new(-30, 100), size, [left, primary]) == new Point(0, 100), "Mostly-primary placement moves fully onto primary.");
Check(OverlayPlacement.Clamp(new(10, 10), new Size(3000, 2000), [primary]) == Point.Empty, "Oversized overlay anchors at work-area origin.");
Check(OverlayPlacement.Clamp(new(int.MaxValue, int.MinValue), size, [primary]) == new Point(1638, 0), "Extreme serialized coordinates do not overflow.");
Check(OverlayPlacement.Clamp(new(31, 41), size, []) == new Point(31, 41), "An unavailable desktop does not invent a monitor.");

if (args.Contains("--native")) NativeOverlayTests.Run(Check);

Console.WriteLine($"PASS: {checks} overlay checks.");
