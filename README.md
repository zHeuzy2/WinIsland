# WinIsland

A **Dynamic Island** for Windows. A floating pill at the top of your screen that
shows what's playing, a Pomodoro timer, a live camera preview, your tasks, and
mirrors system notifications — all with fluid animations and full High-DPI
support.

Written in pure C# on top of **SkiaSharp** and **Win32** (a *layered* window with
no WinForms/WPF), with GPU rendering and a focus on being lightweight.

![WinIsland demo](WinIsland/demo/C2kZ4bM65H.gif)

## ✨ Features

- 🎵 **Music** — controls the active system media session (Spotify, browsers,
  etc.) via GSMTC: play/pause, previous/next track, a scrubbable progress bar
  with seeking, and album art.
- ⏱️ **Pomodoro timer** — Focus, Break and Long Break presets.
- 📷 **Camera** — a live webcam preview right inside the island.
- ✅ **Tasks** — a checklist with due date and time, editable inline.
- 🔔 **Notifications** — mirrors Windows toast notifications on the island.
- 🎨 **Customization** — screen position (left/center/right), accent color,
  language (Portuguese/English), and which tabs are visible.
- 🖱️ **Tray icon** — a system tray icon with a right-click menu to quit the app.

## 🚀 Getting started

### Prerequisites

- Windows 10 version 19041 (2004) or later
- [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)

### Build and run

```bash
cd WinIsland
dotnet run -c Release
```

To produce an executable:

```bash
dotnet build -c Release
```

The binary lands in `WinIsland/bin/x64/Release/net8.0-windows10.0.19041.0/win-x64/`.

To quit the app, right-click the WinIsland icon in the system tray and choose
**Exit**.

## 🧩 Project structure

| File / Folder | Responsibility |
| --- | --- |
| `Program.cs` | Main loop, layered Win32 window, mouse/keyboard input |
| `Renderer.cs` | All UI drawing with SkiaSharp |
| `Model.cs` | Shared application state (`AppState`) |
| `Settings.cs` | Settings persistence and localization (`Loc`) |
| `Composer.cs` | Inline task editor |
| `Spring.cs` | Damped-spring integrator for animations |
| `Native.cs` | Win32 P/Invoke declarations |
| `Services/` | Media, camera, notifications and timer |

Settings are stored in `%AppData%\WinIsland\settings.json`.

## ⚙️ Permissions

- **Camera**: requires camera access permission in Windows settings.
- **Notifications**: requires *UserNotificationListener* permission. Without it,
  music and timer alerts still work normally.

## 🛠️ Built with

- C# / .NET 8
- [SkiaSharp](https://github.com/mono/SkiaSharp) for rendering
- Win32 API (UpdateLayeredWindow, keyboard hooks, GDI, tray icon)
- Windows Runtime APIs (GSMTC, MediaCapture, UserNotificationListener)

## 🤝 Contributing

Contributions are welcome! Please open an issue to discuss larger changes before
submitting a pull request.

## 📄 License

Distributed under the MIT License. See [`LICENSE`](LICENSE) for details.
