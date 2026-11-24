using MicPtt;
using MicPtt.Input;
using System.Windows.Forms;

int checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

const int gamePid = 101, otherPid = 202;
nint gameWindow = 1001, otherWindow = 2002, secondGameWindow = 1002;
var settings = new InputGuardSettings
{
    ChatRules = [new() { ProcessName = "RobloxPlayerBeta.exe", OpenShortcut = new(Keys.T) }]
};
var guard = new TypingGuardPolicy();
guard.Configure(settings);
string? Reason(nint window = 1001, int pid = gamePid, bool text = false, bool unknown = false) =>
    guard.GetReason(window, pid, text, unknown);

Check(Reason() is null, "An idle game starts without a chat latch.");
Check(!guard.IsChatProtected(gameWindow, gamePid), "The explicit chat query is false for an idle game.");
Check(Reason(text: true) == "텍스트 입력 중", "Focused editable input activates protection.");
Check(Reason(unknown: true) == "입력 상태 확인 중", "Unknown focus blocks new activation.");
Check(!guard.IsChatProtected(gameWindow, gamePid), "Text and unknown metadata cannot create a game-chat latch.");
Check(!guard.ProcessCommand(Keys.T, otherWindow, otherPid, "Discord"), "A game's chat key does not apply in Discord.");
Check(!guard.ProcessCommand(Keys.Control | Keys.T, gameWindow, gamePid, "RobloxPlayerBeta"), "Ctrl must not activate a plain game-chat shortcut.");
Check(guard.ProcessCommand(Keys.T, gameWindow, gamePid, "robloxplayerbeta.EXE"), "Process matching ignores case and optional exe suffix.");
Check(Reason() == "게임 채팅 입력 중", "Chat key latches protection for the game.");
Check(guard.IsChatProtected(gameWindow, gamePid), "The explicit chat query identifies the latched game.");
Check(!guard.IsChatProtected(otherWindow, otherPid) && !guard.IsChatProtected(gameWindow, otherPid),
    "The chat query cannot confuse an app window or another process with the game latch.");
Check(Reason(otherWindow, otherPid) is null, "Alt-Tab does not apply the game's latch to another application.");
Check(Reason() == "게임 채팅 입력 중", "Returning from Alt-Tab retains the game's open chat.");
Check(Reason(secondGameWindow, gamePid) is null, "Another window of the same process has independent chat state.");
Check(Reason(gameWindow, otherPid) is null, "A window handle in a different process does not inherit a latch.");
Check(!guard.ProcessCommand(Keys.Enter, otherWindow, otherPid, "Discord"), "Enter elsewhere cannot close the game's latch.");
Check(!guard.ProcessCommand(Keys.Shift | Keys.Enter, gameWindow, gamePid, "RobloxPlayerBeta"), "Shift+Enter keeps game chat protected.");
Check(!guard.ProcessCommand(Keys.Control | Keys.Enter, gameWindow, gamePid, "RobloxPlayerBeta"), "Ctrl+Enter keeps game chat protected.");
Check(guard.ProcessCommand(Keys.Enter, gameWindow, gamePid, "RobloxPlayerBeta"), "Plain Enter clears only the matching game's latch.");
Check(!guard.IsChatProtected(gameWindow, gamePid), "A closed game chat no longer appears in the explicit chat query.");
Check(Reason() is null, "Closed chat no longer blocks ordinary shortcuts.");
Check(Reason(text: true) is not null, "Enter cannot override text input still reported by UIA.");
guard.ProcessCommand(Keys.T, gameWindow, gamePid, "RobloxPlayerBeta");
Check(guard.ProcessCommand(Keys.Escape, gameWindow, gamePid, null), "Escape closes a known latch without needing a process-name lookup.");
Check(!guard.ProcessCommand(Keys.T, nint.Zero, gamePid, "RobloxPlayerBeta"), "Invalid window context cannot create a latch.");
Check(!guard.ProcessCommand(Keys.T, gameWindow, 0, "RobloxPlayerBeta"), "Invalid process context cannot create a latch.");

