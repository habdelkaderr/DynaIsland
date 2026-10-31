using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using DynaIsland.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynaIsland;

// Settings menu (shared by the tray icon and the island's right-click), appearance, screens, visibility.
public partial class MainWindow
{
    private static readonly Drawing.Font MenuFont = new("Segoe UI", 9.5f);
    private IntPtr _currentMonitor;

    private double ShadowOpacity => _settings.Theme == "Translucent" ? 0.3 : 0.55;

    // ---- Menu --------------------------------------------------------------------------------

    private Forms.ContextMenuStrip CreateMenu()
    {
        Forms.ToolStripManager.Renderer = new TrayIcon.DarkRenderer();
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, ShowCheckMargin = true, Font = MenuFont };
        menu.Opening += (_, _) => PopulateMenu(menu); // rebuilt each time so ticks and the timer state are current
        PopulateMenu(menu);
        return menu;
    }

    private void ShowMenuAtCursor()
    {
        _menu.Show(Forms.Cursor.Position);
        Native.SetForegroundWindow(_menu.Handle); // so clicking elsewhere closes it
    }

    private void PopulateMenu(Forms.ContextMenuStrip menu)
    {
        foreach (var old in menu.Items.Cast<Forms.ToolStripItem>().ToList()) old.Dispose();
        menu.Items.Clear();

        menu.Items.Add(new Forms.ToolStripLabel("DynaIsland") { Font = new Drawing.Font(MenuFont, Drawing.FontStyle.Bold) });
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Check("Show island", _settings.ShowIsland, SetIslandVisible));
        menu.Items.Add(TimerMenu());
        menu.Items.Add(new Forms.ToolStripSeparator());

        menu.Items.Add(Submenu("Show on island",
            Toggle("Now playing", () => _settings.ShowMedia, v => _settings.ShowMedia = v),
            Toggle("Volume and mute", () => _settings.ShowVolume, v => _settings.ShowVolume = v),
            Toggle("Brightness", () => _settings.ShowBrightness, v => _settings.ShowBrightness = v),
            Toggle("Battery and charging", () => _settings.ShowBattery, v => _settings.ShowBattery = v),
            new Forms.ToolStripSeparator(),
            Toggle("Notifications", () => _settings.ShowNotifications, v => _settings.ShowNotifications = v),
            Toggle("Calendar (classic Outlook)", () => _settings.ShowCalendar, v => _settings.ShowCalendar = v),
            Toggle("Do Not Disturb", () => _settings.ShowDoNotDisturb, v => _settings.ShowDoNotDisturb = v),
            new Forms.ToolStripSeparator(),
            Toggle("Bluetooth devices", () => _settings.ShowBluetooth, v => _settings.ShowBluetooth = v),
            Toggle("Network and VPN", () => _settings.ShowNetwork, v => _settings.ShowNetwork = v),
            Toggle("Mic and camera in use", () => _settings.ShowPrivacy, v => _settings.ShowPrivacy = v),
            new Forms.ToolStripSeparator(),
            Toggle("Screenshots", () => _settings.ShowScreenshots, v => _settings.ShowScreenshots = v),
            Toggle("Downloads", () => _settings.ShowDownloads, v => _settings.ShowDownloads = v),
            Toggle("Clipboard copies", () => _settings.ShowClipboard, v => _settings.ShowClipboard = v),
            Toggle("Caps Lock", () => _settings.ShowCapsLock, v => _settings.ShowCapsLock = v),
            Toggle("Keyboard layout", () => _settings.ShowKeyboardLayout, v => _settings.ShowKeyboardLayout = v)));

        menu.Items.Add(Submenu("Appearance",
            Submenu("Theme",
                Radio("Black", _settings.Theme == "Black", () => SetTheme("Black")),
                Radio("Graphite", _settings.Theme == "Graphite", () => SetTheme("Graphite")),
                Radio("Translucent", _settings.Theme == "Translucent", () => SetTheme("Translucent"))),
            Submenu("Size",
                Radio("Small", _settings.Size == "Small", () => SetSize("Small")),
                Radio("Default", _settings.Size == "Default", () => SetSize("Default")),
                Radio("Large", _settings.Size == "Large", () => SetSize("Large"))),
            ScreenMenu(),
            new Forms.ToolStripSeparator(),
            Toggle("Expand on hover (off: click)", () => _settings.ExpandOnHover, v => { _settings.ExpandOnHover = v; _pinned = false; }),
            Toggle("Fade out when idle", () => _settings.HideWhenIdle, v => _settings.HideWhenIdle = v),
            Toggle("Hide in full-screen apps", () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v),
            Check("Low-memory mode (restarts)", _settings.LowMemoryMode, v =>
            {
                _settings.LowMemoryMode = v;
                _settings.Save();
                ((App)Application.Current).Restart();
            })));

        menu.Items.Add(Check("Start with Windows", Settings.StartWithWindows, v =>
        {
            try { Settings.StartWithWindows = v; }
            catch (Exception ex) { Log.Error("startup", ex); }
        }));
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Item("Quit DynaIsland", Close));
    }

    private Forms.ToolStripMenuItem TimerMenu()
    {
        var items = new List<Forms.ToolStripItem>();
        if (_timer.IsActive)
        {
            string state = TimerService.Format(_timer.Display, roundUp: _timer.Kind != TimerKind.Stopwatch);
            items.Add(new Forms.ToolStripLabel($"{_timer.Label} · {state}{(_timer.IsPaused ? " (paused)" : "")}"));
            items.Add(Item(_timer.IsPaused ? "Resume" : "Pause", () =>
            {
                if (_timer.IsPaused) _timer.Resume();
                else _timer.Pause();
                UpdateTimerUi();
            }));
            if (_timer.Kind != TimerKind.Stopwatch) items.Add(Item("Add 1 minute", () => { _timer.AddMinute(); UpdateTimerUi(); }));
            items.Add(Item("Stop", StopTimer));
            items.Add(new Forms.ToolStripSeparator());
        }

        foreach (int minutes in new[] { 1, 3, 5, 10, 15, 30, 60 })
        {
            string label = minutes == 1 ? "1 minute" : minutes == 60 ? "1 hour" : $"{minutes} minutes";
            items.Add(Item(label, () => StartTimer(TimerKind.Timer, TimeSpan.FromMinutes(minutes))));
        }
        items.Add(new Forms.ToolStripSeparator());
        items.Add(Item("Focus session · 25 min", () => StartTimer(TimerKind.Focus, TimeSpan.FromMinutes(25))));
        items.Add(Item("Focus session · 50 min", () => StartTimer(TimerKind.Focus, TimeSpan.FromMinutes(50))));
        items.Add(new Forms.ToolStripSeparator());
        items.Add(Item("Stopwatch", () => StartTimer(TimerKind.Stopwatch)));
        return Submenu(_timer.IsActive ? $"Timer ({_timer.Label.ToLowerInvariant()} running)" : "Timer", [.. items]);
    }

    private Forms.ToolStripMenuItem ScreenMenu()
    {
        var items = new List<Forms.ToolStripItem>
        {
            Radio("Main display", _settings.Screen == "Primary", () => SetScreen("Primary")),
            Radio("Follow the active window", _settings.Screen == "Follow", () => SetScreen("Follow")),
            new Forms.ToolStripSeparator(),
        };
        var screens = Forms.Screen.AllScreens;
        for (int i = 0; i < screens.Length; i++)
        {
            var s = screens[i];
            string label = $"Display {i + 1} · {s.Bounds.Width}×{s.Bounds.Height}{(s.Primary ? " (main)" : "")}";
            items.Add(Radio(label, _settings.Screen == s.DeviceName, () => SetScreen(s.DeviceName)));
        }
        return Submenu("Screen", [.. items]);
    }

    private Forms.ToolStripMenuItem Toggle(string text, Func<bool> get, Action<bool> set) => Check(text, get(), set);

    private Forms.ToolStripMenuItem Check(string text, bool value, Action<bool> set)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = value };
        item.Click += (_, _) =>
        {
            set(!value);
            AfterSettingChanged();
        };
        return item;
    }

    private Forms.ToolStripMenuItem Radio(string text, bool selected, Action select)
    {
        var item = new Forms.ToolStripMenuItem(text) { Checked = selected };
        item.Click += (_, _) =>
        {
            select();
            AfterSettingChanged();
        };
        return item;
    }

    private static Forms.ToolStripMenuItem Item(string text, Action onClick)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static Forms.ToolStripMenuItem Submenu(string text, params Forms.ToolStripItem[] items)
    {
        var item = new Forms.ToolStripMenuItem(text);
        item.DropDownItems.AddRange(items);
        if (item.DropDown is Forms.ToolStripDropDownMenu dropDown)
        {
            dropDown.ShowImageMargin = false;
            dropDown.ShowCheckMargin = true;
        }
        return item;
    }

    private void AfterSettingChanged()
    {
        _settings.Save();
        _lastVolume = -1;
        UpdateState();
    }

    // ---- Appearance --------------------------------------------------------------------------

    private void SetTheme(string theme)
    {
        _settings.Theme = theme;
        ApplyAppearance();
    }

    private void SetSize(string size)
    {
        _settings.Size = size;
        ApplyAppearance();
    }

    private void ApplyAppearance()
    {
        double scale = _settings.Size switch { "Small" => 0.85, "Large" => 1.2, _ => 1.0 };
        RootScale.ScaleX = RootScale.ScaleY = scale;
        Width = BaseWidth * scale;
        Height = BaseHeight * scale;

        var (background, border) = _settings.Theme switch
        {
            "Graphite" => (Color.FromRgb(0x1C, 0x1C, 0x1E), Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF)),
            "Translucent" => (Color.FromArgb(0xC8, 0x14, 0x14, 0x16), Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            _ => (Colors.Black, Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF)),
        };
        var fill = Freeze(new SolidColorBrush(background));
        Island.Background = SideBubble.Background = fill;
        Island.BorderBrush = Freeze(new SolidColorBrush(border));
        Shadow.BeginAnimation(OpacityProperty, new DoubleAnimation(_faded ? 0 : ShadowOpacity, TimeSpan.FromMilliseconds(200)));
        PositionWindow();
    }

    // ---- Screens -----------------------------------------------------------------------------

    private void SetScreen(string screen)
    {
        _settings.Screen = screen;
        PositionWindow();
    }

    private IntPtr TargetMonitor()
    {
        var primary = Native.MonitorFromPoint(new Native.POINT(), Native.MONITOR_DEFAULTTOPRIMARY);
        switch (_settings.Screen)
        {
            case "Primary":
                return primary;
            case "Follow":
                var foreground = Native.GetForegroundWindow();
                if (foreground == IntPtr.Zero || foreground == _hwnd) return _currentMonitor != IntPtr.Zero ? _currentMonitor : primary;
                return Native.MonitorFromWindow(foreground, Native.MONITOR_DEFAULTTONEAREST);
            default:
                var screen = Forms.Screen.AllScreens.FirstOrDefault(s => s.DeviceName == _settings.Screen);
                if (screen is null) return primary;
                var center = new Native.POINT { X = screen.Bounds.X + screen.Bounds.Width / 2, Y = screen.Bounds.Y + screen.Bounds.Height / 2 };
                return Native.MonitorFromPoint(center, Native.MONITOR_DEFAULTTONEAREST);
        }
    }

    /// <summary>Centres the window at the top of the chosen monitor (physical pixels, so mixed DPI works).</summary>
    private void PositionWindow()
    {
        if (_hwnd == IntPtr.Zero) return;
        var monitor = TargetMonitor();
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        uint dpi = Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX : 96;

        int width = (int)Math.Round(Width * dpi / 96.0);
        var rc = info.rcMonitor;
        int x = rc.Left + (rc.Right - rc.Left - width) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, rc.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        _currentMonitor = monitor;
    }

    // ---- Visibility & start-up ---------------------------------------------------------------

    private void SetIslandVisible(bool visible)
    {
        _settings.ShowIsland = visible;
        _settings.Save();
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        bool show = _settings.ShowIsland && !_fullscreenHidden;
        if (show == IsVisible) return;
        if (show)
        {
            Show();
            PositionWindow();
            KeepOnTop();
        }
        else
        {
            Hide();
        }
    }

    /// <summary>
    /// Turns auto-start on the first time the app runs, and keeps the Run entry pointing at
    /// this exe if it was moved (for example after reinstalling to a new folder).
    /// </summary>
    private void EnsureAutoStart()
    {
        try
        {
            if (!_settings.AutoStartConfigured)
            {
                Settings.StartWithWindows = true;
                _settings.AutoStartConfigured = true;
                _settings.Save();
            }
            else if (Settings.StartWithWindows)
            {
                Settings.StartWithWindows = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("auto-start", ex);
        }
    }
}
