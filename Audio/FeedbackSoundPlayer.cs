using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace MicPtt.Audio;

public sealed record SoundOutputDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

public enum FeedbackTone { Soft, Digital, Click, None }

/// <summary>
/// Plays generated feedback only to an output device. No microphone is opened,
/// no audio is recorded, and neither device nor system volume is changed.
/// PlaybackFailed is raised on the audio worker thread; UI callers must marshal it.
/// </summary>
public sealed class FeedbackSoundPlayer : IDisposable
{
    private const int SampleRate = 48000;
    private const uint WaveMapper = uint.MaxValue;
    // Windows SDK mmddk.h: DRV_RESERVED (0x800) + 17 / 18.
    private const uint QueryInstanceId = 0x811;
    private const uint QueryInstanceIdSize = 0x812;
    private const uint QueryInterface = 0x80C;
    private const uint QueryInterfaceSize = 0x80D;
    private readonly object gate = new();
    private readonly AutoResetEvent wake = new(false);
    private readonly Thread worker;
    private Request? pending;
    private long generation;
    private bool stopped;
    private bool backendUnavailable;

    public event Action<string>? PlaybackFailed;

    public FeedbackSoundPlayer()
    {
        worker = new Thread(WorkerLoop) { IsBackground = true, Name = "MicKey feedback audio" };
        worker.Start();
    }

    public static IReadOnlyList<SoundOutputDevice> ListOutputDevices()
    {
        var fullNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var defaultName = "Windows 기본 출력 장치";
        try
        {
            foreach (var device in CoreAudio.ListPlaybackDevices())
                fullNames["endpoint:" + device.Id] = device.Name;
            var defaultId = CoreAudio.GetDefaultPlaybackId();
            if (defaultId is not null && fullNames.TryGetValue("endpoint:" + defaultId, out var name))
                defaultName = "Windows 기본 출력 · " + name;
        }
        catch (COMException) { /* Keep WinMM names if a device disappears during enumeration. */ }

        var result = new List<SoundOutputDevice>
        {
            new("default", defaultName)
        };
        result.AddRange(EnumerateOutputs()
            .Select(device => fullNames.TryGetValue(device.Id, out var name) ? device with { Name = name } : device)
            .OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(device => new SoundOutputDevice(device.Id, device.Name)));
        return result;
    }