Keys pause = Keys.Control | Keys.Alt | Keys.F10;
Check(guard.ProcessCommand(pause, nint.Zero, 0, null) && guard.ManualPaused, "Pause works even without an accessible foreground process.");
Check(Reason(text: true, unknown: true) == "단축키 일시정지", "Manual pause has clear UI priority.");
Check(!guard.IsChatProtected(gameWindow, gamePid), "Manual pause alone must not be tracked as a game-chat latch.");
guard.Configure(settings);
Check(guard.ManualPaused, "Reconfiguring retains manual pause.");
Check(guard.ProcessCommand(pause, gameWindow, gamePid, "RobloxPlayerBeta") && !guard.ManualPaused, "The next pause command resumes shortcut handling.");
guard.ProcessCommand(Keys.T, gameWindow, gamePid, "RobloxPlayerBeta");
guard.Configure(settings);
Check(Reason() == "게임 채팅 입력 중", "An unchanged configuration retains active game chat protection.");
settings.AutoDetectTextInput = false;
guard.Configure(settings);
Check(Reason() == "게임 채팅 입력 중", "Changing automatic detection does not discard game latches.");
Check(Reason(otherWindow, otherPid, text: true, unknown: true) is null, "Disabled automatic detection ignores both text and unknown metadata.");
settings.ChatRules[0].OpenShortcut = new(Keys.Y);
Check(guard.ProcessCommand(Keys.T, secondGameWindow, gamePid, "RobloxPlayerBeta"), "Policy owns a snapshot independent of later settings mutations.");
guard.Configure(settings);
Check(Reason() is null && Reason(secondGameWindow, gamePid) is null, "A changed chat rule clears obsolete latches.");
Check(!guard.ProcessCommand(Keys.T, gameWindow, gamePid, "RobloxPlayerBeta"), "Old chat keys no longer match after configuration.");
Check(guard.ProcessCommand(Keys.Y, gameWindow, gamePid, "RobloxPlayerBeta"), "Updated chat key is applied.");
settings.ChatKeyProtection = false;
guard.Configure(settings);
Check(Reason() is null, "Disabling chat-key protection removes its latch.");
Check(!guard.IsChatProtected(gameWindow, gamePid), "The chat query respects disabled chat-key protection.");
Check(!guard.ProcessCommand(Keys.Y, gameWindow, gamePid, "RobloxPlayerBeta"), "Disabled game rules cannot re-latch protection.");
settings.ChatKeyProtection = true;
settings.AutoDetectTextInput = true;
guard.Configure(settings);
Check(Reason() is null, "Re-enabling protection does not resurrect old chat latches.");
guard.ProcessCommand(Keys.Y, gameWindow, gamePid, "RobloxPlayerBeta");
guard.ProcessCommand(Keys.Y, secondGameWindow, gamePid, "RobloxPlayerBeta");
guard.ToggleManualPause();
Check(guard.IsChatProtected(gameWindow, gamePid), "The chat query still finds a game latch hidden by the manual-pause reason.");
Check(guard.ResumeCurrentWindow(gameWindow, gamePid), "Explicit resume clears manual pause and this game's latch.");
Check(!guard.ManualPaused && Reason() is null, "Explicit resume returns this game to idle protection state.");
Check(Reason(secondGameWindow, gamePid) == "게임 채팅 입력 중", "Explicit resume does not discard another window's chat state.");
Check(Reason(text: true) == "텍스트 입력 중", "Explicit resume never bypasses an actual focused text field.");
Check(!guard.ResumeCurrentWindow(gameWindow, gamePid), "Resuming an already-clear window is a no-op.");

