using System.Windows.Forms;

namespace MicPtt.Input;

/// <summary>
/// Pure shortcut protection policy. Stores only pause state and which game
/// windows have received their configured chat command; it stores no typed text.
/// The caller processes commands before the PTT edge from the same key event.
/// </summary>
public sealed class TypingGuardPolicy
{
    private bool autoDetectTextInput = true;
    private bool chatKeyProtection = true;
    private Hotkey pauseShortcut = new(Keys.F10, Control: true, Alt: true);
    private Dictionary<string, Hotkey> chatRules = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<(nint Window, int ProcessId)> chatWindows = new();

    public bool ManualPaused { get; private set; }

    public TypingGuardPolicy() => Configure(new InputGuardSettings());

    /// <summary>
    /// Takes an independent, normalized snapshot. Manual pause survives settings
    /// changes; chat latches clear only when chat protection or its rules change.
    /// </summary>
    public void Configure(InputGuardSettings replacement)
    {
        ArgumentNullException.ThrowIfNull(replacement);
        var snapshot = new InputGuardSettings
        {
            AutoDetectTextInput = replacement.AutoDetectTextInput,
            ChatKeyProtection = replacement.ChatKeyProtection,
            PauseShortcut = replacement.PauseShortcut,
            ChatRules = replacement.ChatRules?.Where(rule => rule is not null)
                .Select(rule => new ChatKeyRule
                {
                    ProcessName = rule.ProcessName,
                    OpenShortcut = rule.OpenShortcut
                }).ToList() ?? []
        };
        snapshot.Normalize();
        var nextRules = snapshot.ChatRules.ToDictionary(rule => rule.ProcessName,
            rule => rule.OpenShortcut, StringComparer.OrdinalIgnoreCase);
        bool rulesChanged = chatRules.Count != nextRules.Count || chatRules.Any(pair =>
            !nextRules.TryGetValue(pair.Key, out var nextShortcut) || pair.Value != nextShortcut);
        if (chatKeyProtection != snapshot.ChatKeyProtection || rulesChanged)
            chatWindows.Clear();

        autoDetectTextInput = snapshot.AutoDetectTextInput;
        chatKeyProtection = snapshot.ChatKeyProtection;
        pauseShortcut = snapshot.PauseShortcut;
        chatRules = nextRules;
    }

    public void ToggleManualPause() => ManualPaused = !ManualPaused;

    /// <summary>
    /// Whether this exact game window has an active chat-key latch. Unlike
    /// GetReason, this ignores manual pause and accessibility focus metadata.
    /// </summary>
    public bool IsChatProtected(nint window, int processId) =>
        chatKeyProtection && chatWindows.Contains((window, processId));

    /// <summary>
    /// Handles one fresh key-down command, never keyboard auto-repeat. The pause
    /// shortcut resumes manual pause/current game chat if either is active;
    /// otherwise it starts manual pause. Automatic text detection is unaffected.
    /// True means
    /// this guard handled the event, so the caller must skip any PTT activation
    /// from that same event. It does not mean consuming the Windows key message.
    /// Window/process identify the foreground context at the time of the event.
    /// </summary>
    public bool ProcessCommand(Keys keyData, nint window, int processId, string? processName)
    {
        Hotkey.TryFromKeyData(keyData, out var pressed);
        if (pressed is not null && pressed == pauseShortcut)
        {
            if (ManualPaused || chatWindows.Contains((window, processId)))
                ResumeCurrentWindow(window, processId);
            else
                ManualPaused = true;
            return true;
        }

        if (!chatKeyProtection || window == nint.Zero || processId <= 0) return false;
        var identity = (window, processId);
        string name = NormalizeProcessName(processName);
        if (pressed is not null && chatRules.TryGetValue(name, out var chatShortcut)
            && pressed == chatShortcut)
        {
            chatWindows.Add(identity);
            return true;
        }

        // Shift+Enter may insert a newline; other modified commands are also not
        // evidence that game chat closed. Only close this exact window's latch.
        if (keyData is Keys.Enter or Keys.Escape)
            return chatWindows.Remove(identity);
        return false;
    }

    /// <summary>
    /// Returns why shortcut activation is blocked, or null when allowed. Unknown
    /// UIA results block new activation only while automatic detection is enabled.
    /// </summary>
    public string? GetReason(nint window, int processId, bool detectedText, bool unknown)
    {
        if (ManualPaused) return "단축키 일시정지";
        if (IsChatProtected(window, processId)) return "게임 채팅 입력 중";
        if (!autoDetectTextInput) return null;
        if (detectedText) return "텍스트 입력 중";
        return unknown ? "입력 상태 확인 중" : null;
    }

    /// <summary>
    /// Explicit user recovery for a stale game-chat latch. Resumes manual pause
    /// and clears only this window; automatic text detection remains effective.
    /// Returns true when stored protection state changed.
    /// </summary>
    public bool ResumeCurrentWindow(nint window, int processId)
    {
        bool changed = ManualPaused;
        ManualPaused = false;
        return chatWindows.Remove((window, processId)) || changed;
    }

    private static string NormalizeProcessName(string? processName)
    {
        string name = (processName ?? "").Trim();
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}
