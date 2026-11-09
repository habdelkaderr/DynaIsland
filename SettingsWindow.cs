using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using DynaIsland.Ui;

namespace DynaIsland;

/// <summary>
/// Central settings window (sidebar + pages). Every change applies immediately and is saved.
/// </summary>
internal sealed class SettingsWindow : Window
{
    private static readonly string[] Pages = ["General", "Dynamic Island", "Dock", "Menu bar", "Appearance", "Shortcuts", "About"];

    private readonly Settings _s;
    private readonly Action _changed;
    private readonly StackPanel _sidebar = new() { Margin = new Thickness(10, 14, 10, 10) };
    private readonly StackPanel _page = new() { Margin = new Thickness(28, 22, 28, 28) };
    private string _current = "General";

    public SettingsWindow(Settings settings, Action changed, string page = "General")
    {
        _s = settings;
        _changed = changed;
        Title = "DynaIsland Settings";
        Width = 760;
        Height = 620;
        MinWidth = 640;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        FontSize = 13;
        SetResourceReference(BackgroundProperty, "Ui.Window");
        SetResourceReference(ForegroundProperty, "Ui.Text");

        var side = new Border { Width = 200, Child = _sidebar };
        side.SetResourceReference(Border.BackgroundProperty, "Ui.Tile");
        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Content = _page };
        var root = new DockPanel();
        DockPanel.SetDock(side, Dock.Left);
        root.Children.Add(side);
        root.Children.Add(scroll);
        Content = root;

