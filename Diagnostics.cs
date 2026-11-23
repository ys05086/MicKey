using System.Text.Json;
using MicPtt.Audio;

namespace MicPtt;

internal static class Diagnostics
{
    public static void WriteReport(string path, bool testMute)
    {
        var results = new List<object>();
        bool success = true;
        string? error = null;
        string? defaultId = null;
        try
        {
            defaultId = CoreAudio.GetDefaultCaptureId();
            foreach (var device in CoreAudio.ListCaptureDevices())
            {
                try
                {
                    using var endpoint = new CoreAudio(device.Id);
                    bool original = endpoint.IsMuted;
                    bool? muteVerified = null;
                    bool? restored = null;
                    if (testMute && device.Id == defaultId)
                    {
                        try
                        {
                            endpoint.SetMuted(true);
                            muteVerified = endpoint.IsMuted;
                            if (muteVerified != true) success = false;
                        }
                        finally
                        {
                            endpoint.SetMuted(original);
                            restored = endpoint.IsMuted == original;
                            if (restored != true) success = false;
                        }
                    }
                    results.Add(new { device.Id, device.Name, originalMuted = original, muteVerified, restored });
                }
                catch (Exception ex) { success = false; results.Add(new { device.Id, device.Name, error = ex.Message }); }
            }
        }
        catch (Exception ex) { success = false; error = ex.ToString(); }
        File.WriteAllText(path, JsonSerializer.Serialize(new { success, testMute, defaultId, devices = results, error },
            new JsonSerializerOptions { WriteIndented = true }));
        Environment.ExitCode = success ? 0 : 1;
    }
}
