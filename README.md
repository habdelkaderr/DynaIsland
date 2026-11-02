# DynaIsland

An Apple-style **Dynamic Island** for Windows 10/11, built with WPF on .NET 10.
Everything runs locally. The app makes no network calls, collects no telemetry and uses no cloud services.

## What it shows

| Activity | Behaviour |
|---|---|
| **Now playing** | Album art and animated bars tinted from the cover art. Hover to expand to title, artist, source app, a seekable timeline and prev / play-pause / next. **Swipe left/right** on the island to skip tracks. A new track briefly "peeks" wider. Works with anything that uses the Windows media overlay (Spotify, browsers/YouTube, Media Player, Apple Music, …). |
| **Timer, focus, stopwatch** | Start from the menu (1 min to 1 hour, 25/50-min focus sessions, stopwatch). Shows the time in the compact pill. Hover for a progress ring, +1 minute, pause and stop. With music playing, the timer moves to a small bubble beside the island (like the iPhone's split island); click it for the controls. Chimes when done. |
| **Notifications** | Mirrors new Windows notifications (Teams, WhatsApp, mail, …) with the app's real name and icon. Click to open the app. Read-only from the local notification database through Windows' own signed SQLite (`winsqlite3.dll`). Skipped during Do Not Disturb. |
| **Calendar** | "In 10 min" and "Starting now" heads-ups from **classic Outlook**'s calendar while Outlook is running (local COM, never launches Outlook). The new Outlook has no local API, but its reminders still arrive as mirrored notifications. |
| **Volume and mute** | Level bar for system volume, plus "Muted" for the speakers and "Microphone muted / on" for the default mic. Scroll over the island to change the volume. |
| **Brightness** | Level bar when the built-in display's brightness changes. Event-driven. |
| **Mic and camera in use** | Alert when an app starts using the mic or camera, plus an orange (mic) / green (camera) dot beside the island while it's in use. Uses the same registry data as Windows' privacy indicator; event-driven. |
| **Bluetooth** | Connected / disconnected for paired devices, with battery level where the device reports it. |
| **Network and VPN** | Connected to Wi-Fi/Ethernet (with network name), limited connection, offline, VPN on/off. |
| **Screenshots** | Thumbnail when a screenshot is saved to *Pictures\Screenshots* (click to open), or "Image copied" for images copied to the clipboard. |
| **Downloads** | "Downloading" and "Download complete" for browser downloads (Chrome/Edge `.crdownload`, Firefox/Zen `.part`). Click to show the file in Explorer. Explorer file copies can't be tracked (Windows exposes no API for them). |
| **Keyboard layout** | Shows the new layout (e.g. العربية / English) when you switch. |
| **Do Not Disturb** | On/off. |
| **Battery, Caps Lock, clipboard** | Charger plugged/unplugged and low battery; Caps Lock on/off; "Copied" with a short preview (password-manager content is never previewed). |
| **Clock** | Hovering the idle island shows the time, date and battery. |

The island hides while a full-screen app or game is running. It never steals focus and doesn't appear in Alt+Tab or the taskbar.

## Controls and settings

**Tray icon:** left-click shows or hides the island; right-click opens the menu. Right-clicking the island opens the same menu. Windows 11 puts new icons in the hidden-icons overflow (**^**); drag it onto the taskbar to keep it visible.

- **Timer:** start, pause/resume, +1 minute, stop.
- **Show on island:** switch each activity on or off.
- **Appearance:** theme (Black, Graphite, Translucent), size (Small, Default, Large), screen (main display, follow the active window, or a specific display), expand on hover or on click, fade out when idle, hide in full-screen apps, **low-memory mode**.
- **Start with Windows:** on by default after the first run (HKCU Run key, no admin needed).

**Low-memory mode** switches to software rendering: about **29 MB** instead of about 115 MB, using roughly twice the CPU while animating. That's still about 2–3% of one 20-thread CPU during non-stop animation, and nothing when idle.

Settings: `%APPDATA%\DynaIsland\settings.json` · Log: `%LOCALAPPDATA%\DynaIsland\log.txt`

## Download

Get the portable zip from [Releases](https://github.com/habdelkaderr/DynaIsland/releases): extract it and run `DynaIsland.exe`. No installation or .NET runtime is needed. Each release lists SHA-256 fingerprints for the zip and the exe.

## Build & run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```powershell
./build.ps1                           # single self-contained .\publish\DynaIsland.exe
./build.ps1 -Install                  # ...then install to %LOCALAPPDATA%\Programs\DynaIsland and start it
```

Use `-Install` for everyday use. Auto-start then points at a stable copy that later builds don't wipe.
`-Install` first runs the new exe with `--self-test`. If Windows blocks the build, the installed copy is left running and untouched.

**Making a release** (built and packaged on this PC; nothing is built in the cloud):

```powershell
./build.ps1                                         # build, then test the exe
git tag -a v1.2.3 -m "DynaIsland 1.2.3"; git push origin v1.2.3   # version must match DynaIsland.csproj
./release.ps1 -Publish                              # zip + SHA-256, create the GitHub release, upload
```

`release.ps1` ships the exe you already built and tested (it never rebuilds), and uses the GitHub login git already has saved. Optional highlights go in `docs/release-notes/v<version>.md`.

> **Smart App Control:** run the single-file `publish\DynaIsland.exe`, not `dotnet run` or the `bin\` output.
> A normal build loads a loose, unsigned `DynaIsland.dll`, which Smart App Control blocks. Smart App Control
> also judges each unsigned exe build on its own (cloud reputation), so a given build may be allowed or
> blocked ("An Application Control policy has blocked this file"). If a new build is blocked, Windows has
> rejected that exact file. Keep the last build that ran.

## Project layout

```
App.xaml(.cs)                 single-instance startup, low-memory mode, restart, shared button style
MainWindow.xaml(.cs)          views, state machine, spring animation, transient cards, gestures
MainWindow.Media.cs           now playing
MainWindow.Timer.cs           timer / focus / stopwatch, progress rings
MainWindow.Activities.cs      system activity -> alerts and cards
MainWindow.Menu.cs            shared menu, appearance, screens, visibility, auto-start
TrayIcon.cs                   notification-area icon, dark menu renderer
Spring.cs / ColorUtil.cs      spring physics; album-art accent colour
Settings.cs / Native.cs       JSON settings; Win32 interop
Services/MediaService         Windows media sessions (GSMTC)
Services/AudioVolume          Core Audio volume and mute (speakers and mic)
Services/PowerService         battery / charger events
Services/TimerService         timer state
Services/NotificationService  toast mirroring (read-only wpndatabase via winsqlite3)
Services/ShellApps            app name and icon from an AppUserModelID
Services/CalendarService      classic Outlook calendar (COM, read-only)
Services/BluetoothService     paired device connect / disconnect / battery
Services/NetworkService       Wi-Fi / Ethernet / offline / VPN
Services/PrivacyService       mic / camera in use (registry change notifications)
Services/FolderWatchService   downloads and screenshots folders
Controls/Visualizer           animated equaliser bars
```
