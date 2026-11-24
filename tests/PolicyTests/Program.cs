using MicPtt.Input;
using System.Windows.Forms;

var checks = 0;
void Check(bool condition, string description)
{
    if (!condition) throw new InvalidOperationException(description);
    checks++;
}

var hold = new TalkStateMachine();
Check(hold.Muted, "New controller starts muted.");
hold.Reset(TalkMode.HoldToTalk, false);
Check(hold.Update(false), "Idle hold mode remains muted.");
Check(!hold.Update(true), "Press opens microphone in hold mode.");
Check(!hold.Update(true), "Repeated down samples keep microphone open.");
Check(hold.Update(false), "Release mutes immediately.");

foreach (var mode in Enum.GetValues<TalkMode>())
{
    var state = new TalkStateMachine();
    state.Reset(mode, true);
    Check(state.Update(true), $"{mode}: key held at activation must not unmute.");
    Check(state.Update(true), $"{mode}: held key stays blocked across polls.");
    Check(state.Update(false), $"{mode}: release alone must not unmute.");
    Check(!state.Update(true), $"{mode}: fresh press after activation unmutes.");
    state.ForceMute(true);
    Check(state.Muted, $"{mode}: safety interruption mutes immediately.");
    Check(state.Update(true), $"{mode}: held shortcut after interruption stays blocked.");
    Check(state.Update(false), $"{mode}: release after interruption stays muted.");
    Check(!state.Update(true), $"{mode}: fresh press after interruption works.");
    state.ForceMute(false);
    Check(state.Update(false), $"{mode}: interrupted idle input stays muted.");
    Check(!state.Update(true), $"{mode}: next press after idle interruption works.");
}

var toggle = new TalkStateMachine();
toggle.Reset(TalkMode.ToggleMute, false);
Check(!toggle.Update(true), "First toggle press opens microphone.");
Check(!toggle.Update(true), "Holding toggle key does not repeatedly toggle.");
Check(!toggle.Update(false), "Releasing toggle key leaves microphone open.");
Check(toggle.Update(true), "Second toggle press mutes.");
Check(toggle.Update(false), "Toggle release after mute remains muted.");
toggle.Reset(TalkMode.HoldToTalk, true);
Check(toggle.Update(true), "Mode change while held blocks microphone opening.");
Check(toggle.Update(false), "Mode-change release stays muted.");
Check(!toggle.Update(true), "Hold mode works after mode-change release.");
Check(toggle.Update(false), "Hold mode release works after mode change.");

Check(Hotkey.TryFromKeyData(Keys.V, out var letter) && letter == new Hotkey(Keys.V),
    "Plain keyboard key parses.");
Check(Hotkey.TryFromKeyData(Keys.Control | Keys.Shift | Keys.F8, out var combo)
    && combo == new Hotkey(Keys.F8, Control: true, Shift: true), "Modifier chord parses.");
Check(combo!.DisplayName == "Ctrl + Shift + F8", "Chord display is readable.");
Check(new Hotkey(Keys.D9).DisplayName == "9", "Number key has a readable display.");
Check(new Hotkey(Keys.NumPad9).DisplayName == "Num 9", "Numpad key is distinguishable.");
Check(new Hotkey(Keys.V).IsValid, "Valid persisted shortcut is accepted.");
Check(!new Hotkey(Keys.V | Keys.Control).IsValid, "Corrupted primary key flags are rejected.");

foreach (var key in new[] { Keys.None, Keys.ControlKey, Keys.LControlKey, Keys.RControlKey,
    Keys.ShiftKey, Keys.Menu, Keys.LWin, Keys.RWin, Keys.Escape, Keys.Enter, Keys.Tab,
    Keys.CapsLock, Keys.Control | Keys.Alt | Keys.Delete, Keys.Alt | Keys.F4,
    Keys.Alt | Keys.Space, (Keys)0x40000000, (Keys)0xFFFF })
{
    Check(!Hotkey.TryFromKeyData(key, out _), $"Reserved or invalid shortcut {key} is rejected.");
}

try
{
    hold.Reset((TalkMode)999, false);
    throw new Exception("Invalid mode accepted.");
}
catch (ArgumentOutOfRangeException)
{
    checks++;
}

