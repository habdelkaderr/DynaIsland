using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DynaIsland.Services;
using DynaIsland.Ui;
using Windows.Devices.Radios;

namespace DynaIsland;

/// <summary>
/// Compact Control Center. Every control drives the real Windows setting: Wi-Fi and Bluetooth radios,
/// volume and mute, built-in display brightness, the Windows light/dark theme and media playback.
/// Do Not Disturb has no public API to set, so its tile shows the real state and opens Windows' own toggle.
/// </summary>
internal sealed class ControlCenterWindow : GlassPopup
{
    private const double PanelWidth = 330;
    private readonly DesktopHost _host;
    private readonly StackPanel _body = new() { Width = PanelWidth, Margin = new Thickness(12) };

    public ControlCenterWindow(DesktopHost host)
    {
        _host = host;
        Blur = host.Settings.GlassBlur;
        SetBody(_body);
        Build();
        host.Radios.Changed += OnExternalChange;
        host.VolumeChanged += OnVolume;
        host.Media.Changed += OnExternalChange;
        Theme.Changed += OnExternalChange;
        Closed += (_, _) =>
        {
            host.Radios.Changed -= OnExternalChange;
            host.VolumeChanged -= OnVolume;
            host.Media.Changed -= OnExternalChange;
            Theme.Changed -= OnExternalChange;
        };
    }

    // Outside changes (radios, media, theme) rebuild the panel, but never while the pointer is over it:
    // the rebuild waits until it leaves, so sliders and buttons are never replaced under a click.
    private bool _rebuildPending;

