using MicPtt.Audio;

namespace MicPtt;

public sealed class SoundSettings
{
    public bool Enabled { get; set; } = true;
    public int VolumePercent { get; set; } = 35;
    public string OutputDeviceId { get; set; } = "default";
    public FeedbackTone OnTone { get; set; } = FeedbackTone.Soft;
    public FeedbackTone OffTone { get; set; } = FeedbackTone.Soft;

    public void Normalize()
    {
        VolumePercent = Math.Clamp(VolumePercent, 0, 100);
        if (string.IsNullOrWhiteSpace(OutputDeviceId)) OutputDeviceId = "default";
        if (!Enum.IsDefined(OnTone)) OnTone = FeedbackTone.Soft;
        if (!Enum.IsDefined(OffTone)) OffTone = FeedbackTone.Soft;
    }
}