// Hook events can arrive in a complete down/up burst between two UI timer ticks.
var edges = new ShortcutTransitionBuffer();
edges.Configure(new Hotkey(Keys.F8, Control: true, Shift: true), new bool[256]);
edges.Observe((uint)Keys.ControlKey, 0x1D, 0, true);
edges.Observe((uint)Keys.ShiftKey, 0x2A, 0, true);
edges.Observe((uint)Keys.F8, 0x42, 0, true);
edges.Observe((uint)Keys.F8, 0x42, 0, true);
edges.Observe((uint)Keys.F8, 0x42, 0, false);
edges.Observe((uint)Keys.ShiftKey, 0x2A, 0, false);
edges.Observe((uint)Keys.ControlKey, 0x1D, 0, false);
Check(edges.TryRead(out var edge) && edge, "Fast chord press is retained until the timer reads it.");
Check(edges.TryRead(out edge) && !edge, "Fast chord release is retained in order.");
Check(!edges.TryRead(out _), "Auto-repeat and modifier release do not add duplicate chord edges.");
Check(!edges.CurrentIsDown, "Final hook cache reflects the complete fast tap.");

edges.Configure(new Hotkey(Keys.F8, Control: true, Shift: true), new bool[256]);
edges.Observe((uint)Keys.ControlKey, 0x1D, 1, true); // Extended -> right Ctrl.
edges.Observe((uint)Keys.ShiftKey, 0x36, 0, true); // Scan code -> right Shift.
edges.Observe((uint)Keys.F8, 0x42, 0, true);
Check(edges.TryRead(out edge) && edge, "Right modifiers normalize correctly.");
edges.Observe((uint)Keys.ShiftKey, 0x36, 0, false);
Check(edges.TryRead(out edge) && !edge, "Releasing a required right modifier ends the chord.");

edges.Configure(new Hotkey(Keys.V), new bool[256]);
edges.Observe((uint)Keys.LControlKey, 0, 0, true);
edges.Observe((uint)Keys.V, 0, 0, true);
Check(!edges.TryRead(out _), "Unexpected modifier suppresses a plain shortcut.");
edges.Observe((uint)Keys.LControlKey, 0, 0, false);
Check(edges.TryRead(out edge) && edge, "Removing an unexpected modifier updates chord state.");
edges.Observe((uint)Keys.LWin, 0, 0, true);
Check(edges.TryRead(out edge) && !edge, "Windows modifier ends a chord.");

var heldSnapshot = new bool[256];
heldSnapshot[(int)Keys.V] = true;
edges.Configure(new Hotkey(Keys.V), heldSnapshot);
Check(edges.CurrentIsDown, "Configure snapshots an already-held key.");
Check(!edges.TryRead(out _), "Configure clears old transitions without inventing a press.");
var combined = new TalkStateMachine();
combined.Reset(TalkMode.ToggleMute, edges.CurrentIsDown);
edges.Observe((uint)Keys.V, 0, 0, false);
edges.Observe((uint)Keys.V, 0, 0, true);
edges.Observe((uint)Keys.V, 0, 0, false);
while (edges.TryRead(out edge)) combined.Update(edge);
Check(!combined.Muted, "Buffered fresh tap toggles exactly once after releasing an initial held key.");
edges.Observe((uint)Keys.V, 0, 0, true);
edges.Observe((uint)Keys.V, 0, 0, false);
while (edges.TryRead(out edge)) combined.Update(edge);
Check(combined.Muted, "Second buffered fast tap toggles back to mute.");

// Explicit window/tray actions must survive idle polling and shortcut-only
// typing guards without changing the configured keyboard mode.
var manualHold = new TalkStateMachine();
manualHold.Reset(TalkMode.HoldToTalk, false);
Check(!manualHold.ManualHoldOpen, "Hold mode starts without a manual-open latch.");
Check(!manualHold.ToggleManual(false) && manualHold.ManualHoldOpen,
    "A manual opening latches the microphone on in hold mode.");
Check(!manualHold.Update(false) && !manualHold.Update(false) && manualHold.ManualHoldOpen,
    "Idle timer polls cannot undo a manual opening.");
Check(!manualHold.SetInputBlocked(true, false) && manualHold.InputBlocked,
    "Typing protection preserves an explicitly opened microphone.");
Check(!manualHold.Update(true) && !manualHold.Update(false) && manualHold.ManualHoldOpen,
    "Shortcut presses while typing cannot consume the manual-open latch.");
Check(!manualHold.SetInputBlocked(true, false) && manualHold.ManualHoldOpen,
    "Repeated protection snapshots leave the manual opening intact.");
manualHold.Update(true);
manualHold.SetInputBlocked(false, true);
Check(!manualHold.Update(true) && manualHold.ManualHoldOpen,
    "A shortcut already held while typing ends cannot take over the manual opening.");
