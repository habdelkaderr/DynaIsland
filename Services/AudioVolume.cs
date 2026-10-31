using System.Runtime.InteropServices;

namespace DynaIsland.Services;

/// <summary>
/// Reads / writes the default playback device's master volume through Core Audio.
/// Follows default-device switches (headphones, HDMI, ...).
/// </summary>
public sealed class AudioVolume : IDisposable
{
    private static readonly Guid IID_IAudioEndpointVolume = new("5CDF2C82-841E-4546-9722-0CF74078229A");
    private const int eRender = 0;
    private const int eMultimedia = 1;
    private const int CLSCTX_ALL = 0x17;

    private IMMDeviceEnumerator? _enumerator;
    private IAudioEndpointVolume? _endpoint;
    private string? _deviceId;
    private DateTime _lastDeviceCheck;

    /// <summary>True when the last <see cref="TryRead"/> switched to a new default device.</summary>
    public bool DeviceChanged { get; private set; }

    public bool TryRead(out float level, out bool muted)
    {
        level = 0;
        muted = false;
        try
        {
            EnsureEndpoint();
            if (_endpoint is null) return false;
            Marshal.ThrowExceptionForHR(_endpoint.GetMasterVolumeLevelScalar(out level));
            Marshal.ThrowExceptionForHR(_endpoint.GetMute(out muted));
            return true;
        }
        catch
        {
            Release();
            return false;
        }
    }

    public void SetLevel(float level)
    {
        try
        {
            EnsureEndpoint();
            if (_endpoint is null) return;
            var ctx = Guid.Empty;
            _endpoint.SetMasterVolumeLevelScalar(Math.Clamp(level, 0f, 1f), ref ctx);
            if (level > 0) _endpoint.SetMute(false, ref ctx);
        }
        catch
        {
            Release();
        }
    }

    private void EnsureEndpoint()
    {
        DeviceChanged = false;
        if (_endpoint is not null && DateTime.UtcNow - _lastDeviceCheck < TimeSpan.FromSeconds(2)) return;
        _lastDeviceCheck = DateTime.UtcNow;

        _enumerator ??= (IMMDeviceEnumerator)new MMDeviceEnumeratorComObject();
        if (_enumerator.GetDefaultAudioEndpoint(eRender, eMultimedia, out var device) != 0 || device is null)
        {
            ReleaseEndpoint();
            return;
        }

        try
        {
            device.GetId(out string id);
            if (_endpoint is not null && id == _deviceId) return;

            ReleaseEndpoint();
            var iid = IID_IAudioEndpointVolume;
            Marshal.ThrowExceptionForHR(device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object obj));
            _endpoint = (IAudioEndpointVolume)obj;
            DeviceChanged = _deviceId is not null;
            _deviceId = id;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void ReleaseEndpoint()
    {
        if (_endpoint is not null) Marshal.ReleaseComObject(_endpoint);
        _endpoint = null;
    }

    private void Release()
    {
        ReleaseEndpoint();
        if (_enumerator is not null) Marshal.ReleaseComObject(_enumerator);
        _enumerator = null;
    }

    public void Dispose() => Release();

    // ---- Core Audio COM interop -------------------------------------------------------------

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumeratorComObject;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig] int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
        [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        [PreserveSig] int OpenPropertyStore(int access, out IntPtr properties);
        [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
        [PreserveSig] int GetState(out int state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        [PreserveSig] int RegisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int UnregisterControlChangeNotify(IntPtr notify);
        [PreserveSig] int GetChannelCount(out uint count);
        [PreserveSig] int SetMasterVolumeLevel(float levelDb, ref Guid context);
        [PreserveSig] int SetMasterVolumeLevelScalar(float level, ref Guid context);
        [PreserveSig] int GetMasterVolumeLevel(out float levelDb);
        [PreserveSig] int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig] int SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);
        [PreserveSig] int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
        [PreserveSig] int GetChannelVolumeLevel(uint channel, out float levelDb);
        [PreserveSig] int GetChannelVolumeLevelScalar(uint channel, out float level);
        [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
        [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
    }
}