    /// <summary>
    /// Returns immediately. A new request interrupts the previous sound; requests
    /// never accumulate. None or zero volume also stops a sound already playing.
    /// Calls after Dispose are ignored so feedback cannot disrupt microphone control.
    /// </summary>
    public void Play(bool microphoneOn, FeedbackTone tone, int volumePercent, string outputDeviceId)
    {
        lock (gate)
        {
            if (stopped)
                return;
            pending = new Request(++generation, microphoneOn, tone, Math.Clamp(volumePercent, 0, 100),
                outputDeviceId ?? string.Empty);
            wake.Set();
        }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (stopped)
                return;
            stopped = true;
            pending = null;
            ++generation;
            wake.Set();
        }
        // Native resources remain owned by the worker even if a broken audio
        // driver takes longer. Do not freeze microphone/UI shutdown indefinitely.
        if (Thread.CurrentThread != worker)
            worker.Join(1200);
        GC.SuppressFinalize(this);
    }

    private void WorkerLoop()
    {
        try
        {
            while (true)
            {
                Request? request;
                lock (gate)
                {
                    if (stopped)
                        break;
                    request = pending;
                    pending = null;
                    // Consume the signal belonging to this request while holding
                    // the same lock as Play; later requests will set it again.
                    wake.Reset();
                }
                if (request is null)
                {
                    wake.WaitOne();
                    continue;
                }
                if (request.Tone == FeedbackTone.None || request.Volume == 0)
                    continue;
                if (backendUnavailable)
                {
                    NotifyFailure("효과음 장치를 정리하지 못했습니다. 앱을 다시 실행해 주세요.");
                    continue;
                }

                NativePlayback? playback = null;
                try
                {
                    if (!Enum.IsDefined(request.Tone))
                        throw new InvalidOperationException("지원하지 않는 효과음 종류입니다.");
                    var output = ResolveOutput(request.OutputId);
                    var samples = CreateSamples(request.MicrophoneOn, request.Tone, request.Volume);
                    if (IsObsolete(request.Generation))
                        continue;

                    playback = new NativePlayback();
                    playback.Open(output.Index);
                    // Verify the explicit selection again after opening. If the
                    // device list changed, do not emit the clip to another output.
                    if (output.Id != "default" && !MatchesCurrentOutput(output))
                        throw new InvalidOperationException("효과음 출력 장치가 변경되었습니다. 장치를 다시 선택해 주세요.");
                    if (IsObsolete(request.Generation))
                        continue;
                    playback.Write(samples);
                    var deadline = Stopwatch.StartNew();
                    while (!playback.IsDone && !IsObsolete(request.Generation))
                    {
                        if (deadline.ElapsedMilliseconds > 2500)
                            throw new InvalidOperationException("효과음 출력 장치가 응답하지 않습니다.");
                        // CALLBACK_NULL plus a short worker-only wait avoids a
                        // managed callback or movable memory being kept by WinMM.
                        wake.WaitOne(8);
                    }
                }
                catch (Exception error)
                {
                    if (!IsObsolete(request.Generation))
                        NotifyFailure(error.Message);
                }
                finally
                {
                    if (playback is not null && !playback.TryClose(out var error))
                    {
                        backendUnavailable = true;
                        NotifyFailure(error);
                    }
                }
            }
        }
        catch (Exception error)
        {
            NotifyFailure($"효과음 재생을 중지했습니다. {error.Message}");
        }
        finally
        {
            lock (gate)
            {
                stopped = true;
                pending = null;
                wake.Dispose();
            }
        }
    }

    private bool IsObsolete(long requestGeneration)
    {
        lock (gate)
            return stopped || requestGeneration != generation;
    }

    private void NotifyFailure(string message)
    {
        Action<string>? handlers;
        lock (gate)
        {
            if (stopped)
                return;
            handlers = PlaybackFailed;
        }
        if (handlers is null)
            return;
        foreach (Action<string> handler in handlers.GetInvocationList())
        {
            try { handler(message); }
            catch { /* A UI subscriber must not terminate the audio worker. */ }
        }
    }

    private static Output ResolveOutput(string id)
    {
        if (id == "default")
            return new Output(WaveMapper, "default", "Windows 기본 출력 장치");
        var matches = EnumerateOutputs().Where(device =>
            string.Equals(device.Id, id, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("선택한 효과음 출력 장치를 찾을 수 없습니다. 이어폰 연결을 확인하고 장치를 다시 선택해 주세요.");
        return matches[0];
    }

    private static bool MatchesCurrentOutput(Output output) =>
        EnumerateOutputs().Any(current => current.Index == output.Index &&
            string.Equals(current.Id, output.Id, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<Output> EnumerateOutputs()
    {
        var devices = new List<Output>();
        var count = waveOutGetNumDevs();
        for (uint index = 0; index < count; index++)
        {
            if (waveOutGetDevCapsW((UIntPtr)index, out var caps, (uint)Marshal.SizeOf<WaveOutCaps>()) != 0)
                continue; // A device can disappear between the count and this call.
            var name = string.IsNullOrWhiteSpace(caps.Name) ? $"오디오 출력 {index + 1}" : caps.Name;
            var instance = ReadDeviceString(index, QueryInstanceIdSize, QueryInstanceId);
            var id = !string.IsNullOrEmpty(instance) ? "endpoint:" + instance : null;
            if (id is null)
            {
                var deviceInterface = ReadDeviceString(index, QueryInterfaceSize, QueryInterface);
                if (!string.IsNullOrEmpty(deviceInterface))
                    id = "interface:" + deviceInterface;
            }
            if (id is null)
            {
                // Legacy fallback includes the name AND index: a different
                // device at that index must never silently inherit the selection.
                id = $"legacy:{index}:{caps.Manufacturer}:{caps.Product}:{caps.DriverVersion}:{name}";
                name += $" · 출력 {index + 1}";
            }
            devices.Add(new Output(index, id, name));
        }
        // An old driver may expose the same interface for several endpoints.
        // Disambiguate those entries conservatively by name and current index.
        var duplicates = devices.GroupBy(device => device.Id, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1).Select(group => group.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return devices.Select(device => duplicates.Contains(device.Id)
            ? device with { Id = $"{device.Id}|{device.Index}|{device.Name}", Name = $"{device.Name} · 출력 {device.Index + 1}" }
            : device).ToArray();
    }

    private static string? ReadDeviceString(uint index, uint sizeMessage, uint valueMessage)
    {
        var sizePointer = Marshal.AllocHGlobal(sizeof(uint));
        IntPtr valuePointer = IntPtr.Zero;
        try
        {
            Marshal.WriteInt32(sizePointer, 0);
            if (waveOutMessage((IntPtr)index, sizeMessage, sizePointer, UIntPtr.Zero) != 0)
                return null;
            var byteCount = Marshal.ReadInt32(sizePointer);
            if (byteCount < 2 || byteCount > 65536 || (byteCount & 1) != 0)
                return null;
            valuePointer = Marshal.AllocHGlobal(byteCount);
            // WinMM returns a null-terminated UTF-16 string; bounded conversion
            // also protects against a malformed third-party driver response.
            if (waveOutMessage((IntPtr)index, valueMessage, valuePointer, (UIntPtr)(uint)byteCount) != 0)
                return null;
            return Marshal.PtrToStringUni(valuePointer, byteCount / 2)?.TrimEnd('\0');
        }
        finally
        {
            if (valuePointer != IntPtr.Zero) Marshal.FreeHGlobal(valuePointer);
            Marshal.FreeHGlobal(sizePointer);
        }
    }

    internal static short[] CreateSamples(bool microphoneOn, FeedbackTone tone, int volumePercent)
    {
        if (tone == FeedbackTone.None)
            return Array.Empty<short>();
        double[] pitches;
        double noteSeconds;
        double gapSeconds;
        switch (tone)
        {
            case FeedbackTone.Soft:
                pitches = [587.33, 880.0]; noteSeconds = 0.067; gapSeconds = 0.010; break;
            case FeedbackTone.Digital:
                pitches = [523.25, 783.99, 1046.5]; noteSeconds = 0.036; gapSeconds = 0.006; break;
            case FeedbackTone.Click:
                pitches = [740.0, 1244.5]; noteSeconds = 0.021; gapSeconds = 0.012; break;
            default:
                throw new ArgumentOutOfRangeException(nameof(tone));
        }
        if (!microphoneOn)
            Array.Reverse(pitches);
        var noteLength = (int)(SampleRate * noteSeconds);
        var gapLength = (int)(SampleRate * gapSeconds);
        var result = new short[noteLength * pitches.Length + gapLength * (pitches.Length - 1)];
        var gain = Math.Clamp(volumePercent, 0, 100) / 100.0 * 0.23;
        for (int note = 0; note < pitches.Length; note++)
        {
            for (int sample = 0; sample < noteLength; sample++)
            {
                var progress = sample / (double)(noteLength - 1);
                var phase = 2 * Math.PI * pitches[note] * sample / SampleRate;
                var envelope = Math.Pow(Math.Sin(Math.PI * progress), 1.4);
                var value = Math.Sin(phase);
                if (tone == FeedbackTone.Digital)
                    value = (value + 0.17 * Math.Sin(3 * phase) + 0.06 * Math.Sin(5 * phase)) / 1.23;
                if (tone == FeedbackTone.Click)
                    envelope *= Math.Exp(-3.2 * progress);
                result[note * (noteLength + gapLength) + sample] =
                    (short)Math.Round(value * envelope * gain * short.MaxValue);
            }
        }
        return result;
    }

    private static void Check(uint result, string action)
    {
        if (result == 0)
            return;
        var description = new StringBuilder(256);
        waveOutGetErrorTextW(result, description, (uint)description.Capacity);
        throw new InvalidOperationException($"{action}: {description} (오류 {result})");
    }

    private sealed record Request(long Generation, bool MicrophoneOn, FeedbackTone Tone, int Volume, string OutputId);
    private sealed record Output(uint Index, string Id, string Name);

    /// <summary>Owned exclusively by the worker; native memory never moves.</summary>
    private sealed class NativePlayback
    {
        private static readonly List<NativePlayback> QuarantinedBuffers = new();
        private static readonly uint HeaderSize = (uint)Marshal.SizeOf<WaveHeader>();
        private IntPtr device;
        private IntPtr samples;
        private IntPtr header;
        private bool prepared;
        private bool written;

        public bool IsDone => !written || (Marshal.PtrToStructure<WaveHeader>(header).Flags & 1) != 0;

        public void Open(uint outputIndex)
        {
            var format = new WaveFormat
            {
                FormatTag = 1, Channels = 1, SamplesPerSecond = SampleRate,
                AverageBytesPerSecond = SampleRate * 2, BlockAlign = 2, BitsPerSample = 16, ExtraSize = 0
            };
            // Explicit IDs are opened directly, without WAVE_MAPPED. There is
            // no fallback to a default/speaker device on an explicit-ID failure.
            Check(waveOutOpen(out device, (UIntPtr)outputIndex, ref format, IntPtr.Zero, UIntPtr.Zero, 0),
                "효과음 출력 장치를 열 수 없습니다");
        }

        public void Write(short[] data)
        {
            samples = Marshal.AllocHGlobal(data.Length * sizeof(short));
            Marshal.Copy(data, 0, samples, data.Length);
            header = Marshal.AllocHGlobal((int)HeaderSize);
            var value = new WaveHeader { Data = samples, BufferLength = (uint)(data.Length * sizeof(short)) };
            Marshal.StructureToPtr(value, header, false);
            Check(waveOutPrepareHeader(device, header, HeaderSize), "효과음을 준비할 수 없습니다");
            prepared = true;
            Check(waveOutWrite(device, header, HeaderSize), "효과음을 재생할 수 없습니다");
            written = true;
        }

        public bool TryClose(out string error)
        {
            error = string.Empty;
            if (device != IntPtr.Zero)
            {
                // Reset first returns queued buffers even when a new PTT event
                // interrupts this clip. Never free a prepared/in-flight header.
                if (written && !IsDone)
                    waveOutReset(device);
                if (prepared)
                {
                    var deadline = Stopwatch.StartNew();
                    uint result;
                    do
                    {
                        result = waveOutUnprepareHeader(device, header, HeaderSize);
                        if (result != 33) break; // WAVERR_STILLPLAYING
                        waveOutReset(device);
                        Thread.Sleep(8);
                    } while (deadline.ElapsedMilliseconds < 800);
                    if (result != 0)
                    {
                        // A broken driver must not receive dangling memory. Keep
                        // this small allocation alive until process exit instead
                        // of risking use-after-free; disable further playback.
                        lock (QuarantinedBuffers) QuarantinedBuffers.Add(this);
                        error = $"효과음 출력 장치의 버퍼를 해제하지 못했습니다. 앱을 다시 실행해 주세요. (오류 {result})";
                        return false;
                    }
                    prepared = false;
                }
                var closeResult = waveOutClose(device);
                if (closeResult != 0)
                {
                    lock (QuarantinedBuffers) QuarantinedBuffers.Add(this);
                    error = $"효과음 출력 장치를 닫지 못했습니다. 앱을 다시 실행해 주세요. (오류 {closeResult})";
                    return false;
                }
                device = IntPtr.Zero;
            }
            if (header != IntPtr.Zero) { Marshal.FreeHGlobal(header); header = IntPtr.Zero; }
            if (samples != IntPtr.Zero) { Marshal.FreeHGlobal(samples); samples = IntPtr.Zero; }
            return true;
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WaveOutCaps
    {
        public ushort Manufacturer;
        public ushort Product;
        public uint DriverVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        public uint Formats;
        public ushort Channels;
        public ushort Reserved;
        public uint Support;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 2)]
    private struct WaveFormat
    {
        public ushort FormatTag;
        public ushort Channels;
        public uint SamplesPerSecond;
        public uint AverageBytesPerSecond;
        public ushort BlockAlign;
        public ushort BitsPerSample;
        public ushort ExtraSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WaveHeader
    {
        public IntPtr Data;
        public uint BufferLength;
        public uint BytesRecorded;
        public UIntPtr User;
        public uint Flags;
        public uint Loops;
        public IntPtr Next;
        public UIntPtr Reserved;
    }

    [DllImport("winmm.dll")] private static extern uint waveOutGetNumDevs();
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveOutGetDevCapsW(UIntPtr deviceId, out WaveOutCaps caps, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutMessage(IntPtr deviceId, uint message, IntPtr parameter1, UIntPtr parameter2);
    [DllImport("winmm.dll")] private static extern uint waveOutOpen(out IntPtr device, UIntPtr deviceId, ref WaveFormat format, IntPtr callback, UIntPtr instance, uint flags);
    [DllImport("winmm.dll")] private static extern uint waveOutPrepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutWrite(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutReset(IntPtr device);
    [DllImport("winmm.dll")] private static extern uint waveOutUnprepareHeader(IntPtr device, IntPtr header, uint size);
    [DllImport("winmm.dll")] private static extern uint waveOutClose(IntPtr device);
    [DllImport("winmm.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint waveOutGetErrorTextW(uint error, StringBuilder text, uint length);
}
