using System.Runtime.InteropServices;
using System.Windows;
using DynaIsland.Services;
using DynaIsland.Ui;

namespace DynaIsland;

// The desktop modules (menu bar, dock, Launchpad, Control Center, Settings) around the island.
// Each module can be switched on/off live; the island owns the shared services.
public partial class MainWindow
{
    private WinEvents? _winEvents;
    private RadioService? _radios;
    private DesktopHost? _host;
    private MenuBarWindow? _menuBar;
    private DockWindow? _dockWindow;
    private LaunchpadWindow? _launchpad;
    private ControlCenterWindow? _controlCenter;
    private SettingsWindow? _settingsWindow;
    private Hotkeys? _hotkeys;
    private double _menuBarInset;
    private (bool Island, bool Dock, bool Bar, bool Pad, bool Cc, bool Side)? _beforeAllOff;

    private void InitDesktop()
    {
        // Lightweight by design: the window hooks and radio watcher are cheap objects that only start
        // working when a module that needs them is on (see ApplyModules); modules that are off don't exist.
        _winEvents = new WinEvents(start: false);
        _radios = new RadioService(Dispatcher);
        _host = new DesktopHost
        {
            Settings = _settings,
            Media = _media,
            Network = _network,
            Power = _power,
            Volume = _volume,
            Radios = _radios,
            Events = _winEvents,
            Monitor = CurrentMonitor,
            OpenLaunchpad = OpenLaunchpad,
            ToggleControlCenter = ToggleControlCenter,
            OpenSettings = () => OpenSettings("General"),
            OpenSettingsPage = OpenSettings,
            ApplyModules = ApplyModules,
            Quit = Close,
        };
        _winEvents.WindowsChanged += () => _dockWindow?.Refresh();
        _hotkeys = new Hotkeys(_hwnd);
        ApplyModules();
    }

    /// <summary>Applies every module's on/off state and settings. Safe to call repeatedly.</summary>
    private void ApplyModules()
    {
        if (_host is null) return;
        Motion.Speed = Math.Clamp(_settings.AnimationSpeed, 0.5, 2);
        Theme.Configure(_settings.UiTheme, _settings.GlassOpacity);
        var monitor = CurrentMonitor();
        bool suppressed = _fullscreenHidden;

        // Shared watchers run only while a module needs them.
        bool needWindows = _settings.MenuBarEnabled || _settings.DockEnabled;
        _winEvents!.Enabled = needWindows;
        if (_settings.MenuBarEnabled || _settings.ControlCenterEnabled) _ = _radios!.StartAsync();
        else _radios!.Stop();

        // A module that's switched off is destroyed (not just hidden), so it costs nothing.
        // Full-screen apps only hide them for a moment.
        bool bar = _settings.MenuBarEnabled && !suppressed;
        if (_settings.MenuBarEnabled) (_menuBar ??= new MenuBarWindow(_host)).Apply(monitor, bar);
        else if (_menuBar is not null) { _menuBar.Close(); _menuBar = null; }

        bool dock = _settings.DockEnabled && !suppressed;
        if (_settings.DockEnabled) (_dockWindow ??= new DockWindow(_settings, OpenLaunchpad)).Apply(monitor, dock);
        else if (_dockWindow is not null) { _dockWindow.Close(); _dockWindow = null; }
        ApplyTaskbar();

        if (!_settings.LaunchpadEnabled) _launchpad?.Close();
        if (!_settings.ControlCenterEnabled) _controlCenter?.Dismiss();
        if (!_settings.DockEnabled && !_settings.LaunchpadEnabled) AppCatalog.Unload();

        // The floating island moves below the menu bar; as a notch it sits in it.
        _menuBarInset = bar ? MenuBarWindow.BarHeight : 0;
        ApplyAppearance();
        ApplyVisibility();
        KeepOnTop();
        RegisterHotkeys();
        LoadAiApps(); // island shortcuts may have been edited in Settings
        _lastVolume = -1;
        UpdateState();
    }

    // ---- Windows taskbar ---------------------------------------------------------------------

    /// <summary>
    /// While the dock replaces the taskbar, the taskbar is set to auto-hide. The original state is saved
    /// in settings first, so it's restored even after a crash.
    /// </summary>
    private void ApplyTaskbar()
    {
        bool replace = _settings.DockEnabled && _settings.DockReplacesTaskbar;
        if (replace)
        {
            if (_settings.TaskbarAutoHideBefore is null)
            {
                _settings.TaskbarAutoHideBefore = AppBar.TaskbarAutoHide;
                _settings.Save();
            }
            if (!AppBar.TaskbarAutoHide) AppBar.TaskbarAutoHide = true;
        }
        else
        {
            RestoreTaskbar();
        }
    }

