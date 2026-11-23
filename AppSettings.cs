using System.Text.Json;
using MicPtt.Input;

namespace MicPtt;

internal sealed class AppSettings
{
    private const int MaximumFileBytes = 1024 * 1024;
    private static readonly object SaveGate = new();
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IgnoreReadOnlyProperties = true,
        MaxDepth = 32
    };

    public string? DeviceId { get; set; }
    public Hotkey Shortcut { get; set; } = new(Keys.V);
    public TalkMode Mode { get; set; } = TalkMode.HoldToTalk;
    public SoundSettings Sound { get; set; } = new();
    public OverlaySettings Overlay { get; set; } = new();
    public InputGuardSettings InputGuard { get; set; } = new();

    public static string StoragePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "MicKey", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            return ReadFrom(StoragePath);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            // Do not rewrite a corrupt or inaccessible file here. Startup must
            // remain read-only; the caller decides when an explicit save occurs.
            var defaults = new AppSettings();
            defaults.Normalize();
            return defaults;
        }
    }

    public void Normalize()
    {
        if (string.IsNullOrWhiteSpace(DeviceId)) DeviceId = null;
        if (Shortcut is null || !Shortcut.IsValid) Shortcut = new(Keys.V);
        if (!Enum.IsDefined(Mode)) Mode = TalkMode.HoldToTalk;
        Sound ??= new();
        Overlay ??= new();
        InputGuard ??= new();
        Sound.Normalize();
        Overlay.Normalize();
        InputGuard.Normalize();
        if (InputGuard.PauseShortcut == Shortcut)
        {
            var defaultPause = new Hotkey(Keys.F10, Control: true, Alt: true);
            InputGuard.PauseShortcut = Shortcut == defaultPause
                ? new Hotkey(Keys.F11, Control: true, Alt: true) : defaultPause;
        }
    }

    /// <summary>
    /// Reads an import without changing the current settings or personal storage.
    /// Only a UTF-8 JSON object up to 1 MiB is accepted. Missing properties keep
    /// their defaults so files exported by older MicKey versions remain usable.
    /// </summary>
    public static AppSettings ReadFrom(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        if (input.Length == 0)
            throw new InvalidDataException("설정 파일이 비어 있습니다.");
        if (input.Length > MaximumFileBytes)
            throw new InvalidDataException("설정 파일이 너무 큽니다. 1MB 이하의 JSON 파일을 선택해 주세요.");
        try
        {
            using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = JsonOptions.MaxDepth });
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("설정 파일에는 JSON 객체가 있어야 합니다.");
            var settings = document.RootElement.Deserialize<AppSettings>(JsonOptions)
                ?? throw new InvalidDataException("설정 파일에서 설정을 읽을 수 없습니다.");
            settings.Normalize();
            return settings;
        }
        catch (JsonException error)
        {
            throw new JsonException("설정 파일 형식이 올바르지 않습니다. " + error.Message, error);
        }
    }

    public void Save() => SaveTo(StoragePath);

    /// <summary>
    /// Writes a normalized snapshot next to the destination, flushes it, and
    /// atomically replaces the target. A failed write never truncates the target.
    /// </summary>
    public void SaveTo(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)
            ?? throw new ArgumentException("설정 파일을 저장할 폴더를 확인해 주세요.", nameof(path));
        var snapshot = Clone();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, JsonOptions);
        if (bytes.Length > MaximumFileBytes)
            throw new InvalidDataException("설정 내용이 너무 큽니다. 1MB 이하로 줄여 주세요.");
        // Simultaneous autosave/export calls within this process must not race
        // while Windows replaces an existing destination file.
        lock (SaveGate)
            WriteAtomic(fullPath, directory, bytes);
    }

    private static void WriteAtomic(string fullPath, string directory, byte[] bytes)
    {
        Directory.CreateDirectory(directory);

        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            // Do not hide the original write/replace failure if cleanup itself
            // is blocked by antivirus, permissions, or an unavailable drive.
            try { File.Delete(temporary); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
        }
    }

    public AppSettings Clone()
    {
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.SerializeToUtf8Bytes(this, JsonOptions), JsonOptions)
            ?? throw new InvalidOperationException("설정을 복사할 수 없습니다.");
        copy.Normalize();
        return copy;
    }

    /// <summary>Copies all settings without retaining references to editable child objects.</summary>
    public void CopyFrom(AppSettings other)
    {
        ArgumentNullException.ThrowIfNull(other);
        var copy = other.Clone();
        DeviceId = copy.DeviceId;
        Shortcut = copy.Shortcut;
        Mode = copy.Mode;
        Sound = copy.Sound;
        Overlay = copy.Overlay;
        InputGuard = copy.InputGuard;
    }
}