// A command sharing the PTT key must win before the PTT edge is considered.
settings.ChatRules[0].OpenShortcut = new(Keys.V);
guard.Configure(settings);
var collision = new TalkStateMachine();
collision.Reset(TalkMode.ToggleMute, false);
bool handled = guard.ProcessCommand(Keys.V, gameWindow, gamePid, "RobloxPlayerBeta");
collision.SetInputBlocked(Reason() is not null, true);
if (!handled) collision.Update(true);
Check(handled && collision.Muted, "Opening chat with the PTT key cannot toggle the microphone first.");
settings.PauseShortcut = new(Keys.V);
guard.Configure(settings);
Check(Reason() == "게임 채팅 입력 중", "Changing the pause shortcut alone preserves existing game chat state.");
Check(guard.ProcessCommand(Keys.V, gameWindow, gamePid, "RobloxPlayerBeta") && !guard.ManualPaused && Reason() is null,
    "Pause command takes priority and resumes an already-latched game instead of adding manual pause.");
Check(guard.ProcessCommand(Keys.V, gameWindow, gamePid, "RobloxPlayerBeta") && guard.ManualPaused,
    "Pause command starts manual pause when this game has no active protection latch.");
Check(guard.ProcessCommand(Keys.V, gameWindow, gamePid, "RobloxPlayerBeta") && !guard.ManualPaused,
    "Pause command resumes a manual pause without opening chat when the commands collide.");
Check(Reason(text: true) == "텍스트 입력 중" && Reason(unknown: true) == "입력 상태 확인 중",
    "A pause shortcut cannot bypass UIA text or unknown protection.");

foreach (bool initiallyMuted in new[] { true, false })
{
    var toggle = new TalkStateMachine();
    toggle.Reset(TalkMode.ToggleMute, false);
    if (!initiallyMuted) { toggle.Update(true); toggle.Update(false); }
    Check(toggle.SetInputBlocked(true, false) == initiallyMuted, "Typing guard preserves the existing toggle state on entry.");
    Check(toggle.Update(true) == initiallyMuted && toggle.Update(false) == initiallyMuted,
        "Typed shortcut taps never alter a blocked toggle state.");
    Check(toggle.SetInputBlocked(false, false) == initiallyMuted, "Leaving typing preserves the toggle state.");
    Check(toggle.Update(false) == initiallyMuted, "No typed press replays after focus leaves an input.");
    Check(toggle.Update(true) != initiallyMuted, "A fresh press after leaving typing toggles once.");

    toggle.Reset(TalkMode.ToggleMute, false);
    if (!initiallyMuted) { toggle.Update(true); toggle.Update(false); }
    toggle.SetInputBlocked(true, false);
    toggle.Update(true);
    toggle.SetInputBlocked(false, true);
    Check(toggle.Update(true) == initiallyMuted, "A shortcut held while typing ends cannot immediately toggle.");
    Check(toggle.Update(false) == initiallyMuted, "Releasing the held shortcut does not toggle.");
    Check(toggle.Update(true) != initiallyMuted, "Releasing and pressing again rearms the toggle.");
}

var hold = new TalkStateMachine();
hold.Reset(TalkMode.HoldToTalk, false);
Check(!hold.Update(true), "Hold mode opens before text input receives focus.");
Check(hold.SetInputBlocked(true, true), "Entering typing immediately mutes a held microphone.");
Check(hold.Update(false), "Releasing while blocked always leaves hold mode muted.");
Check(hold.Update(true), "A new shortcut pressed while typing stays muted.");
hold.SetInputBlocked(false, true);
Check(hold.Update(true), "Leaving typing while holding the shortcut cannot reopen hold mode.");
Check(hold.Update(false), "Release after protection ends rearms without reopening.");
Check(!hold.Update(true), "A fresh post-protection hold opens the microphone.");
Check(hold.Update(false), "A normal hold release still mutes immediately.");

