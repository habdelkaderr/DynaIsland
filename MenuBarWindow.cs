using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynaIsland.Services;
using DynaIsland.Ui;

namespace DynaIsland;

/// <summary>
/// macOS-style menu bar across the top of the screen: logo menu, active app and its window menu,
/// Go menu, then media, keyboard, Wi-Fi, volume, battery, notifications, Control Center and the clock.
/// Reserves its strip like the taskbar; the island sits in its centre as the notch. Event-driven.
/// </summary>
internal sealed class MenuBarWindow : Window
{
    public const double BarHeight = 30;

    private readonly DesktopHost _host;
    private readonly IntPtr _hwnd;
    private readonly AppBar _appBar;
    private readonly StackPanel _left = new() { Orientation = Orientation.Horizontal, Margin = new Thickness(8, 0, 0, 0) };
    private readonly StackPanel _right = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 0, 8, 0) };
    private readonly DispatcherTimer _clockTimer = new();

    private TextBlock _appName = null!, _clock = null!, _battery = null!, _keyboard = null!, _mediaTitle = null!;
    private TextBlock _wifiGlyph = null!, _volumeGlyph = null!, _batteryGlyph = null!, _mediaGlyph = null!;
    private Border _media = null!, _keyboardItem = null!, _wifi = null!, _volume = null!, _batteryItem = null!, _bell = null!;
    private Ellipse _bellDot = null!;
    private IntPtr _activeWindow;
    private IntPtr _monitor;
    private bool _wanted;

    public MenuBarWindow(DesktopHost host)
    {
        _host = host;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Title = "DynaIsland menu bar";
        Height = BarHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        UseLayoutRounding = true;

        var grid = new Grid();
        grid.Children.Add(_left);
        grid.Children.Add(_right);
        Content = grid;
        BuildItems();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        Glass.Apply(this, Theme.SurfaceTint, host.Settings.GlassBlur, round: false, Theme.IsDark);
        _appBar = new AppBar(_hwnd, "DynaIslandMenuBarAppBar");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) =>
        {
            Theme.Changed -= OnThemeChanged;
            _appBar.Remove();
        };

        // Everything below is event-driven; the clock ticks once a minute (or second, if shown).
        _clockTimer.Tick += (_, _) => UpdateClock();
        host.Events.ForegroundChanged += hwnd => SetActiveWindow(hwnd);
        host.Media.Changed += (_, _) => UpdateMedia();
        host.Network.Changed += (_, _) => UpdateWifi();
        host.Radios.Changed += (_, _) => UpdateWifi();
        host.Power.StatusChanged += (_, _) => UpdateBattery();
        host.VolumeChanged += UpdateVolume;
        host.KeyboardChanged += UpdateKeyboard;
        host.NotificationsChanged += () => _bellDot.Visibility = host.UnreadNotifications ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnThemeChanged(object? sender, EventArgs e) => Glass.SetTint(_hwnd, Theme.SurfaceTint, _host.Settings.GlassBlur);

    // ---- Placement ---------------------------------------------------------------------------

    public void Apply(IntPtr monitor, bool visible)
    {
        _monitor = monitor;
        _wanted = visible;
        if (!visible)
        {
            _appBar.Remove();
            _clockTimer.Stop();
            if (IsVisible) Hide();
            return;
        }
        Glass.SetTint(_hwnd, Theme.SurfaceTint, _host.Settings.GlassBlur);
        RefreshAll();
        if (!IsVisible) Show();
        Place();
    }

    private void Place()
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (_monitor == IntPtr.Zero || !Native.GetMonitorInfo(_monitor, ref info)) return;
        double scale = Native.GetDpiForMonitor(_monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        int height = (int)Math.Round(BarHeight * scale);
        var bar = _appBar.Reserve(AppBar.ABE_TOP, info.rcMonitor, height);
        Width = (info.rcMonitor.Right - info.rcMonitor.Left) / scale;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, info.rcMonitor.Left, bar.Top,
            info.rcMonitor.Right - info.rcMonitor.Left, height, Native.SWP_NOACTIVATE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_appBar.CallbackMessage && wParam.ToInt32() == AppBar.ABN_POSCHANGED && _wanted)
            Dispatcher.InvokeAsync(Place);
        return IntPtr.Zero;
    }

    // ---- Items -------------------------------------------------------------------------------

    private void BuildItems()
    {
        // Left: logo, active app, Go
        var logo = new Rectangle { Width = 17, Height = 9, RadiusX = 4.5, RadiusY = 4.5 };
        logo.SetResourceReference(Shape.FillProperty, "Ui.Text");
        _left.Children.Add(Item(logo, b => ShowLogoMenu(b), padding: 12));

        _appName = Ux.Text("Desktop", 13, weight: FontWeights.SemiBold);
        _left.Children.Add(Item(_appName, b => ShowAppMenu(b)));
        _left.Children.Add(Item(Ux.Text("Go"), b => ShowGoMenu(b)));

        // Right: media, keyboard, Wi-Fi, volume, battery, notifications, Control Center, clock
        _mediaGlyph = Icons.Glyph("", 11);
        _mediaGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        _mediaTitle = Ux.Text("", 12.5, "Ui.TextSecondary");
        _mediaTitle.MaxWidth = 190;
        _mediaTitle.Margin = new Thickness(7, 0, 0, 0);
        var media = new StackPanel { Orientation = Orientation.Horizontal, Children = { _mediaGlyph, _mediaTitle } };
        _media = Item(media, b => _ = _host.Media.TogglePlayPauseAsync());
        _right.Children.Add(_media);

        _keyboard = Ux.Text("EN", 12, weight: FontWeights.SemiBold);
        _keyboardItem = Item(_keyboard, _ => SystemActions.SwitchKeyboardLayout());
        _right.Children.Add(_keyboardItem);

        _wifiGlyph = Glyph("");
        _wifi = Item(_wifiGlyph, b => ShowWifiMenu(b));
        _right.Children.Add(_wifi);

        _volumeGlyph = Glyph("");
        _volume = Item(_volumeGlyph, b => ShowVolumePopup(b));
        _right.Children.Add(_volume);

        _batteryGlyph = Glyph("", 15);
        _battery = Ux.Text("", 12.5);
        _battery.Margin = new Thickness(5, 0, 0, 0);
        _batteryItem = Item(new StackPanel { Orientation = Orientation.Horizontal, Children = { _batteryGlyph, _battery } }, b => ShowBatteryMenu(b));
        _right.Children.Add(_batteryItem);

        _bellDot = new Ellipse { Width = 6, Height = 6, Fill = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A)), HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, -2, -3, 0), Visibility = Visibility.Collapsed };
        var bell = new Grid { Children = { Glyph(""), _bellDot } };
        _bell = Item(bell, _ => { _host.ClearNotifications(); SystemActions.OpenNotificationCenter(); });
        _right.Children.Add(_bell);

        _right.Children.Add(Item(ControlCenterGlyph(), _ => _host.ToggleControlCenter()));

        _clock = Ux.Text("", 13);
        _right.Children.Add(Item(_clock, _ => SystemActions.OpenNotificationCenter(), padding: 10));
    }

    private static TextBlock Glyph(string glyph, double size = 14)
    {
        var t = Icons.Glyph(glyph, size);
        t.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        return t;
    }

    /// <summary>The Control Center symbol: two small toggles.</summary>
    private static FrameworkElement ControlCenterGlyph()
    {
        var canvas = new Canvas { Width = 15, Height = 13 };
        void Toggle(double y, bool knobRight)
        {
            var track = new Rectangle { Width = 15, Height = 5.5, RadiusX = 2.75, RadiusY = 2.75, StrokeThickness = 1.1 };
            track.SetResourceReference(Shape.StrokeProperty, "Ui.Text");
            Canvas.SetTop(track, y);
            var knob = new Ellipse { Width = 5.5, Height = 5.5 };
            knob.SetResourceReference(Shape.FillProperty, "Ui.Text");
            Canvas.SetTop(knob, y);
            Canvas.SetLeft(knob, knobRight ? 9.5 : 0);
            canvas.Children.Add(track);
            canvas.Children.Add(knob);
        }
        Toggle(0.5, false);
        Toggle(7, true);
        return canvas;
    }

    private Border Item(UIElement content, Action<Border> onClick, double padding = 8)
    {
        var b = new Border
        {
            Child = content,
            Padding = new Thickness(padding, 0, padding, 0),
            Margin = new Thickness(1, 3, 1, 3),
            CornerRadius = new CornerRadius(5),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
        };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Ui.Control");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(b); };
        return b;
    }

    /// <summary>Bottom-left (or bottom-right) of an item in screen pixels, for dropdowns.</summary>
    private static (int X, int Y) Below(Border item, bool rightAligned = false)
    {
        var p = item.PointToScreen(new Point(rightAligned ? item.ActualWidth : 0, item.ActualHeight + 4));
        return ((int)p.X, (int)p.Y);
    }

    private void ShowMenu(Border anchor, IEnumerable<GlassMenu.Entry> entries, bool rightAligned = false, double width = 230)
    {
        var (x, y) = Below(anchor, rightAligned);
        new GlassMenu(entries, width) { Blur = _host.Settings.GlassBlur }
            .ShowAt(x, y, rightAligned ? HorizontalAlignment.Right : HorizontalAlignment.Left);
    }

    // ---- Updates -----------------------------------------------------------------------------

    private void RefreshAll()
    {
        var s = _host.Settings;
        _media.Visibility = s.MenuBarShowMedia ? Visibility.Visible : Visibility.Collapsed;
        _keyboardItem.Visibility = s.MenuBarShowKeyboard ? Visibility.Visible : Visibility.Collapsed;
        _wifi.Visibility = s.MenuBarShowWifi ? Visibility.Visible : Visibility.Collapsed;
        _volume.Visibility = s.MenuBarShowVolume ? Visibility.Visible : Visibility.Collapsed;
        _batteryItem.Visibility = s.MenuBarShowBattery && PowerService.HasBattery ? Visibility.Visible : Visibility.Collapsed;
        _bell.Visibility = s.MenuBarShowNotifications ? Visibility.Visible : Visibility.Collapsed;
        UpdateClock();
        UpdateMedia();
        UpdateWifi();
        UpdateVolume();
        UpdateBattery();
        UpdateKeyboard();
        SetActiveWindow(Native.GetForegroundWindow());
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        bool seconds = _host.Settings.MenuBarShowSeconds;
        string time = seconds ? now.ToString("T") : now.ToString("t");
        _clock.Text = _host.Settings.MenuBarShowDate ? $"{now:ddd d MMM}   {time}" : time;
        // Next tick exactly on the next second/minute boundary.
        var next = seconds ? TimeSpan.FromMilliseconds(1000 - now.Millisecond) : TimeSpan.FromSeconds(60 - now.Second) - TimeSpan.FromMilliseconds(now.Millisecond);
        _clockTimer.Stop();
        _clockTimer.Interval = next < TimeSpan.FromMilliseconds(50) ? TimeSpan.FromMilliseconds(50) : next;
        if (_wanted) _clockTimer.Start();
    }

    private void UpdateMedia()
    {
        var m = _host.Settings.ShowMedia ? _host.Media.Current : null;
        bool show = _host.Settings.MenuBarShowMedia && m is not null;
        _media.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (m is null) return;
        _mediaGlyph.Text = m.IsPlaying ? "" : "";
        _mediaTitle.Text = string.IsNullOrEmpty(m.Artist) ? m.Title : $"{m.Title} · {m.Artist}";
    }

    private void UpdateWifi()
    {
        var state = _host.Network.Current;
        bool online = state?.Online == true;
        _wifiGlyph.Text = state?.IsWifi == false && online ? "" : "";
        _wifiGlyph.Opacity = online ? 1 : 0.35;
    }

    private void UpdateVolume()
    {
        float level = _host.VolumeLevel;
        _volumeGlyph.Text = _host.Muted || level < 0.005f ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
    }

    private void UpdateBattery()
    {
        if (!PowerService.HasBattery) return;
        int pct = PowerService.Percent;
        bool plugged = PowerService.IsPluggedIn;
        _battery.Text = $"{pct}%";
        _batteryGlyph.Text = char.ConvertFromUtf32((plugged ? 0xEBAB : 0xEBA0) + Math.Clamp(pct / 10, 0, 10));
        if (!plugged && pct <= 20) _batteryGlyph.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x45, 0x3A));
        else _batteryGlyph.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
    }

    private void UpdateKeyboard()
    {
        int id = _host.KeyboardLanguage;
        if (id == 0) return;
        try { _keyboard.Text = CultureInfo.GetCultureInfo(id).TwoLetterISOLanguageName.ToUpperInvariant(); }
        catch (CultureNotFoundException) { _keyboard.Text = "??"; }
    }

    private void SetActiveWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        if (pid == Environment.ProcessId) return; // our own panels don't count as the active app
        _activeWindow = hwnd;
        _appName.Text = Native.IsAppWindow(hwnd) ? AppNameFor(hwnd) : "Desktop";
    }

    private static AppWindow WindowInfo(IntPtr hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out uint pid);
        var (exe, aumid) = Native.ProcessIdentity(pid);
        return new AppWindow(hwnd, Native.WindowTitle(hwnd), exe, ShellApps.WindowAumid(hwnd) ?? aumid);
    }

    private static string AppNameFor(IntPtr hwnd)
    {
        var window = WindowInfo(hwnd);
        if (AppCatalog.ForWindow(window) is { } app) return app.Name;
        try
        {
            if (window.Exe is not null)
            {
                string? d = FileVersionInfo.GetVersionInfo(window.Exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(d)) return d.Trim();
                return System.IO.Path.GetFileNameWithoutExtension(window.Exe);
            }
        }
        catch
        {
            // fall back to the title
        }
        return window.Title;
    }

    // ---- Menus -------------------------------------------------------------------------------

    private void ShowLogoMenu(Border anchor)
    {
        var version = typeof(App).Assembly.GetName().Version;
        ShowMenu(anchor,
        [
            new("About DynaIsland", () => _host.OpenSettingsPage("About"), $"v{version?.ToString(3)}"),
            new("DynaIsland Settings…", _host.OpenSettings),
            GlassMenu.Entry.Separator,
            new("Launchpad", _host.OpenLaunchpad, Enabled: _host.Settings.LaunchpadEnabled),
            new("Control Center", _host.ToggleControlCenter, Enabled: _host.Settings.ControlCenterEnabled),
            GlassMenu.Entry.Separator,
            new("Windows Settings…", () => SystemActions.OpenSettings()),
            new("Task Manager", SystemActions.TaskManager),
            GlassMenu.Entry.Separator,
            new("Sleep", SystemActions.Sleep),
            new("Restart…", SystemActions.Restart),
            new("Shut Down…", SystemActions.ShutDown),
            GlassMenu.Entry.Separator,
            new("Lock Screen", SystemActions.Lock),
            new("Sign Out…", SystemActions.SignOut),
            GlassMenu.Entry.Separator,
            new("Quit DynaIsland", _host.Quit),
        ]);
    }

    private void ShowAppMenu(Border anchor)
    {
        var hwnd = _activeWindow;
        if (hwnd == IntPtr.Zero || !Native.IsAppWindow(hwnd))
        {
            ShowMenu(anchor,
            [
                GlassMenu.Entry.Header("Desktop"),
                new("Show Desktop", SystemActions.ShowDesktop),
                new("Open File Explorer", () => ShellApps.Start("explorer.exe")),
            ]);
            return;
        }

        var info = WindowInfo(hwnd);
        var app = AppCatalog.ForWindow(info);
        string name = _appName.Text;
        var siblings = WindowTracker.Snapshot().Where(w =>
            (app is not null && AppCatalog.Owns(app, w)) ||
            (app is null && string.Equals(w.Exe, info.Exe, StringComparison.OrdinalIgnoreCase))).ToList();

        var entries = new List<GlassMenu.Entry> { GlassMenu.Entry.Header(name) };
        foreach (var w in siblings.Take(10))
        {
            var handle = w.Handle;
            entries.Add(new(w.Title.Length > 48 ? w.Title[..48] + "…" : w.Title, () => WindowTracker.Activate(handle), Checked: handle == hwnd));
        }
        entries.Add(GlassMenu.Entry.Separator);
        bool zoomed = IsZoomed(hwnd);
        entries.Add(new("Minimize", () => Native.ShowWindow(hwnd, 6)));
        entries.Add(new(zoomed ? "Restore" : "Zoom", () => Native.ShowWindow(hwnd, zoomed ? Native.SW_RESTORE : 3 /* SW_MAXIMIZE */)));
        entries.Add(new("Close Window", () => PostMessage(hwnd, 0x0010, IntPtr.Zero, IntPtr.Zero)));
        if (app is not null) entries.Add(new("New Window", () => AppCatalog.Launch(app, _host.Settings)));
        entries.Add(GlassMenu.Entry.Separator);
        entries.Add(new($"Quit {name}", () => { foreach (var w in siblings) PostMessage(w.Handle, 0x0010, IntPtr.Zero, IntPtr.Zero); }));
        ShowMenu(anchor, entries);
    }

    private void ShowGoMenu(Border anchor)
    {
        var entries = new List<GlassMenu.Entry>();
        foreach (var item in _host.Settings.MenuBarGoItems)
        {
            string label = System.IO.Directory.Exists(item) ? System.IO.Path.GetFileName(item.TrimEnd('\\')) : item;
            entries.Add(new(label, () => SystemActions.Go(item), Enabled: SystemActions.GoTarget(item) is not null));
        }
        entries.Add(GlassMenu.Entry.Separator);
        entries.Add(new("Applications", _host.OpenLaunchpad, Enabled: _host.Settings.LaunchpadEnabled));
        entries.Add(new("Edit this menu…", () => _host.OpenSettingsPage("Menu bar")));
        ShowMenu(anchor, entries, width: 200);
    }

    private void ShowWifiMenu(Border anchor)
    {
        var state = _host.Network.Current;
        var entries = new List<GlassMenu.Entry> { GlassMenu.Entry.Header("Wi-Fi") };
        if (_host.Radios.WifiOn is bool on)
        {
            entries.Add(_host.Radios.CanControl
                ? new(on ? "Turn Wi-Fi Off" : "Turn Wi-Fi On", () => _ = _host.Radios.SetAsync(Windows.Devices.Radios.RadioKind.WiFi, !on))
                : new($"Wi-Fi is {(on ? "on" : "off")}", Enabled: false));
        }
        entries.Add(GlassMenu.Entry.Separator);
        entries.Add(state is { Online: true }
            ? new($"{(state.IsWifi ? "✓  " : "")}{(string.IsNullOrEmpty(state.Name) ? "Connected" : state.Name)}", Enabled: false, Hint: state.Limited ? "Limited" : "Connected")
            : new("Not connected", Enabled: false));
        if (state?.Vpn is { } vpn) entries.Add(new($"VPN: {vpn}", Enabled: false));
        entries.Add(GlassMenu.Entry.Separator);
        entries.Add(new("Other Networks…", () => SystemActions.OpenSettings("network-wifi")));
        entries.Add(new("Network & Internet Settings…", () => SystemActions.OpenSettings("network")));
        ShowMenu(anchor, entries, rightAligned: true, width: 260);
    }

    private void ShowBatteryMenu(Border anchor)
    {
        int pct = PowerService.Percent;
        string status = PowerService.IsCharging ? "Charging" : PowerService.IsPluggedIn ? "Plugged in" :
            PowerService.TimeRemaining is { } t ? $"{(int)t.TotalHours}h {t.Minutes}m remaining" : "On battery";
        ShowMenu(anchor,
        [
            GlassMenu.Entry.Header($"Battery  {pct}%"),
            new(status, Enabled: false),
            GlassMenu.Entry.Separator,
            new("Power & Battery Settings…", () => SystemActions.OpenSettings("batterysaver")),
        ], rightAligned: true, width: 240);
    }

    private void ShowVolumePopup(Border anchor)
    {
        var popup = new GlassPopup { Blur = _host.Settings.GlassBlur };
        var title = Ux.Text("Sound", 13, weight: FontWeights.SemiBold);
        var slider = Ux.Slider(_host.VolumeLevel, v => _host.Volume.SetLevel((float)v));
        slider.Width = 220;
        var mute = Ux.RoundButton(_host.Muted ? "" : "", _host.Muted, 30);
        mute.MouseLeftButtonUp += (_, _) =>
        {
            bool m = !_host.Muted;
            _host.Volume.SetMute(m);
            Ux.SetOn(mute, m);
            ((TextBlock)mute.Child).Text = m ? "" : "";
        };
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 0), Children = { mute, slider } };
        slider.Margin = new Thickness(10, 0, 0, 0);
        var settings = Ux.Text("Sound Settings…", 12.5, "Ui.TextSecondary");
        settings.Margin = new Thickness(0, 12, 0, 0);
        settings.Cursor = Cursors.Hand;
        settings.MouseLeftButtonUp += (_, _) => { popup.Dismiss(); SystemActions.OpenSettings("sound"); };
        popup.SetBody(new StackPanel { Margin = new Thickness(14, 12, 14, 12), Children = { title, row, settings } });
        var (x, y) = Below(anchor, rightAligned: true);
        popup.ShowAt(x, y, HorizontalAlignment.Right);
    }

    [DllImport("user32.dll")]
    private static extern bool IsZoomed(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
