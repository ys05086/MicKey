using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace MicPtt.Audio;

public sealed record AudioDevice(string Id, string Name)
{
    public override string ToString() => Name;
}

/// <summary>
/// Controls one Windows capture endpoint. Its mute setting applies to every app
/// using that microphone. Create, use, and dispose this object on the UI thread.
/// </summary>
public sealed class CoreAudio : IDisposable
{
    private const uint DeviceStateActive = 0x00000001;
    private const uint ClassContextAll = 0x17;
    private const int ElementNotFound = unchecked((int)0x80070490);
    private static readonly Guid EventContext = new("A2DBB215-6B83-4B4E-98E9-E06B452AD7F7");
    private IAudioEndpointVolume? volume;

    public string DeviceId { get; }

    public CoreAudio(string deviceId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(deviceId);
        DeviceId = deviceId;
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        object? activated = null;
        try
        {
            enumerator = CreateEnumerator();
            Check(enumerator.GetDevice(deviceId, out device), "마이크를 찾을 수 없습니다");
            Check(((IMMEndpoint)device).GetDataFlow(out var flow), "마이크 종류를 확인할 수 없습니다");
            if (flow != DataFlow.Capture)
                throw new InvalidOperationException("선택한 장치가 마이크 입력 장치가 아닙니다.");
            Check(device.GetState(out var state), "마이크 상태를 확인할 수 없습니다");
            if ((state & DeviceStateActive) == 0)
                throw new InvalidOperationException("선택한 마이크가 연결되어 있지 않거나 사용 중지되어 있습니다.");

            var interfaceId = typeof(IAudioEndpointVolume).GUID;
            Check(device.Activate(ref interfaceId, ClassContextAll, IntPtr.Zero, out activated),
                "마이크 음소거 제어를 시작할 수 없습니다");
            volume = (IAudioEndpointVolume)activated;
            activated = null; // Ownership transfers to this instance.
        }
        finally
        {
            Release(activated);
            Release(device);
            Release(enumerator);
        }
    }

    public bool IsMuted
    {
        get
        {
            var current = GetVolume();
            Check(current.GetMute(out var muted), "마이크 음소거 상태를 읽을 수 없습니다");
            return muted;
        }
    }

    public void SetMuted(bool muted)
    {
        var current = GetVolume();
        var context = EventContext;
        Check(current.SetMute(muted, ref context), "마이크 음소거 상태를 변경할 수 없습니다");
    }

    public static IReadOnlyList<AudioDevice> ListCaptureDevices() => ListDevices(DataFlow.Capture, "마이크");

    /// <summary>Lists active playback endpoints with their complete Windows friendly names.</summary>
    public static IReadOnlyList<AudioDevice> ListPlaybackDevices() => ListDevices(DataFlow.Render, "출력 장치");

