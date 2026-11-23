using MicPtt.Input;

namespace MicPtt;

public sealed class InputGuardSettings
{
    public bool AutoDetectTextInput { get; set; } = true;
    public bool ChatKeyProtection { get; set; } = true;
    public Hotkey PauseShortcut { get; set; } = new(Keys.F10, Control: true, Alt: true);
    public List<ChatKeyRule> ChatRules { get; set; } =
    [
        new() { ProcessName = "RobloxPlayerBeta", OpenShortcut = new(Keys.OemQuestion) },
        new() { ProcessName = "RobloxStudioBeta", OpenShortcut = new(Keys.OemQuestion) }
    ];

    public void Normalize()
    {
        if (PauseShortcut is null || !PauseShortcut.IsValid) PauseShortcut = new(Keys.F10, Control: true, Alt: true);
        ChatRules ??= [];
        ChatRules = ChatRules.Where(r => r is not null).Select(r => { r.Normalize(); return r; })
            .Where(r => r.ProcessName.Length > 0).GroupBy(r => r.ProcessName, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.Last()).Take(32).ToList();
    }
}

public sealed class ChatKeyRule
{
    public string ProcessName { get; set; } = "";
    public Hotkey OpenShortcut { get; set; } = new(Keys.T);

    public void Normalize()
    {
        ProcessName = (ProcessName ?? "").Trim();
        if (ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) ProcessName = ProcessName[..^4];
        if (ProcessName.Length > 100 || ProcessName.IndexOfAny(['/', '\\', ':', '\r', '\n', '\0']) >= 0) ProcessName = "";
        if (OpenShortcut is null || !OpenShortcut.IsValid) OpenShortcut = new(Keys.T);
    }

    public override string ToString() => ProcessName + ".exe  ·  " + OpenShortcut.DisplayName;
}