        SourceInitialized += (_, _) => DarkTitleBar();
        Theme.Changed += OnTheme;
        Closed += (_, _) => Theme.Changed -= OnTheme;
        Show(page);
    }

    private void OnTheme(object? sender, EventArgs e)
    {
        DarkTitleBar();
        Show(_current);
    }

    private void DarkTitleBar()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        int dark = Theme.IsDark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, 20, ref dark, sizeof(int));
    }

    /// <summary>Redraws the current page (after changes made elsewhere, e.g. the tray menu).</summary>
    public void Refresh() => Show(_current);

    public void Show(string page)
    {
        _current = Pages.Contains(page) ? page : "General";
        BuildSidebar();
        _page.Children.Clear();
        _page.Children.Add(Ux.Text(_current, 22, weight: FontWeights.SemiBold));
        _page.Children.Add(Ux.Gap(14));
        switch (_current)
        {
            case "General": General(); break;
            case "Dynamic Island": Island(); break;
            case "Dock": DockPage(); break;
            case "Menu bar": MenuBar(); break;
            case "Appearance": Appearance(); break;
            case "Shortcuts": Shortcuts(); break;
            case "About": About(); break;
        }
    }

    private void BuildSidebar()
    {
        _sidebar.Children.Clear();
        var glyphs = new Dictionary<string, string>
        {
            ["General"] = "", ["Dynamic Island"] = "", ["Dock"] = "", ["Menu bar"] = "",
            ["Appearance"] = "", ["Shortcuts"] = "", ["About"] = "",
        };
        foreach (var name in Pages)
        {
            var icon = Icons.Glyph(glyphs[name], 14);
            icon.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
            icon.Width = 22;
            var label = Ux.Text(name, 13);
            label.Margin = new Thickness(8, 0, 0, 0);
            var row = new Border
            {
                CornerRadius = new CornerRadius(7),
                Padding = new Thickness(10, 7, 10, 7),
                Margin = new Thickness(0, 1, 0, 1),
                Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { icon, label } },
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
            };
            if (name == _current) row.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
            row.MouseLeftButtonUp += (_, _) => Show(name);
            _sidebar.Children.Add(row);
        }
    }

    // ---- Building blocks ---------------------------------------------------------------------

    private void Section(string title)
    {
        var t = Ux.Text(title, 12, "Ui.TextSecondary", FontWeights.SemiBold);
        t.Margin = new Thickness(4, 14, 0, 6);
        _page.Children.Add(t);
    }

    private StackPanel Group()
    {
        var stack = new StackPanel();
        _page.Children.Add(Ux.Tile(stack, 12, new Thickness(14, 4, 14, 4)));
        return stack;
    }

    private static void AddRow(StackPanel group, string label, string? help, UIElement control)
    {
        if (group.Children.Count > 0)
        {
            var line = new Border { Height = 1 };
            line.SetResourceReference(Border.BackgroundProperty, "Ui.Divider");
            group.Children.Add(line);
        }
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ux.Text(label, 13));
        if (help is not null)
        {
            var h = Ux.Text(help, 11.5, "Ui.TextSecondary");
            h.TextWrapping = TextWrapping.Wrap;
            h.TextTrimming = TextTrimming.None;
            text.Children.Add(h);
        }
        var row = new DockPanel { Margin = new Thickness(0, 9, 0, 9) };
        if (control is FrameworkElement fe) fe.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(control, Dock.Right);
        row.Children.Add(control);
        text.Margin = new Thickness(0, 0, 16, 0);
        row.Children.Add(text);
        group.Children.Add(row);
    }

    private void Toggle(StackPanel group, string label, string? help, Func<bool> get, Action<bool> set)
    {
        var sw = new Switch(get());
        sw.Toggled += (_, on) => { set(on); Commit(); };
        AddRow(group, label, help, sw);
    }

    private void Choice(StackPanel group, string label, string? help, (string, string)[] options, Func<string> get, Action<string> set)
    {
        AddRow(group, label, help, new Segmented(options, get(), v => { set(v); Commit(); }));
    }

    private void SliderRow(StackPanel group, string label, double min, double max, Func<double> get, Action<double> set, Func<double, string> format)
    {
        var value = Ux.Text(format(get()), 12, "Ui.TextSecondary");
        value.Width = 44;
        value.TextAlignment = TextAlignment.Right;
        var slider = Ux.Slider((get() - min) / (max - min), t =>
        {
            double v = min + t * (max - min);
            value.Text = format(v);
            set(v);
            DeferCommit(); // sliders fire continuously while dragging
        });
        slider.Width = 180;
        AddRow(group, label, null, new StackPanel { Orientation = Orientation.Horizontal, Children = { slider, value } });
    }

    private Border Button(string text, Action onClick, bool accent = false)
    {
        var label = Ux.Text(text, 12.5);
        var b = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(14, 6, 14, 7), Child = label, Cursor = Cursors.Hand, Margin = new Thickness(0, 0, 8, 0) };
        if (accent)
        {
            b.SetResourceReference(Border.BackgroundProperty, "Ui.Accent");
            label.Foreground = Brushes.White;
        }
        else
        {
            Ux.Hoverable(b);
        }
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    private void Commit()
    {
        _deferred?.Stop();
        _s.Save();
        _changed();
    }

    private System.Windows.Threading.DispatcherTimer? _deferred;

    private void DeferCommit()
    {
        if (_deferred is null)
        {
            _deferred = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(150) };
            _deferred.Tick += (_, _) => Commit();
        }
        _deferred.Stop();
        _deferred.Start();
    }

    // ---- Pages -------------------------------------------------------------------------------

    private void General()
    {
        Section("PRESETS");
        var presets = new WrapPanel { Margin = new Thickness(0, 0, 0, 4) };
        presets.Children.Add(Button("Everything", () => Preset(true, true), accent: true));
        presets.Children.Add(Button("Island only", () => Preset(true, false)));
        presets.Children.Add(Button("All off", () => Preset(false, false)));
        _page.Children.Add(presets);
        var note = Ux.Text("All off keeps DynaIsland in the notification area, ready to switch back on.", 11.5, "Ui.TextSecondary");
        note.TextWrapping = TextWrapping.Wrap;
        _page.Children.Add(note);

        Section("MODULES");
        var g = Group();
        Toggle(g, "Dynamic Island", "Media, alerts, notifications, timers and AI at the top of the screen", () => _s.ShowIsland, v => _s.ShowIsland = v);
        Toggle(g, "Dock", "App launcher and switcher at the edge of the screen", () => _s.DockEnabled, v => _s.DockEnabled = v);
        Toggle(g, "Menu bar", "Active app, menus, status and clock across the top", () => _s.MenuBarEnabled, v => _s.MenuBarEnabled = v);
        Toggle(g, "Launchpad", "Full-screen grid of your apps", () => _s.LaunchpadEnabled, v => _s.LaunchpadEnabled = v);
        Toggle(g, "Control Center", "Wi-Fi, Bluetooth, sound, display and more from the menu bar", () => _s.ControlCenterEnabled, v => _s.ControlCenterEnabled = v);
        Toggle(g, "Side pop-ups", "Volume and brightness slide in from the screen edges", () => _s.SidePopups, v => _s.SidePopups = v);

        Section("SYSTEM");
        var sys = Group();
        Toggle(sys, "Start with Windows", null, () => Settings.StartWithWindows, v => { try { Settings.StartWithWindows = v; } catch (Exception ex) { Log.Error("startup", ex); } });
        Toggle(sys, "Hide in full-screen apps", "Island, dock and menu bar step aside for games and videos", () => _s.HideInFullscreen, v => _s.HideInFullscreen = v);
        Toggle(sys, "Low-memory mode", "Software rendering: about 30 MB instead of 115 MB. Restarts DynaIsland.", () => _s.LowMemoryMode, v =>
        {
            _s.LowMemoryMode = v;
            _s.Save();
            ((App)Application.Current).Restart();
        });
    }

    private void Preset(bool island, bool desktop)
    {
        _s.ShowIsland = island;
        _s.DockEnabled = _s.MenuBarEnabled = _s.LaunchpadEnabled = _s.ControlCenterEnabled = desktop;
        _s.SidePopups = island;
        Commit();
        Show("General");
    }

    private void Island()
    {
        Section("LOOK");
        var look = Group();
        Choice(look, "Shape", null, [("Floating island", "Island"), ("Notch", "Notch")], () => _s.Shape, v => _s.Shape = v);
        Choice(look, "Colour", null, [("Black", "Black"), ("Graphite", "Graphite"), ("Translucent", "Translucent")], () => _s.Theme, v => _s.Theme = v);
        Choice(look, "Size", null, [("Small", "Small"), ("Default", "Default"), ("Large", "Large")], () => _s.Size, v => _s.Size = v);
        Toggle(look, "Expand on hover", "Off: click the island to expand it", () => _s.ExpandOnHover, v => _s.ExpandOnHover = v);
        Toggle(look, "Fade out when idle", null, () => _s.HideWhenIdle, v => _s.HideWhenIdle = v);

        Section("SHOW IN THE ISLAND");
        var g = Group();
        Toggle(g, "Now playing", null, () => _s.ShowMedia, v => _s.ShowMedia = v);
        Toggle(g, "Volume and mute", null, () => _s.ShowVolume, v => _s.ShowVolume = v);
        Toggle(g, "Brightness", null, () => _s.ShowBrightness, v => _s.ShowBrightness = v);
        Toggle(g, "Battery and charging", null, () => _s.ShowBattery, v => _s.ShowBattery = v);
        Toggle(g, "Notifications", null, () => _s.ShowNotifications, v => _s.ShowNotifications = v);
        Toggle(g, "Calendar (classic Outlook)", null, () => _s.ShowCalendar, v => _s.ShowCalendar = v);
        Toggle(g, "Do Not Disturb", null, () => _s.ShowDoNotDisturb, v => _s.ShowDoNotDisturb = v);
        Toggle(g, "Bluetooth devices", null, () => _s.ShowBluetooth, v => _s.ShowBluetooth = v);
        Toggle(g, "Network and VPN", null, () => _s.ShowNetwork, v => _s.ShowNetwork = v);
        Toggle(g, "Mic and camera in use", null, () => _s.ShowPrivacy, v => _s.ShowPrivacy = v);
        Toggle(g, "Screenshots", null, () => _s.ShowScreenshots, v => _s.ShowScreenshots = v);
        Toggle(g, "Downloads", null, () => _s.ShowDownloads, v => _s.ShowDownloads = v);
        Toggle(g, "Clipboard copies", null, () => _s.ShowClipboard, v => _s.ShowClipboard = v);
        Toggle(g, "Caps Lock", null, () => _s.ShowCapsLock, v => _s.ShowCapsLock = v);
        Toggle(g, "Keyboard layout", "Hidden while the menu bar shows the layout", () => _s.ShowKeyboardLayout, v => _s.ShowKeyboardLayout = v);

        Section("SHORTCUTS");
        var help = Ux.Text("Apps at the top right of the island's Home card. Ask opens a prompt box and hands your text to the app; Open just opens it.", 11.5, "Ui.TextSecondary");
        help.TextWrapping = TextWrapping.Wrap;
        help.Margin = new Thickness(4, 0, 0, 6);
        _page.Children.Add(help);
        var list = _s.IslandShortcuts ??= [];
        var sc = Group();
        for (int i = 0; i < list.Count; i++)
        {
            int index = i;
            var (name, icon) = Services.ShellApps.Lookup(list[i].Id);
            var controls = new StackPanel { Orientation = Orientation.Horizontal };
            controls.Children.Add(new Segmented([("Ask", "ask"), ("Open", "open")], list[i].Ask ? "ask" : "open",
                v => { list[index] = list[index] with { Ask = v == "ask" }; Commit(); }));
            controls.Children.Add(IconButton("", index > 0, () => { (list[index], list[index - 1]) = (list[index - 1], list[index]); Commit(); Show(_current); }));
            controls.Children.Add(IconButton("", index < list.Count - 1, () => { (list[index], list[index + 1]) = (list[index + 1], list[index]); Commit(); Show(_current); }));
            controls.Children.Add(IconButton("", true, () => { list.RemoveAt(index); Commit(); Show(_current); }));
            var label = new StackPanel { Orientation = Orientation.Horizontal };
            label.Children.Add(new Image { Source = icon, Width = 20, Height = 20, Margin = new Thickness(0, 0, 10, 0) });
            label.Children.Add(Ux.Text(name, 13));
            AddRow(sc, "", null, controls);
            ((DockPanel)sc.Children[^1]).Children.Add(label); // icon + name on the left
        }
        if (list.Count == 0) AddRow(sc, "No shortcuts", null, new Border());
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        add.Children.Add(Button("Add app…", () => PickApp(app =>
        {
            if (list.Any(s => string.Equals(s.Id, app.Id, StringComparison.OrdinalIgnoreCase))) return;
            list.Add(new IslandShortcut(app.Id, Ask: app.Category == "AI"));
            Commit();
            Show(_current);
        })));
        _page.Children.Add(add);

        Section("AI");
        var ai = Group();
        Toggle(ai, "Send automatically", "Press Enter in the app after handing over your prompt (only when its message box has focus)", () => _s.AiAutoSend, v => _s.AiAutoSend = v);
    }

    private static Border IconButton(string glyph, bool enabled, Action onClick)
    {
        var icon = Icons.Glyph(glyph, 12);
        icon.SetResourceReference(TextBlock.ForegroundProperty, enabled ? "Ui.Text" : "Ui.TextFaint");
        var b = new Border { Width = 28, Height = 28, CornerRadius = new CornerRadius(7), Child = icon, Margin = new Thickness(4, 0, 0, 0), Background = Brushes.Transparent };
        if (!enabled) return b;
        b.Cursor = Cursors.Hand;
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, _) => onClick();
        return b;
    }

    /// <summary>Searchable list of installed apps (from Windows' Start list).</summary>
    private void PickApp(Action<Services.InstalledApp> picked)
    {
        var window = new Window
        {
            Owner = this,
            Title = "Choose an app",
            Width = 380,
            Height = 500,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            FontFamily = FontFamily,
        };
        window.SetResourceReference(BackgroundProperty, "Ui.Window");
        window.SourceInitialized += (_, _) =>
        {
            int dark = Theme.IsDark ? 1 : 0;
            DwmSetWindowAttribute(new WindowInteropHelper(window).Handle, 20, ref dark, sizeof(int));
        };

        var search = new TextBox { FontSize = 13, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(12), BorderThickness = new Thickness(0) };
        search.SetResourceReference(BackgroundProperty, "Ui.Control");
        search.SetResourceReference(ForegroundProperty, "Ui.Text");
        search.SetResourceReference(TextBox.CaretBrushProperty, "Ui.Text");
        var rows = new StackPanel { Margin = new Thickness(8, 0, 8, 8) };
        var scroll = new ScrollViewer { Content = rows, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var root = new DockPanel();
        DockPanel.SetDock(search, Dock.Top);
        root.Children.Add(search);
        root.Children.Add(scroll);
        window.Content = root;

        void Fill()
        {
            rows.Children.Clear();
            string q = search.Text.Trim();
            foreach (var app in Services.AppCatalog.All.Where(a => q.Length == 0 || a.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)).Take(80))
            {
                var image = new Image { Width = 22, Height = 22, Margin = new Thickness(0, 0, 10, 0) };
                Dispatcher.InvokeAsync(() => image.Source = app.Icon(32), System.Windows.Threading.DispatcherPriority.Background);
                var row = new Border
                {
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(8, 6, 8, 6),
                    Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { image, Ux.Text(app.Name, 13) } },
                    Cursor = Cursors.Hand,
                    Background = Brushes.Transparent,
                };
                row.MouseEnter += (_, _) => row.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
                row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
                row.MouseLeftButtonUp += (_, _) => { window.Close(); picked(app); };
                rows.Children.Add(row);
            }
        }
        search.TextChanged += (_, _) => Fill();
        window.Loaded += (_, _) => { Fill(); search.Focus(); };
        window.ShowDialog();
        Services.ShellApps.ForgetIcons(32);
    }

    private void DockPage()
    {
        var g = Group();
        Toggle(g, "Show the dock", null, () => _s.DockEnabled, v => _s.DockEnabled = v);
        Choice(g, "Position on screen", null, [("Left", "Left"), ("Bottom", "Bottom"), ("Right", "Right")], () => _s.DockPosition, v => _s.DockPosition = v);
        Toggle(g, "Automatically hide and show", "Move the pointer to the screen edge to show it", () => _s.DockAutoHide, v => _s.DockAutoHide = v);
        Toggle(g, "Keep windows clear of the dock", "Maximized windows stop at the dock", () => _s.DockReserveSpace, v => _s.DockReserveSpace = v);
        Toggle(g, "Replace the Windows taskbar", "Sets the taskbar to auto-hide while the dock is on; restored when the dock is off or DynaIsland quits", () => _s.DockReplacesTaskbar, v => _s.DockReplacesTaskbar = v);

        Section("SIZE");
        var size = Group();
        SliderRow(size, "Icon size", 32, 80, () => _s.DockIconSize, v => _s.DockIconSize = Math.Round(v), v => $"{Math.Round(v)}");
        SliderRow(size, "Magnification", 0, 1, () => _s.DockMagnification, v => _s.DockMagnification = v, v => v < 0.02 ? "Off" : $"{Math.Round(v * 100)}%");
        SliderRow(size, "Spacing", 0, 16, () => _s.DockSpacing, v => _s.DockSpacing = Math.Round(v), v => $"{Math.Round(v)}");

        Section("APPS");
        var apps = new WrapPanel();
        apps.Children.Add(Button("Reset pinned apps", () => { _s.DockPins = null; Commit(); }));
        _page.Children.Add(apps);
        var tip = Ux.Text("Drag apps from Launchpad onto the dock to pin them; drag an icon off the dock to remove it.", 11.5, "Ui.TextSecondary");
        tip.TextWrapping = TextWrapping.Wrap;
        tip.Margin = new Thickness(0, 8, 0, 0);
        _page.Children.Add(tip);
    }

    private void MenuBar()
    {
        var g = Group();
        Toggle(g, "Show the menu bar", null, () => _s.MenuBarEnabled, v => _s.MenuBarEnabled = v);
        Toggle(g, "Now playing", null, () => _s.MenuBarShowMedia, v => _s.MenuBarShowMedia = v);
        Toggle(g, "Keyboard layout", null, () => _s.MenuBarShowKeyboard, v => _s.MenuBarShowKeyboard = v);
        Toggle(g, "Wi-Fi", null, () => _s.MenuBarShowWifi, v => _s.MenuBarShowWifi = v);
        Toggle(g, "Volume", null, () => _s.MenuBarShowVolume, v => _s.MenuBarShowVolume = v);
        Toggle(g, "Battery", null, () => _s.MenuBarShowBattery, v => _s.MenuBarShowBattery = v);
        Toggle(g, "Notifications", null, () => _s.MenuBarShowNotifications, v => _s.MenuBarShowNotifications = v);
        Toggle(g, "Show the date", null, () => _s.MenuBarShowDate, v => _s.MenuBarShowDate = v);
        Toggle(g, "Show seconds", null, () => _s.MenuBarShowSeconds, v => _s.MenuBarShowSeconds = v);

        Section("GO MENU");
        var go = Group();
        foreach (var item in _s.MenuBarGoItems.ToList())
        {
            var remove = Button("Remove", () => { _s.MenuBarGoItems.Remove(item); Commit(); Show("Menu bar"); });
            remove.Margin = new Thickness(0);
            AddRow(go, item, Services.SystemActions.GoTarget(item) is null ? "Not found" : null, remove);
        }
        var add = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        add.Children.Add(Button("Add folder…", () =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Add a folder to the Go menu" };
            if (dialog.ShowDialog(this) == true && !_s.MenuBarGoItems.Contains(dialog.FolderName))
            {
                _s.MenuBarGoItems.Add(dialog.FolderName);
                Commit();
                Show("Menu bar");
            }
        }));
        _page.Children.Add(add);
    }

    private void Appearance()
    {
        var g = Group();
        Choice(g, "Appearance", "Menu bar, dock, Control Center, Launchpad and Settings", [("Auto", "System"), ("Dark", "Dark"), ("Light", "Light")], () => _s.UiTheme, v => _s.UiTheme = v);
        Toggle(g, "Blur behind glass", "Off: solid surfaces (slightly lighter on the GPU)", () => _s.GlassBlur, v => _s.GlassBlur = v);
        SliderRow(g, "Glass tint", 0.25, 0.95, () => _s.GlassOpacity, v => _s.GlassOpacity = v, v => $"{Math.Round(v * 100)}%");
        SliderRow(g, "Animation speed", 0.5, 2, () => _s.AnimationSpeed, v => _s.AnimationSpeed = Math.Round(v, 2), v => $"{v:0.0}×");
    }

    private void Shortcuts()
    {
        var note = Ux.Text("Click a shortcut, then press the new key combination. Backspace clears it.", 12, "Ui.TextSecondary");
        note.TextWrapping = TextWrapping.Wrap;
        _page.Children.Add(note);
        _page.Children.Add(Ux.Gap(8));
        var g = Group();
        HotkeyRow(g, "Open Launchpad", () => _s.HotkeyLaunchpad, v => _s.HotkeyLaunchpad = v);
        HotkeyRow(g, "Open Control Center", () => _s.HotkeyControlCenter, v => _s.HotkeyControlCenter = v);
        HotkeyRow(g, "Turn everything on/off", () => _s.HotkeyToggleAll, v => _s.HotkeyToggleAll = v);
        HotkeyRow(g, "Open Settings", () => _s.HotkeySettings, v => _s.HotkeySettings = v);
    }

    private void HotkeyRow(StackPanel group, string label, Func<string> get, Action<string> set)
    {
        var text = Ux.Text(get().Length > 0 ? get() : "None", 12.5);
        var box = new Border { CornerRadius = new CornerRadius(7), Padding = new Thickness(12, 5, 12, 6), MinWidth = 150, Child = text, Focusable = true, Cursor = Cursors.Hand };
        Ux.Hoverable(box);
        box.MouseLeftButtonUp += (_, _) => { text.Text = "Press keys…"; box.Focus(); Keyboard.Focus(box); };
        box.LostKeyboardFocus += (_, _) => text.Text = get().Length > 0 ? get() : "None";
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key == Key.Back || key == Key.Delete) { set(""); text.Text = "None"; Commit(); return; }
            if (key == Key.Escape) { Keyboard.ClearFocus(); return; }
            if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftAlt or Key.RightAlt or Key.LeftShift or Key.RightShift or Key.LWin or Key.RWin) return;
            var mods = Keyboard.Modifiers;
            if (mods == ModifierKeys.None) { text.Text = "Add Ctrl, Alt or Shift"; return; }
            string combo = Hotkeys.Format(mods, key);
            set(combo);
            text.Text = combo;
            Commit();
            Keyboard.ClearFocus();
        };
        AddRow(group, label, null, box);
    }

    private void About()
    {
        var version = typeof(App).Assembly.GetName().Version?.ToString(3);
        _page.Children.Add(Ux.Text($"DynaIsland {version}", 15, weight: FontWeights.SemiBold));
        _page.Children.Add(Ux.Gap(6));
        var body = Ux.Text("A Dynamic Island, dock, menu bar, Launchpad and Control Center for Windows. " +
                           "Everything runs on this PC: no network access, no telemetry, no accounts.", 13, "Ui.TextSecondary");
        body.TextWrapping = TextWrapping.Wrap;
        _page.Children.Add(body);
        _page.Children.Add(Ux.Gap(16));
        var links = new WrapPanel();
        links.Children.Add(Button("Open settings folder", () => Services.ShellApps.Start("explorer.exe",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynaIsland"))));
        links.Children.Add(Button("Open log folder", () => Services.ShellApps.Start("explorer.exe",
            System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DynaIsland"))));
        _page.Children.Add(links);
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);
}