foreach (var mode in Enum.GetValues<TalkMode>())
{
    var state = new TalkStateMachine();
    state.Reset(mode, false);
    state.SetInputBlocked(false, true);
    Check(!state.Update(true), "Repeated unblocked snapshots must not swallow a pending press.");
    state.SetInputBlocked(true, true);
    state.ForceMute(true);
    Check(state.InputBlocked && state.Muted, "Safety mute preserves typing protection and overrides toggle-on state.");
    Check(state.Update(false) && state.Update(true), "A forced mute stays muted throughout typing.");
    state.Reset(mode, true);
    Check(!state.InputBlocked && state.Update(true), "Reset clears input protection while still requiring release of a held key.");
    Check(state.Update(false) && !state.Update(true), "The existing reset-release-press behavior remains intact.");
}

// Exercise the same raw-event ordering layer used by the Windows hook, without
// installing hooks or inspecting the user's desktop.
var ordered = new KeyboardSignalBuffer();
InputGuardSettings IntegrationSettings() => new()
{
    AutoDetectTextInput = true,
    ChatRules = [new() { ProcessName = "RobloxPlayerBeta", OpenShortcut = new(Keys.T) }]
};
var integrationGuard = new TypingGuardPolicy();
integrationGuard.Configure(IntegrationSettings());
var integratedTalk = new TalkStateMachine();
void ConfigureQueue(Keys ptt = Keys.V)
{
    ordered.Configure(new Hotkey(ptt), new bool[256]);
    ordered.ConfigureGuard([Keys.T, Keys.F10]);
}
void Key(Keys key, bool down, bool textBlocked = false, nint window = 1001, int pid = gamePid) =>
    ordered.Observe((uint)key, 0, 0, down, () => new(window, pid, textBlocked));
void Tap(Keys key, bool textBlocked = false, nint window = 1001, int pid = gamePid)
{
    Key(key, true, textBlocked, window, pid);
    Key(key, false, textBlocked, window, pid);
}
void Drain(nint currentWindow = 1001, int currentPid = gamePid, bool textInput = false)
{
    string? CurrentReason() => integrationGuard.GetReason(currentWindow, currentPid, textInput, false);
    // Synchronize against the processed state, not a key-down still in the queue.
    integratedTalk.SetInputBlocked(CurrentReason() is not null, integratedTalk.LastKeyDown);
    long consumed = -1;
    while (ordered.TryRead(out var signal))
    {
        if (signal.Kind == KeyboardSignalKind.Command)
        {
            string processName = signal.ProcessId == gamePid ? "RobloxPlayerBeta" : "Discord";
            if (integrationGuard.ProcessCommand(signal.KeyData, signal.WindowHandle, signal.ProcessId, processName))
                consumed = signal.EventId;
        }
        else
        {
            bool blocked = CurrentReason() is not null || signal.TextInputBlocked || consumed == signal.EventId
                || signal.WindowHandle != currentWindow || signal.ProcessId != currentPid;
            integratedTalk.Update(signal.IsDown, blocked);
        }
    }
    integratedTalk.SetInputBlocked(CurrentReason() is not null, ordered.CurrentIsDown);
}

ConfigureQueue();
integratedTalk.Reset(TalkMode.ToggleMute, false);
Tap(Keys.T);
Tap(Keys.V);
Drain();
Check(integratedTalk.Muted, "A fast T -> V burst in one queue cannot toggle after opening game chat.");
Tap(Keys.Enter);
Tap(Keys.V);
Drain();
Check(!integratedTalk.Muted, "Enter followed by a fresh PTT tap in the same queue resumes exactly once.");
Tap(Keys.T);
Tap(Keys.V);
Drain();
Check(!integratedTalk.Muted, "Opening chat and typing V preserves an already-on toggle state.");
Tap(Keys.Enter, window: otherWindow, pid: otherPid);
Tap(Keys.V);
Drain();
Check(!integratedTalk.Muted && integrationGuard.GetReason(gameWindow, gamePid, false, false) is not null,
    "An Enter event from Discord cannot clear game chat even when processed after returning to the game.");
integrationGuard.ResumeCurrentWindow(gameWindow, gamePid);

