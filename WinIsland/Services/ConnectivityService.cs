using SkiaSharp;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;
using Windows.Networking.Connectivity;

namespace WinIsland.Services;

/// <summary>
/// Raises island alerts for connectivity changes: Wi-Fi connect/disconnect
/// (with SSID when available) and Bluetooth device connect/disconnect. Wi-Fi
/// rides the WinRT <see cref="NetworkInformation.NetworkStatusChanged"/> event;
/// Bluetooth is polled and diffed, which is reliable for unpackaged apps without
/// a declared capability.
/// </summary>
public sealed class ConnectivityService
{
    private readonly AppState _state;
    private readonly Action _notify;

    private bool _wifiConnected;
    private string _wifiSsid = "";
    private bool _primedWifi;

    private readonly HashSet<string> _btConnected = new();
    private bool _primedBt;

    public ConnectivityService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void Start()
    {
        try
        {
            (_wifiConnected, _wifiSsid) = ReadWifi();
            _primedWifi = true;
            NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
        }
        catch { /* connectivity APIs unavailable */ }

        _ = Task.Run(BluetoothLoopAsync);
    }

    // ---- Wi-Fi ----

    private void OnNetworkStatusChanged(object sender)
    {
        try
        {
            var (connected, ssid) = ReadWifi();
            if (!_primedWifi) { _wifiConnected = connected; _wifiSsid = ssid; _primedWifi = true; return; }

            if (connected == _wifiConnected && ssid == _wifiSsid) return;
            bool wasConnected = _wifiConnected;
            _wifiConnected = connected;
            _wifiSsid = ssid;

            if (connected && !wasConnected)
                ShowAlert("\uE701", Loc.T("wifi.connected"),
                    string.IsNullOrEmpty(ssid) ? null : ssid, new SKColor(10, 132, 255));
            else if (!connected && wasConnected)
                ShowAlert("\uEB5E", Loc.T("wifi.disconnected"), null, new SKColor(142, 142, 147));
        }
        catch { }
    }

    /// <summary>Returns the current Wi-Fi state and SSID (empty when unknown).</summary>
    private static (bool connected, string ssid) ReadWifi()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            if (profile == null || !profile.IsWlanConnectionProfile) return (false, "");

            bool connected = profile.GetNetworkConnectivityLevel() != NetworkConnectivityLevel.None;
            string ssid = "";
            try { ssid = profile.WlanConnectionProfileDetails?.GetConnectedSsid() ?? ""; }
            catch { }
            return (connected, ssid);
        }
        catch { return (false, ""); }
    }

    // ---- Bluetooth ----

    private async Task BluetoothLoopAsync()
    {
        string selector;
        try { selector = BluetoothDevice.GetDeviceSelectorFromConnectionStatus(BluetoothConnectionStatus.Connected); }
        catch { return; } // Bluetooth stack unavailable

        while (true)
        {
            try { await PollBluetoothAsync(selector); }
            catch { }
            await Task.Delay(3000);
        }
    }

    private async Task PollBluetoothAsync(string selector)
    {
        var devices = await DeviceInformation.FindAllAsync(selector);
        var current = new Dictionary<string, string>();
        foreach (var d in devices)
            current[d.Id] = d.Name ?? "Bluetooth";

        if (!_primedBt)
        {
            foreach (var id in current.Keys) _btConnected.Add(id);
            _primedBt = true;
            return;
        }

        // Newly connected.
        foreach (var kv in current)
            if (!_btConnected.Contains(kv.Key))
                ShowAlert("\uE702", kv.Value, Loc.T("bt.connected"), new SKColor(10, 132, 255));

        // Newly disconnected.
        foreach (var id in _btConnected.ToArray())
            if (!current.ContainsKey(id))
                ShowAlert("\uE703", Loc.T("bt.disconnected"), null, new SKColor(142, 142, 147));

        _btConnected.Clear();
        foreach (var id in current.Keys) _btConnected.Add(id);
    }

    private void ShowAlert(string icon, string title, string? subtitle, SKColor accent)
    {
        if (!_state.Settings.AlertConnection) return;
        _state.SetAlert(new AlertInfo
        {
            Icon = icon,
            Title = title,
            Subtitle = subtitle,
            Accent = accent,
        }, 4000);
        _notify();
    }
}
