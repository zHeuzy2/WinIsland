using SkiaSharp;
using Windows.System.Power;

namespace WinIsland.Services;

/// <summary>
/// Raises island alerts for power events on laptops: charger connected /
/// disconnected, low and critical battery. Only runs when the device actually
/// has a battery — on desktops it never starts, so the feature stays invisible.
/// </summary>
public sealed class BatteryService
{
    private const int LowThreshold = 20;
    private const int CriticalThreshold = 10;

    private readonly AppState _state;
    private readonly Action _notify;

    private bool _charging;
    private bool _lowNotified;
    private bool _criticalNotified;
    private bool _started;

    public BatteryService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void Start()
    {
        try
        {
            // No battery (desktop / VM): bail out and never surface power alerts.
            if (PowerManager.BatteryStatus == BatteryStatus.NotPresent) return;

            _charging = IsCharging();
            _started = true;

            PowerManager.BatteryStatusChanged += OnPowerChanged;
            PowerManager.PowerSupplyStatusChanged += OnPowerChanged;
            PowerManager.RemainingChargePercentChanged += OnChargeChanged;
        }
        catch { /* power APIs unavailable: degrade silently */ }
    }

    private static bool IsCharging() =>
        PowerManager.BatteryStatus == BatteryStatus.Charging ||
        PowerManager.PowerSupplyStatus == PowerSupplyStatus.Adequate;

    private void OnPowerChanged(object? sender, object e)
    {
        if (!_started) return;
        try
        {
            bool charging = IsCharging();
            if (charging == _charging) return;
            _charging = charging;

            int pct = SafePercent();
            if (charging)
            {
                _lowNotified = false;
                _criticalNotified = false;
                ShowBattery("\uEBC4", Loc.T("battery.charging"), pct, new SKColor(52, 199, 89));
            }
            else
            {
                ShowBattery(BatteryGlyph(pct), Loc.T("battery.unplugged"), pct, new SKColor(255, 159, 10));
            }
        }
        catch { }
    }

    private void OnChargeChanged(object? sender, object e)
    {
        if (!_started) return;
        try
        {
            int pct = SafePercent();
            if (pct < 0) return;

            // Recovering above the thresholds re-arms the warnings.
            if (pct > LowThreshold) _lowNotified = false;
            if (pct > CriticalThreshold) _criticalNotified = false;
            if (_charging) return; // no low warnings while plugged in

            if (pct <= CriticalThreshold && !_criticalNotified)
            {
                _criticalNotified = true;
                _lowNotified = true;
                ShowBattery("\uE909", Loc.T("battery.critical"), pct, new SKColor(255, 59, 48));
            }
            else if (pct <= LowThreshold && !_lowNotified)
            {
                _lowNotified = true;
                ShowBattery("\uE851", Loc.T("battery.low"), pct, new SKColor(255, 159, 10));
            }
        }
        catch { }
    }

    private static int SafePercent()
    {
        try { return PowerManager.RemainingChargePercent; }
        catch { return -1; }
    }

    private static string BatteryGlyph(int pct) =>
        pct < 0 ? "\uE83F"
        : pct <= 10 ? "\uE850"
        : pct <= 30 ? "\uE851"
        : pct <= 60 ? "\uE854"
        : "\uE83F";

    private void ShowBattery(string icon, string title, int pct, SKColor accent)
    {
        if (!_state.Settings.AlertBattery) return;
        _state.SetAlert(new AlertInfo
        {
            Icon = icon,
            Title = title,
            Subtitle = pct >= 0 ? pct + "%" : null,
            Accent = accent,
        }, 4500);
        _notify();
    }
}
