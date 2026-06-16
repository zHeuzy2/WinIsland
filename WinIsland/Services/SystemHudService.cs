using System.Management;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace WinIsland.Services;

/// <summary>
/// Surfaces a macOS-style HUD on the island when the system volume or screen
/// brightness changes. Volume is read through the Core Audio COM API
/// (<see cref="IAudioEndpointVolume"/>); brightness through WMI
/// (<c>WmiMonitorBrightness</c>), which only exists on laptop internal panels —
/// when it's absent the brightness HUD simply never fires.
/// </summary>
public sealed class SystemHudService
{
    private readonly AppState _state;
    private readonly Action _notify;

    private IAudioEndpointVolume? _endpoint;
    private float _lastVolume = -1f;
    private bool _lastMute;
    private int _lastBrightness = -1;
    private bool _primedVolume;   // skip the very first reading (baseline)
    private bool _primedBrightness;

    public SystemHudService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void Start()
    {
        _ = Task.Run(VolumeLoopAsync);
        _ = Task.Run(BrightnessLoopAsync);
    }

    // ---- Volume (Core Audio, polled) ----

    private async Task VolumeLoopAsync()
    {
        try { _endpoint = GetEndpointVolume(); }
        catch { return; } // audio device unavailable

        while (true)
        {
            try { PollVolume(); }
            catch { /* keep polling; device may have changed */ }
            await Task.Delay(150);
        }
    }

    private void PollVolume()
    {
        if (_endpoint == null) return;
        if (_endpoint.GetMasterVolumeLevelScalar(out float vol) != 0) return;
        _endpoint.GetMute(out bool mute);

        // Prime on the first read so we don't flash a HUD at startup.
        if (!_primedVolume)
        {
            _lastVolume = vol;
            _lastMute = mute;
            _primedVolume = true;
            return;
        }

        bool changed = Math.Abs(vol - _lastVolume) > 0.005f || mute != _lastMute;
        _lastVolume = vol;
        _lastMute = mute;
        if (!changed) return;
        if (!_state.Settings.AlertVolume) return;

        string icon = mute || vol <= 0.001f ? "\uE74F"      // muted
            : vol < 0.33f ? "\uE993"                          // low
            : vol < 0.66f ? "\uE994"                          // medium
            : "\uE995";                                       // high
        string title = mute ? Loc.T("hud.muted") : Loc.T("hud.volume");

        ShowHud(icon, title, mute ? 0f : vol, new SKColor(10, 132, 255));
    }

    // ---- Brightness (WMI, polled) ----

    private async Task BrightnessLoopAsync()
    {
        // Probe once: if the WMI class isn't there (desktop / external monitor),
        // give up quietly so we never show a brightness HUD that can't change.
        if (ReadBrightness() < 0) return;

        while (true)
        {
            try { PollBrightness(); }
            catch { }
            await Task.Delay(400);
        }
    }

    private void PollBrightness()
    {
        int level = ReadBrightness();
        if (level < 0) return;

        if (!_primedBrightness)
        {
            _lastBrightness = level;
            _primedBrightness = true;
            return;
        }

        if (level == _lastBrightness) return;
        _lastBrightness = level;
        if (!_state.Settings.AlertBrightness) return;

        ShowHud("\uE706", Loc.T("hud.brightness"), level / 100f, new SKColor(255, 159, 10));
    }

    /// <summary>Current brightness 0..100, or -1 when WMI brightness isn't available.</summary>
    private static int ReadBrightness()
    {
        try
        {
            using var searcher = new ManagementObjectSearcher(
                "root\\WMI", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
            foreach (var o in searcher.Get())
                return Convert.ToInt32(o["CurrentBrightness"]);
        }
        catch { }
        return -1;
    }

    private void ShowHud(string icon, string title, float progress, SKColor accent)
    {
        _state.SetAlert(new AlertInfo
        {
            Icon = icon,
            Title = title,
            Accent = accent,
            Progress = Math.Clamp(progress, 0f, 1f),
        }, 1800);
        _notify();
    }

    // ---- Core Audio COM plumbing ----

    private static IAudioEndpointVolume GetEndpointVolume()
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out IMMDevice device);
        var iid = typeof(IAudioEndpointVolume).GUID;
        device.Activate(ref iid, 0x17 /* CLSCTX_ALL */, IntPtr.Zero, out object o);
        return (IAudioEndpointVolume)o;
    }

    private enum EDataFlow { eRender, eCapture, eAll }
    private enum ERole { eConsole, eMultimedia, eCommunications }

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator { }

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        int NotImpl1();
        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppDevice);
        // remaining methods unused
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        [PreserveSig]
        int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
            [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
        // remaining methods unused
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        int NotImpl1();
        int NotImpl2();
        [PreserveSig]
        int GetChannelCount(out int channelCount);
        [PreserveSig]
        int SetMasterVolumeLevel(float level, ref Guid eventContext);
        [PreserveSig]
        int SetMasterVolumeLevelScalar(float level, ref Guid eventContext);
        [PreserveSig]
        int GetMasterVolumeLevel(out float level);
        [PreserveSig]
        int GetMasterVolumeLevelScalar(out float level);
        [PreserveSig]
        int SetChannelVolumeLevel(uint channelNumber, float level, ref Guid eventContext);
        [PreserveSig]
        int SetChannelVolumeLevelScalar(uint channelNumber, float level, ref Guid eventContext);
        [PreserveSig]
        int GetChannelVolumeLevel(uint channelNumber, out float level);
        [PreserveSig]
        int GetChannelVolumeLevelScalar(uint channelNumber, out float level);
        [PreserveSig]
        int SetMute([MarshalAs(UnmanagedType.Bool)] bool isMuted, ref Guid eventContext);
        [PreserveSig]
        int GetMute([MarshalAs(UnmanagedType.Bool)] out bool isMuted);
        // remaining methods unused
    }
}
