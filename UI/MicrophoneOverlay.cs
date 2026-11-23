using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace MicPtt.UI;

/// <summary>
/// Independent desktop overlay. It never activates a window or attaches to a game.
/// All members, including disposal, are intended for the creating UI thread.
/// </summary>
public sealed class MicrophoneOverlay : Form
{
    private const int ExTransparent = 0x20;
    private const int ExToolWindow = 0x80;
    private const int ExLayered = 0x80000;
    private const int ExNoActivate = 0x08000000;
    private OverlaySettings settings = new();
    private bool active;
    private bool muted = true;
    private bool error;
    private bool editing;
    private bool dragging;
    private bool changingLayout;
    private string shortcut = "V";
    private string? inputProtection;
    private Point dragPointerOrigin;
    private Point dragWindowOrigin;

    public event Action<Point>? PositionChanged;

    public MicrophoneOverlay()
    {
        Text = "MicKey · 마이크 상태";
        AccessibleName = "MicKey 마이크 상태 오버레이";
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        AutoScaleMode = AutoScaleMode.None;
        TopMost = true;
        BackColor = Theme.Background;
        AllowTransparency = true;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        UpdateLayoutAndPosition(useSavedPosition: true);
    }

    protected override bool ShowWithoutActivation => true;

    protected override CreateParams CreateParams
    {
        get
        {
            var parameters = base.CreateParams;
            parameters.ExStyle |= ExToolWindow | ExNoActivate | ExLayered;
            if (!editing) parameters.ExStyle |= ExTransparent;
            else parameters.ExStyle &= ~ExTransparent;
            return parameters;
        }
    }

    public void ApplySettings(OverlaySettings replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        if (IsDisposed) return;
        settings = new OverlaySettings
        {
            Enabled = replacement.Enabled,
            OpacityPercent = replacement.OpacityPercent,
            ScalePercent = replacement.ScalePercent,
            ShowWhenInactive = replacement.ShowWhenInactive,
            X = replacement.X,
            Y = replacement.Y
        };
        settings.Normalize();
        UpdateLayoutAndPosition(useSavedPosition: true);
        ApplyNativeAppearance();
        UpdateVisibility();
        Invalidate();
    }

    public void SetStatus(bool active, bool muted, string shortcut, bool error = false, string? inputProtection = null)
    {
        if (IsDisposed) return;
        bool changed = this.active != active || this.muted != muted || this.error != error || this.shortcut != shortcut || this.inputProtection != inputProtection;
        this.active = active;
        this.muted = muted;
        this.error = error;
        this.shortcut = shortcut ?? "";
        this.inputProtection = inputProtection;
        UpdateVisibility();
        if (changed) Invalidate();
    }

    public void SetEditing(bool editing)
    {
        if (IsDisposed) return;
        if (this.editing == editing)
        {
            if (editing) EnableEditingWindow();
            return;
        }
        FinishDrag();
        this.editing = editing;
        Cursor = editing ? Cursors.SizeAll : Cursors.Default;
        UpdateLayoutAndPosition(useSavedPosition: false);
        ApplyNativeAppearance();
        UpdateVisibility();
        if (editing) EnableEditingWindow();
        Invalidate();
    }

    private void EnableEditingWindow()
    {
        if (!IsHandleCreated || IsDisposed) return;
        // ShowDialog disables every visible top-level HWND on its UI thread,
        // including this unowned overlay. Opt only this window back into mouse
        // input for the explicit positioning operation. EnableWindow neither
        // activates it nor re-enables the modal dialog's owner.
        EnableWindow(Handle, true);
    }

    private float RenderScale => DeviceDpi / 96f * settings.ScalePercent / 100f;

    private void UpdateVisibility()
    {
        bool shouldShow = editing || (settings.Enabled && (active || settings.ShowWhenInactive));
        if (shouldShow && !Visible)
        {
            // Deliberately do not assign an owner: minimizing the main window should
            // leave this status display visible above the user's game.
            Show();
            ApplyNativeAppearance();
        }
        else if (!shouldShow && Visible)
        {
            FinishDrag();
            Hide();
        }
    }