    private void OnExternalChange(object? sender, EventArgs e) => Dispatcher.InvokeAsync(() =>
    {
        if (IsMouseOver || Mouse.Captured is not null) _rebuildPending = true;
        else Build();
    });

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        if (_rebuildPending && Mouse.Captured is null)
        {
            _rebuildPending = false;
            Build();
        }
    }

    // Volume changes (keys, other apps) move the slider in place instead of rebuilding.
    private Slider? _soundSlider;
    private TextBlock? _soundIcon;
    private bool _syncing;

    private void OnVolume() => Dispatcher.InvokeAsync(() =>
    {
        if (_soundSlider is null || Mouse.Captured is not null) return;
        _syncing = true;
        _soundSlider.Value = _host.Muted ? 0 : _host.VolumeLevel;
        _syncing = false;
        if (_soundIcon is not null) _soundIcon.Text = _host.Muted ? "" : "";
    });

    // Read once when the panel opens (a WMI query); updated as the slider moves.
    private int? _brightness = BrightnessControl.Get();

    private void Build()
    {
        _body.Children.Clear();

        // Row 1: connectivity (left) and two small tiles (right)
        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.Children.Add(Connectivity());
        var side = new StackPanel();
        side.Children.Add(DoNotDisturb());
        side.Children.Add(Ux.Gap(10));
        side.Children.Add(Appearance());
        Grid.SetColumn(side, 2);
        row.Children.Add(side);
        _body.Children.Add(row);

        // Display brightness (only where Windows can control it)
        if (_brightness is int brightness)
        {
            _body.Children.Add(Ux.Gap(10));
            _body.Children.Add(SliderTile("Display", "", brightness / 100.0, v =>
            {
                _brightness = (int)Math.Round(v * 100);
                BrightnessControl.Request(_brightness.Value);
            }));
        }

        // Sound
        _body.Children.Add(Ux.Gap(10));
        var sound = SliderTile("Sound", _host.Muted ? "" : "", _host.Muted ? 0 : _host.VolumeLevel,
            v => { if (!_syncing) _host.Volume.SetLevel((float)v); },
            onIcon: () => _host.Volume.SetMute(!_host.Muted));
        _body.Children.Add(sound);

        // Now playing
        if (_host.Settings.ShowMedia && _host.Media.Current is { } media)
        {
            _body.Children.Add(Ux.Gap(10));
            _body.Children.Add(NowPlaying(media));
        }

        // Battery
        if (PowerService.HasBattery)
        {
            _body.Children.Add(Ux.Gap(10));
            _body.Children.Add(Battery());
        }

        // Settings shortcuts
        _body.Children.Add(Ux.Gap(12));
        var links = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Center };
        foreach (var (label, page) in new[] { ("Settings", ""), ("Network", "network"), ("Bluetooth", "bluetooth"), ("Display", "display"), ("Sound", "sound") })
        {
            var chip = Ux.Hoverable(new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(10, 4, 10, 5), Margin = new Thickness(3), Child = Ux.Text(label, 12) });
            chip.MouseLeftButtonUp += (_, _) => { Dismiss(); SystemActions.OpenSettings(page); };
            links.Children.Add(chip);
        }
        _body.Children.Add(links);
    }

    // ---- Tiles -------------------------------------------------------------------------------

    private UIElement Connectivity()
    {
        var radios = _host.Radios;
        var network = _host.Network.Current;
        var stack = new StackPanel();
        stack.Children.Add(RadioRow("", "Wi-Fi", radios.WifiOn,
            network is { Online: true, IsWifi: true } ? network.Name : radios.WifiOn == false ? "Off" : network?.Online == true ? "Ethernet" : "Not connected",
            RadioKind.WiFi, "network-wifi"));
        stack.Children.Add(Ux.Gap(12));
        stack.Children.Add(RadioRow("", "Bluetooth", radios.BluetoothOn,
            radios.BluetoothOn is null ? "Unavailable" : radios.BluetoothOn == true ? "On" : "Off",
            RadioKind.Bluetooth, "bluetooth"));
        if (network?.Vpn is { } vpn)
        {
            stack.Children.Add(Ux.Gap(12));
            stack.Children.Add(Row(Ux.RoundButton("", true, 30, () => { Dismiss(); SystemActions.OpenSettings("network-vpn"); }), "VPN", vpn).Row);
        }
        return Ux.Tile(stack, 16);
    }

    /// <summary>
    /// Real radio toggle. It flips immediately when pressed (like macOS), then Windows confirms through
    /// the radio's change event; if Windows refuses, it flips back and Settings opens.
    /// </summary>
    private UIElement RadioRow(string glyph, string title, bool? on, string status, RadioKind kind, string page)
    {
        var button = Ux.RoundButton(glyph, on == true, 30);
        var (row, statusText) = Row(button, title, status);
        bool state = on == true;
        button.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            if (on is null || !_host.Radios.CanControl)
            {
                Dismiss();
                SystemActions.OpenSettings(page);
                return;
            }
            state = !state;
            Ux.SetOn(button, state);
            statusText.Text = state ? "On" : "Off";
            if (await _host.Radios.SetAsync(kind, state)) return;
            state = !state; // refused: put it back
            Ux.SetOn(button, state);
            statusText.Text = status;
            Dismiss();
            SystemActions.OpenSettings(page);
        };
        return row;
    }

    private static (UIElement Row, TextBlock Status) Row(Border button, string title, string status)
    {
        var text = new StackPanel { Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ux.Text(title, 13, weight: FontWeights.SemiBold));
        var s = Ux.Text(status, 11.5, "Ui.TextSecondary");
        s.MaxWidth = 95;
        text.Children.Add(s);
        return (new StackPanel { Orientation = Orientation.Horizontal, Children = { button, text } }, s);
    }

    /// <summary>Do Not Disturb: a real toggle (presses Windows' own button; see SystemActions).</summary>
    private UIElement DoNotDisturb()
    {
        bool on = Native.IsDoNotDisturbOn() == true;
        var button = Ux.RoundButton("", on, 30);
        var (row, status) = Row(button, "Focus", on ? "Do Not Disturb" : "Off");
        var tile = Ux.Tile(row, 16, new Thickness(10));
        tile.Cursor = Cursors.Hand;
        bool busy = false;
        tile.MouseLeftButtonUp += async (_, _) =>
        {
            if (busy) return;
            busy = true;
            on = !on;
            Ux.SetOn(button, on);
            status.Text = on ? "Do Not Disturb" : "Off";
            CloseOnDeactivate = false; // the notification center briefly takes focus
            bool ok = await SystemActions.ToggleDoNotDisturbAsync();
            CloseOnDeactivate = true;
            if (!ok)
            {
                on = Native.IsDoNotDisturbOn() == true;
                Ux.SetOn(button, on);
                status.Text = on ? "Do Not Disturb" : "Off";
            }
            Activate();
            busy = false;
        };
        return tile;
    }

    /// <summary>Windows light/dark theme: flips immediately and changes the real Windows setting.</summary>
    private UIElement Appearance()
    {
        bool light = Theme.WindowsUsesLightTheme();
        var button = Ux.RoundButton(light ? "" : "", !light, 30);
        var (row, status) = Row(button, light ? "Light Mode" : "Dark Mode", "Windows theme");
        var title = (TextBlock)((StackPanel)((StackPanel)row).Children[1]).Children[0];
        var tile = Ux.Tile(row, 16, new Thickness(10));
        tile.Cursor = Cursors.Hand;
        tile.MouseLeftButtonUp += (_, _) =>
        {
            light = !light;
            Ux.SetOn(button, !light);
            ((TextBlock)button.Child).Text = light ? "" : "";
            title.Text = light ? "Light Mode" : "Dark Mode";
            Theme.SetWindowsLightTheme(light); // the real Windows setting
        };
        return tile;
    }

    private UIElement SliderTile(string title, string glyph, double value, Action<double> onChange, Action? onIcon = null)
    {
        var stack = new StackPanel();
        stack.Children.Add(Ux.Text(title, 13, weight: FontWeights.SemiBold));
        var icon = Icons.Glyph(glyph, 15);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        icon.Width = 22;
        if (onIcon is not null)
        {
            icon.Cursor = Cursors.Hand;
            icon.MouseLeftButtonUp += (_, _) => onIcon();
        }
        var slider = Ux.Slider(value, onChange);
        slider.Margin = new Thickness(8, 0, 0, 0);
        if (title == "Sound")
        {
            _soundSlider = slider;
            _soundIcon = icon;
        }
        var row = new DockPanel { Margin = new Thickness(0, 8, 0, 0) };
        DockPanel.SetDock(icon, Dock.Left);
        row.Children.Add(icon);
        row.Children.Add(slider);
        stack.Children.Add(row);
        return Ux.Tile(stack, 16);
    }

    private UIElement NowPlaying(MediaSnapshot media)
    {
        var art = new Border { Width = 44, Height = 44, CornerRadius = new CornerRadius(9) };
        if (media.Art is BitmapSource a) art.Background = new ImageBrush(a) { Stretch = Stretch.UniformToFill };
        else art.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");

        var text = new StackPanel { Margin = new Thickness(10, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ux.Text(media.Title, 13, weight: FontWeights.SemiBold));
        text.Children.Add(Ux.Text(media.Artist, 12, "Ui.TextSecondary"));

        var controls = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        controls.Children.Add(MediaButton("", () => _ = _host.Media.PreviousAsync()));
        controls.Children.Add(MediaButton(media.IsPlaying ? "" : "", () => _ = _host.Media.TogglePlayPauseAsync(), 18));
        controls.Children.Add(MediaButton("", () => _ = _host.Media.NextAsync()));

        var grid = new DockPanel();
        DockPanel.SetDock(art, Dock.Left);
        DockPanel.SetDock(controls, Dock.Right);
        grid.Children.Add(art);
        grid.Children.Add(controls);
        grid.Children.Add(text);
        return Ux.Tile(grid, 16);
    }

    private static UIElement MediaButton(string glyph, Action action, double size = 14)
    {
        var icon = Icons.Glyph(glyph, size);
        icon.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        var b = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(16), Child = icon, Background = Brushes.Transparent, Cursor = Cursors.Hand };
        b.MouseEnter += (_, _) => b.SetResourceReference(Border.BackgroundProperty, "Ui.Control");
        b.MouseLeave += (_, _) => b.Background = Brushes.Transparent;
        b.MouseLeftButtonUp += (_, e) => { e.Handled = true; action(); };
        return b;
    }

    private UIElement Battery()
    {
        int pct = PowerService.Percent;
        bool plugged = PowerService.IsPluggedIn;
        string status = PowerService.IsCharging ? "Charging" : plugged ? "Plugged in" :
            PowerService.TimeRemaining is { } t ? $"{(int)t.TotalHours}h {t.Minutes}m remaining" : "On battery";
        var glyph = Icons.Glyph(char.ConvertFromUtf32((plugged ? 0xEBAB : 0xEBA0) + Math.Clamp(pct / 10, 0, 10)), 20);
        glyph.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Ux.Text($"Battery  {pct}%", 13, weight: FontWeights.SemiBold));
        text.Children.Add(Ux.Text(status, 12, "Ui.TextSecondary"));
        var row = new StackPanel { Orientation = Orientation.Horizontal, Children = { glyph, text } };
        var tile = Ux.Tile(row, 16);
        tile.Cursor = Cursors.Hand;
        tile.MouseLeftButtonUp += (_, _) => { Dismiss(); SystemActions.OpenSettings("batterysaver"); };
        return tile;
    }
}