    private static IReadOnlyList<AudioDevice> ListDevices(DataFlow flow, string kind)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDeviceCollection? collection = null;
        var devices = new List<AudioDevice>();
        try
        {
            enumerator = CreateEnumerator();
            Check(enumerator.EnumAudioEndpoints(flow, DeviceStateActive, out collection),
                $"{kind} 목록을 불러올 수 없습니다");
            Check(collection.GetCount(out var count), $"{kind} 수를 확인할 수 없습니다");
            for (uint index = 0; index < count; index++)
            {
                IMMDevice? device = null;
                try
                {
                    Check(collection.Item(index, out device), $"{kind}를 확인할 수 없습니다");
                    Check(device.GetId(out var id), $"{kind} 식별자를 읽을 수 없습니다");
                    devices.Add(new AudioDevice(id, ReadFriendlyName(device) ?? $"{kind} {index + 1}"));
                }
                finally
                {
                    Release(device);
                }
            }
            return devices.OrderBy(device => device.Name, StringComparer.CurrentCultureIgnoreCase).ToArray();
        }
        finally
        {
            Release(collection);
            Release(enumerator);
        }
    }

    /// <summary>Returns the default communications capture endpoint, or null when none exists.</summary>
    public static string? GetDefaultCaptureId() => GetDefaultId(DataFlow.Capture, Role.Communications, "마이크");

    /// <summary>Returns the default console playback endpoint, or null when none exists.</summary>
    public static string? GetDefaultPlaybackId() => GetDefaultId(DataFlow.Render, Role.Console, "출력 장치");

    private static string? GetDefaultId(DataFlow flow, Role role, string kind)
    {
        IMMDeviceEnumerator? enumerator = null;
        IMMDevice? device = null;
        try
        {
            enumerator = CreateEnumerator();
            var result = enumerator.GetDefaultAudioEndpoint(flow, role, out device);
            if (result == ElementNotFound)
                return null;
            Check(result, $"기본 {kind}를 찾을 수 없습니다");
            Check(device.GetId(out var id), $"기본 {kind} 식별자를 읽을 수 없습니다");
            return id;
        }
        finally
        {
            Release(device);
            Release(enumerator);
        }
    }

    public void Dispose()
    {
        var oldVolume = volume;
        volume = null;
        Release(oldVolume);
        GC.SuppressFinalize(this);
    }

    private IAudioEndpointVolume GetVolume() =>
        volume ?? throw new ObjectDisposedException(nameof(CoreAudio));

    private static IMMDeviceEnumerator CreateEnumerator() =>
        (IMMDeviceEnumerator)new MMDeviceEnumerator();

    private static string? ReadFriendlyName(IMMDevice device)
    {
        IPropertyStore? properties = null;
        try
        {
            // A friendly-name failure must not hide an otherwise usable endpoint.
            if (device.OpenPropertyStore(0, out properties) < 0)
                return null;
            var key = new PropertyKey
            {
                FormatId = new Guid("A45C254E-DF1C-4EFD-8020-67D146A850E0"),
                PropertyId = 14
            };
            PropVariant value = default;
            try
            {
                if (properties.GetValue(ref key, out value) < 0)
                    return null;
                // PKEY_Device_FriendlyName is VT_LPWSTR (31).
                return value.VariantType == 31 ? Marshal.PtrToStringUni(value.PointerValue) : null;
            }
            finally
            {
                PropVariantClear(ref value);
            }
        }
        finally
        {
            Release(properties);
        }
    }

    private static void Check(int result, string message)
    {
        if (result >= 0)
            return;
        var detail = Marshal.GetExceptionForHR(result)?.Message;
        throw new COMException($"{message}. {detail} (0x{result:X8})", result);
    }

    private static void Release(object? value)
    {
        if (value is not null && Marshal.IsComObject(value))
            Marshal.ReleaseComObject(value);
    }

    [DllImport("ole32.dll", PreserveSig = true)]
    private static extern int PropVariantClear(ref PropVariant value);

    private enum DataFlow { Render, Capture, All }
    private enum Role { Console, Multimedia, Communications }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey
    {
        public Guid FormatId;
        public uint PropertyId;
    }

    // The BLOB member gives the union its native size: 16 bytes on x64 and 8
    // bytes on x86, making the full PROPVARIANT 24 and 16 bytes respectively.
    [StructLayout(LayoutKind.Sequential)]
    private struct Blob
    {
        public uint Size;
        public IntPtr Data;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort VariantType;
        [FieldOffset(2)] private ushort reserved1;
        [FieldOffset(4)] private ushort reserved2;
        [FieldOffset(6)] private ushort reserved3;
        [FieldOffset(8)] public IntPtr PointerValue;
        [FieldOffset(8)] private Blob blobValue;
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(DataFlow dataFlow, uint stateMask, out IMMDeviceCollection devices);
        [PreserveSig] int GetDefaultAudioEndpoint(DataFlow dataFlow, Role role, out IMMDevice device);
        [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr client);
        [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr client);
    }

    // IID verified against Microsoft's Windows SDK mmdeviceapi.h.
    [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceCollection
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int Item(uint index, out IMMDevice device);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid interfaceId, uint classContext, IntPtr activationParameters,
            [MarshalAs(UnmanagedType.IUnknown)] out object instance);
        [PreserveSig] int OpenPropertyStore(uint access, out IPropertyStore properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out uint state);
    }

    [ComImport, Guid("1BE09788-6894-4089-8586-9A2A6C265AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMEndpoint
    {
        [PreserveSig] int GetDataFlow(out DataFlow dataFlow);
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notification);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notification);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float level, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float level);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float level);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
        [PreserveSig] int GetVolumeStepInfo(out uint step, out uint count);
        [PreserveSig] int VolumeStepUp(ref Guid context);
        [PreserveSig] int VolumeStepDown(ref Guid context);
        [PreserveSig] int QueryHardwareSupport(out uint mask);
        [PreserveSig] int GetVolumeRange(out float minimum, out float maximum, out float increment);
    }
}
