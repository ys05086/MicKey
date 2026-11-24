using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace MicPtt.Input;

public enum KeyboardSignalKind { Command, Shortcut }
public readonly record struct KeyboardSignal(KeyboardSignalKind Kind, bool IsDown, Keys KeyData,
    nint WindowHandle, int ProcessId, bool TextInputBlocked, long EventId);

/// <summary>
/// Observes shortcut edges without consuming input. Create, configure, read and
/// dispose on the same thread, which must run a Windows message loop (the UI thread).
/// The callback only updates a small keyboard-state cache and queues chord edges;
/// audio and UI work belong to the caller after TryRead.
/// </summary>
public sealed class GlobalShortcutObserver : IDisposable
{
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private readonly KeyboardHook callback;
    private readonly KeyboardSignalBuffer buffer = new();
    public Func<bool>? TextInputBlockedProbe { get; set; }
    private nint hook;
    private bool disposed;

    public GlobalShortcutObserver(Hotkey shortcut)
    {
        Configure(shortcut);
        callback = ObserveKeyboard;
        hook = SetWindowsHookExW(13, callback, GetModuleHandleW(null), 0);
        if (hook == nint.Zero)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "전역 단축키를 감지할 수 없습니다.");
    }

    public bool CurrentIsDown
    {
        get
        {
            VerifyAccess();
            return buffer.CurrentIsDown;
        }
    }

    /// <summary>
    /// Discards old edges and snapshots current key state. Reset the talk policy
    /// using CurrentIsDown immediately afterward to block an already-held shortcut.
    /// </summary>
    public void Configure(Hotkey shortcut)
    {
        VerifyAccess();
        ArgumentNullException.ThrowIfNull(shortcut);
        if (!shortcut.IsValid) throw new ArgumentException("유효하지 않은 단축키입니다.", nameof(shortcut));

        var snapshot = new bool[256];
        for (var key = 0; key < snapshot.Length; key++)
            snapshot[key] = (GetAsyncKeyState(key) & 0x8000) != 0;
        buffer.Configure(shortcut, snapshot);
    }

    public void ConfigureGuard(IEnumerable<Keys> guardKeys)
    {
        VerifyAccess();
        buffer.ConfigureGuard(guardKeys);
    }

    public bool TryReadSignal(out KeyboardSignal signal)
    {
        VerifyAccess();
        return buffer.TryRead(out signal);
    }

    /// <summary>
    /// Returns queued chord transitions in order, including complete presses shorter
    /// than the timer interval. CurrentIsDown is the last event state, not an OS poll.
    /// </summary>
    public bool TryRead(out bool isDown)
    {
        VerifyAccess();
        while (buffer.TryRead(out var signal))
        {
            if (signal.Kind != KeyboardSignalKind.Shortcut) continue;
            isDown = signal.IsDown;
            return true;
        }
        isDown = false;
        return false;
    }

    private nint ObserveKeyboard(int code, nint message, nint data)
    {
        if (code >= 0 && !disposed)
        {
            var kind = (int)message;
            if (kind is 0x0100 or 0x0101 or 0x0104 or 0x0105)
            {
                var key = Marshal.PtrToStructure<KeyboardEvent>(data);
                bool down = kind is 0x0100 or 0x0104;
                buffer.Observe(key.VirtualKey, key.ScanCode, key.Flags, down, () =>
                {
                    nint foregroundWindow = GetForegroundWindow();
                    GetWindowThreadProcessId(foregroundWindow, out uint foregroundProcess);
                    bool textBlocked = TextInputBlockedProbe?.Invoke() ?? false;
                    return new KeyboardSignalContext(foregroundWindow, unchecked((int)foregroundProcess), textBlocked);
                });
            }
        }
        // Never swallow input: Roblox and all other applications receive it normally.
        return CallNextHookEx(hook, code, message, data);
    }

    private void VerifyAccess()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("단축키 감지기는 생성한 UI 스레드에서 사용해야 합니다.");
    }

    public void Dispose()
    {
        if (disposed) return;
        VerifyAccess();
        disposed = true;
        if (hook != nint.Zero)
        {
            UnhookWindowsHookEx(hook);
            hook = nint.Zero;
        }
        // Keep the delegate rooted through unhooking; callbacks use this exact instance.
        GC.KeepAlive(callback);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardEvent
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Timestamp;
        public nuint ExtraInfo;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint KeyboardHook(int code, nint message, nint data);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowsHookExW(int hookType, KeyboardHook callback, nint module, uint threadId);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint CallNextHookEx(nint hook, int code, nint message, nint data);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("kernel32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandleW(string? module);

    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern short GetAsyncKeyState(int virtualKey);
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern nint GetForegroundWindow();
    [DllImport("user32.dll", ExactSpelling = true)]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);
}

internal readonly record struct KeyboardSignalContext(nint WindowHandle, int ProcessId, bool TextInputBlocked);