    private void UpdateLayoutAndPosition(bool useSavedPosition)
    {
        if (changingLayout || IsDisposed) return;
        changingLayout = true;
        try
        {
            float scale = RenderScale;
            ClientSize = new Size((int)Math.Round(282 * scale), (int)Math.Round((editing ? 98 : 72) * scale));
            var areas = Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray();
            Point requested = Location;
            if (useSavedPosition)
            {
                if (settings.X.HasValue && settings.Y.HasValue)
                    requested = new Point(settings.X.Value, settings.Y.Value);
                else
                {
                    var area = Screen.PrimaryScreen?.WorkingArea ?? SystemInformation.WorkingArea;
                    int margin = (int)Math.Round(24 * scale);
                    requested = new Point(area.Right - Width - margin, area.Top + margin);
                }
            }
            Location = OverlayPlacement.Clamp(requested, Size, areas);
            UpdateRoundedRegion();
        }
        finally { changingLayout = false; }
    }

    private void UpdateRoundedRegion()
    {
        if (Width < 1 || Height < 1) return;
        using var outline = Theme.Rounded(new RectangleF(0, 0, Width, Height), 18 * RenderScale);
        var previous = Region;
        Region = new Region(outline);
        previous?.Dispose();
    }

    private void ApplyNativeAppearance()
    {
        if (!IsHandleCreated || IsDisposed) return;
        nint currentStyle = GetWindowLongPtr(Handle, -20);
        long desiredStyle = currentStyle.ToInt64() | ExToolWindow | ExNoActivate | ExLayered;
        desiredStyle = editing ? desiredStyle & ~ExTransparent : desiredStyle | ExTransparent;
        SetWindowLongPtr(Handle, -20, (nint)desiredStyle);
        // Explicit layered alpha keeps click-through working even at 100% opacity.
        byte alpha = (byte)Math.Round(settings.OpacityPercent * 255.0 / 100);
        SetLayeredWindowAttributes(Handle, 0, alpha, 0x2);
        SetWindowPos(Handle, (nint)(-1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10 | 0x20);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyNativeAppearance();
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        UpdateLayoutAndPosition(useSavedPosition: true);
        ApplyNativeAppearance();
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        UpdateLayoutAndPosition(useSavedPosition: false);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        var graphics = e.Graphics;
        float scale = RenderScale;
        graphics.SmoothingMode = SmoothingMode.AntiAlias;
        graphics.Clear(Theme.Background);
        Color color = error ? Theme.Red : !active ? Theme.Secondary : muted ? Theme.Amber : Theme.Mint;

        using var outline = Theme.Rounded(new RectangleF(1, 1, Width - 2, Height - 2), 18 * scale);
        using var background = new SolidBrush(Theme.Background);
        using var border = new Pen(editing ? Theme.Mint : Theme.Edge, Math.Max(1, scale));
        graphics.FillPath(background, outline);
        graphics.DrawPath(border, outline);

        using var iconBackground = new SolidBrush(Color.FromArgb(35, color));
        using var iconShape = Theme.Rounded(new RectangleF(14 * scale, 14 * scale, 44 * scale, 44 * scale), 13 * scale);
        graphics.FillPath(iconBackground, iconShape);
        DrawMicrophone(graphics, color, scale);

        string title = error ? "마이크 확인 필요" : !active ? "제어 중지" : muted ? "음소거 중" : "마이크 켜짐";
        using var titleFont = new Font("맑은 고딕", 16 * scale, FontStyle.Bold, GraphicsUnit.Pixel);
        using var shortcutFont = new Font("맑은 고딕", 11.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
        var titleBounds = Rectangle.Round(new RectangleF(71 * scale, 12 * scale, Width - 86 * scale, 25 * scale));
        var shortcutBounds = Rectangle.Round(new RectangleF(71 * scale, 38 * scale, Width - 86 * scale, 20 * scale));
        var textFlags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.VerticalCenter;
        TextRenderer.DrawText(graphics, title, titleFont, titleBounds, color, textFlags);
        TextRenderer.DrawText(graphics, (inputProtection is null ? "단축키  " : "입력 보호 · ") + shortcut,
            shortcutFont, shortcutBounds, inputProtection is null ? Theme.Secondary : Theme.Amber, textFlags);

        if (editing)
        {
            using var separator = new Pen(Theme.Edge, Math.Max(1, scale));
            graphics.DrawLine(separator, 14 * scale, 71 * scale, Width - 14 * scale, 71 * scale);
            using var hintFont = new Font("맑은 고딕", 10.5f * scale, FontStyle.Regular, GraphicsUnit.Pixel);
            var hintBounds = Rectangle.Round(new RectangleF(12 * scale, 73 * scale, Width - 24 * scale, 22 * scale));
            TextRenderer.DrawText(graphics, "드래그하여 이동", hintFont, hintBounds, Theme.Mint,
                textFlags | TextFormatFlags.HorizontalCenter);
        }
    }

    private void DrawMicrophone(Graphics graphics, Color color, float scale)
    {
        var saved = graphics.Save();
        graphics.TranslateTransform(23 * scale, 21 * scale);
        graphics.ScaleTransform(scale, scale);
        using var brush = new SolidBrush(color);
        using var capsule = Theme.Rounded(new RectangleF(9, 0, 8, 17), 4);
        using var pen = new Pen(color, 1.8f) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        graphics.FillPath(brush, capsule);
        graphics.DrawArc(pen, 5, 6, 16, 16, 0, 180);
        graphics.DrawLine(pen, 13, 22, 13, 28);
        graphics.DrawLine(pen, 8, 28, 18, 28);
        if (muted || !active || error)
        {
            using var underlay = new Pen(Theme.Background, 5) { StartCap = LineCap.Round, EndCap = LineCap.Round };
            graphics.DrawLine(underlay, 2, 1, 24, 25);
            graphics.DrawLine(pen, 2, 1, 24, 25);
        }
        graphics.Restore(saved);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (!editing || e.Button != MouseButtons.Left) return;
        dragging = true;
        dragPointerOrigin = Cursor.Position;
        dragWindowOrigin = Location;
        Capture = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!editing || !dragging) return;
        Point pointer = Cursor.Position;
        var requested = new Point(dragWindowOrigin.X + pointer.X - dragPointerOrigin.X,
            dragWindowOrigin.Y + pointer.Y - dragPointerOrigin.Y);
        Location = OverlayPlacement.Clamp(requested, Size, Screen.AllScreens.Select(screen => screen.WorkingArea).ToArray());
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.Button == MouseButtons.Left) FinishDrag();
    }

