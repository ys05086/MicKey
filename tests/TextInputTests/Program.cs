using MicPtt.Input;
using System.Diagnostics;
using System.Windows.Automation;

var checks = 0;
void Check(bool condition, string label)
{
    if (!condition) throw new InvalidOperationException(label);
    checks++;
}

var edit = new FocusedInputMetadata(ControlType.Edit.Id, true, true, true, false, false, null, false, null);
Check(TextInputClassificationPolicy.Classify(edit) == new TextInputClassification(true, false), "Focused Edit without patterns suppresses shortcuts.");
Check(!TextInputClassificationPolicy.Classify(edit with { IsEnabled = false }).IsTextInput, "Disabled edit controls do not receive typing.");
Check(TextInputClassificationPolicy.Classify(edit with { HasKeyboardFocus = false }).IsUnknown, "A stale unfocused element is unknown.");
Check(TextInputClassificationPolicy.Classify(edit with { IsPassword = true }).IsTextInput, "Password role suppresses without reading its content.");
Check(!TextInputClassificationPolicy.Classify(edit with { HasValuePattern = true, ValueIsReadOnly = true }).IsTextInput, "Read-only Edit is not classified as writable.");

var document = edit with { ControlTypeId = ControlType.Document.Id, HasTextPattern = true };
Check(!TextInputClassificationPolicy.Classify(document).IsTextInput, "TextPattern presence alone is not proof of an input field.");
Check(!TextInputClassificationPolicy.Classify(document with { TextIsReadOnly = true }).IsTextInput, "Read-only web document is not an input field.");
Check(TextInputClassificationPolicy.Classify(document with { TextIsReadOnly = false }).IsTextInput, "Explicitly writable rich document is an input field.");

var custom = edit with { ControlTypeId = ControlType.Custom.Id, HasValuePattern = true };
Check(TextInputClassificationPolicy.Classify(custom with { ValueIsReadOnly = false }).IsTextInput, "Custom writable ValuePattern is an input field.");
Check(!TextInputClassificationPolicy.Classify(custom with { ValueIsReadOnly = true }).IsTextInput, "Read-only custom value is not an input field.");
var combo = custom with { ControlTypeId = ControlType.ComboBox.Id, ValueIsReadOnly = false };
Check(TextInputClassificationPolicy.Classify(combo).IsTextInput, "Editable combo box suppresses shortcuts.");
Check(!TextInputClassificationPolicy.Classify(combo with { ValueIsReadOnly = true }).IsTextInput, "Read-only combo box does not suppress as text input.");

foreach (var role in new[] { ControlType.Pane, ControlType.Window, ControlType.Button, ControlType.Text })
{
    var result = TextInputClassificationPolicy.Classify(edit with { ControlTypeId = role.Id, IsKeyboardFocusable = null });
    Check(result == new TextInputClassification(false, false), $"Generic {role.ProgrammaticName} without editing metadata does not permanently block gameplay.");
}
Check(TextInputClassificationPolicy.Classify(edit with { ControlTypeId = 0 }).IsUnknown, "Missing role and editability are unknown.");

var foreground = new ForegroundWindowInfo((nint)1234, 88);
var cache = new FocusSnapshotCache(foreground);
var oldProbe = cache.Capture();
var nonTextResult = new TextInputSnapshot(false, false, 88, (nint)1234, Stopwatch.GetTimestamp());
cache.Invalidate(foreground); // A text box gets focus inside the same native window.
Check(cache.Current.IsUnknown, "Same-HWND focus changes immediately invalidate cached non-text state.");
Check(!cache.TryPublish(oldProbe, nonTextResult, foreground), "Late prior-focus response cannot overwrite the same-HWND invalidation.");
Check(cache.Current.IsUnknown, "Rejecting a stale response keeps protection unknown until rechecked.");
var freshProbe = cache.Capture();
var textResult = nonTextResult with { IsTextInput = true };
Check(cache.TryPublish(freshProbe, textResult, foreground) && cache.Current.IsTextInput, "Fresh matching-focus result is accepted.");
Check(!cache.TryPublish(freshProbe, nonTextResult, foreground), "A consumed probe cannot overwrite a later result.");
var crossAppProbe = cache.Capture();
Check(!cache.TryPublish(crossAppProbe, textResult, new ForegroundWindowInfo((nint)9999, 89)), "A foreground process/window change rejects the response.");
Check(!cache.TryPublish(crossAppProbe, textResult, new ForegroundWindowInfo((nint)2222, 88)), "A different foreground window in the same process also rejects the response.");

if (args.Contains("--probe"))
{
    using var detector = new TextInputDetector();
    // Only role-derived booleans and HWND/PID metadata are printed; never text.
    for (int sample = 0; sample < 12; sample++)
    {
        Thread.Sleep(100);
        var timer = Stopwatch.StartNew();
        var snapshot = detector.Snapshot;
        var process = detector.GetForegroundProcessName();
        timer.Stop();
        Check(timer.ElapsedMilliseconds < 100, "Snapshot reads remain nonblocking.");
        Console.WriteLine($"text={snapshot.IsTextInput} unknown={snapshot.IsUnknown} pid={snapshot.ProcessId} hwnd={snapshot.WindowHandle} ageMs={snapshot.Age.TotalMilliseconds:F0} process={process ?? "pending"}");
    }
}

if (args.Contains("--native")) NativeTextInputFixture.Run(Check);

Console.WriteLine($"PASS: {checks} text-input detection checks.");