Check(!manualHold.Update(false) && manualHold.ManualHoldOpen,
    "Releasing a shortcut held during typing preserves the manual opening.");
Check(!manualHold.Update(true) && !manualHold.ManualHoldOpen,
    "A new allowed shortcut press returns manual-open hold mode to ordinary PTT.");
Check(manualHold.Update(false), "Releasing that fresh shortcut mutes in ordinary hold mode.");
Check(!manualHold.Update(true) && manualHold.Update(false),
    "The keyboard remains in hold mode after manual operation.");

manualHold.Reset(TalkMode.HoldToTalk, true);
Check(!manualHold.ToggleManual(true) && manualHold.ManualHoldOpen,
    "Manual opening works while the shortcut was already held.");
Check(!manualHold.Update(true) && !manualHold.Update(true),
    "Repeated held samples do not treat a pre-click shortcut as a fresh press.");
Check(!manualHold.Update(false) && manualHold.ManualHoldOpen,
    "The first release after a held-key manual click cannot cancel its latch.");
Check(manualHold.ToggleManual(false) && !manualHold.ManualHoldOpen,
    "A second explicit click closes a manually opened microphone.");
Check(manualHold.Update(false), "Idle polling preserves an explicit manual mute.");
manualHold.Update(true);
Check(manualHold.ToggleManual(true) && !manualHold.ManualHoldOpen,
    "Manual mute can close a microphone opened by a currently held PTT shortcut.");
Check(manualHold.Update(true) && manualHold.Update(false),
    "The pre-click held key and its release cannot reopen a manually muted microphone.");
Check(!manualHold.Update(true) && manualHold.Update(false),
    "A release and new press restore normal PTT after manual mute.");

manualHold.ToggleManual(false);
edges.Configure(new Hotkey(Keys.V), new bool[256]);
edges.Observe((uint)Keys.V, 0, 0, true);
edges.Observe((uint)Keys.V, 0, 0, false);
while (edges.TryRead(out edge)) manualHold.Update(edge);
Check(manualHold.Muted && !manualHold.ManualHoldOpen,
    "A complete fresh PTT tap between timer ticks ends the manual-open latch and mutes.");

foreach (var mode in Enum.GetValues<TalkMode>())
{
    var manual = new TalkStateMachine();
    manual.Reset(mode, false);
    manual.SetInputBlocked(true, false);
    Check(!manual.ToggleManual(false) && manual.InputBlocked,
        $"{mode}: explicit UI unmute works without disabling shortcut protection.");
    Check(!manual.Update(true) && !manual.Update(false),
        $"{mode}: typing cannot undo an explicit UI action.");
    Check(manual.ToggleManual(false) && manual.InputBlocked && !manual.ManualHoldOpen,
        $"{mode}: explicit UI mute works during shortcut protection.");
    manual.ToggleManual(true);
    manual.ForceMute(true);
    Check(manual.Muted && !manual.ManualHoldOpen && manual.InputBlocked,
        $"{mode}: a safety mute always clears the manual latch and retains protection.");
    Check(manual.Update(true) && manual.Update(false),
        $"{mode}: blocked keys cannot undo the safety mute.");
    manual.ToggleManual(true);
    manual.Reset(mode, true);
    Check(manual.Muted && !manual.ManualHoldOpen && !manual.InputBlocked,
        $"{mode}: reset clears manual opening and resets protection state.");
    Check(manual.Update(true) && manual.Update(false),
        $"{mode}: reset still requires release of a key held during a manual opening.");
    Check(!manual.Update(true), $"{mode}: a fresh press after reset still works.");
}

var manualToggle = new TalkStateMachine();
manualToggle.Reset(TalkMode.ToggleMute, false);
Check(!manualToggle.ToggleManual(true) && !manualToggle.ManualHoldOpen,
    "Toggle mode manual opening does not create a hold-mode latch.");
Check(!manualToggle.Update(true) && !manualToggle.Update(false),
    "A key held at a toggle-mode click does not immediately double-toggle.");
Check(manualToggle.Update(true), "A fresh keyboard press toggles off after manual toggle-mode opening.");
Check(!manualToggle.ToggleManual(true), "A second explicit UI action can open while a toggle key is held.");
Check(!manualToggle.Update(true) && !manualToggle.Update(false),
    "The held key and release preserve that second explicit action.");
Check(manualToggle.Update(true) && manualToggle.Update(false),
    "Subsequent keyboard operation stays in toggle mode after explicit UI actions.");

Console.WriteLine($"PASS: {checks} keyboard and microphone-policy checks.");
