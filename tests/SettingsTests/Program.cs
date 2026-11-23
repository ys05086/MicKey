using System.Text;
using System.Text.Json;
using MicPtt;
using MicPtt.Audio;
using MicPtt.Input;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

static void ExpectReadFailure(string path, string message)
{
    try { AppSettings.ReadFrom(path); }
    catch (Exception error) when (error is JsonException or InvalidDataException)
    {
        Assert(!string.IsNullOrWhiteSpace(error.Message), "Import errors should explain the problem");
        return;
    }
    throw new InvalidOperationException(message);
}

// Deliberately never call Load(), Save(), or access the user's AppData files.
var temporaryParent = Path.GetFullPath(Path.GetTempPath());
var directoryName = "MicKey.SettingsTests." + Guid.NewGuid().ToString("N");
var testRoot = Path.GetFullPath(Path.Combine(temporaryParent, directoryName));
Directory.CreateDirectory(testRoot);
try
{
    var source = new AppSettings
    {
        DeviceId = "{0.0.1.00000000}.{example-device-on-another-computer}",
        Shortcut = new Hotkey(Keys.F8, Control: true, Shift: true),
        Mode = TalkMode.ToggleMute,
        Sound = new SoundSettings
        {
            Enabled = false, VolumePercent = 47,
            OutputDeviceId = "endpoint:headphones-on-another-computer",
            OnTone = FeedbackTone.Digital, OffTone = FeedbackTone.Click
        },
        Overlay = new OverlaySettings
        {
            Enabled = true, OpacityPercent = 63, ScalePercent = 140,
            ShowWhenInactive = true, X = -2100, Y = 340
        },
        InputGuard = new InputGuardSettings
        {
            AutoDetectTextInput = false, ChatKeyProtection = false,
            PauseShortcut = new Hotkey(Keys.F9, Control: true, Alt: true),
            ChatRules =
            [
                new ChatKeyRule { ProcessName = "GameClient.exe", OpenShortcut = new Hotkey(Keys.T) },
                new ChatKeyRule { ProcessName = "RobloxPlayerBeta", OpenShortcut = new Hotkey(Keys.OemQuestion) }
            ]
        }
    };
    source.Normalize();
    var exportPath = Path.Combine(testRoot, "exports", "설정.json");
    source.SaveTo(exportPath);
    var loaded = AppSettings.ReadFrom(exportPath);
    Assert(JsonSerializer.Serialize(source) == JsonSerializer.Serialize(loaded), "Every setting must roundtrip");
    Assert(loaded.DeviceId == source.DeviceId && loaded.Sound.OutputDeviceId == source.Sound.OutputDeviceId,
        "Import must preserve foreign-device choices without silently selecting another device");
    Assert(!File.ReadAllText(exportPath).Contains("DisplayName"), "Exports should omit computed shortcut display properties");

    var copied = new AppSettings();
    copied.CopyFrom(source);
    Assert(JsonSerializer.Serialize(source) == JsonSerializer.Serialize(copied), "CopyFrom must include every setting");
    Assert(!ReferenceEquals(source.Sound, copied.Sound) && !ReferenceEquals(source.Overlay, copied.Overlay)
        && !ReferenceEquals(source.InputGuard, copied.InputGuard), "Editable child settings must not alias the source");
    Assert(!ReferenceEquals(source.InputGuard.ChatRules, copied.InputGuard.ChatRules)
        && !ReferenceEquals(source.InputGuard.ChatRules[0], copied.InputGuard.ChatRules[0]),
        "Guard rule lists and their editable rules must be copied independently");
    var clone = source.Clone();
    source.Sound.VolumePercent = 12;
    source.Overlay.X = 100;
    source.InputGuard.ChatRules[0].ProcessName = "ChangedGame";
    Assert(copied.Sound.VolumePercent == 47 && clone.Sound.VolumePercent == 47 && copied.Overlay.X == -2100,
        "Editing the source must not mutate an imported/copied snapshot");
    Assert(copied.InputGuard.ChatRules[0].ProcessName == "GameClient"
        && clone.InputGuard.ChatRules[0].ProcessName == "GameClient"
        && !copied.InputGuard.AutoDetectTextInput && !copied.InputGuard.ChatKeyProtection
        && copied.InputGuard.PauseShortcut == new Hotkey(Keys.F9, Control: true, Alt: true),
        "Guard settings must roundtrip and nested rule edits must stay isolated");
    copied.CopyFrom(copied);
    Assert(copied.Sound.VolumePercent == 47, "CopyFrom should also support self-copy");

    var invalidPath = Path.Combine(testRoot, "invalid.json");
    foreach (var text in new[]
    {
        "", "  ", "{broken}", "null", "42", "true", "[]",
        "{\"Sound\":\"loud\"}", "{\"Shortcut\":3}", "{\"Mode\":1.5}",
        "{\"Sound\":{\"VolumePercent\":\"loud\"}}"
    })
    {
        File.WriteAllText(invalidPath, text);
        ExpectReadFailure(invalidPath, $"Invalid root or property type was accepted: {text}");
    }
    File.WriteAllText(invalidPath, new string(' ', 1024 * 1024 + 1));
    ExpectReadFailure(invalidPath, "Oversized imports must be rejected before parsing");
    File.WriteAllText(invalidPath, "{\"unknown\":" + new string('[', 40) + "0" + new string(']', 40) + "}");
    ExpectReadFailure(invalidPath, "Excessive JSON nesting must be rejected");

    var legacyPath = Path.Combine(testRoot, "legacy.json");
    File.WriteAllText(legacyPath, "{}", new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    var legacy = AppSettings.ReadFrom(legacyPath);
    Assert(legacy.Shortcut == new Hotkey(Keys.V) && legacy.Mode == TalkMode.HoldToTalk,
        "Older files with missing properties retain defaults (including UTF-8 BOM files)");
    File.WriteAllText(legacyPath,
        "{\"DeviceId\":\"  \",\"Shortcut\":null,\"Mode\":999,\"Sound\":null,\"Overlay\":null,\"InputGuard\":null}");
    var normalized = AppSettings.ReadFrom(legacyPath);
    Assert(normalized.DeviceId is null && normalized.Shortcut == new Hotkey(Keys.V)
        && normalized.Mode == TalkMode.HoldToTalk && normalized.Sound is not null
        && normalized.Overlay is not null && normalized.InputGuard is not null,
        "Null and invalid settings must normalize to usable defaults");
    File.WriteAllText(legacyPath,
        "{\"Shortcut\":{\"Key\":46,\"Control\":true,\"Alt\":true}," +
        "\"Sound\":{\"VolumePercent\":200,\"OutputDeviceId\":\" \",\"OnTone\":999,\"OffTone\":-1}," +
        "\"Overlay\":{\"OpacityPercent\":0,\"ScalePercent\":1000,\"X\":10,\"Y\":null}}");
    normalized = AppSettings.ReadFrom(legacyPath);
    Assert(normalized.Shortcut == new Hotkey(Keys.V), "Reserved Windows shortcuts should normalize");
    Assert(normalized.Sound.VolumePercent == 100 && normalized.Sound.OutputDeviceId == "default"
        && normalized.Sound.OnTone == FeedbackTone.Soft && normalized.Sound.OffTone == FeedbackTone.Soft,
        "Sound import bounds and enum values should normalize");
    Assert(normalized.Overlay.OpacityPercent == 35 && normalized.Overlay.ScalePercent == 175
        && normalized.Overlay.X is null && normalized.Overlay.Y is null, "Overlay import should normalize");
    File.WriteAllText(legacyPath,
        "{\"InputGuard\":{\"PauseShortcut\":null,\"ChatRules\":[" +
        "{\"ProcessName\":\" sample.exe \",\"OpenShortcut\":null}," +
        "{\"ProcessName\":\"SAMPLE\",\"OpenShortcut\":{\"Key\":114}}," +
        "{\"ProcessName\":\"C:\\\\bad.exe\"},null]}}");
    normalized = AppSettings.ReadFrom(legacyPath);
    Assert(normalized.InputGuard.PauseShortcut == new Hotkey(Keys.F10, Control: true, Alt: true)
        && normalized.InputGuard.ChatRules.Count == 1 && normalized.InputGuard.ChatRules[0].ProcessName == "SAMPLE"
        && normalized.InputGuard.ChatRules[0].OpenShortcut == new Hotkey(Keys.F3),
        "Guard imports must normalize pause shortcut, process names, duplicate rules and null rules");
    var collision = new AppSettings { Shortcut = new Hotkey(Keys.F10, Control: true, Alt: true) };
    collision.Normalize();
    Assert(collision.InputGuard.PauseShortcut == new Hotkey(Keys.F11, Control: true, Alt: true),
        "The default pause shortcut must move to F11 when it collides with the microphone shortcut");
    collision.Shortcut = new Hotkey(Keys.V);
    collision.InputGuard.PauseShortcut = new Hotkey(Keys.V);
    collision.Normalize();
    Assert(collision.InputGuard.PauseShortcut == new Hotkey(Keys.F10, Control: true, Alt: true),
        "A custom pause/microphone collision must restore the normal pause shortcut");

    var atomicPath = Path.Combine(testRoot, "atomic.json");
    copied.SaveTo(atomicPath);
    var originalBytes = File.ReadAllBytes(atomicPath);
    var replacement = copied.Clone();
    replacement.Sound.VolumePercent = 89;
    using (var held = new FileStream(atomicPath, FileMode.Open, FileAccess.Read, FileShare.Read))
    {
        bool failed = false;
        try { replacement.SaveTo(atomicPath); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failed = true; }
        Assert(failed, "Replacing a target without delete sharing should fail");
        Assert(File.ReadAllBytes(atomicPath).SequenceEqual(originalBytes), "A failed atomic replacement must preserve the old target");
    }
    Assert(Directory.GetFiles(testRoot, "*.tmp", SearchOption.AllDirectories).Length == 0,
        "Failed atomic saves should clean up their unique temporary files");
    replacement.DeviceId = new string('x', 1024 * 1024 + 1);
    bool rejectedLargeSave = false;
    try { replacement.SaveTo(atomicPath); }
    catch (InvalidDataException) { rejectedLargeSave = true; }
    Assert(rejectedLargeSave && File.ReadAllBytes(atomicPath).SequenceEqual(originalBytes),
        "Oversized exports must preserve an existing target");

    var concurrentPath = Path.Combine(testRoot, "concurrent.json");
    Parallel.For(0, 12, index =>
    {
        var next = copied.Clone();
        next.Sound.VolumePercent = index;
        next.SaveTo(concurrentPath);
    });
    Assert(AppSettings.ReadFrom(concurrentPath).Sound.VolumePercent is >= 0 and < 12,
        "Concurrent saves must leave one complete valid file");
    Assert(Directory.GetFiles(testRoot, "*.tmp", SearchOption.AllDirectories).Length == 0,
        "Successful atomic saves should not leave temporary files");

    var previousWorkingDirectory = Environment.CurrentDirectory;
    try
    {
        Environment.CurrentDirectory = testRoot;
        copied.SaveTo("relative-settings.json");
        Assert(AppSettings.ReadFrom("relative-settings.json").Shortcut == copied.Shortcut,
            "A filename without a directory should save in the current directory");
    }
    finally { Environment.CurrentDirectory = previousWorkingDirectory; }

    Console.WriteLine("PASS: settings roundtrip, normalization, deep copy, invalid/large JSON, atomic failure preservation, concurrent and relative saves");
}
finally
{
    // Only delete the exact, uniquely named test directory created above.
    var expected = Path.GetFullPath(Path.Combine(temporaryParent, directoryName));
    if (string.Equals(testRoot, expected, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Path.GetDirectoryName(testRoot)?.TrimEnd(Path.DirectorySeparatorChar),
            temporaryParent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase))
        Directory.Delete(testRoot, recursive: true);
}
