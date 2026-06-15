using SkiaSharp;
using System.Globalization;
using WinIsland.Services;

namespace WinIsland;

public sealed class HitRegion
{
    public SKRect Rect;
    public Action OnClick = () => { };

    // Optional drag support (e.g. the media seek bar). When OnDrag is set the
    // region becomes draggable: OnDrag fires on press and while the cursor moves
    // (live preview), OnDragCommit fires once the button is released (commit).
    // The float passed is the horizontal cursor position as a 0..1 fraction
    // within Rect.
    public Action<float>? OnDrag;
    public Action<float>? OnDragCommit;
}

public interface IIslandActions
{
    void MediaControl(string action);
    void MediaSeek(float fraction);
    void MediaSeekPreview(float fraction);
    void SelectView(ViewKind view);
    void TimerToggle();
    void TimerReset();
    void TimerSelectPreset(int index);
    void SetPosition(IslandSide side);
    void SetLanguage(AppLanguage lang);
    void SetAccent(int argb);
    void ToggleTab(ViewKind view);
    void PickCustomAccent();
    void TestNotification();
    void ToggleTask(int index);
    void RemoveTask(int index);
    void AddTask();
    void BeginEditTask(int index);
    void ComposerCommit();
    void ComposerCancel();
    void ComposerQuickDate(int kind);   // 0 = today, 1 = tomorrow
    void ComposerAdjustDate(int days);
    void ComposerClearDate();
    void ComposerToggleTime();
    void ComposerAdjustHour(int delta);
    void ComposerAdjustMinute(int delta);
}

/// <summary>Draws the whole island with SkiaSharp based on AppState.</summary>
public sealed class Renderer
{
    private readonly AppState _state;
    private readonly IIslandActions _actions;

    private readonly SKTypeface _text;
    private readonly SKTypeface _textBold;
    private readonly SKTypeface _icons;

    // Wall-clock seconds from the render loop, used to blink the composer caret.
    private double _caretClock;

    public Renderer(AppState state, IIslandActions actions)
    {
        _state = state;
        _actions = actions;
        _text = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.SemiBold,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        _textBold = SKTypeface.FromFamilyName("Segoe UI", SKFontStyleWeight.Bold,
            SKFontStyleWidth.Normal, SKFontStyleSlant.Upright);
        // Load the icon font directly from file: FromFamilyName silently falls
        // back to a default (no glyphs) when the family is missing, which is why
        // icons showed as boxes. Segoe Fluent Icons (Win11) → MDL2 (Win10).
        _icons = SKTypeface.FromFile(@"C:\Windows\Fonts\SegoeIcons.ttf")
            ?? SKTypeface.FromFile(@"C:\Windows\Fonts\segmdl2.ttf")
            ?? SKTypeface.Default;
    }

    // ---- text helpers ----
    private void Text(SKCanvas c, string s, float x, float y, SKTypeface tf, float size,
        SKColor color, SKTextAlign align = SKTextAlign.Left)
    {
        using var p = new SKPaint
        {
            IsAntialias = true,
            Color = color,
            Typeface = tf,
            TextSize = size,
            TextAlign = align,
            SubpixelText = true,
        };
        c.DrawText(s, x, y, p);
    }

    private float Measure(string s, SKTypeface tf, float size)
    {
        using var p = new SKPaint { Typeface = tf, TextSize = size };
        return p.MeasureText(s);
    }

    private static float Clamp01(float v) => v < 0 ? 0 : (v > 1 ? 1 : v);

    /// <summary>User-chosen theme accent.</summary>
    private SKColor Accent => (SKColor)(uint)_state.Settings.AccentArgb;

    public void Render(SKCanvas canvas, SKRect rect, float radius, float scale,
        float expandedness, List<HitRegion> regions, float winX, float winY, double timeSec)
    {
        _caretClock = timeSec;
        canvas.Clear(SKColors.Transparent);
        DrawPill(canvas, rect, radius, scale);

        float t = expandedness;
        float smallAlpha = Clamp01(1 - t * 2f);
        float expAlpha = Clamp01((t - 0.5f) * 2f);

        var alert = _state.GetActiveAlert();

        if (smallAlpha > 0.01f)
        {
            if (alert != null) DrawAlert(canvas, rect, smallAlpha, alert, scale);
            else DrawCompact(canvas, rect, smallAlpha, scale, timeSec);
        }

        if (expAlpha > 0.01f)
        {
            DrawExpanded(canvas, rect, expAlpha, scale, regions, winX, winY, expAlpha > 0.6f);
        }
    }

