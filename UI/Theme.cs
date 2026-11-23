using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MicPtt.UI;

internal static class Theme
{
    public static readonly Color Background = Color.FromArgb(17, 22, 30);
    public static readonly Color Surface = Color.FromArgb(26, 33, 44);
    public static readonly Color Edge = Color.FromArgb(47, 59, 75);
    public static readonly Color Text = Color.FromArgb(235, 241, 250);
    public static readonly Color Secondary = Color.FromArgb(154, 172, 194);
    public static readonly Color Mint = Color.FromArgb(112, 231, 193);
    public static readonly Color Amber = Color.FromArgb(255, 200, 117);
    public static readonly Color Red = Color.FromArgb(255, 137, 148);

    public static GraphicsPath Rounded(RectangleF r, float radius)
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

    public static Icon CreateIcon(Color color)
    {
        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Background);
            g.FillEllipse(bg, 0, 0, 32, 32);
            using var brush = new SolidBrush(color);
            using var capsule = Rounded(new RectangleF(12, 5, 8, 15), 4);
            g.FillPath(brush, capsule);
            using var pen = new Pen(color, 2.3f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            g.DrawArc(pen, 8, 11, 16, 14, 0, 180);
            g.DrawLine(pen, 16, 25, 16, 28);
            g.DrawLine(pen, 12, 28, 20, 28);
        }
        var handle = bitmap.GetHicon();
        try { return (Icon)Icon.FromHandle(handle).Clone(); }
        finally { DestroyIcon(handle); }
    }

    [DllImport("user32.dll")] private static extern bool DestroyIcon(IntPtr handle);
}

internal sealed class RoundedButton : Button
{
    public bool Accent { get; set; }
    public bool Selected { get; set; }
    private bool hover;
    public RoundedButton()
    {
        FlatStyle = FlatStyle.Flat;
        FlatAppearance.BorderSize = 0;
        Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer, true);
    }
    protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { hover = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? Theme.Background);
        e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        var fill = Accent ? Theme.Mint : Selected ? Color.FromArgb(35, 66, 64) : Theme.Surface;
        if (!Enabled) fill = Theme.Surface;
        else if (hover && !Accent) fill = ControlPaint.Light(fill, .12f);
        using var path = Theme.Rounded(new RectangleF(1, 1, Width - 3, Height - 3), 10 * DeviceDpi / 96f);
        using var brush = new SolidBrush(fill);
        using var pen = new Pen(Selected || Focused ? Theme.Mint : Theme.Edge, Focused ? 2 : 1);
        e.Graphics.FillPath(brush, path);
        e.Graphics.DrawPath(pen, path);
        TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle,
            !Enabled ? Theme.Secondary : Accent ? Theme.Background : Theme.Text,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);
    }
}