    protected override void OnMouseCaptureChanged(EventArgs e)
    {
        base.OnMouseCaptureChanged(e);
        if (!Capture) FinishDrag();
    }

    private void FinishDrag()
    {
        if (!dragging) return;
        dragging = false;
        Capture = false;
        settings.X = Left;
        settings.Y = Top;
        PositionChanged?.Invoke(Location);
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x21) // WM_MOUSEACTIVATE: deliver mouse input without stealing focus.
        {
            message.Result = (nint)3; // MA_NOACTIVATE
            return;
        }
        if (message.Msg == 0x84 && !editing) // WM_NCHITTEST
        {
            message.Result = (nint)(-1); // HTTRANSPARENT, alongside layered WS_EX_TRANSPARENT.
            return;
        }
        base.WndProc(ref message);
        if (message.Msg == 0x7E && IsHandleCreated && !IsDisposed) // WM_DISPLAYCHANGE
            BeginInvoke((Action)(() => { if (!IsDisposed) UpdateLayoutAndPosition(useSavedPosition: false); }));
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            dragging = false;
            PositionChanged = null;
            var previous = Region;
            Region = null;
            previous?.Dispose();
        }
        base.Dispose(disposing);
    }

    private static nint GetWindowLongPtr(nint window, int index) => nint.Size == 8
        ? GetWindowLongPtrW(window, index) : (nint)GetWindowLongW(window, index);

    private static nint SetWindowLongPtr(nint window, int index, nint value) => nint.Size == 8
        ? SetWindowLongPtrW(window, index, value) : (nint)SetWindowLongW(window, index, value.ToInt32());

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetWindowLongPtrW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int GetWindowLongW(nint window, int index);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint SetWindowLongPtrW(nint window, int index, nint value);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern int SetWindowLongW(nint window, int index, int value);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetLayeredWindowAttributes(nint window, uint colorKey, byte alpha, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnableWindow(nint window, [MarshalAs(UnmanagedType.Bool)] bool enable);
}