ConfigureQueue();
integratedTalk.Reset(TalkMode.ToggleMute, false);
integratedTalk.SetInputBlocked(true, false);
Tap(Keys.V, textBlocked: true);
Key(Keys.V, true);
Drain();
Check(!integratedTalk.Muted && ordered.CurrentIsDown,
    "A blocked tap followed by a distinct allowed press activates once even when both arrive before polling.");
Key(Keys.V, false);
Drain();
Check(!integratedTalk.Muted, "The fresh press's release does not add a second toggle.");
Tap(Keys.V, textBlocked: true);
Drain();
Check(!integratedTalk.Muted, "Captured UIA protection still applies after focus leaves the input before queue processing.");

ConfigureQueue();
integratedTalk.Reset(TalkMode.HoldToTalk, false);
Key(Keys.V, true);
Drain();
Check(!integratedTalk.Muted, "A held shortcut opens before game chat begins.");
Tap(Keys.T);
Drain();
Check(integratedTalk.Muted, "A command-only chat opener immediately mutes hold mode without waiting for a PTT edge.");
Key(Keys.V, false, textBlocked: true);
Drain();
Check(integratedTalk.Muted, "A release recorded during UIA protection still mutes hold mode.");
integrationGuard.ResumeCurrentWindow(gameWindow, gamePid);
Drain();
Key(Keys.V, true, textBlocked: true);
Drain();
Check(integratedTalk.Muted, "Leaving an input while its PTT key remains held never reopens the microphone.");
Key(Keys.V, false);
Key(Keys.V, true);
Drain();
Check(!integratedTalk.Muted, "The next real release and fresh press rearms hold mode.");
Key(Keys.V, false, window: otherWindow, pid: otherPid);
Drain(currentWindow: otherWindow, currentPid: otherPid, textInput: true);
Check(integratedTalk.Muted, "A hold release in another focused application cannot leave the microphone open.");

ConfigureQueue();
Key(Keys.LWin, true);
Tap(Keys.T);
Key(Keys.LWin, false);
Check(!ordered.TryRead(out _), "Windows+T is not mistaken for a plain game-chat command.");
Key(Keys.LControlKey, true);
Key(Keys.LMenu, true);
Key(Keys.LWin, true);
Tap(Keys.F10);
Key(Keys.LWin, false);
Key(Keys.LMenu, false);
Key(Keys.LControlKey, false);
Check(!ordered.TryRead(out _), "Windows+Ctrl+Alt+F10 cannot accidentally trigger the pause command.");
Key(Keys.LControlKey, true);
Key(Keys.V, true);
Key(Keys.LControlKey, false);
Check(!ordered.TryRead(out _), "Releasing Ctrl after Ctrl+V does not invent a fresh PTT press.");
Key(Keys.V, false);
Check(ordered.TryRead(out var releaseSignal) && releaseSignal.Kind == KeyboardSignalKind.Shortcut && !releaseSignal.IsDown,
    "A release is retained even when the corresponding synthetic press was suppressed.");

ConfigureQueue();
integrationGuard.ResumeCurrentWindow(gameWindow, gamePid);
integratedTalk.Reset(TalkMode.ToggleMute, false);
void PauseTap()
{
    Key(Keys.LControlKey, true);
    Key(Keys.LMenu, true);
    Tap(Keys.F10);
    Key(Keys.LMenu, false);
    Key(Keys.LControlKey, false);
}
PauseTap();
Tap(Keys.V);
Drain();
Check(integrationGuard.ManualPaused && integratedTalk.Muted, "A pause command blocks a following fast PTT tap in the same queue.");
PauseTap();
Tap(Keys.V);
Drain();
Check(!integrationGuard.ManualPaused && !integratedTalk.Muted, "Resume followed by a new tap in one queue toggles normally.");

