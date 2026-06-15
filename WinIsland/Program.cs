using System.Diagnostics;
using System.Runtime.InteropServices;
using SkiaSharp;
using WinIsland.Services;
using static WinIsland.Native;

namespace WinIsland;

internal static class Program
{
    [STAThread]
    private static void Main() => new IslandApp().Run();
}

internal sealed class IslandApp : IIslandActions
{
    // Logical layout
    private const float WinW = 520, WinH = 320;
    private const float TopMargin = 14;
    private const float CompactH = 42, AlertW = 300, AlertH = 58;

    private IntPtr _hwnd, _memDC, _dib, _bits;
    private SKSurface? _surface;
    private int _winX, _winY, _pw, _ph;
    private float _scale = 1f;

    private readonly AppState _state = new();
    private Renderer _renderer = null!;
    private MediaService _media = null!;
    private PomodoroTimer _timer = null!;
    private NotificationService _notifications = null!;
    private CameraService _camera = null!;

    private readonly Spring _sw = new(220, 24);
    private readonly Spring _sh = new(220, 24);
    private readonly Spring _sExp = new(200, 26);

    private readonly List<HitRegion> _regions = new();
    private HitRegion? _drag;
    private volatile bool _needsRender = true;
    private float _pillL, _pillT, _pillR, _pillB;
    private WndProc _wndProcDelegate = null!;

    // Low-level keyboard hook, installed only while the inline task composer is
    // open so we can capture typing without the (NOACTIVATE) island taking focus.
    private IntPtr _kbHook;
    private HookProc? _kbProc;

    // System tray icon (right-click to quit). Kept as a field so we can remove
    // it on shutdown.
    private NOTIFYICONDATA _trayIcon;
    private bool _trayAdded;
    private IntPtr _appIcon;

    public void Run()
    {
        SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2);

        _state.Settings = AppSettings.Load();
        Loc.Lang = _state.Settings.Language;

