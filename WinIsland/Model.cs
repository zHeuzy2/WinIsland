using SkiaSharp;

namespace WinIsland;

public enum ViewKind { Music, Timer, Camera, Tasks, Settings }

public sealed class MediaSnapshot
{
    public bool HasSession;
    public string Title = "";
    public string Artist = "";
    public bool IsPlaying;
    public SKImage? Art;
    public string ArtKey = "";

    // Playback position (for the compact elapsed-time readout).
    public double PositionSec;
    public double DurationSec;
    public long PosTicks; // UtcNow ticks when Position was sampled
}

public sealed class AlertInfo
{
    public string Icon = "";
    public string Title = "";
    public string? Subtitle;
    public SKColor Accent = new(10, 132, 255);
    public SKImage? Image; // optional artwork shown instead of the glyph
}

/// <summary>
/// Shared, thread-safe-ish application state. Background services update it and
/// the render loop reads it. Reference assignments for snapshots are atomic;
/// simple value fields are marked volatile.
/// </summary>
public sealed class AppState
{
    public volatile MediaSnapshot Media = new();

    // Interaction
    public volatile bool Hovered;
    public volatile bool Pinned;
    public ViewKind View = ViewKind.Music;

    // Persisted user settings (loaded at startup, mutated on the UI thread).
    public AppSettings Settings = new();

    // Inline task composer (typing area drawn inside the Tasks view). Lives and
    // is mutated on the UI thread only.
    public readonly Composer Composer = new();

    // Media seek interaction (driven on the UI thread). While the user is
    // dragging/clicking the progress bar, the bar follows the cursor instead of
    // the reported playback position. MediaSeekStickyTicks keeps the preview
    // pinned for a short moment after committing so the bar doesn't snap back
    // before GSMTC reports the new position on its next poll.
    public volatile bool MediaSeeking;
    public double MediaSeekFraction;
    public long MediaSeekStickyTicks;

    // Timer (updated on timer thread)
    public volatile bool TimerRunning;
    public volatile int TimerRemaining;
    public volatile int TimerTotal = 25 * 60;
    public volatile string TimerLabel = "Foco";
    public int TimerAccentArgb = unchecked((int)0xFFFF6B6B);

    // Camera frame: a reusable bitmap guarded by CameraLock (written by the
    // camera thread, read by the render thread).
    public readonly object CameraLock = new();
    public SKBitmap? CameraBitmap;
    public volatile bool HasCameraFrame;
    public volatile string? CameraError;

    // Alert (transient)
    private AlertInfo? _alert;
    private long _alertExpiryTicks;
    private readonly object _alertLock = new();

    public void SetAlert(AlertInfo alert, int durationMs)
    {
        lock (_alertLock)
        {
            _alert = alert;
            _alertExpiryTicks = DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(durationMs).Ticks;
        }
    }

    public AlertInfo? GetActiveAlert()
    {
        lock (_alertLock)
        {
            if (_alert == null) return null;
            if (DateTime.UtcNow.Ticks > _alertExpiryTicks)
            {
                _alert = null;
                return null;
            }
            return _alert;
        }
    }

    public bool ExpandedRequested => Hovered || Pinned || Composer.Active;
}