ConfigureQueue(Keys.T);
Key(Keys.T, true);
Key(Keys.T, true); // Auto-repeat.
Key(Keys.T, false);
Check(ordered.TryRead(out var first) && first.Kind == KeyboardSignalKind.Command,
    "A shared chat/PTT key queues its command first.");
Check(ordered.TryRead(out var second) && second.Kind == KeyboardSignalKind.Shortcut && second.IsDown
    && second.EventId == first.EventId, "Command and PTT signals retain the same physical event identity.");
Check(ordered.TryRead(out var third) && third.Kind == KeyboardSignalKind.Shortcut && !third.IsDown
    && third.EventId != second.EventId, "The shared key's release follows in a distinct physical event.");
Check(!ordered.TryRead(out _), "Auto-repeat does not create duplicate commands or PTT presses.");

ConfigureQueue();
ordered.Observe((uint)Keys.V, 0, 0, true, () => throw new InvalidOperationException("Metadata probe unavailable"));
Check(ordered.TryRead(out var failedProbe) && failedProbe.TextInputBlocked,
    "A metadata-probe exception produces a blocked signal instead of escaping the native hook.");
int contextCalls = 0;
ordered.Observe((uint)Keys.A, 0, 0, true, () => { contextCalls++; return default; });
Check(contextCalls == 0 && !ordered.TryRead(out _), "Unrelated typed keys are not queued or sent to the metadata probe.");

// Holding Shift for sprint/capitalization must not create a gap between the
// microphone's plain-key matcher and the game's chat-opening protection.
var shiftedGuard = new TypingGuardPolicy();
var shiftedSettings = IntegrationSettings();
shiftedGuard.Configure(shiftedSettings);
Check(shiftedGuard.ProcessCommand(Keys.Shift | Keys.T, gameWindow, gamePid, "RobloxPlayerBeta")
    && shiftedGuard.IsChatProtected(gameWindow, gamePid),
    "A plain T chat rule protects Shift+T before UIA can observe the new chat focus.");
Check(!shiftedGuard.ProcessCommand(Keys.Shift | Keys.Enter, gameWindow, gamePid, "RobloxPlayerBeta")
    && shiftedGuard.IsChatProtected(gameWindow, gamePid),
    "Shift+Enter still cannot close a chat opened with Shift+T.");
Check(!shiftedGuard.ProcessCommand(Keys.Shift | Keys.Escape, gameWindow, gamePid, "RobloxPlayerBeta")
    && shiftedGuard.IsChatProtected(gameWindow, gamePid),
    "Shift+Escape also leaves the shifted chat latch intact.");
shiftedGuard.ResumeCurrentWindow(gameWindow, gamePid);
foreach (var modifiers in new[] { Keys.Control, Keys.Alt, Keys.Control | Keys.Shift, Keys.Alt | Keys.Shift })
{
    Check(!shiftedGuard.ProcessCommand(modifiers | Keys.T, gameWindow, gamePid, "RobloxPlayerBeta")
        && !shiftedGuard.IsChatProtected(gameWindow, gamePid),
        $"Unexpected {modifiers} cannot activate a plain T chat rule.");
}
Check(!shiftedGuard.ProcessCommand(pause | Keys.Shift, gameWindow, gamePid, "RobloxPlayerBeta")
    && !shiftedGuard.ManualPaused, "Adding Shift to the explicit pause chord must not activate it.");
shiftedSettings.PauseShortcut = new(Keys.F10);
shiftedGuard.Configure(shiftedSettings);
Check(!shiftedGuard.ProcessCommand(Keys.Shift | Keys.F10, gameWindow, gamePid, "RobloxPlayerBeta")
    && !shiftedGuard.ManualPaused, "Even a plain pause shortcut retains exact matching.");
Check(shiftedGuard.ProcessCommand(Keys.F10, gameWindow, gamePid, "RobloxPlayerBeta")
    && shiftedGuard.ManualPaused, "The exact plain pause shortcut continues to work.");
