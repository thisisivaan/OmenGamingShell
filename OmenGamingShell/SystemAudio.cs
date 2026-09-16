using System.Runtime.InteropServices;

namespace OmenGamingShell;

internal static class SystemAudio
{
    private static IAudioEndpointVolume? _volume;

    private static readonly Guid AudioEndptVolumeGuid = new("5CDF2C82-841E-4546-9722-0CF74078229A");

    private static IAudioEndpointVolume? VolumeEndpoint()
    {
        if (_volume is not null) return _volume;
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(0 /* eRender */, 1 /* eMultimedia */, out var device);
            if (device is null) return null;
            var iid = AudioEndptVolumeGuid;
            device.Activate(ref iid, 0x13 /* CLSCTX_ALL */, IntPtr.Zero, out object iface);
            _volume = (IAudioEndpointVolume)iface;
            Marshal.ReleaseComObject(device);
        }
        catch { }
        return _volume;
    }

    public static bool IsAvailable => VolumeEndpoint() is not null;

    public static float GetVolume()
    {
        try
        {
            var endpoint = VolumeEndpoint();
            if (endpoint is null) return 100f;
            endpoint.GetMasterVolumeLevelScalar(out float scalar);
            return Math.Clamp(scalar * 100f, 0f, 100f);
        }
        catch { return 100f; }
    }

    public static bool IsMuted()
    {
        try
        {
            var endpoint = VolumeEndpoint();
            if (endpoint is null) return false;
            endpoint.GetMute(out bool mute);
            return mute;
        }
        catch { return false; }
    }

    public static void SetVolume(float percent)
    {
        try
        {
            var endpoint = VolumeEndpoint();
            if (endpoint is null) return;
            var context = Guid.Empty;
            endpoint.SetMasterVolumeLevelScalar(Math.Clamp(percent / 100f, 0f, 1f), ref context);
        }
        catch { }
    }

    public static void SetMuted(bool mute)
    {
        try
        {
            var endpoint = VolumeEndpoint();
            if (endpoint is null) return;
            var context = Guid.Empty;
            endpoint.SetMute(mute, ref context);
        }
        catch { }
    }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int EnumAudioEndpoints(int dataFlow, uint stateMask, out IntPtr devices);
        int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
        int GetDevice(string pwstrId, out IMMDevice device);
        int RegisterEndpointNotificationCallback(IntPtr pClient);
        int UnregisterEndpointNotificationCallback(IntPtr pClient);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        int Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object iface);
        int OpenPropertyStore(int stgmAccess, out IntPtr properties);
        int GetId(IntPtr pId);
        int GetState(out uint state);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int RegisterControlChangeNotify(IntPtr pNotify);
        int UnregisterControlChangeNotify(IntPtr pNotify);
        int GetChannelCount(out uint channelCount);
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        int GetMasterVolumeLevel(out float level);
        int GetMasterVolumeLevelScalar(out float level);
        int SetChannelVolumeLevel(uint channel, float level, ref Guid eventContext);
        int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid eventContext);
        int GetChannelVolumeLevel(uint channel, out float level);
        int GetChannelVolumeLevelScalar(uint channel, out float level);
        int SetMute(bool mute, ref Guid eventContext);
        int GetMute(out bool mute);
        int GetVolumeStepInfo(out uint step, out uint stepCount);
        int VolumeStepUp(ref Guid eventContext);
        int VolumeStepDown(ref Guid eventContext);
        int QueryHardwareSupport(out uint mask);
        int GetVolumeRange(out float min, out float max, out float step);
    }
}