/// <summary>
/// Pure ordering layer for the native observer. Only relevant commands and PTT
/// edges enter its queue; other typed keys are never retained as an event history.
/// </summary>
internal sealed class KeyboardSignalBuffer
{
    private readonly ShortcutTransitionBuffer chord = new();
    private readonly Queue<KeyboardSignal> signals = new();
    private HashSet<Keys> commandKeys = [Keys.Return, Keys.Escape];
    private long eventSequence;

    public bool CurrentIsDown => chord.CurrentIsDown;

    public void Configure(Hotkey shortcut, bool[] snapshot)
    {
        chord.Configure(shortcut, snapshot);
        signals.Clear();
    }

    public void ConfigureGuard(IEnumerable<Keys> guardKeys)
    {
        ArgumentNullException.ThrowIfNull(guardKeys);
        commandKeys = guardKeys.Select(key => key & Keys.KeyCode).ToHashSet();
        commandKeys.Add(Keys.Return);
        commandKeys.Add(Keys.Escape);
        signals.Clear();
    }

    public bool TryRead(out KeyboardSignal signal) => signals.TryDequeue(out signal);

    public void Observe(uint virtualKey, uint scanCode, uint flags, bool down,
        Func<KeyboardSignalContext> readContext)
    {
        bool edge = chord.Observe(virtualKey, scanCode, flags, down);
        bool command = edge && down && !chord.WindowsModifierDown && commandKeys.Contains((Keys)virtualKey);
        bool hasShortcut = chord.TryRead(out bool shortcutDown);
        // Releasing an extra modifier can make a held key match the chord. That
        // is not a fresh press: Ctrl+V / Win+V must not become PTT on key release.
        if (hasShortcut && shortcutDown && !down) hasShortcut = false;
        if (!command && !hasShortcut) return;

        KeyboardSignalContext context;
        try { context = readContext(); }
        catch { context = new(nint.Zero, 0, true); }
        long sequence = ++eventSequence;
        // A chat opener or pause always precedes its possible PTT edge, even if
        // the complete key press and release arrive between two timer ticks.
        if (command) signals.Enqueue(new(KeyboardSignalKind.Command, true,
            (Keys)virtualKey | chord.Modifiers, context.WindowHandle, context.ProcessId, context.TextInputBlocked, sequence));
        if (hasShortcut) signals.Enqueue(new(KeyboardSignalKind.Shortcut, shortcutDown,
            Keys.None, context.WindowHandle, context.ProcessId, context.TextInputBlocked, sequence));
    }
}

/// <summary>Pure reducer shared with the policy tests; no Win32 calls or side effects.</summary>
internal sealed class ShortcutTransitionBuffer
{
    private readonly bool[] keys = new bool[256];
    private readonly Queue<bool> transitions = new();
    private Hotkey shortcut = new(Keys.V);

    public bool CurrentIsDown { get; private set; }
    public bool WindowsModifierDown => keys[(int)Keys.LWin] || keys[(int)Keys.RWin];
    public Keys Modifiers => (keys[(int)Keys.LControlKey] || keys[(int)Keys.RControlKey] ? Keys.Control : Keys.None)
        | (keys[(int)Keys.LMenu] || keys[(int)Keys.RMenu] ? Keys.Alt : Keys.None)
        | (keys[(int)Keys.LShiftKey] || keys[(int)Keys.RShiftKey] ? Keys.Shift : Keys.None);

    public void Configure(Hotkey replacement, bool[] snapshot)
    {
        shortcut = replacement;
        Array.Copy(snapshot, keys, keys.Length);
        transitions.Clear();
        CurrentIsDown = ReadChord();
    }

    public bool TryRead(out bool down) => transitions.TryDequeue(out down);

    public bool Observe(uint virtualKey, uint scanCode, uint flags, bool down)
    {
        if (virtualKey >= keys.Length) return false;
        var key = NormalizeModifier((int)virtualKey, scanCode, flags);
        if (keys[key] == down) return false; // Keyboard auto-repeat is not a new press.
        keys[key] = down;
        var chordDown = ReadChord();
        if (chordDown == CurrentIsDown) return true;
        CurrentIsDown = chordDown;
        transitions.Enqueue(chordDown);
        return true;
    }

    private bool ReadChord() => shortcut.MatchesState(keys[(int)shortcut.Key],
        keys[(int)Keys.LControlKey] || keys[(int)Keys.RControlKey],
        keys[(int)Keys.LMenu] || keys[(int)Keys.RMenu],
        keys[(int)Keys.LShiftKey] || keys[(int)Keys.RShiftKey],
        WindowsModifierDown);

    private static int NormalizeModifier(int key, uint scanCode, uint flags) => (Keys)key switch
    {
        Keys.ControlKey => (int)((flags & 1) != 0 ? Keys.RControlKey : Keys.LControlKey),
        Keys.Menu => (int)((flags & 1) != 0 ? Keys.RMenu : Keys.LMenu),
        Keys.ShiftKey => (int)(scanCode == 0x36 ? Keys.RShiftKey : Keys.LShiftKey),
        _ => key
    };
}
