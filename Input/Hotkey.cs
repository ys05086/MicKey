using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MicPtt.Input;

/// <summary>A keyboard shortcut observed without consuming or injecting key events.</summary>
public sealed record Hotkey(Keys Key, bool Control = false, bool Alt = false, bool Shift = false)
{
    public string DisplayName
    {
        get
        {
            var parts = new List<string>(4);
            if (Control) parts.Add("Ctrl");
            if (Alt) parts.Add("Alt");
            if (Shift) parts.Add("Shift");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    public bool IsValid => TryFromKeyData(
        Key | (Control ? Keys.Control : Keys.None) |
        (Alt ? Keys.Alt : Keys.None) | (Shift ? Keys.Shift : Keys.None), out var parsed)
        && parsed == this;

    /// <summary>
    /// Reads the current keyboard state. A plain binding permits Shift used for
    /// movement; explicit modifier bindings still require their exact modifiers.
    /// Only the high bit is used; the low bit is shared across other processes.
    /// </summary>
    public bool IsDown()
    {
        return MatchesState(IsKeyDown(Key), IsKeyDown(Keys.ControlKey), IsKeyDown(Keys.Menu),
            IsKeyDown(Keys.ShiftKey), IsKeyDown(Keys.LWin) || IsKeyDown(Keys.RWin));
    }

    /// <summary>
    /// Shared pure matching rule for polling and hook events. Only a completely
    /// unmodified binding tolerates extra Shift. Ctrl, Alt and Windows shortcuts
    /// remain separate, and explicitly configured modifier chords remain exact.
    /// </summary>
    public bool MatchesState(bool keyDown, bool controlDown, bool altDown, bool shiftDown, bool windowsDown = false)
    {
        bool plainBinding = !Control && !Alt && !Shift;
        return keyDown && !windowsDown && controlDown == Control && altDown == Alt
            && (plainBinding || shiftDown == Shift);
    }

    /// <summary>
    /// Matches event metadata without changing shortcut capture or record equality.
    /// Keys has no Windows-modifier flag; callers with that metadata pass it separately.
    /// </summary>
    public bool MatchesKeyData(Keys keyData, bool windowsDown = false)
    {
        const Keys knownBits = Keys.KeyCode | Keys.Control | Keys.Alt | Keys.Shift;
        if ((keyData & ~knownBits) != 0 || (keyData & Keys.KeyCode) != Key) return false;
        return MatchesState(true, (keyData & Keys.Control) != 0, (keyData & Keys.Alt) != 0,
            (keyData & Keys.Shift) != 0, windowsDown);
    }

    public static bool TryFromKeyData(Keys keyData, out Hotkey? hotkey)
    {
        hotkey = null;
        const Keys knownBits = Keys.KeyCode | Keys.Control | Keys.Alt | Keys.Shift;
        if ((keyData & ~knownBits) != 0) return false;

        var key = keyData & Keys.KeyCode;
        var control = (keyData & Keys.Control) != 0;
        var alt = (keyData & Keys.Alt) != 0;
        var shift = (keyData & Keys.Shift) != 0;

        if (!IsBindableKey(key)) return false;
        // These chords belong to Windows and cannot be used reliably by this app.
        if (control && alt && key == Keys.Delete) return false;
        if (alt && (key == Keys.F4 || key == Keys.Space)) return false;

        hotkey = new Hotkey(key, control, alt, shift);
        return true;
    }

    private static bool IsBindableKey(Keys key)
    {
        if (key is >= Keys.A and <= Keys.Z
            or >= Keys.D0 and <= Keys.D9
            or >= Keys.NumPad0 and <= Keys.Divide
            or >= Keys.F1 and <= Keys.F24)
            return true;

        return key is Keys.Back or Keys.Space or Keys.PageUp or Keys.PageDown
            or Keys.End or Keys.Home or Keys.Left or Keys.Up or Keys.Right or Keys.Down
            or Keys.Insert or Keys.Delete or Keys.Pause or Keys.PrintScreen
            or Keys.OemSemicolon or Keys.Oemplus or Keys.Oemcomma or Keys.OemMinus
            or Keys.OemPeriod or Keys.OemQuestion or Keys.Oemtilde
            or Keys.OemOpenBrackets or Keys.OemPipe or Keys.OemCloseBrackets
            or Keys.OemQuotes or Keys.OemBackslash;
    }

    private static string KeyName(Keys key) => key switch
    {
        >= Keys.D0 and <= Keys.D9 => ((int)key - (int)Keys.D0).ToString(),
        >= Keys.NumPad0 and <= Keys.NumPad9 => "Num " + ((int)key - (int)Keys.NumPad0),
        Keys.Back => "Backspace",
        Keys.PageUp => "Page Up",
        Keys.PageDown => "Page Down",
        Keys.PrintScreen => "Print Screen",
        Keys.Add => "Num +",
        Keys.Subtract => "Num -",
        Keys.Multiply => "Num *",
        Keys.Divide => "Num /",
        Keys.Decimal => "Num .",
        Keys.Separator => "Num Separator",
        Keys.OemSemicolon => ";",
        Keys.Oemplus => "=",
        Keys.Oemcomma => ",",
        Keys.OemMinus => "-",
        Keys.OemPeriod => ".",
        Keys.OemQuestion => "/",
        Keys.Oemtilde => "`",
        Keys.OemOpenBrackets => "[",
        Keys.OemPipe => "\\",
        Keys.OemCloseBrackets => "]",
        Keys.OemQuotes => "'",
        Keys.OemBackslash => "OEM 102",
        _ => key.ToString()
    };

    private static bool IsKeyDown(Keys key) => (GetAsyncKeyState((int)key) & 0x8000) != 0;

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);
}
