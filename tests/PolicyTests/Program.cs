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

Console.WriteLine($"PASS: {checks} keyboard and microphone-policy checks.");
