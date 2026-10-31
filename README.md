# DynaIsland

An Apple-style **Dynamic Island** for Windows 10/11, built with WPF on .NET 10.
Everything runs locally. The app makes no network calls, collects no telemetry and uses no cloud services.

## What it shows

| Activity | Behaviour |
|---|---|
| **Now playing** | Album art and animated bars tinted from the cover art. Hover to expand to title, artist, source app, a seekable timeline and prev / play-pause / next. A new track briefly "peeks" wider. Works with anything that uses the Windows media overlay (Spotify, browsers/YouTube, Media Player, Apple Music, …). |
| **Volume & mute** | Pops a level bar when the system volume changes, plus "Muted" for speaker mute and "Microphone muted / on" for the default mic. This works for keyboard keys, the Windows slider and apps. Scroll over the island to change the volume. |
| **Brightness** | Level bar when the built-in display's brightness changes (keys, slider, or adaptive brightness). Event-driven, so it costs nothing while idle. |
| **Battery** | Charger plugged in/unplugged, plus a low-battery warning (laptops only). |
| **Caps Lock** | On/off indicator. |
| **Clipboard** | "Copied" with a short preview. Content that password managers mark as excluded from clipboard monitoring is never previewed. |
| **Clock** | Hovering the idle island shows the time, date and battery. |

The island hides while a full-screen app or game is running. It never steals focus and doesn't appear in Alt+Tab or the taskbar.

**Tray icon:** DynaIsland adds an icon to the notification area. Left-click it to show or hide the island. Right-click it to toggle each activity, "Start with Windows", "Fade out when idle", or to quit. Windows 11 puts new icons in the hidden-icons overflow (**^**); drag it onto the taskbar, or turn it on under *Settings → Personalization → Taskbar → Other system tray icons*. Right-clicking the island opens the same options.

**Starts with Windows:** the first run turns auto-start on (HKCU Run key, no admin needed). It points at whichever copy of `DynaIsland.exe` you last ran. You can switch it off from either menu or from Task Manager → Startup apps.

Settings: `%APPDATA%\DynaIsland\settings.json` · Log: `%LOCALAPPDATA%\DynaIsland\log.txt`

## Build & run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
./build.ps1                           # single self-contained .\publish\DynaIsland.exe
./build.ps1 -Install                  # ...then install to %LOCALAPPDATA%\Programs\DynaIsland and start it
```

Use `-Install` for everyday use. Auto-start then points at a stable copy that later builds don't wipe.
`-Install` first runs the new exe with `--self-test`. If Windows blocks the build, the installed copy is left running and untouched.

> **Smart App Control:** run the single-file `publish\DynaIsland.exe`, not `dotnet run` or the `bin\` output.
> A normal build loads a loose, unsigned `DynaIsland.dll`, which Smart App Control blocks. Smart App Control
> also judges each unsigned exe build on its own (cloud reputation), so a given build may be allowed or
> blocked ("An Application Control policy has blocked this file"). If a new build is blocked, Windows has
> rejected that exact file. Keep the last build that ran.

## Project layout

```
App.xaml(.cs)          single-instance startup, global styles (buttons, dark context menu)
MainWindow.xaml(.cs)   the island: views, state machine, spring animation, system hooks
Spring.cs              damped spring physics for the morphing shape
ColorUtil.cs           accent colour extraction from album art
Settings.cs            JSON settings + "Start with Windows" (HKCU Run key)
Native.cs              Win32 interop (window styles, clipboard listener, full-screen detection)
Services/MediaService  Windows media sessions (GlobalSystemMediaTransportControls)
Services/AudioVolume   Core Audio endpoint volume (COM)
Services/PowerService  battery / charger events (Windows.System.Power)
Controls/Visualizer    animated equaliser bars
```
