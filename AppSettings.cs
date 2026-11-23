using System.Text.Json;
using MicPtt.Input;

namespace MicPtt;

internal sealed class AppSettings
{
    public string? DeviceId { get; set; }
    public Hotkey Shortcut { get; set; } = new(Keys.V);
    public TalkMode Mode { get; set; } = TalkMode.HoldToTalk;
    public SoundSettings Sound { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();

    public static string StoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicKey", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(StoragePath)) ?? new();
            if (settings.Shortcut is null || !settings.Shortcut.IsValid)
                settings.Shortcut = new(Keys.V);
            if (!Enum.IsDefined(settings.Mode)) settings.Mode = TalkMode.HoldToTalk;
            settings.Sound ??= new();
            settings.Overlay ??= new();
            settings.Sound.Normalize();
            settings.Overlay.Normalize();
            return settings;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new();
        }
    }

    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(StoragePath)!);
        string temporary = StoragePath + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, StoragePath, true);
    }
}