        IntPtr hInstance = GetModuleHandle(null);
        _appIcon = LoadAppIcon();
        _wndProcDelegate = WindowProc;
        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = hInstance,
            hCursor = LoadCursor(IntPtr.Zero, IDC_ARROW),
            hIcon = _appIcon,
            hIconSm = _appIcon,
            lpszClassName = "WinIslandClass",
        };
        RegisterClassEx(ref wc);

        _hwnd = CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE,
            "WinIslandClass", "Dynamic Island", WS_POPUP,
            0, 0, 10, 10, IntPtr.Zero, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (_hwnd == IntPtr.Zero) return;

        Action notify = () =>
        {
            _needsRender = true;
            PostMessage(_hwnd, WM_APP_UPDATE, IntPtr.Zero, IntPtr.Zero);
        };

        _renderer = new Renderer(_state, this);
        _media = new MediaService(_state, notify);
        _timer = new PomodoroTimer(_state, notify);
        _notifications = new NotificationService(_state, notify);
        _camera = new CameraService(_state, notify);

        BuildSurface();
        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);

        AddTrayIcon();

        _media.Start();
        _notifications.Start();

        RunLoop();

        RemoveTrayIcon();
    }

    private void AddTrayIcon()
    {
        _trayIcon = new NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _hwnd,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = _appIcon != IntPtr.Zero ? _appIcon : LoadIcon(IntPtr.Zero, IDI_APPLICATION),
            szTip = "WinIsland",
        };
        _trayAdded = Shell_NotifyIcon(NIM_ADD, ref _trayIcon);
    }

    /// <summary>Loads logo.ico shipped next to the exe; null icon falls back to the system one.</summary>
    private static IntPtr LoadAppIcon()
    {
        try
        {
            string path = Path.Combine(AppContext.BaseDirectory, "logo.ico");
            if (File.Exists(path))
                return LoadImage(IntPtr.Zero, path, IMAGE_ICON, 0, 0,
                    LR_LOADFROMFILE | LR_DEFAULTSIZE);
        }
        catch { /* fall back below */ }
        return IntPtr.Zero;
    }

    private void RemoveTrayIcon()
    {
        if (!_trayAdded) return;
        Shell_NotifyIcon(NIM_DELETE, ref _trayIcon);
        _trayAdded = false;
    }

    private void ShowTrayMenu()
    {
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return;
        try
        {
            AppendMenu(menu, MF_STRING, (UIntPtr)ID_TRAY_EXIT, Loc.T("tray.exit"));

            // The menu needs a foreground window to dismiss correctly when the
            // user clicks elsewhere; bring ours forward, then nudge it after.
            SetForegroundWindow(_hwnd);
            GetCursorPos(out POINT pt);
            uint cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                pt.X, pt.Y, 0, _hwnd, IntPtr.Zero);
            PostMessage(_hwnd, WM_NULL, IntPtr.Zero, IntPtr.Zero);

            if (cmd == ID_TRAY_EXIT) DestroyWindow(_hwnd);
        }
        finally { DestroyMenu(menu); }
    }

    private void BuildSurface()
    {
        uint dpi = GetDpiForWindow(_hwnd);
        _scale = dpi == 0 ? 1f : dpi / 96f;
        _pw = (int)(WinW * _scale);
        _ph = (int)(WinH * _scale);

        int screenW = GetSystemMetrics(SM_CXSCREEN);
        _winX = ComputeWinX(screenW);
        _winY = (int)(4 * _scale);

        _surface?.Dispose();
        if (_memDC != IntPtr.Zero) { DeleteDC(_memDC); _memDC = IntPtr.Zero; }
        if (_dib != IntPtr.Zero) { DeleteObject(_dib); _dib = IntPtr.Zero; }

        IntPtr screenDC = GetDC(IntPtr.Zero);
        _memDC = CreateCompatibleDC(screenDC);
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = _pw,
            biHeight = -_ph,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = 0,
        };
        _dib = CreateDIBSection(screenDC, ref header, 0, out _bits, IntPtr.Zero, 0);
        SelectObject(_memDC, _dib);
        ReleaseDC(IntPtr.Zero, screenDC);

        _surface = SKSurface.Create(new SKImageInfo(_pw, _ph, SKColorType.Bgra8888, SKAlphaType.Premul), _bits, _pw * 4);

        // Initialise springs to the compact size.
        var (cw, ch) = TargetSize();
        _sw.Value = _sw.Target = cw;
        _sh.Value = _sh.Target = ch;
    }

    private (float w, float h) TargetSize()
    {
        if (_state.Hovered || _state.Composer.Active)
        {
            if (_state.Composer.Active) return (360 * _scale, 300 * _scale);
            return _state.View switch
            {
                ViewKind.Timer => (330 * _scale, 190 * _scale),
                ViewKind.Camera => (360 * _scale, 236 * _scale),
                ViewKind.Music => (360 * _scale, 172 * _scale),
                ViewKind.Tasks => (340 * _scale, 250 * _scale),
                ViewKind.Settings => (360 * _scale, 312 * _scale),
                _ => (360 * _scale, 150 * _scale),
            };
        }
        if (_state.GetActiveAlert() != null) return (AlertW * _scale, AlertH * _scale);

        bool hasMusic = _state.Media.HasSession && !string.IsNullOrEmpty(_state.Media.Title);
        float w = (_state.TimerRunning, hasMusic) switch
        {
            (true, true) => 265,   // music + running timer side by side
            (true, false) => 150,  // timer only
            (false, true) => 245,  // music only
            _ => 130,              // idle clock
        };
        return (w * _scale, CompactH * _scale);
    }

    private void RunLoop()
    {
        var sw = Stopwatch.StartNew();
        double last = sw.Elapsed.TotalSeconds;

        while (true)
        {
            while (PeekMessage(out MSG msg, IntPtr.Zero, 0, 0, PM_REMOVE))
            {
                if (msg.message == WM_QUIT) return;
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            double now = sw.Elapsed.TotalSeconds;
            double dt = now - last;
            last = now;
            if (dt > 0.05) dt = 0.05;

            var (tw, th) = TargetSize();
            _sw.Target = tw; _sh.Target = th;
            bool expanded = _state.Hovered || _state.Composer.Active;
            _sExp.Target = expanded ? 1 : 0;

            // Install/remove the keyboard hook to match the composer's lifetime.
            // Doing it here (on the loop thread) keeps it out of the hook callback.
            if (_state.Composer.Active && _kbHook == IntPtr.Zero) InstallKeyboardHook();
            else if (!_state.Composer.Active && _kbHook != IntPtr.Zero) RemoveKeyboardHook();

            bool anim = false;
            anim |= _sw.Step(dt);
            anim |= _sh.Step(dt);
            anim |= _sExp.Step(dt);

            bool alertActive = _state.GetActiveAlert() != null;
            bool eqLive = _state.Media.IsPlaying && !_state.Hovered;
            bool cameraLive = _state.Hovered && _state.View == ViewKind.Camera;
            bool live = anim || alertActive || expanded || eqLive || cameraLive;

            if (anim || _needsRender || live)
            {
                _needsRender = false;
                Render(now);
            }

            if (live) Thread.Sleep(16);
            else MsgWaitForMultipleObjects(0, IntPtr.Zero, false, INFINITE, QS_ALLINPUT);
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
            {
                float lx = LoWord(lParam) - _winX;
                float ly = HiWord(lParam) - _winY;
                bool inside = lx >= _pillL && lx <= _pillR && ly >= _pillT && ly <= _pillB;
                return inside ? new IntPtr(HTCLIENT) : new IntPtr(HTTRANSPARENT);
            }

            case WM_MOUSEMOVE:
                // While dragging the seek bar, keep the bar following the cursor
                // (the window has mouse capture so moves arrive even outside it).
                if (_drag != null)
                {
                    _drag.OnDrag?.Invoke(Frac(_drag.Rect, LoWord(lParam)));
                    _needsRender = true;
                    return IntPtr.Zero;
                }
                if (!_state.Hovered)
                {
                    _state.Hovered = true;
                    _needsRender = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = hWnd,
                    };
                    TrackMouseEvent(ref tme);
                }
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                // Don't collapse the island mid-drag.
                if (_drag != null) return IntPtr.Zero;
                _state.Hovered = false;
                _needsRender = true;
                UpdateCamera();
                return IntPtr.Zero;

            case WM_LBUTTONDOWN:
            {
                float lx = LoWord(lParam);
                float ly = HiWord(lParam);
                // Iterate a copy to avoid races with the render thread rebuild.
                HitRegion[] snapshot;
                lock (_regions) snapshot = _regions.ToArray();
                foreach (var r in snapshot)
                {
                    if (lx >= r.Rect.Left && lx <= r.Rect.Right && ly >= r.Rect.Top && ly <= r.Rect.Bottom)
                    {
                        if (r.OnDrag != null || r.OnDragCommit != null)
                        {
                            // Draggable region (seek bar): capture the mouse so we
                            // keep receiving moves while the button is held.
                            _drag = r;
                            SetCapture(hWnd);
                            r.OnDrag?.Invoke(Frac(r.Rect, lx));
                        }
                        else
                        {
                            r.OnClick();
                        }
                        _needsRender = true;
                        break;
                    }
                }
                return IntPtr.Zero;
            }

            case WM_LBUTTONUP:
            {
                if (_drag != null)
                {
                    var d = _drag;
                    _drag = null;
                    ReleaseCapture();
                    d.OnDragCommit?.Invoke(Frac(d.Rect, LoWord(lParam)));
                    _needsRender = true;
                }
                return IntPtr.Zero;
            }

            case WM_DPICHANGED:
            case WM_DISPLAYCHANGE:
                BuildSurface();
                _needsRender = true;
                return IntPtr.Zero;

            case WM_APP_UPDATE:
                _needsRender = true;
                return IntPtr.Zero;

            case WM_TRAYICON:
                // lParam's low word carries the mouse event over the tray icon.
                uint ev = (uint)LoWord(lParam);
                if (ev == WM_RBUTTONUP || ev == WM_CONTEXTMENU || ev == WM_LBUTTONUP)
                    ShowTrayMenu();
                return IntPtr.Zero;

            case WM_DESTROY:
                RemoveTrayIcon();
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void Render(double timeSec)
    {
        if (_surface == null) return;

        float w = (float)_sw.Value;
        float h = (float)_sh.Value;
        float cx = _pw / 2f;
        float top = TopMargin * _scale;
        var rect = new SKRect(cx - w / 2f, top, cx + w / 2f, top + h);
        float radius = Math.Min(h / 2f, 30 * _scale);

        _pillL = rect.Left; _pillT = rect.Top; _pillR = rect.Right; _pillB = rect.Bottom;

        lock (_regions) _regions.Clear();
        var newRegions = new List<HitRegion>();
        _renderer.Render(_surface.Canvas, rect, radius, _scale,
            (float)_sExp.Value, newRegions, _winX, _winY, timeSec);
        lock (_regions) { _regions.Clear(); _regions.AddRange(newRegions); }

        _surface.Canvas.Flush();
        Present();
    }

    private void Present()
    {
        var ptDst = new POINT { X = _winX, Y = _winY };
        var size = new SIZE { cx = _pw, cy = _ph };
        var ptSrc = new POINT { X = 0, Y = 0 };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA,
        };
        UpdateLayeredWindow(_hwnd, IntPtr.Zero, ref ptDst, ref size, _memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);
    }

    // ---- IIslandActions ----
    public void MediaControl(string action) { _media.Control(action); _needsRender = true; }

    public void MediaSeekPreview(float fraction)
    {
        _state.MediaSeeking = true;
        _state.MediaSeekFraction = fraction;
        _needsRender = true;
    }

    public void MediaSeek(float fraction)
    {
        _state.MediaSeekFraction = fraction;
        _state.MediaSeeking = false;
        // Keep the bar pinned at the new spot briefly so it doesn't snap back to
        // the stale position before GSMTC reports the updated one on its next poll.
        _state.MediaSeekStickyTicks = DateTime.UtcNow.Ticks + TimeSpan.FromMilliseconds(1200).Ticks;
        _media.Seek(fraction);
        _needsRender = true;
    }

    /// <summary>Cursor X (window-local) as a clamped 0..1 fraction across a rect.</summary>
    private static float Frac(SKRect r, float x)
    {
        if (r.Width <= 0) return 0;
        float f = (x - r.Left) / r.Width;
        return f < 0 ? 0 : (f > 1 ? 1 : f);
    }

    public void SelectView(ViewKind view)
    {
        // Leaving the Tasks tab discards an open composer.
        if (_state.Composer.Active && view != ViewKind.Tasks) _state.Composer.Close();
        _state.View = view;
        _needsRender = true;
        UpdateCamera();
    }

    public void TimerToggle() => _timer.Toggle();
    public void TimerReset() => _timer.Reset();
    public void TimerSelectPreset(int index)
    {
        if (index >= 0 && index < PomodoroTimer.Presets.Length)
            _timer.Select(PomodoroTimer.Presets[index]);
    }

    public void SetPosition(IslandSide side)
    {
        _state.Settings.Position = side;
        _state.Settings.Save();
        _winX = ComputeWinX(GetSystemMetrics(SM_CXSCREEN));
        _needsRender = true;
    }

    public void SetLanguage(AppLanguage lang)
    {
        _state.Settings.Language = lang;
        Loc.Lang = lang;
        _state.Settings.Save();
        _needsRender = true;
    }

    public void SetAccent(int argb)
    {
        _state.Settings.AccentArgb = argb;
        _state.Settings.Save();
        _needsRender = true;
    }

    // Persisted custom-color slots for the native picker (reused across opens).
    private readonly int[] _customColors = Enumerable.Repeat(0x00FFFFFF, 16).ToArray();

    public void PickCustomAccent()
    {
        // ARGB (0xAARRGGBB) -> COLORREF (0x00BBGGRR).
        int argb = _state.Settings.AccentArgb;
        int r = (argb >> 16) & 0xFF, g = (argb >> 8) & 0xFF, b = argb & 0xFF;
        int colorRef = r | (g << 8) | (b << 16);

        var handle = GCHandle.Alloc(_customColors, GCHandleType.Pinned);
        try
        {
            var cc = new CHOOSECOLOR
            {
                lStructSize = Marshal.SizeOf<CHOOSECOLOR>(),
                hwndOwner = _hwnd,
                rgbResult = colorRef,
                lpCustColors = handle.AddrOfPinnedObject(),
                Flags = CC_RGBINIT | CC_FULLOPEN | CC_ANYCOLOR,
            };
            if (ChooseColor(ref cc))
            {
                int nr = cc.rgbResult & 0xFF;
                int ng = (cc.rgbResult >> 8) & 0xFF;
                int nb = (cc.rgbResult >> 16) & 0xFF;
                _state.Settings.AccentArgb = unchecked((int)0xFF000000) | (nr << 16) | (ng << 8) | nb;
                _state.Settings.Save();
                _needsRender = true;
            }
        }
        finally { handle.Free(); }
    }

    public void TestNotification()
    {
        // Mirror what NotificationService produces so the preview is faithful.
        // Alerts only show on the collapsed island, so the user sees it once the
        // cursor leaves the (currently expanded) Settings view.
        _state.SetAlert(new AlertInfo
        {
            Icon = "\uE7ED", // bell
            Title = Loc.T("test.title"),
            Subtitle = Loc.T("test.subtitle"),
            Accent = (SKColor)(uint)_state.Settings.AccentArgb,
        }, 5000);
        _needsRender = true;
    }

    public void ToggleTask(int index)
    {
        var t = _state.Settings.Tasks;
        if (index >= 0 && index < t.Count)
        {
            t[index].Done = !t[index].Done;
            _state.Settings.Save();
            _needsRender = true;
        }
    }

    public void RemoveTask(int index)
    {
        var t = _state.Settings.Tasks;
        if (index >= 0 && index < t.Count)
        {
            t.RemoveAt(index);
            _state.Settings.Save();
            _needsRender = true;
        }
    }

    public void AddTask()
    {
        _state.View = ViewKind.Tasks;
        _state.Composer.BeginNew();
        _needsRender = true;
    }

    public void BeginEditTask(int index)
    {
        var t = _state.Settings.Tasks;
        if (index < 0 || index >= t.Count) return;
        _state.View = ViewKind.Tasks;
        _state.Composer.BeginEdit(index, t[index]);
        _needsRender = true;
    }

    public void ComposerCommit()
    {
        var comp = _state.Composer;
        string text = comp.Text.Trim();
        if (text.Length == 0) { comp.Close(); _needsRender = true; return; }
        if (text.Length > 200) text = text[..200];

        bool hasTime = comp.HasDate && comp.HasTime;
        var due = comp.BuildDueAt();
        var tasks = _state.Settings.Tasks;

        if (comp.EditIndex >= 0 && comp.EditIndex < tasks.Count)
        {
            var item = tasks[comp.EditIndex];
            item.Text = text;
            item.DueAt = due;
            item.HasTime = hasTime;
        }
        else
        {
            tasks.Add(new TaskItem { Text = text, DueAt = due, HasTime = hasTime });
        }

        comp.Close();
        _state.Settings.Save();
        _needsRender = true;
    }

    public void ComposerCancel() { _state.Composer.Close(); _needsRender = true; }

    public void ComposerQuickDate(int kind)
    {
        _state.Composer.EnableDate(kind == 1 ? DateTime.Today.AddDays(1) : DateTime.Today);
        _needsRender = true;
    }

    public void ComposerAdjustDate(int days) { _state.Composer.AdjustDate(days); _needsRender = true; }
    public void ComposerClearDate() { _state.Composer.ClearDate(); _needsRender = true; }
    public void ComposerToggleTime() { _state.Composer.ToggleTime(); _needsRender = true; }
    public void ComposerAdjustHour(int delta) { _state.Composer.AdjustHour(delta); _needsRender = true; }
    public void ComposerAdjustMinute(int delta) { _state.Composer.AdjustMinute(delta); _needsRender = true; }

    // ---- inline keyboard capture ----
    private void InstallKeyboardHook()
    {
        if (_kbHook != IntPtr.Zero) return;
        _kbProc = KeyboardHook;
        _kbHook = SetWindowsHookEx(WH_KEYBOARD_LL, _kbProc, GetModuleHandle(null), 0);
    }

    private void RemoveKeyboardHook()
    {
        if (_kbHook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_kbHook);
        _kbHook = IntPtr.Zero;
        _kbProc = null;
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _state.Composer.Active)
        {
            uint msg = (uint)wParam;
            var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            int vk = (int)kb.vkCode;
            bool isModifier = vk is VK_SHIFT or VK_CONTROL or VK_MENU or VK_CAPITAL
                or VK_LWIN or VK_RWIN;

            // Let system shortcuts through (Alt+Tab, Win combos, Alt+F4) so the
            // user is never trapped in the composer.
            bool systemCombo = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0
                || (GetAsyncKeyState(VK_LWIN) & 0x8000) != 0
                || (GetAsyncKeyState(VK_RWIN) & 0x8000) != 0;

            if (!systemCombo)
            {
                if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN)
                {
                    if (!isModifier && HandleComposerKey(kb))
                    {
                        _needsRender = true;
                        PostMessage(_hwnd, WM_APP_UPDATE, IntPtr.Zero, IntPtr.Zero);
                        return (IntPtr)1; // swallow so it doesn't leak to the app behind
                    }
                }
                else if (msg == WM_KEYUP && !isModifier)
                {
                    // Swallow the matching key-up too (modifiers pass through so the OS
                    // keeps tracking Shift/Caps for character translation).
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    /// <summary>Returns true when the key was consumed by the composer.</summary>
    private bool HandleComposerKey(KBDLLHOOKSTRUCT kb)
    {
        var comp = _state.Composer;
        int vk = (int)kb.vkCode;
        bool ctrl = (GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0;

        switch (vk)
        {
            case VK_RETURN: ComposerCommit(); return true;
            case VK_ESCAPE: ComposerCancel(); return true;
            case VK_BACK: comp.Backspace(); return true;
            case VK_DELETE: comp.DeleteForward(); return true;
            case VK_LEFT: comp.MoveCaret(-1); return true;
            case VK_RIGHT: comp.MoveCaret(1); return true;
            case VK_HOME: comp.CaretHome(); return true;
            case VK_END: comp.CaretEnd(); return true;
        }

        if (ctrl)
        {
            if (vk == VK_KEY_V) comp.Insert(GetClipboardText());
            return true; // swallow other Ctrl combos rather than typing letters
        }

        string s = TranslateKey(kb);
        if (!string.IsNullOrEmpty(s)) comp.Insert(s);
        return true; // while composing, every non-modifier key is ours
    }

    /// <summary>Maps a key event to typed text, honouring Shift/Caps and layout.</summary>
    private static string TranslateKey(KBDLLHOOKSTRUCT kb)
    {
        var state = new byte[256];
        GetKeyboardState(state);
        // The LL hook fires before the OS updates its state, so refresh modifiers.
        state[VK_SHIFT] = (byte)((GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0 ? 0x80 : 0);
        state[VK_CONTROL] = (byte)((GetAsyncKeyState(VK_CONTROL) & 0x8000) != 0 ? 0x80 : 0);
        state[VK_MENU] = (byte)((GetAsyncKeyState(VK_MENU) & 0x8000) != 0 ? 0x80 : 0);
        state[VK_CAPITAL] = (byte)((GetKeyState(VK_CAPITAL) & 1) != 0 ? 1 : 0);

        var sb = new System.Text.StringBuilder(8);
        int rc = ToUnicode(kb.vkCode, kb.scanCode, state, sb, sb.Capacity, 0);
        if (rc <= 0) return "";

        var outSb = new System.Text.StringBuilder(rc);
        foreach (char ch in sb.ToString())
            if (!char.IsControl(ch)) outSb.Append(ch);
        return outSb.ToString();
    }

    private string GetClipboardText()
    {
        if (!IsClipboardFormatAvailable(CF_UNICODETEXT)) return "";
        if (!OpenClipboard(_hwnd)) return "";
        try
        {
            IntPtr h = GetClipboardData(CF_UNICODETEXT);
            if (h == IntPtr.Zero) return "";
            IntPtr ptr = GlobalLock(h);
            if (ptr == IntPtr.Zero) return "";
            try { return Marshal.PtrToStringUni(ptr) ?? ""; }
            finally { GlobalUnlock(h); }
        }
        finally { CloseClipboard(); }
    }

    public void ToggleTab(ViewKind view)
    {
        var enabled = _state.Settings.EnabledTabs;
        if (enabled.Contains(view)) enabled.Remove(view);
        else
        {
            // Keep the configured display order rather than append order.
            enabled.Add(view);
            enabled.Sort((x, y) =>
                Array.IndexOf(AppSettings.ToggleableTabs, x)
                    .CompareTo(Array.IndexOf(AppSettings.ToggleableTabs, y)));
        }

        // If the current view just got hidden, fall back to the first visible
        // tab (or Settings, which is always present).
        if (_state.View != ViewKind.Settings && !enabled.Contains(_state.View))
        {
            _state.View = enabled.Count > 0 ? enabled[0] : ViewKind.Settings;
            UpdateCamera();
        }

        _state.Settings.Save();
        _needsRender = true;
    }

    /// <summary>Horizontal window origin for the configured anchor side.</summary>
    private int ComputeWinX(int screenW)
    {
        int margin = (int)(8 * _scale);
        return _state.Settings.Position switch
        {
            IslandSide.Left => margin,
            IslandSide.Right => screenW - _pw - margin,
            _ => (screenW - _pw) / 2,
        };
    }

    private void UpdateCamera()
    {
        bool active = _state.Hovered && _state.View == ViewKind.Camera;
        _camera.SetActive(active);
    }
}