shiftedGuard.ResumeCurrentWindow(gameWindow, gamePid);
shiftedSettings.ChatRules[0].OpenShortcut = new(Keys.T, Shift: true);
shiftedGuard.Configure(shiftedSettings);
Check(!shiftedGuard.ProcessCommand(Keys.T, gameWindow, gamePid, "RobloxPlayerBeta"),
    "An explicitly configured Shift+T chat rule still requires Shift.");
Check(!shiftedGuard.ProcessCommand(Keys.Control | Keys.Shift | Keys.T, gameWindow, gamePid, "RobloxPlayerBeta"),
    "Explicit Shift+T cannot gain an unexpected Ctrl modifier.");
Check(shiftedGuard.ProcessCommand(Keys.Shift | Keys.T, gameWindow, gamePid, "RobloxPlayerBeta"),
    "The exact explicit Shift+T chat rule still activates normally.");

ConfigureQueue(Keys.T);
integrationGuard.Configure(IntegrationSettings());
integrationGuard.ResumeCurrentWindow(gameWindow, gamePid);
integratedTalk.Reset(TalkMode.ToggleMute, false);
Key(Keys.LShiftKey, true);
Key(Keys.W, true);
Tap(Keys.T);
Drain();
Check(integratedTalk.Muted && integrationGuard.IsChatProtected(gameWindow, gamePid),
    "Shift+W+T sharing the chat/PTT key prioritizes chat protection in the same event queue.");
Key(Keys.W, false);
Key(Keys.LShiftKey, false);
Drain();
Check(integratedTalk.Muted, "Releasing sprint keys cannot replay a chat-blocked PTT tap.");

// Without a T chat rule, the exact movement combination from the user report
// must reach the microphone policy, while uppercase typing remains protected.
var noChatRules = IntegrationSettings();
noChatRules.ChatRules.Clear();
integrationGuard.Configure(noChatRules);
foreach (var mode in Enum.GetValues<TalkMode>())
{
    ConfigureQueue(Keys.T);
    integratedTalk.Reset(mode, false);
    Key(Keys.LShiftKey, true);
    Key(Keys.W, true);
    Key(Keys.T, true);
    Drain();
    Check(!integratedTalk.Muted && !integrationGuard.IsChatProtected(gameWindow, gamePid),
        $"{mode}: Shift+W+T opens the microphone when no T chat rule is configured.");
    Key(Keys.T, false);
    Drain();
    Check(integratedTalk.Muted == (mode == TalkMode.HoldToTalk),
        $"{mode}: releasing T while sprinting preserves the configured hold/toggle semantics.");
}
foreach (bool initiallyMuted in new[] { true, false })
{
    ConfigureQueue(Keys.T);
    integratedTalk.Reset(TalkMode.ToggleMute, false);
    if (!initiallyMuted) { integratedTalk.Update(true); integratedTalk.Update(false); }
    Key(Keys.LShiftKey, true);
    Tap(Keys.T, textBlocked: true);
    Key(Keys.LShiftKey, false);
    Drain();
    Check(integratedTalk.Muted == initiallyMuted,
        "Uppercase T typed into an input preserves the current toggle state after Shift-compatible matching.");
}

ConfigureQueue(Keys.T);
integrationGuard.Configure(IntegrationSettings());
integrationGuard.ResumeCurrentWindow(gameWindow, gamePid);
integratedTalk.Reset(TalkMode.ToggleMute, false);
Key(Keys.LWin, true);
Key(Keys.LShiftKey, true);
Key(Keys.W, true);
Tap(Keys.T);
Key(Keys.W, false);
Key(Keys.LShiftKey, false);
Key(Keys.LWin, false);
Drain();
Check(integratedTalk.Muted && !integrationGuard.IsChatProtected(gameWindow, gamePid),
    "Windows+Shift+W+T is neither a game chat command nor a microphone shortcut.");

Console.WriteLine($"PASS: {checks} typing-guard policy and protected microphone-state checks.");
