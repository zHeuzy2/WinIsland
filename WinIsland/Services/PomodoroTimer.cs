using SkiaSharp;

namespace WinIsland.Services;

public sealed record TimerPreset(string Label, int Seconds, SKColor Accent);

public sealed class PomodoroTimer
{
    public static readonly TimerPreset[] Presets =
    {
        new("Foco", 25 * 60, new SKColor(255, 107, 107)),
        new("Pausa", 5 * 60, new SKColor(52, 199, 89)),
        new("Longa", 15 * 60, new SKColor(10, 132, 255)),
    };

    private readonly AppState _state;
    private readonly Action _notify;
    private readonly System.Threading.Timer _timer;
    private TimerPreset _current = Presets[0];

    public PomodoroTimer(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
        _timer = new System.Threading.Timer(Tick, null, System.Threading.Timeout.Infinite, 1000);

        _state.TimerTotal = _current.Seconds;
        _state.TimerRemaining = _current.Seconds;
        _state.TimerLabel = _current.Label;
        _state.TimerAccentArgb = (int)(uint)_current.Accent;
    }

    public void Select(TimerPreset preset)
    {
        _timer.Change(System.Threading.Timeout.Infinite, 1000);
        _current = preset;
        _state.TimerRunning = false;
        _state.TimerTotal = preset.Seconds;
        _state.TimerRemaining = preset.Seconds;
        _state.TimerLabel = preset.Label;
        _state.TimerAccentArgb = (int)(uint)preset.Accent;
        _notify();
    }

    public void Toggle()
    {
        if (_state.TimerRunning) Pause();
        else Start();
    }

    public void Start()
    {
        if (_state.TimerRemaining <= 0) _state.TimerRemaining = _state.TimerTotal;
        _state.TimerRunning = true;
        _timer.Change(1000, 1000);
        _notify();
    }

    public void Pause()
    {
        _state.TimerRunning = false;
        _timer.Change(System.Threading.Timeout.Infinite, 1000);
        _notify();
    }

    public void Reset()
    {
        _timer.Change(System.Threading.Timeout.Infinite, 1000);
        _state.TimerRunning = false;
        _state.TimerRemaining = _state.TimerTotal;
        _notify();
    }

    private void Tick(object? _)
    {
        if (!_state.TimerRunning) return;

        int next = _state.TimerRemaining - 1;
        if (next <= 0)
        {
            _state.TimerRemaining = 0;
            _state.TimerRunning = false;
            _timer.Change(System.Threading.Timeout.Infinite, 1000);
            _state.SetAlert(new AlertInfo
            {
                Icon = "\uE823", // clock
                Title = $"{Loc.T("preset." + _current.Label)} {Loc.T("timer.done")}",
                Subtitle = Loc.T("timer.timeup"),
                Accent = _current.Accent,
            }, 6000);
        }
        else
        {
            _state.TimerRemaining = next;
        }
        _notify();
    }

    public static string Format(int seconds)
    {
        int m = seconds / 60;
        int s = seconds % 60;
        return $"{m:00}:{s:00}";
    }
}
