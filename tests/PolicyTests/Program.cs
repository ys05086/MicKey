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

// Sprinting with Shift must not suppress an otherwise plain microphone key.
// Capture/equality and explicitly modified shortcuts still retain exact meanings.
var plainT = new Hotkey(Keys.T);
Check(plainT.MatchesKeyData(Keys.T) && plainT.MatchesKeyData(Keys.Shift | Keys.T),
    "A plain microphone binding accepts its key with or without Shift.");
Check(!plainT.MatchesKeyData(Keys.W) && !plainT.MatchesKeyData(Keys.Control | Keys.T)
    && !plainT.MatchesKeyData(Keys.Alt | Keys.T) && !plainT.MatchesKeyData(Keys.Shift | Keys.T, windowsDown: true),
    "Unrelated keys and Ctrl/Alt/Windows combinations remain separate from plain T.");
Check(!plainT.MatchesKeyData(Keys.T | (Keys)0x40000000), "Unknown key-data flags cannot match a microphone shortcut.");
Check(!plainT.MatchesState(false, false, false, true), "Shift alone never counts as the microphone key.");
Check(new Hotkey(Keys.T, Shift: true).MatchesKeyData(Keys.Shift | Keys.T)
    && !new Hotkey(Keys.T, Shift: true).MatchesKeyData(Keys.T), "Explicit Shift+T still requires Shift.");
Check(new Hotkey(Keys.T, Control: true).MatchesKeyData(Keys.Control | Keys.T)
    && !new Hotkey(Keys.T, Control: true).MatchesKeyData(Keys.Control | Keys.Shift | Keys.T),
    "Explicit Ctrl+T continues to reject extra Shift.");
Check(new Hotkey(Keys.T, Alt: true).MatchesKeyData(Keys.Alt | Keys.T)
    && !new Hotkey(Keys.T, Alt: true).MatchesKeyData(Keys.Alt | Keys.Shift | Keys.T),
    "Explicit Alt+T continues to reject extra Shift.");
Check(new Hotkey(Keys.T, Control: true, Shift: true).MatchesKeyData(Keys.Control | Keys.Shift | Keys.T)
    && !new Hotkey(Keys.T, Control: true, Shift: true).MatchesKeyData(Keys.Control | Keys.T),
    "Explicit Ctrl+Shift+T keeps both required modifiers.");
Check(Hotkey.TryFromKeyData(Keys.Shift | Keys.T, out var capturedShiftT)
    && capturedShiftT == new Hotkey(Keys.T, Shift: true) && capturedShiftT != plainT,
    "Shortcut capture and record equality do not discard an explicitly chosen Shift modifier.");

void Feed(KeyboardSignalBuffer buffer, Keys key, bool down) => buffer.Observe((uint)key, 0, 0, down,
    () => new KeyboardSignalContext((nint)1, 2, false));

List<bool> Pump(KeyboardSignalBuffer buffer, TalkStateMachine state)
{
    var observed = new List<bool>();
    while (buffer.TryRead(out var signal))
    {
        if (signal.Kind != KeyboardSignalKind.Shortcut) continue;
        observed.Add(signal.IsDown);
        state.Update(signal.IsDown);
    }
    return observed;
}

foreach (var mode in Enum.GetValues<TalkMode>())
foreach (var sprintShift in new[] { Keys.LShiftKey, Keys.RShiftKey })
{
    var buffer = new KeyboardSignalBuffer();
    buffer.Configure(plainT, new bool[256]);
    var state = new TalkStateMachine();
    state.Reset(mode, false);
    Feed(buffer, sprintShift, true);
    Feed(buffer, Keys.W, true);
    Check(Pump(buffer, state).Count == 0 && state.Muted, $"{mode}/{sprintShift}: sprint/movement alone has no microphone edge.");
    Feed(buffer, Keys.T, true);
    Check(Pump(buffer, state).SequenceEqual([true]) && !state.Muted,
        $"{mode}/{sprintShift}: pressing T while Shift+W are held activates the microphone.");
    Check(buffer.CurrentIsDown && plainT.MatchesState(true, false, false, true),
        $"{mode}/{sprintShift}: hook state and polling matcher agree while sprinting.");

    Feed(buffer, sprintShift, false);
    Feed(buffer, sprintShift, true);
    Keys otherShift = sprintShift == Keys.LShiftKey ? Keys.RShiftKey : Keys.LShiftKey;
    Feed(buffer, otherShift, true);
    Feed(buffer, sprintShift, false);
    Feed(buffer, otherShift, false);
    Feed(buffer, Keys.W, false);
    Feed(buffer, Keys.W, true);
    Check(Pump(buffer, state).Count == 0 && buffer.CurrentIsDown && !state.Muted,
        $"{mode}/{sprintShift}: both Shift keys and movement can change while T stays held, without mute or retoggle.");
    Check(plainT.MatchesState(true, false, false, false),
        $"{mode}/{sprintShift}: releasing optional Shift cannot make polling mute held PTT.");
    Feed(buffer, Keys.T, false);
    Check(Pump(buffer, state).SequenceEqual([false]) && state.Muted == (mode == TalkMode.HoldToTalk),
        $"{mode}/{sprintShift}: releasing T retains normal hold/toggle semantics.");

    state.Reset(mode, false);
    Feed(buffer, sprintShift, true);
    Feed(buffer, Keys.T, true);
    Feed(buffer, Keys.T, false);
    Check(Pump(buffer, state).SequenceEqual([true, false]) && state.Muted == (mode == TalkMode.HoldToTalk),
        $"{mode}/{sprintShift}: a complete fast T tap during Shift+W is preserved between timer ticks.");
    Feed(buffer, Keys.T, true);
    Feed(buffer, Keys.T, false);
    Pump(buffer, state);
    Check(state.Muted, $"{mode}/{sprintShift}: the next complete tap ends muted with the configured mode unchanged.");
}

