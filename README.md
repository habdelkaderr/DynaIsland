# DynaIsland

An Apple-style **Dynamic Island** for Windows 10/11, built with WPF on .NET 10.
Everything runs locally. The app makes no network calls, collects no telemetry and uses no cloud services.

## What it shows

| Activity | Behaviour |
|---|---|
| **Now playing** | Album art and animated bars tinted from the cover art. Hover to expand to title, artist, source app, a seekable timeline and prev / play-pause / next. A new track briefly "peeks" wider. Works with anything that uses the Windows media overlay (Spotify, browsers/YouTube, Media Player, Apple Music, …). |
| **Volume** | Pops a level bar when the system volume or mute changes. Scroll over the island to change the volume. |
| **Battery** | Charger plugged in/unplugged, plus a low-battery warning (laptops only). |
| **Caps Lock** | On/off indicator. |
| **Clipboard** | "Copied" with a short preview. Content that password managers mark as excluded from clipboard monitoring is never previewed. |
| **Clock** | Hovering the idle island shows the time, date and battery. |

The island hides while a full-screen app or game is running. It never steals focus and doesn't appear in Alt+Tab or the taskbar.

**Right-click the island** to toggle each activity, set "Start with Windows", turn on "Fade out when idle", or quit.

Settings: `%APPDATA%\DynaIsland\settings.json` · Log: `%LOCALAPPDATA%\DynaIsland\log.txt`

## Build & run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
dotnet run -c Release                 # run from source
./build.ps1                           # publish to .\publish\DynaIsland.exe
```

> **Smart App Control:** with Smart App Control enforced, Windows blocks locally built, unsigned binaries ("Part of this app has been blocked"). Turn Smart App Control off, or sign the build with a certificate from a trusted CA.

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
