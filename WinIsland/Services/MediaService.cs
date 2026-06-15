using SkiaSharp;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace WinIsland.Services;

/// <summary>
/// Reads the active media session (Spotify, browsers, etc.) via GSMTC and keeps
/// AppState.Media up to date. Also exposes transport controls.
/// </summary>
public sealed class MediaService
{
    private readonly AppState _state;
    private readonly Action _notify;
    private GlobalSystemMediaTransportControlsSessionManager? _manager;

    private string _lastArtKey = "";
    private SKImage? _lastArt;

    public MediaService(AppState state, Action notify)
    {
        _state = state;
        _notify = notify;
    }

    public void Start() => _ = LoopAsync();

    private async Task LoopAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch { /* GSMTC unavailable */ }

        while (true)
        {
            try { await PollAsync(); }
            catch { /* keep polling */ }
            await Task.Delay(700);
        }
    }

    private async Task PollAsync()
    {
        var session = _manager?.GetCurrentSession();
        var snap = new MediaSnapshot();

        if (session != null)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                snap.HasSession = true;
                snap.Title = props.Title ?? "";
                snap.Artist = props.Artist ?? "";

                var pb = session.GetPlaybackInfo();
                snap.IsPlaying = pb.PlaybackStatus ==
                    GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

                try
                {
                    var tl = session.GetTimelineProperties();
                    snap.PositionSec = tl.Position.TotalSeconds;
                    snap.DurationSec = tl.EndTime.TotalSeconds;

                    // Anchor the interpolation to when GSMTC actually sampled the
                    // position (LastUpdatedTime), not to "now". The Position value is
                    // cached by the source app and often stays the same across several
                    // polls, so using UtcNow here resets the interpolation base every
                    // poll and makes the readout bounce (e.g. 4-5 4-5 4-5) before
                    // jumping ahead. LastUpdatedTime keeps the count moving smoothly.
                    long updatedTicks = tl.LastUpdatedTime.UtcTicks;
                    snap.PosTicks = updatedTicks > 0 ? updatedTicks : DateTime.UtcNow.Ticks;
                }
                catch { }

                snap.ArtKey = snap.Title + "|" + snap.Artist;
                if (snap.ArtKey == _lastArtKey && _lastArt != null)
                {
                    snap.Art = _lastArt;
                }
                else
                {
                    var art = await LoadArtAsync(props);
                    if (art != null)
                    {
                        // New artwork is ready: swap and cache it.
                        snap.Art = art;
                        _lastArt = art;
                        _lastArtKey = snap.ArtKey;
                    }
                    else
                    {
                        // The source app hasn't published the new thumbnail yet.
                        // Keep showing the previous art (instead of blanking to the
                        // default placeholder) and retry on the next poll by not
                        // committing the new key to the cache.
                        snap.Art = _lastArt;
                    }
                }
            }
            catch { }
        }

        var prev = _state.Media;
        _state.Media = snap;

        bool trackChanged = snap.HasSession && !string.IsNullOrEmpty(snap.Title)
            && prev.HasSession && snap.ArtKey != prev.ArtKey;
        if (trackChanged)
        {
            _state.SetAlert(new AlertInfo
            {
                Icon = "\uE8D6", // music
                Title = snap.Title,
                Subtitle = snap.Artist,
                Accent = new SKColor(10, 132, 255),
                Image = snap.Art, // show album art when available
            }, 4200);
        }

        _notify();
    }

    private static async Task<SKImage?> LoadArtAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
    {
        try
        {
            var streamRef = props.Thumbnail;
            if (streamRef == null) return null;

            using var stream = await streamRef.OpenReadAsync();
            uint size = (uint)stream.Size;
            if (size == 0) return null;

            var reader = new DataReader(stream);
            await reader.LoadAsync(size);
            var bytes = new byte[size];
            reader.ReadBytes(bytes);

            return SKImage.FromEncodedData(bytes);
        }
        catch { return null; }
    }

    public async void Control(string action)
    {
        try
        {
            var session = _manager?.GetCurrentSession();
            if (session == null) return;

            switch (action)
            {
                case "toggle": await session.TryTogglePlayPauseAsync(); break;
                case "next": await session.TrySkipNextAsync(); break;
                case "previous": await session.TrySkipPreviousAsync(); break;
            }
        }
        catch { }
    }

    /// <summary>
    /// Jumps the active session to <paramref name="fraction"/> (0..1) of the
    /// current track duration. No-op when the source doesn't expose a duration
    /// or doesn't support seeking.
    /// </summary>
    public async void Seek(double fraction)
    {
        try
        {
            var session = _manager?.GetCurrentSession();
            if (session == null) return;

            double dur = _state.Media.DurationSec;
            if (dur <= 0) return;

            fraction = fraction < 0 ? 0 : (fraction > 1 ? 1 : fraction);
            long ticks = (long)(fraction * dur * TimeSpan.TicksPerSecond);
            await session.TryChangePlaybackPositionAsync(ticks);
        }
        catch { }
    }
}