// Releasing Ctrl/Alt/Windows while T is held must not manufacture a new press.
foreach (var extraModifier in new[] { Keys.LControlKey, Keys.RControlKey, Keys.LMenu, Keys.RMenu, Keys.LWin, Keys.RWin })
{
    var buffer = new KeyboardSignalBuffer();
    buffer.Configure(plainT, new bool[256]);
    var state = new TalkStateMachine();
    state.Reset(TalkMode.HoldToTalk, false);
    Feed(buffer, Keys.LShiftKey, true);
    Feed(buffer, Keys.W, true);
    Feed(buffer, extraModifier, true);
    Feed(buffer, Keys.T, true);
    Check(Pump(buffer, state).Count == 0 && state.Muted, $"Extra {extraModifier} still blocks a shifted plain binding.");
    Feed(buffer, extraModifier, false);
    Check(Pump(buffer, state).Count == 0 && state.Muted, $"Releasing extra {extraModifier} does not invent a T press.");
    Feed(buffer, Keys.T, false);
    Pump(buffer, state);
    Feed(buffer, Keys.T, true);
    Check(Pump(buffer, state).SequenceEqual([true]) && !state.Muted, $"Fresh T after releasing {extraModifier} works while Shift+W remain held.");
    Feed(buffer, extraModifier, true);
    Check(Pump(buffer, state).SequenceEqual([false]) && state.Muted, $"Pressing {extraModifier} during held PTT still mutes.");
    Feed(buffer, extraModifier, false);
    Check(Pump(buffer, state).Count == 0 && state.Muted, $"Removing interrupting {extraModifier} cannot reopen PTT until a fresh key press.");
}

var requiredShiftBuffer = new KeyboardSignalBuffer();
requiredShiftBuffer.Configure(new Hotkey(Keys.T, Shift: true), new bool[256]);
var requiredShiftState = new TalkStateMachine();
requiredShiftState.Reset(TalkMode.HoldToTalk, false);
Feed(requiredShiftBuffer, Keys.LShiftKey, true);
Feed(requiredShiftBuffer, Keys.RShiftKey, true);
Feed(requiredShiftBuffer, Keys.W, true);
Feed(requiredShiftBuffer, Keys.T, true);
Check(Pump(requiredShiftBuffer, requiredShiftState).SequenceEqual([true]) && !requiredShiftState.Muted,
    "Explicit Shift+T opens while its required modifier is held.");
Feed(requiredShiftBuffer, Keys.LShiftKey, false);
Check(Pump(requiredShiftBuffer, requiredShiftState).Count == 0 && !requiredShiftState.Muted,
    "Explicit Shift+T remains held when the other Shift key is still down.");
Feed(requiredShiftBuffer, Keys.RShiftKey, false);
Check(Pump(requiredShiftBuffer, requiredShiftState).SequenceEqual([false]) && requiredShiftState.Muted,
    "Releasing the last required Shift mutes explicit Shift+T even while T stays down.");

var shiftedSnapshot = new bool[256];
shiftedSnapshot[(int)Keys.LShiftKey] = shiftedSnapshot[(int)Keys.W] = shiftedSnapshot[(int)Keys.T] = true;
var shiftedManualBuffer = new KeyboardSignalBuffer();
shiftedManualBuffer.Configure(plainT, shiftedSnapshot);
var shiftedManual = new TalkStateMachine();
shiftedManual.Reset(TalkMode.HoldToTalk, shiftedManualBuffer.CurrentIsDown);
Check(shiftedManualBuffer.CurrentIsDown && shiftedManual.Muted, "Shift+W+T held at activation remains blocked until release.");
shiftedManual.ToggleManual(true);
Feed(shiftedManualBuffer, Keys.LShiftKey, false);
Feed(shiftedManualBuffer, Keys.W, false);
Feed(shiftedManualBuffer, Keys.T, false);
Pump(shiftedManualBuffer, shiftedManual);
Check(!shiftedManual.Muted && shiftedManual.ManualHoldOpen,
    "Manual opening during held Shift+W+T survives releases from the old chord.");
Feed(shiftedManualBuffer, Keys.LShiftKey, true);
Feed(shiftedManualBuffer, Keys.W, true);
Feed(shiftedManualBuffer, Keys.T, true);
Pump(shiftedManualBuffer, shiftedManual);
Check(!shiftedManual.Muted && !shiftedManual.ManualHoldOpen,
    "Fresh shifted T transfers manual-open hold mode back to normal PTT.");
shiftedManual.ToggleManual(true);
Feed(shiftedManualBuffer, Keys.LShiftKey, false);
Feed(shiftedManualBuffer, Keys.W, false);
Check(Pump(shiftedManualBuffer, shiftedManual).Count == 0 && shiftedManual.Muted,
    "Manual mute while Shift+W+T is held is not undone by Shift/movement release.");
Feed(shiftedManualBuffer, Keys.T, false);
Pump(shiftedManualBuffer, shiftedManual);
Feed(shiftedManualBuffer, Keys.T, true);
Check(Pump(shiftedManualBuffer, shiftedManual).SequenceEqual([true]) && !shiftedManual.Muted,
    "Release then fresh T works after manual mute of the shifted chord.");

Console.WriteLine($"PASS: {checks} keyboard and microphone-policy checks.");