    private void DrawPill(SKCanvas canvas, SKRect rect, float radius, float scale)
    {
        using (var shadow = new SKPaint
        {
            IsAntialias = true,
            Color = new SKColor(0, 0, 0, 150),
            MaskFilter = SKMaskFilter.CreateBlur(SKBlurStyle.Normal, 16 * scale),
        })
        {
            var sr = new SKRect(rect.Left, rect.Top + 6 * scale, rect.Right, rect.Bottom + 7 * scale);
            canvas.DrawRoundRect(sr, radius, radius, shadow);
        }

        using (var body = new SKPaint { IsAntialias = true })
        {
            body.Shader = SKShader.CreateLinearGradient(
                new SKPoint(rect.Left, rect.Top),
                new SKPoint(rect.Left, rect.Bottom),
                new[] { new SKColor(34, 34, 40, 252), new SKColor(14, 14, 18, 255) },
                new float[] { 0f, 1f }, SKShaderTileMode.Clamp);
            canvas.DrawRoundRect(rect, radius, radius, body);
        }

        using (var rim = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = 1 * scale,
            Color = new SKColor(255, 255, 255, 36),
        })
        {
            var inset = new SKRect(rect.Left + 0.5f, rect.Top + 0.5f, rect.Right - 0.5f, rect.Bottom - 0.5f);
            canvas.DrawRoundRect(inset, radius, radius, rim);
        }
    }

    // ---- Compact ----
    private void DrawCompact(SKCanvas c, SKRect rect, float alpha, float scale, double timeSec)
    {
        byte a = (byte)(alpha * 255);
        var media = _state.Media;
        float midY = rect.MidY;
        bool hasMusic = media.HasSession && !string.IsNullOrEmpty(media.Title);

        // Timer running with no music: keep the centered timer readout.
        if (_state.TimerRunning && !hasMusic)
        {
            var accent = (SKColor)(uint)_state.TimerAccentArgb;
            accent = accent.WithAlpha(a);
            string time = PomodoroTimer.Format(_state.TimerRemaining);
            float iconW = Measure("\uE916", _icons, 15 * scale);
            float tw = Measure(time, _textBold, 14 * scale);
            float total = iconW + 8 * scale + tw;
            float x = rect.MidX - total / 2f;
            Text(c, "\uE916", x, midY + 5 * scale, _icons, 15 * scale, accent);
            Text(c, time, x + iconW + 8 * scale, midY + 5 * scale, _textBold, 14 * scale,
                new SKColor(245, 245, 247, a));
            return;
        }

        if (hasMusic)
        {
            float pad = 15 * scale;
            float artSize = 24 * scale;
            float left = rect.Left + pad;

            if (media.Art != null)
            {
                var ar = new SKRect(left, midY - artSize / 2, left + artSize, midY + artSize / 2);
                DrawRoundImage(c, media.Art, ar, 6 * scale, a);
                left += artSize + 9 * scale;
            }
            else
            {
                Text(c, "\uE8D6", left, midY + 5 * scale, _icons, 15 * scale, Accent.WithAlpha(a));
                left += 22 * scale;
            }

            // Right-side readout: the running timer takes priority over the song's
            // elapsed time so both music and timer stay visible at once.
            float rightPad = 15 * scale;
            float rightEdge = rect.Right - rightPad;
            float rightSize = 12.5f * scale;
            float rightBlockW;

            if (_state.TimerRunning)
            {
                var accent = ((SKColor)(uint)_state.TimerAccentArgb).WithAlpha(a);
                string t = PomodoroTimer.Format(_state.TimerRemaining);
                float iconW = Measure("\uE916", _icons, 11.5f * scale);
                float tw = Measure(t, _textBold, rightSize);
                rightBlockW = iconW + 5 * scale + tw;
                Text(c, "\uE916", rightEdge - rightBlockW, midY + 4.5f * scale, _icons, 11.5f * scale, accent);
                Text(c, t, rightEdge, midY + 4.5f * scale, _textBold, rightSize,
                    new SKColor(245, 245, 247, a), SKTextAlign.Right);
            }
            else
            {
                string t = PomodoroTimer.Format((int)ComputeElapsed(media));
                rightBlockW = Measure(t, _textBold, rightSize);
                Text(c, t, rightEdge, midY + 4.5f * scale, _textBold, rightSize,
                    Accent.WithAlpha(a), SKTextAlign.Right);
            }

            float titleMax = (rightEdge - rightBlockW - 10 * scale) - left;
            string title = Truncate(media.Title, _text, 13 * scale, Math.Max(40 * scale, titleMax));
            Text(c, title, left, midY + 4.5f * scale, _text, 13 * scale, new SKColor(245, 245, 247, a));
            return;
        }

        // Idle: clock + dot
        using (var dot = new SKPaint { IsAntialias = true, Color = new SKColor(52, 199, 89, a) })
        {
            string clock = DateTime.Now.ToString("HH:mm");
            float tw = Measure(clock, _textBold, 13 * scale);
            float dotR = 4f * scale;
            float total = dotR * 2 + 9 * scale + tw;
            float x = rect.MidX - total / 2f;
            c.DrawCircle(x + dotR, rect.MidY, dotR, dot);
            Text(c, clock, x + dotR * 2 + 9 * scale, rect.MidY + 4.5f * scale, _textBold, 13 * scale,
                new SKColor(245, 245, 247, a));
        }
    }

    /// <summary>
    /// Live-computed song position from the last GSMTC sample. Interpolated while
    /// playing and clamped to the track duration.
    /// </summary>
    private static double ComputeElapsed(MediaSnapshot media)
    {
        double elapsed = media.PositionSec;
        if (media.IsPlaying && media.PosTicks > 0)
            elapsed += (DateTime.UtcNow.Ticks - media.PosTicks) / 1e7;
        if (media.DurationSec > 0) elapsed = Math.Min(elapsed, media.DurationSec);
        return elapsed < 0 ? 0 : elapsed;
    }

    // ---- Alert ----
    private void DrawAlert(SKCanvas c, SKRect rect, float alpha, AlertInfo alert, float scale)
    {
        byte a = (byte)(alpha * 255);
        float pad = 14 * scale;
        float iconBox = 34 * scale;
        float left = rect.Left + pad;
        float iconY = rect.MidY - iconBox / 2;

        using (var bg = new SKPaint { IsAntialias = true, Color = alert.Accent.WithAlpha((byte)(a * 0.25f)) })
        {
            var ir = new SKRect(left, iconY, left + iconBox, iconY + iconBox);
            c.DrawRoundRect(ir, 10 * scale, 10 * scale, bg);
        }
        if (alert.Image != null)
        {
            var ir = new SKRect(left, iconY, left + iconBox, iconY + iconBox);
            DrawRoundImage(c, alert.Image, ir, 10 * scale, a);
        }
        else
        {
            Text(c, alert.Icon, left + iconBox / 2, rect.MidY + 6 * scale, _icons, 16 * scale,
                alert.Accent.WithAlpha(a), SKTextAlign.Center);
        }

        float tx = left + iconBox + 11 * scale;
        bool hasSub = !string.IsNullOrEmpty(alert.Subtitle);
        float titleY = hasSub ? rect.MidY - 1 * scale : rect.MidY + 5 * scale;
        string title = Truncate(alert.Title, _textBold, 13 * scale, 230 * scale);
        Text(c, title, tx, titleY, _textBold, 13 * scale, new SKColor(245, 245, 247, a));
        if (hasSub)
        {
            string sub = Truncate(alert.Subtitle!, _text, 11.5f * scale, 230 * scale);
            Text(c, sub, tx, rect.MidY + 14 * scale, _text, 11.5f * scale,
                new SKColor(255, 255, 255, (byte)(a * 0.65f)));
        }
    }

    // ---- Expanded ----
    private void DrawExpanded(SKCanvas c, SKRect rect, float alpha, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        byte a = (byte)(alpha * 255);
        float padX = 16 * scale;
        float navH = 34 * scale;
        var bodyRect = new SKRect(rect.Left, rect.Top, rect.Right, rect.Bottom - navH);

        switch (_state.View)
        {
            case ViewKind.Music: DrawMusic(c, bodyRect, a, scale, regions, winX, winY, interactive); break;
            case ViewKind.Timer: DrawTimer(c, bodyRect, a, scale, regions, winX, winY, interactive); break;
            case ViewKind.Camera: DrawCamera(c, bodyRect, a, scale); break;
            case ViewKind.Tasks: DrawTasks(c, bodyRect, a, scale, regions, winX, winY, interactive); break;
            case ViewKind.Settings: DrawSettings(c, bodyRect, a, scale, regions, winX, winY, interactive); break;
        }

        DrawNav(c, rect, navH, a, scale, regions, winX, winY, interactive);

        if (_state.View == ViewKind.Music) DrawMusicControls(c, bodyRect, a, scale, regions, winX, winY, interactive);
    }

    private void DrawMusic(SKCanvas c, SKRect rect, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        var media = _state.Media;
        float padX = 18 * scale;
        if (!media.HasSession || string.IsNullOrEmpty(media.Title))
        {
            Text(c, Loc.T("music.nothing"), rect.MidX, rect.Top + 34 * scale, _text, 13 * scale,
                new SKColor(255, 255, 255, (byte)(a * 0.6f)), SKTextAlign.Center);
            return;
        }

        float artSize = 52 * scale;
        float artX = rect.Left + padX;
        float artY = rect.Top + 16 * scale;
        var ar = new SKRect(artX, artY, artX + artSize, artY + artSize);
        if (media.Art != null) DrawRoundImage(c, media.Art, ar, 13 * scale, a);
        else
        {
            using var ph = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.08f)) };
            c.DrawRoundRect(ar, 13 * scale, 13 * scale, ph);
            Text(c, "\uE8D6", ar.MidX, ar.MidY + 8 * scale, _icons, 22 * scale,
                new SKColor(255, 255, 255, a), SKTextAlign.Center);
        }

        float tx = artX + artSize + 14 * scale;
        string title = Truncate(media.Title, _textBold, 15 * scale, rect.Width - (tx - rect.Left) - padX);
        Text(c, title, tx, artY + 22 * scale, _textBold, 15 * scale, new SKColor(245, 245, 247, a));
        string artist = Truncate(string.IsNullOrEmpty(media.Artist) ? "—" : media.Artist,
            _text, 12 * scale, rect.Width - (tx - rect.Left) - padX);
        Text(c, artist, tx, artY + 41 * scale, _text, 12 * scale, new SKColor(255, 255, 255, (byte)(a * 0.65f)));

        // Progress bar with elapsed / duration, flanking the track.
        double dur = media.DurationSec;
        double elapsed = ComputeElapsed(media);

        // While the user is dragging/clicking the bar (or just after committing),
        // the bar follows the cursor instead of the reported playback position.
        bool seeking = _state.MediaSeeking || DateTime.UtcNow.Ticks < _state.MediaSeekStickyTicks;
        float prog = dur > 0 ? Clamp01((float)(elapsed / dur)) : 0f;
        if (seeking)
        {
            prog = Clamp01((float)_state.MediaSeekFraction);
            if (dur > 0) elapsed = prog * dur;
        }

        float rowY = artY + artSize + 18 * scale;
        float tsize = 10.5f * scale;
        var subColor = new SKColor(255, 255, 255, (byte)(a * 0.6f));
        string elStr = PomodoroTimer.Format((int)elapsed);
        string durStr = dur > 0 ? PomodoroTimer.Format((int)dur) : "--:--";
        float durW = Measure(durStr, _text, tsize);
        float elW = Measure(elStr, _text, tsize);
        Text(c, elStr, rect.Left + padX, rowY + 3.5f * scale, _text, tsize, subColor);
        Text(c, durStr, rect.Right - padX, rowY + 3.5f * scale, _text, tsize, subColor, SKTextAlign.Right);

        float barLeft = rect.Left + padX + elW + 9 * scale;
        float barRight = rect.Right - padX - durW - 9 * scale;
        if (barRight > barLeft)
        {
            float bh = 3.5f * scale;
            var track = new SKRect(barLeft, rowY - bh / 2, barRight, rowY + bh / 2);
            using (var tp = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.15f)) })
                c.DrawRoundRect(track, bh / 2, bh / 2, tp);
            if (prog > 0)
            {
                var fill = new SKRect(barLeft, rowY - bh / 2, barLeft + (barRight - barLeft) * prog, rowY + bh / 2);
                using var fp = new SKPaint { IsAntialias = true, Color = Accent.WithAlpha(a) };
                c.DrawRoundRect(fill, bh / 2, bh / 2, fp);
            }

            // Draggable seek handle + hit region. Only when the bar is interactive
            // and the source reports a duration (otherwise seeking is impossible).
            if (interactive && dur > 0)
            {
                float knobX = barLeft + (barRight - barLeft) * prog;
                float knobR = (seeking ? 6.5f : 5f) * scale;
                using (var kp = new SKPaint { IsAntialias = true, Color = new SKColor(245, 245, 247, a) })
                    c.DrawCircle(knobX, rowY, knobR, kp);

                // Generous hit area covering the full bar row so the thin bar is
                // easy to grab and click.
                float hitH = 11 * scale;
                var hit = new SKRect(barLeft - knobR, rowY - hitH, barRight + knobR, rowY + hitH);
                float bl = barLeft, br = barRight;
                regions.Add(new HitRegion
                {
                    Rect = hit,
                    OnDrag = f => _actions.MediaSeekPreview(BarFraction(hit, bl, br, f)),
                    OnDragCommit = f => _actions.MediaSeek(BarFraction(hit, bl, br, f)),
                });
            }
        }
    }

    /// <summary>
    /// Converts a 0..1 fraction measured across the (padded) hit region into a
    /// 0..1 fraction across the actual track segment.
    /// </summary>
    private static float BarFraction(SKRect hit, float barLeft, float barRight, float hitFraction)
    {
        float x = hit.Left + hit.Width * hitFraction;
        float span = barRight - barLeft;
        if (span <= 0) return 0;
        return Clamp01((x - barLeft) / span);
    }

    private void DrawMusicControls(SKCanvas c, SKRect rect, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        var media = _state.Media;
        if (!media.HasSession || string.IsNullOrEmpty(media.Title)) return;

        float y = rect.Bottom - 16 * scale;
        float cx = rect.MidX;
        float gap = 30 * scale;
        var color = new SKColor(245, 245, 247, a);

        IconButton(c, "\uE892", cx - gap, y, 18 * scale, color, scale, regions, winX, winY,
            interactive, () => _actions.MediaControl("previous"));
        IconButton(c, media.IsPlaying ? "\uE769" : "\uE768", cx, y, 22 * scale, color, scale, regions, winX, winY,
            interactive, () => _actions.MediaControl("toggle"));
        IconButton(c, "\uE893", cx + gap, y, 18 * scale, color, scale, regions, winX, winY,
            interactive, () => _actions.MediaControl("next"));
    }

    private void DrawTimer(SKCanvas c, SKRect rect, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        var accent = ((SKColor)(uint)_state.TimerAccentArgb).WithAlpha(a);
        string time = PomodoroTimer.Format(_state.TimerRemaining);
        Text(c, time, rect.MidX, rect.Top + 46 * scale, _textBold, 40 * scale, accent, SKTextAlign.Center);

        // Presets
        float py = rect.Top + 70 * scale;
        float chipH = 24 * scale;
        var presets = PomodoroTimer.Presets;
        float[] widths = new float[presets.Length];
        float totalW = 0;
        for (int i = 0; i < presets.Length; i++)
        {
            widths[i] = Measure(Loc.T("preset." + presets[i].Label), _text, 12 * scale) + 22 * scale;
            totalW += widths[i];
        }
        totalW += 7 * scale * (presets.Length - 1);
        float px = rect.MidX - totalW / 2f;
        for (int i = 0; i < presets.Length; i++)
        {
            bool active = presets[i].Label == _state.TimerLabel;
            var chip = new SKRect(px, py, px + widths[i], py + chipH);
            using (var bg = new SKPaint
            {
                IsAntialias = true,
                Color = active ? presets[i].Accent.WithAlpha(a) : new SKColor(255, 255, 255, (byte)(a * 0.08f)),
            })
                c.DrawRoundRect(chip, chipH / 2, chipH / 2, bg);
            Text(c, Loc.T("preset." + presets[i].Label), chip.MidX, chip.MidY + 4.5f * scale, _text, 12 * scale,
                active ? new SKColor(11, 11, 13, a) : new SKColor(255, 255, 255, (byte)(a * 0.85f)),
                SKTextAlign.Center);
            int idx = i;
            AddRegion(regions, chip, winX, winY, interactive, () => _actions.TimerSelectPreset(idx));
            px += widths[i] + 7 * scale;
        }

        // Actions: Start/Pause + Reset
        float ay = py + chipH + 12 * scale;
        float btnH = 30 * scale;
        string startLabel = _state.TimerRunning ? Loc.T("timer.pause") : Loc.T("timer.start");
        float w1 = Measure(startLabel, _textBold, 13 * scale) + 36 * scale;
        float w2 = Measure(Loc.T("timer.reset"), _text, 13 * scale) + 30 * scale;
        float gap = 10 * scale;
        float total = w1 + gap + w2;
        float bx = rect.MidX - total / 2f;

        var startBtn = new SKRect(bx, ay, bx + w1, ay + btnH);
        using (var bg = new SKPaint { IsAntialias = true, Color = ((SKColor)(uint)_state.TimerAccentArgb).WithAlpha(a) })
            c.DrawRoundRect(startBtn, btnH / 2, btnH / 2, bg);
        Text(c, startLabel, startBtn.MidX, startBtn.MidY + 4.5f * scale, _textBold, 13 * scale,
            new SKColor(11, 11, 13, a), SKTextAlign.Center);
        AddRegion(regions, startBtn, winX, winY, interactive, () => _actions.TimerToggle());

        var resetBtn = new SKRect(bx + w1 + gap, ay, bx + w1 + gap + w2, ay + btnH);
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.1f)) })
            c.DrawRoundRect(resetBtn, btnH / 2, btnH / 2, bg);
        Text(c, Loc.T("timer.reset"), resetBtn.MidX, resetBtn.MidY + 4.5f * scale, _text, 13 * scale,
            new SKColor(245, 245, 247, a), SKTextAlign.Center);
        AddRegion(regions, resetBtn, winX, winY, interactive, () => _actions.TimerReset());
    }

    private void DrawCamera(SKCanvas c, SKRect rect, byte a, float scale)
    {
        float padX = 16 * scale;
        var view = new SKRect(rect.Left + padX, rect.Top + 14 * scale, rect.Right - padX, rect.Bottom - 6 * scale);
        float r = 14 * scale;

        lock (_state.CameraLock)
        {
            if (_state.HasCameraFrame && _state.CameraBitmap != null)
            {
                c.Save();
                using (var clip = new SKPath())
                {
                    clip.AddRoundRect(view, r, r);
                    c.ClipPath(clip, antialias: true);
                }
                c.Translate(view.MidX, 0);
                c.Scale(-1, 1);
                c.Translate(-view.MidX, 0);
                var dst = FitCover(_state.CameraBitmap.Width, _state.CameraBitmap.Height, view);
                using var paint = new SKPaint { IsAntialias = true, FilterQuality = SKFilterQuality.Medium };
                c.DrawBitmap(_state.CameraBitmap, dst, paint);
                c.Restore();
                return;
            }
        }

        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.06f)) })
            c.DrawRoundRect(view, r, r, bg);
        string msg = _state.CameraError ?? Loc.T("camera.starting");
        Text(c, msg, view.MidX, view.MidY + 5 * scale, _text, 13 * scale,
            new SKColor(255, 255, 255, (byte)(a * 0.7f)), SKTextAlign.Center);
    }

    private void DrawTasks(SKCanvas c, SKRect rect, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        // While composing, the whole body becomes the inline editor.
        if (_state.Composer.Active)
        {
            DrawComposer(c, rect, a, scale, regions, winX, winY, interactive);
            return;
        }

        float padX = 16 * scale;
        var tasks = _state.Settings.Tasks;

        // Header: title + add button (opens the inline composer).
        float headY = rect.Top + 22 * scale;
        Text(c, Loc.T("tab.tasks"), rect.Left + padX, headY, _textBold, 14 * scale,
            new SKColor(245, 245, 247, a));

        float addR = 11 * scale;
        float addX = rect.Right - padX - addR;
        float addCy = headY - 4 * scale;
        using (var bg = new SKPaint { IsAntialias = true, Color = Accent.WithAlpha(a) })
            c.DrawCircle(addX, addCy, addR, bg);
        Text(c, "\uE710", addX, addCy + 4 * scale, _icons, 11 * scale,
            new SKColor(11, 11, 13, a), SKTextAlign.Center);
        var addHit = new SKRect(addX - addR - 3 * scale, addCy - addR - 3 * scale,
            addX + addR + 3 * scale, addCy + addR + 3 * scale);
        AddRegion(regions, addHit, winX, winY, interactive, () => _actions.AddTask());

        float listTop = headY + 12 * scale;

        if (tasks.Count == 0)
        {
            Text(c, Loc.T("tasks.empty"), rect.MidX, listTop + 28 * scale, _text, 12 * scale,
                new SKColor(255, 255, 255, (byte)(a * 0.5f)), SKTextAlign.Center);
            return;
        }

        float rowH = 30 * scale;
        int maxRows = Math.Max(0, (int)((rect.Bottom - listTop - 4 * scale) / rowH));
        int count = Math.Min(tasks.Count, maxRows);
        float removeX = rect.Right - padX - 9 * scale;

        for (int i = 0; i < count; i++)
        {
            var item = tasks[i];
            float ry = listTop + i * rowH;
            float midY = ry + rowH / 2f;

            // Checkbox glyph (filled when done) — toggles the done state.
            float cbX = rect.Left + padX + 9 * scale;
            string box = item.Done ? "\uE73E" : "\uE739";
            var boxColor = item.Done ? Accent.WithAlpha(a) : new SKColor(255, 255, 255, (byte)(a * 0.7f));
            Text(c, box, cbX, midY + 5.5f * scale, _icons, 16 * scale, boxColor, SKTextAlign.Center);

            float tx = cbX + 16 * scale;

            // Due-date chip on the right (before the remove button).
            float dueRight = removeX - 16 * scale;
            float textRight = dueRight;
            if (item.DueAt is { } due)
            {
                string label = FormatDue(item);
                float dsize = 10.5f * scale;
                float dw = Measure(label, _text, dsize);
                float glyphW = Measure("\uE787", _icons, 9.5f * scale) + 4 * scale;
                bool overdue = item.IsOverdue;
                var dueColor = overdue
                    ? new SKColor(255, 90, 80, a)
                    : (item.Done ? new SKColor(255, 255, 255, (byte)(a * 0.4f)) : Accent.WithAlpha(a));
                float chipX = dueRight - dw;
                Text(c, "\uE787", chipX - glyphW + 2 * scale, midY + 4 * scale, _icons, 9.5f * scale, dueColor);
                Text(c, label, chipX, midY + 4 * scale, _text, dsize, dueColor);
                textRight = chipX - glyphW - 6 * scale;
            }

            // Task text (dimmed when done) — tapping it opens the editor.
            var txtColor = item.Done ? new SKColor(255, 255, 255, (byte)(a * 0.45f))
                                     : new SKColor(245, 245, 247, a);
            string txt = Truncate(item.Text, _text, 13 * scale, Math.Max(20 * scale, textRight - tx));
            Text(c, txt, tx, midY + 4.5f * scale, _text, 13 * scale, txtColor);

            // Remove button.
            Text(c, "\uE711", removeX, midY + 5 * scale, _icons, 12 * scale,
                new SKColor(255, 255, 255, (byte)(a * 0.5f)), SKTextAlign.Center);

            int idx = i;
            // Checkbox area toggles done; the rest of the row opens the editor.
            var checkHit = new SKRect(rect.Left + padX, ry, cbX + 12 * scale, ry + rowH);
            AddRegion(regions, checkHit, winX, winY, interactive, () => _actions.ToggleTask(idx));
            var editHit = new SKRect(cbX + 12 * scale, ry, removeX - 12 * scale, ry + rowH);
            AddRegion(regions, editHit, winX, winY, interactive, () => _actions.BeginEditTask(idx));
            var removeHit = new SKRect(removeX - 11 * scale, ry, removeX + 11 * scale, ry + rowH);
            AddRegion(regions, removeHit, winX, winY, interactive, () => _actions.RemoveTask(idx));
        }
    }

    // ---- inline task composer ----
    private void DrawComposer(SKCanvas c, SKRect rect, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        var comp = _state.Composer;
        float padX = 16 * scale;
        float left = rect.Left + padX;
        float right = rect.Right - padX;
        var labelColor = new SKColor(255, 255, 255, (byte)(a * 0.55f));

        float y = rect.Top + 12 * scale;

        // --- text field ---
        float fieldH = 34 * scale;
        var field = new SKRect(left, y, right, y + fieldH);
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.10f)) })
            c.DrawRoundRect(field, 9 * scale, 9 * scale, bg);
        using (var border = new SKPaint
        {
            IsAntialias = true,
            IsStroke = true,
            StrokeWidth = 1.5f * scale,
            Color = Accent.WithAlpha((byte)(a * 0.9f)),
        })
            c.DrawRoundRect(field, 9 * scale, 9 * scale, border);

        float tsize = 13.5f * scale;
        float textBaseline = field.MidY + 4.5f * scale;
        float textX = field.Left + 12 * scale;
        string content = comp.Text;
        if (content.Length == 0)
        {
            Text(c, Loc.T("tasks.placeholder"), textX, textBaseline, _text, tsize,
                new SKColor(255, 255, 255, (byte)(a * 0.4f)));
        }
        else
        {
            Text(c, content, textX, textBaseline, _text, tsize, new SKColor(245, 245, 247, a));
        }

        // Blinking caret.
        if (((int)(_caretClock * 2) & 1) == 0)
        {
            int caret = Math.Clamp(comp.Caret, 0, content.Length);
            float caretX = textX + Measure(content[..caret], _text, tsize);
            using var cp = new SKPaint { IsAntialias = true, Color = new SKColor(245, 245, 247, a), StrokeWidth = 1.5f * scale };
            c.DrawLine(caretX, field.MidY - 9 * scale, caretX, field.MidY + 9 * scale, cp);
        }
        AddRegion(regions, field, winX, winY, interactive, () => { /* already focused */ });

        y += fieldH + 12 * scale;

        // --- date row ---
        float h = 26 * scale;
        Text(c, Loc.T("tasks.date"), left, y + h / 2f + 4 * scale, _text, 11.5f * scale, labelColor);
        float rowX = left + 42 * scale;

        if (!comp.HasDate)
        {
            rowX += Chip(c, rowX, y, h, Loc.T("tasks.today"), false, a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerQuickDate(0)) + 7 * scale;
            Chip(c, rowX, y, h, Loc.T("tasks.tomorrow"), false, a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerQuickDate(1));
        }
        else
        {
            // ‹ <date> › stepper, then a clear button.
            float arrow = h;
            SquareBtn(c, new SKRect(rowX, y, rowX + arrow, y + h), "\uE76B", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerAdjustDate(-1));
            float dateW = 96 * scale;
            var datePill = new SKRect(rowX + arrow + 4 * scale, y, rowX + arrow + 4 * scale + dateW, y + h);
            using (var bg = new SKPaint { IsAntialias = true, Color = Accent.WithAlpha(a) })
                c.DrawRoundRect(datePill, h / 2, h / 2, bg);
            Text(c, FormatDate(comp.Date), datePill.MidX, datePill.MidY + 4 * scale, _textBold, 12 * scale,
                new SKColor(11, 11, 13, a), SKTextAlign.Center);
            float ax2 = datePill.Right + 4 * scale;
            SquareBtn(c, new SKRect(ax2, y, ax2 + arrow, y + h), "\uE76C", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerAdjustDate(1));
            float clearX = ax2 + arrow + 8 * scale;
            SquareBtn(c, new SKRect(clearX, y, clearX + arrow, y + h), "\uE711", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerClearDate());
        }

        y += h + 10 * scale;

        // --- time row (only meaningful once a date exists) ---
        Text(c, Loc.T("tasks.time"), left, y + h / 2f + 4 * scale, _text, 11.5f * scale, labelColor);
        float trowX = left + 42 * scale;
        if (!comp.HasDate)
        {
            Text(c, "—", trowX, y + h / 2f + 4 * scale, _text, 12 * scale,
                new SKColor(255, 255, 255, (byte)(a * 0.35f)));
        }
        else if (!comp.HasTime)
        {
            Chip(c, trowX, y, h, Loc.T("tasks.addtime"), false, a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerToggleTime());
        }
        else
        {
            float arrow = h;
            // Hour stepper.
            SquareBtn(c, new SKRect(trowX, y, trowX + arrow, y + h), "\uE70E", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerAdjustHour(1));
            SquareBtn(c, new SKRect(trowX, y + h + 3 * scale, trowX + arrow, y + 2 * h + 3 * scale), "\uE70D", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerAdjustHour(-1));
            // The big HH:MM readout sits to the right of the steppers.
            string clock = $"{comp.Hour:00}:{comp.Minute:00}";
            float ttx = trowX + arrow + 10 * scale;
            Text(c, clock, ttx, y + h + 4 * scale, _textBold, 20 * scale, new SKColor(245, 245, 247, a));
            float minStepX = ttx + Measure(clock, _textBold, 20 * scale) + 12 * scale;
            SquareBtn(c, new SKRect(minStepX, y, minStepX + arrow, y + h), "\uE70E", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerAdjustMinute(5));
            SquareBtn(c, new SKRect(minStepX, y + h + 3 * scale, minStepX + arrow, y + 2 * h + 3 * scale), "\uE70D", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerAdjustMinute(-5));
            float rmX = minStepX + arrow + 10 * scale;
            SquareBtn(c, new SKRect(rmX, y + h / 2f, rmX + arrow, y + h / 2f + h), "\uE711", a, scale,
                regions, winX, winY, interactive, () => _actions.ComposerToggleTime());
        }

        // --- action buttons pinned to the bottom ---
        float btnH = 32 * scale;
        float by = rect.Bottom - btnH - 4 * scale;
        float gap = 10 * scale;
        float btnW = (right - left - gap) / 2f;

        var cancelBtn = new SKRect(left, by, left + btnW, by + btnH);
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.1f)) })
            c.DrawRoundRect(cancelBtn, btnH / 2, btnH / 2, bg);
        Text(c, Loc.T("tasks.cancel"), cancelBtn.MidX, cancelBtn.MidY + 4.5f * scale, _text, 13 * scale,
            new SKColor(245, 245, 247, a), SKTextAlign.Center);
        AddRegion(regions, cancelBtn, winX, winY, interactive, () => _actions.ComposerCancel());

        bool canSave = comp.Text.Trim().Length > 0;
        var saveBtn = new SKRect(left + btnW + gap, by, right, by + btnH);
        using (var bg = new SKPaint { IsAntialias = true, Color = Accent.WithAlpha(canSave ? a : (byte)(a * 0.4f)) })
            c.DrawRoundRect(saveBtn, btnH / 2, btnH / 2, bg);
        Text(c, Loc.T("tasks.save"), saveBtn.MidX, saveBtn.MidY + 4.5f * scale, _textBold, 13 * scale,
            new SKColor(11, 11, 13, a), SKTextAlign.Center);
        if (canSave)
            AddRegion(regions, saveBtn, winX, winY, interactive, () => _actions.ComposerCommit());
    }

    /// <summary>A small square icon button used by the composer steppers.</summary>
    private void SquareBtn(SKCanvas c, SKRect r, string glyph, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive, Action onClick)
    {
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.12f)) })
            c.DrawRoundRect(r, 7 * scale, 7 * scale, bg);
        Text(c, glyph, r.MidX, r.MidY + 4.5f * scale, _icons, 12 * scale,
            new SKColor(245, 245, 247, a), SKTextAlign.Center);
        AddRegion(regions, r, winX, winY, interactive, onClick);
    }

    /// <summary>A pill-shaped text chip. Returns its width.</summary>
    private float Chip(SKCanvas c, float x, float y, float h, string label, bool active, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive, Action onClick)
    {
        float w = Measure(label, _text, 12 * scale) + 22 * scale;
        var r = new SKRect(x, y, x + w, y + h);
        using (var bg = new SKPaint
        {
            IsAntialias = true,
            Color = active ? Accent.WithAlpha(a) : new SKColor(255, 255, 255, (byte)(a * 0.1f)),
        })
            c.DrawRoundRect(r, h / 2, h / 2, bg);
        Text(c, label, r.MidX, r.MidY + 4.5f * scale, _text, 12 * scale,
            active ? new SKColor(11, 11, 13, a) : new SKColor(255, 255, 255, (byte)(a * 0.85f)),
            SKTextAlign.Center);
        AddRegion(regions, r, winX, winY, interactive, onClick);
        return w;
    }

    /// <summary>Friendly relative date label (Today / Tomorrow / "ddd, d MMM").</summary>
    private static string FormatDate(DateTime d)
    {
        var today = DateTime.Today;
        if (d.Date == today) return Loc.T("date.today");
        if (d.Date == today.AddDays(1)) return Loc.T("date.tomorrow");
        if (d.Date == today.AddDays(-1)) return Loc.T("date.yesterday");
        var ci = Loc.Lang == AppLanguage.English ? CultureInfo.GetCultureInfo("en-US")
                                                 : CultureInfo.GetCultureInfo("pt-BR");
        return d.ToString("ddd, d MMM", ci);
    }

    /// <summary>Compact due label for a task row, e.g. "Hoje 14:30" or "12 jun".</summary>
    private static string FormatDue(TaskItem item)
    {
        if (item.DueAt is not { } d) return "";
        string date = FormatDate(d);
        return item.HasTime ? $"{date} {d:HH:mm}" : date;
    }

    private void DrawSettings(SKCanvas c, SKRect rect, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        float padX = 18 * scale;
        var labelColor = new SKColor(255, 255, 255, (byte)(a * 0.55f));
        float y = rect.Top + 22 * scale;

        // Screen position
        Text(c, Loc.T("settings.position"), rect.Left + padX, y, _text, 11.5f * scale, labelColor);
        y += 12 * scale;
        var sides = new[] { IslandSide.Left, IslandSide.Center, IslandSide.Right };
        var sideLabels = new[] { Loc.T("pos.Left"), Loc.T("pos.Center"), Loc.T("pos.Right") };
        y = DrawChipRow(c, rect, y, a, scale, regions, winX, winY, interactive, sideLabels,
            i => sides[i] == _state.Settings.Position, i => _actions.SetPosition(sides[i]));

        // Language
        y += 16 * scale;
        Text(c, Loc.T("settings.language"), rect.Left + padX, y, _text, 11.5f * scale, labelColor);
        y += 12 * scale;
        var langs = new[] { AppLanguage.Portuguese, AppLanguage.English };
        var langLabels = new[] { "Português", "English" };
        y = DrawChipRow(c, rect, y, a, scale, regions, winX, winY, interactive, langLabels,
            i => langs[i] == _state.Settings.Language, i => _actions.SetLanguage(langs[i]));

        // Accent color
        y += 16 * scale;
        Text(c, Loc.T("settings.accent"), rect.Left + padX, y, _text, 11.5f * scale, labelColor);
        y += 14 * scale;
        y = DrawColorRow(c, rect, y, a, scale, regions, winX, winY, interactive);

        // Visible tabs
        y += 18 * scale;
        Text(c, Loc.T("settings.tabs"), rect.Left + padX, y, _text, 11.5f * scale, labelColor);
        y += 12 * scale;
        var toggleable = AppSettings.ToggleableTabs;
        var tabLabels = new string[toggleable.Length];
        for (int i = 0; i < toggleable.Length; i++)
            tabLabels[i] = Loc.T("tab." + toggleable[i].ToString().ToLowerInvariant());
        y = DrawChipRow(c, rect, y, a, scale, regions, winX, winY, interactive, tabLabels,
            i => _state.Settings.EnabledTabs.Contains(toggleable[i]),
            i => _actions.ToggleTab(toggleable[i]));

        // Test notification button (full-width). Lets the user preview how an
        // incoming notification looks on the collapsed island.
        y += 16 * scale;
        float btnH = 30 * scale;
        var testBtn = new SKRect(rect.Left + padX, y, rect.Right - padX, y + btnH);
        using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.1f)) })
            c.DrawRoundRect(testBtn, btnH / 2, btnH / 2, bg);
        float iconW = Measure("\uE7ED", _icons, 13 * scale);
        string label = Loc.T("settings.testnotif");
        float labelW = Measure(label, _text, 12.5f * scale);
        float blockX = testBtn.MidX - (iconW + 7 * scale + labelW) / 2f;
        Text(c, "\uE7ED", blockX, testBtn.MidY + 4.5f * scale, _icons, 13 * scale, Accent.WithAlpha(a));
        Text(c, label, blockX + iconW + 7 * scale, testBtn.MidY + 4.5f * scale, _text, 12.5f * scale,
            new SKColor(245, 245, 247, a));
        AddRegion(regions, testBtn, winX, winY, interactive, () => _actions.TestNotification());
    }

    /// <summary>Draws a left-aligned row of selectable color swatches.</summary>
    private float DrawColorRow(SKCanvas c, SKRect rect, float y, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        float r = 11 * scale;
        float gap = 14 * scale;
        float x = rect.Left + 18 * scale + r;
        var palette = AppSettings.AccentPalette;
        foreach (var argb in palette)
        {
            var col = ((SKColor)(uint)argb).WithAlpha(a);
            using (var p = new SKPaint { IsAntialias = true, Color = col })
                c.DrawCircle(x, y + r, r, p);
            if (argb == _state.Settings.AccentArgb)
                using (var ring = new SKPaint
                {
                    IsAntialias = true,
                    IsStroke = true,
                    StrokeWidth = 2 * scale,
                    Color = new SKColor(255, 255, 255, a),
                })
                    c.DrawCircle(x, y + r, r + 3 * scale, ring);
            int picked = argb;
            var hit = new SKRect(x - r - 3 * scale, y - 3 * scale, x + r + 3 * scale, y + 2 * r + 3 * scale);
            AddRegion(regions, hit, winX, winY, interactive, () => _actions.SetAccent(picked));
            x += r * 2 + gap;
        }

        // Custom color swatch — opens the native Windows color picker. When the
        // current accent isn't one of the presets, this swatch shows it (with the
        // selection ring) so a custom color stays visible.
        bool customActive = Array.IndexOf(palette, _state.Settings.AccentArgb) < 0;
        var customFill = customActive
            ? ((SKColor)(uint)_state.Settings.AccentArgb).WithAlpha(a)
            : new SKColor(255, 255, 255, (byte)(a * 0.12f));
        using (var p = new SKPaint { IsAntialias = true, Color = customFill })
            c.DrawCircle(x, y + r, r, p);
        Text(c, "\uE710", x, y + r + 4 * scale, _icons, 12 * scale,
            new SKColor(255, 255, 255, customActive ? a : (byte)(a * 0.85f)), SKTextAlign.Center);
        if (customActive)
            using (var ring = new SKPaint
            {
                IsAntialias = true,
                IsStroke = true,
                StrokeWidth = 2 * scale,
                Color = new SKColor(255, 255, 255, a),
            })
                c.DrawCircle(x, y + r, r + 3 * scale, ring);
        var customHit = new SKRect(x - r - 3 * scale, y - 3 * scale, x + r + 3 * scale, y + 2 * r + 3 * scale);
        AddRegion(regions, customHit, winX, winY, interactive, () => _actions.PickCustomAccent());

        return y + r * 2;
    }

    /// <summary>Draws a left-aligned row of selectable chips. Returns the row's bottom Y.</summary>
    private float DrawChipRow(SKCanvas c, SKRect rect, float y, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive,
        string[] labels, Func<int, bool> isActive, Action<int> onClick)
    {
        float chipH = 26 * scale;
        float x = rect.Left + 18 * scale;
        var accent = Accent;
        for (int i = 0; i < labels.Length; i++)
        {
            float w = Measure(labels[i], _text, 12 * scale) + 22 * scale;
            var chip = new SKRect(x, y, x + w, y + chipH);
            bool active = isActive(i);
            using (var bg = new SKPaint
            {
                IsAntialias = true,
                Color = active ? accent.WithAlpha(a) : new SKColor(255, 255, 255, (byte)(a * 0.08f)),
            })
                c.DrawRoundRect(chip, chipH / 2, chipH / 2, bg);
            Text(c, labels[i], chip.MidX, chip.MidY + 4.5f * scale, _text, 12 * scale,
                active ? new SKColor(245, 245, 247, a) : new SKColor(255, 255, 255, (byte)(a * 0.85f)),
                SKTextAlign.Center);
            int idx = i;
            AddRegion(regions, chip, winX, winY, interactive, () => onClick(idx));
            x += w + 8 * scale;
        }
        return y + chipH;
    }

    private void DrawNav(SKCanvas c, SKRect rect, float navH, byte a, float scale,
        List<HitRegion> regions, float winX, float winY, bool interactive)
    {
        float y0 = rect.Bottom - navH;
        using (var line = new SKPaint { Color = new SKColor(255, 255, 255, (byte)(a * 0.08f)), StrokeWidth = 1 })
            c.DrawLine(rect.Left + 12 * scale, y0, rect.Right - 12 * scale, y0, line);

        (ViewKind view, string icon, string label)[] BuildTabs()
        {
            (ViewKind, string) Meta(ViewKind v) => v switch
            {
                ViewKind.Music => (v, "\uE8D6"),
                ViewKind.Timer => (v, "\uE916"),
                ViewKind.Camera => (v, "\uE722"),
                ViewKind.Tasks => (v, "\uE8FD"),
                ViewKind.Settings => (v, "\uE713"),
                _ => (v, "\uE700"),
            };
            string Label(ViewKind v) => Loc.T("tab." + v.ToString().ToLowerInvariant());

            var list = new List<(ViewKind, string, string)>();
            foreach (var v in AppSettings.ToggleableTabs)
                if (_state.Settings.EnabledTabs.Contains(v))
                {
                    var (vk, ic) = Meta(v);
                    list.Add((vk, ic, Label(vk)));
                }
            // Settings is always available so the user can't lock themselves out.
            var (sv, si) = Meta(ViewKind.Settings);
            list.Add((sv, si, Label(sv)));
            return list.ToArray();
        }

        var tabs = BuildTabs();

        float tabW = (rect.Width - 16 * scale) / tabs.Length;
        float startX = rect.Left + 8 * scale;
        for (int i = 0; i < tabs.Length; i++)
        {
            bool active = _state.View == tabs[i].view;
            var tabRect = new SKRect(startX + i * tabW + 3 * scale, y0 + 4 * scale,
                startX + (i + 1) * tabW - 3 * scale, rect.Bottom - 4 * scale);
            if (active)
                using (var bg = new SKPaint { IsAntialias = true, Color = new SKColor(255, 255, 255, (byte)(a * 0.12f)) })
                    c.DrawRoundRect(tabRect, 9 * scale, 9 * scale, bg);

            byte ta = active ? a : (byte)(a * 0.6f);
            // Icon-only nav: cleaner look, and leaves room for more tabs.
            Text(c, tabs[i].icon, tabRect.MidX, tabRect.MidY + 5 * scale, _icons, 16 * scale,
                new SKColor(245, 245, 247, ta), SKTextAlign.Center);

            var view = tabs[i].view;
            AddRegion(regions, tabRect, winX, winY, interactive, () => _actions.SelectView(view));
        }
    }

    // ---- shared drawing utils ----
    private void IconButton(SKCanvas c, string glyph, float cx, float baselineY, float size, SKColor color,
        float scale, List<HitRegion> regions, float winX, float winY, bool interactive, Action onClick)
    {
        Text(c, glyph, cx, baselineY, _icons, size, color, SKTextAlign.Center);
        float hit = 22 * scale;
        var r = new SKRect(cx - hit, baselineY - hit - 4 * scale, cx + hit, baselineY + hit - 4 * scale);
        AddRegion(regions, r, winX, winY, interactive, onClick);
    }

    private static void AddRegion(List<HitRegion> regions, SKRect localRect, float winX, float winY,
        bool interactive, Action onClick)
    {
        if (!interactive) return;
        regions.Add(new HitRegion
        {
            Rect = new SKRect(localRect.Left, localRect.Top, localRect.Right, localRect.Bottom),
            OnClick = onClick,
        });
    }

    private static void DrawRoundImage(SKCanvas c, SKImage img, SKRect dst, float radius, byte alpha)
    {
        c.Save();
        using (var clip = new SKPath())
        {
            clip.AddRoundRect(dst, radius, radius);
            c.ClipPath(clip, antialias: true);
        }
        var src = FitCover(img.Width, img.Height, dst);
        using var paint = new SKPaint
        {
            IsAntialias = true,
            FilterQuality = SKFilterQuality.Medium,
            Color = new SKColor(255, 255, 255, alpha),
        };
        c.DrawImage(img, src, paint);
        c.Restore();
    }

    private static SKRect FitCover(float imgW, float imgH, SKRect dst)
    {
        float scale = Math.Max(dst.Width / imgW, dst.Height / imgH);
        float w = imgW * scale, h = imgH * scale;
        float x = dst.MidX - w / 2f, y = dst.MidY - h / 2f;
        return new SKRect(x, y, x + w, y + h);
    }

    private string Truncate(string s, SKTypeface tf, float size, float maxWidth)
    {
        if (Measure(s, tf, size) <= maxWidth) return s;
        const string ell = "…";
        var sb = s;
        while (sb.Length > 1 && Measure(sb + ell, tf, size) > maxWidth)
            sb = sb[..^1];
        return sb + ell;
    }
}
