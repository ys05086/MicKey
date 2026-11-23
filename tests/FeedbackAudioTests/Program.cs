using MicPtt.Audio;

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

foreach (var tone in new[] { FeedbackTone.Soft, FeedbackTone.Digital, FeedbackTone.Click })
{
    var on = FeedbackSoundPlayer.CreateSamples(true, tone, 100);
    var off = FeedbackSoundPlayer.CreateSamples(false, tone, 100);
    var quiet = FeedbackSoundPlayer.CreateSamples(true, tone, 20);
    Assert(on.Length > 1000 && on.Length < 10000, $"{tone}: feedback must be short");
    Assert(on.Length == off.Length && !on.SequenceEqual(off), $"{tone}: on/off must sound different");
    Assert(on[0] == 0 && on[^1] == 0, $"{tone}: ramp boundaries should be silent");
    Assert(on.Max(value => Math.Abs((int)value)) <= 7600, $"{tone}: peak should stay below clipping");
    Assert(quiet.Sum(value => Math.Abs((long)value)) < on.Sum(value => Math.Abs((long)value)) / 4,
        $"{tone}: volume is applied to samples");
    Assert(FeedbackSoundPlayer.CreateSamples(true, tone, 0).All(value => value == 0), $"{tone}: zero volume is silent");
    Assert(FeedbackSoundPlayer.CreateSamples(true, tone, -50).All(value => value == 0), $"{tone}: negative volume clamps");
    Assert(FeedbackSoundPlayer.CreateSamples(true, tone, 200).SequenceEqual(on), $"{tone}: high volume clamps");
    // Count rising zero crossings in each note to verify the semantic direction
    // instead of merely comparing two implementation-generated arrays.
    var noteLength = tone switch { FeedbackTone.Soft => 3216, FeedbackTone.Digital => 1728, _ => 1008 };
    static int Crossings(short[] data, int start, int length) => Enumerable.Range(start + 1, length - 1)
        .Count(i => data[i - 1] <= 0 && data[i] > 0);
    Assert(Crossings(on, 0, noteLength) < Crossings(on, on.Length - noteLength, noteLength),
        $"{tone}: microphone-on tones should rise");
    Assert(Crossings(off, 0, noteLength) > Crossings(off, off.Length - noteLength, noteLength),
        $"{tone}: microphone-off tones should fall");
}
Assert(FeedbackSoundPlayer.CreateSamples(true, FeedbackTone.None, 100).Length == 0, "None must be silent");

var outputs = FeedbackSoundPlayer.ListOutputDevices();
Assert(outputs.Count >= 1 && outputs[0].Id == "default", "Default output should always be listed");
Assert(outputs.Select(device => device.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() == outputs.Count,
    "Output identities should be unique");
Console.WriteLine(System.Text.Json.JsonSerializer.Serialize(outputs, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine("Default playback endpoint: " + CoreAudio.GetDefaultPlaybackId());
if (args.Length == 1 && args[0] == "--list")
    return;

using (var failed = new ManualResetEventSlim())
using (var player = new FeedbackSoundPlayer())
{
    string? message = null;
    player.PlaybackFailed += error => { message = error; failed.Set(); };
    player.Play(true, FeedbackTone.Soft, 30, "endpoint:this-device-does-not-exist");
    Assert(failed.Wait(TimeSpan.FromSeconds(3)), "Missing explicit output must report a failure");
    Assert(message?.Contains("출력 장치") == true, "Device failure should be understandable");
    failed.Reset();
    player.Play(true, FeedbackTone.None, 30, "endpoint:this-device-does-not-exist");
    player.Play(false, FeedbackTone.Soft, 0, "endpoint:this-device-does-not-exist");
    Assert(!failed.Wait(TimeSpan.FromMilliseconds(100)), "Silent requests should not open a device");
    player.Dispose();
    player.Play(true, FeedbackTone.Soft, 30, "default"); // Must be an inert no-op after disposal.
}

// Audible integration is opt-in so ordinary verification never changes input
// state or unexpectedly plays a tone. Supply the exact output ID from above.
if (args.Length == 2 && args[0] == "--play")
{
    using var player = new FeedbackSoundPlayer();
    var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
    player.PlaybackFailed += errors.Enqueue;
    foreach (var tone in new[] { FeedbackTone.Soft, FeedbackTone.Digital, FeedbackTone.Click })
    {
        player.Play(true, tone, 20, args[1]);
        Thread.Sleep(250);
        player.Play(false, tone, 20, args[1]);
        Thread.Sleep(250);
    }
    // Exercise replacement while a buffer is in flight and shutdown during play.
    for (int i = 0; i < 20; i++)
    {
        player.Play(i % 2 == 0, FeedbackTone.Soft, 10, args[1]);
        Thread.Sleep(15);
    }
    player.Dispose();
    Assert(errors.IsEmpty, string.Join(Environment.NewLine, errors));
}
Console.WriteLine("PASS: feedback synthesis, device enumeration, explicit-device failure and disposal");