    private void RestoreTaskbar()
    {
        if (_settings.TaskbarAutoHideBefore is not bool before) return;
        AppBar.TaskbarAutoHide = before;
        _settings.TaskbarAutoHideBefore = null;
        _settings.Save();
    }

    // ---- Launchpad, Control Center, Settings -------------------------------------------------

    private void OpenLaunchpad()
    {
        if (_host is null || !_settings.LaunchpadEnabled) return;
        _controlCenter?.Dismiss();
        if (_launchpad is not null)
        {
            _launchpad.Close(); // second press closes it
            return;
        }
        // Built on open and released on close: nothing stays in memory between uses.
        _launchpad = new LaunchpadWindow(_host, () => _settings.DockEnabled ? _dockWindow : null);
        _launchpad.Closed += (_, _) => _launchpad = null;
        _launchpad.Open(CurrentMonitor());
    }

    private void ToggleControlCenter()
    {
        if (_host is null || !_settings.ControlCenterEnabled) return;
        if (_controlCenter is not null)
        {
            _controlCenter.Dismiss();
            return;
        }
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        var monitor = CurrentMonitor();
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        int top = info.rcMonitor.Top + (int)Math.Round((_menuBarInset + 6) * scale);
        int right = info.rcMonitor.Right - (int)Math.Round(8 * scale);

        _controlCenter = new ControlCenterWindow(_host);
        _controlCenter.Closed += (_, _) => _controlCenter = null;
        _controlCenter.ShowAt(right, top, HorizontalAlignment.Right);
    }

    private void OpenSettings(string page)
    {
        if (_settingsWindow is not null)
        {
            _settingsWindow.Show(page);
            _settingsWindow.Activate();
            return;
        }
        _settingsWindow = new SettingsWindow(_settings, ApplyModules, page);
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        ((Window)_settingsWindow).Show();
        _settingsWindow.Activate();
    }

    // ---- Presets & shortcuts -----------------------------------------------------------------

    /// <summary>Everything on, island only, or all off (DynaIsland stays in the tray).</summary>
    private void ApplyPreset(bool island, bool desktop)
    {
        _settings.ShowIsland = island;
        _settings.SidePopups = island;
        _settings.DockEnabled = _settings.MenuBarEnabled = _settings.LaunchpadEnabled = _settings.ControlCenterEnabled = desktop;
        _settings.Save();
        ApplyModules();
    }

    /// <summary>Shortcut: switch everything off, and back to exactly what was on before.</summary>
    private void ToggleAll()
    {
        var s = _settings;
        bool anyOn = s.ShowIsland || s.DockEnabled || s.MenuBarEnabled || s.LaunchpadEnabled || s.ControlCenterEnabled || s.SidePopups;
        if (anyOn)
        {
            _beforeAllOff = (s.ShowIsland, s.DockEnabled, s.MenuBarEnabled, s.LaunchpadEnabled, s.ControlCenterEnabled, s.SidePopups);
            ApplyPreset(false, false);
            return;
        }
        var b = _beforeAllOff ?? (true, true, true, true, true, true);
        (s.ShowIsland, s.DockEnabled, s.MenuBarEnabled, s.LaunchpadEnabled, s.ControlCenterEnabled, s.SidePopups) = b;
        s.Save();
        ApplyModules();
    }

    private void RegisterHotkeys()
    {
        if (_hotkeys is null) return;
        _hotkeys.Clear();
        _hotkeys.Register(_settings.HotkeyLaunchpad, OpenLaunchpad);
        _hotkeys.Register(_settings.HotkeyControlCenter, ToggleControlCenter);
        _hotkeys.Register(_settings.HotkeyToggleAll, ToggleAll);
        _hotkeys.Register(_settings.HotkeySettings, () => OpenSettings("General"));
    }

    private void CloseDesktop()
    {
        _hotkeys?.Dispose();
        _winEvents?.Dispose();
        _launchpad?.Close();
        _controlCenter?.Close();
        _settingsWindow?.Close();
        _menuBar?.Close();
        _dockWindow?.Close();
        RestoreTaskbar(); // the taskbar comes back when DynaIsland quits
    }
}
