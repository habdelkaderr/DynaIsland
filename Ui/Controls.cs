using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;

namespace DynaIsland.Ui;

/// <summary>macOS-style on/off switch.</summary>
public sealed class Switch : Border
{
    private readonly Ellipse _knob;
    private readonly TranslateTransform _slide = new();
    private bool _isOn;

    public event EventHandler<bool>? Toggled;

    public Switch(bool isOn)
    {
        Width = 38;
        Height = 22;
        CornerRadius = new CornerRadius(11);
        Cursor = Cursors.Hand;
        _knob = new Ellipse
        {
            Width = 18,
            Height = 18,
            Fill = Brushes.White,
            HorizontalAlignment = HorizontalAlignment.Left,
            Margin = new Thickness(2, 0, 0, 0),
            RenderTransform = _slide,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 4, ShadowDepth = 1, Opacity = 0.25 },
        };
        Child = _knob;
        _isOn = isOn;
        Update(animate: false);
        MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            IsOn = !IsOn;
            Toggled?.Invoke(this, IsOn);
        };
    }

    public bool IsOn
    {
        get => _isOn;
        set
        {
            if (_isOn == value) return;
            _isOn = value;
            Update(animate: true);
        }
    }

    private void Update(bool animate)
    {
        SetResourceReference(BackgroundProperty, _isOn ? "Ui.Accent" : "Ui.ControlHover");
        double x = _isOn ? 16 : 0;
        if (animate)
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(x, TimeSpan.FromMilliseconds(180)) { EasingFunction = new CubicEase() });
        else
            _slide.X = x;
    }
}

/// <summary>Row of mutually exclusive options (like a macOS segmented control).</summary>
public sealed class Segmented : Border
{
    public Segmented(IEnumerable<(string Label, string Value)> options, string selected, Action<string> onSelect)
    {
        CornerRadius = new CornerRadius(8);
        Padding = new Thickness(2);
        SetResourceReference(BackgroundProperty, "Ui.Control");
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        var buttons = new List<(Border Button, string Value)>();
        foreach (var (label, value) in options)
        {
            var text = new TextBlock { Text = label, FontSize = 12, Margin = new Thickness(12, 4, 12, 5) };
            text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
            var b = new Border { CornerRadius = new CornerRadius(6), Child = text, Cursor = Cursors.Hand, Background = Brushes.Transparent };
            b.MouseLeftButtonUp += (_, _) =>
            {
                foreach (var (other, v) in buttons) Paint(other, v == value);
                onSelect(value);
            };
            buttons.Add((b, value));
            row.Children.Add(b);
        }
        foreach (var (b, v) in buttons) Paint(b, v == selected);
        Child = row;

        static void Paint(Border b, bool on)
        {
            if (on) b.SetResourceReference(BackgroundProperty, "Ui.ControlHover");
            else b.Background = Brushes.Transparent;
        }
    }
}

/// <summary>
/// Blurred pop-up panel (menus, Control Center, Launchpad). Takes focus while open and closes when
/// you click elsewhere or press Esc, like macOS menus.
/// </summary>
public class GlassPopup : Window
{
    protected readonly Grid Root = new();
    private readonly TranslateTransform _enter = new();
    private bool _closing;

    public bool CloseOnDeactivate { get; set; } = true;
    public bool Blur { get; set; } = true;

    public GlassPopup()
    {
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        // Opened off-screen, then moved into place (no flash in the corner).
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        Root.RenderTransform = _enter;
        Content = Root;
        SetResourceReference(ForegroundProperty, "Ui.Text");

        SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            long ex = Native.GetWindowLongPtr(hwnd, Native.GWL_EXSTYLE).ToInt64();
            Native.SetWindowLongPtr(hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
            Glass.Apply(this, Theme.SurfaceTint, Blur, round: true, Theme.IsDark);
        };
        Theme.Changed += OnThemeChanged;
        Closed += (_, _) => Theme.Changed -= OnThemeChanged;
        Deactivated += (_, _) => { if (CloseOnDeactivate) Dismiss(); };
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Dismiss(); } };
    }

    private void OnThemeChanged(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (hwnd != IntPtr.Zero) Glass.SetTint(hwnd, Theme.SurfaceTint, Blur);
    }

    /// <summary>Sets the panel's content (keeps the open/close animation).</summary>
    public void SetBody(UIElement body)
    {
        Root.Children.Clear();
        Root.Children.Add(body);
    }

    /// <summary>
    /// Shows the panel anchored at a screen point in physical pixels: <paramref name="h"/>/<paramref name="v"/>
    /// say which edge (or centre) of the panel sits on the point. Kept inside that monitor's work area.
    /// </summary>
    public void ShowAt(int x, int y, HorizontalAlignment h = HorizontalAlignment.Left, VerticalAlignment v = VerticalAlignment.Top)
    {
        // Window opacity doesn't apply to non-layered (blurred) windows, so the content fades instead.
        Root.Opacity = 0;
        Show();
        UpdateLayout();
        var hwnd = new WindowInteropHelper(this).Handle;
        var monitor = Native.MonitorFromPoint(new Native.POINT { X = x, Y = y }, Native.MONITOR_DEFAULTTONEAREST);
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        int w = (int)Math.Round(ActualWidth * scale), hgt = (int)Math.Round(ActualHeight * scale);
        int left = h switch { HorizontalAlignment.Right => x - w, HorizontalAlignment.Center => x - w / 2, _ => x };
        int top = v switch { VerticalAlignment.Bottom => y - hgt, VerticalAlignment.Center => y - hgt / 2, _ => y };
        var info = new Native.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Native.MONITORINFO>() };
        if (Native.GetMonitorInfo(monitor, ref info))
        {
            var wa = info.rcWork;
            left = Math.Clamp(left, wa.Left + 4, Math.Max(wa.Left + 4, wa.Right - w - 4));
            top = Math.Clamp(top, info.rcMonitor.Top, Math.Max(info.rcMonitor.Top, info.rcMonitor.Bottom - hgt - 4));
        }
        Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, left, top, 0, 0, Native.SWP_NOSIZE);
        Native.SetForegroundWindow(hwnd);
        Activate();
        double ms = 170 / Motion.Speed;
        Root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
        _enter.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-6, 0, TimeSpan.FromMilliseconds(ms * 1.4))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        });
    }

    public void Dismiss()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110 / Motion.Speed));
        fade.Completed += (_, _) => Close();
        Root.BeginAnimation(OpacityProperty, fade);
    }
}

/// <summary>Glass dropdown menu (menu bar, dock, Launchpad). Items are plain records.</summary>
public sealed class GlassMenu : GlassPopup
{
    public sealed record Entry(string Text, Action? Action = null, string? Hint = null, bool Enabled = true,
                               bool Checked = false, bool IsSeparator = false, bool IsHeader = false)
    {
        public static Entry Separator => new("", IsSeparator: true);
        public static Entry Header(string text) => new(text, IsHeader: true, Enabled: false);
    }

    public GlassMenu(IEnumerable<Entry> entries, double minWidth = 220)
    {
        var stack = new StackPanel { Margin = new Thickness(5), MinWidth = minWidth };
        foreach (var entry in entries) stack.Children.Add(Row(entry));
        Root.Children.Add(stack);
    }

    private FrameworkElement Row(Entry entry)
    {
        if (entry.IsSeparator)
        {
            var line = new Border { Height = 1, Margin = new Thickness(8, 4, 8, 4) };
            line.SetResourceReference(BackgroundProperty, "Ui.Divider");
            return line;
        }

        var grid = new Grid { Margin = new Thickness(6, 4, 10, 5) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(18) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        if (entry.Checked)
        {
            var check = new TextBlock { Text = "", FontFamily = Icons.Font, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(check);
        }
        var text = new TextBlock
        {
            Text = entry.Text,
            FontSize = 13,
            FontWeight = entry.IsHeader ? FontWeights.SemiBold : FontWeights.Normal,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 340,
            VerticalAlignment = VerticalAlignment.Center,
        };
        text.SetResourceReference(TextBlock.ForegroundProperty, entry.Enabled || entry.IsHeader ? "Ui.Text" : "Ui.TextFaint");
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);
        if (entry.Hint is { } hint)
        {
            var h = new TextBlock { Text = hint, FontSize = 12, Margin = new Thickness(24, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            h.SetResourceReference(TextBlock.ForegroundProperty, "Ui.TextFaint");
            Grid.SetColumn(h, 2);
            grid.Children.Add(h);
        }

        var row = new Border { CornerRadius = new CornerRadius(6), Child = grid, Background = Brushes.Transparent };
        if (entry.Enabled && entry.Action is { } action)
        {
            row.MouseEnter += (_, _) => row.SetResourceReference(BackgroundProperty, "Ui.Accent");
            row.MouseEnter += (_, _) => { text.Foreground = Brushes.White; };
            row.MouseLeave += (_, _) => { row.Background = Brushes.Transparent; text.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text"); };
            row.MouseLeftButtonUp += (_, _) =>
            {
                Dismiss();
                Dispatcher.InvokeAsync(action);
            };
        }
        return row;
    }
}

/// <summary>Global animation speed (Settings > Animation speed).</summary>
public static class Motion
{
    public static double Speed { get; set; } = 1.0;
}

public static class Icons
{
    public static readonly FontFamily Font = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock Glyph(string glyph, double size = 14) =>
        new() { Text = glyph, FontFamily = Font, FontSize = size, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
}

/// <summary>Small helpers for building panels in code with theme-aware colours.</summary>
public static class Ux
{
    public static TextBlock Text(string text, double size = 13, string brush = "Ui.Text", FontWeight? weight = null)
    {
        var t = new TextBlock { Text = text, FontSize = size, FontWeight = weight ?? FontWeights.Normal, TextTrimming = TextTrimming.CharacterEllipsis };
        t.SetResourceReference(TextBlock.ForegroundProperty, brush);
        return t;
    }

    public static Border Tile(UIElement child, double radius = 14, Thickness? padding = null)
    {
        var b = new Border { CornerRadius = new CornerRadius(radius), Padding = padding ?? new Thickness(12), Child = child };
        b.SetResourceReference(Border.BackgroundProperty, "Ui.Tile");
        b.SetResourceReference(Border.BorderBrushProperty, "Ui.Stroke");
        b.BorderThickness = new Thickness(0.5);
        return b;
    }

    /// <summary>Round icon button that is filled with the accent colour when "on".</summary>
    public static Border RoundButton(string glyph, bool on, double size = 32, Action? onClick = null)
    {
        var icon = Icons.Glyph(glyph, size * 0.42);
        var b = new Border { Width = size, Height = size, CornerRadius = new CornerRadius(size / 2), Child = icon, Cursor = Cursors.Hand };
        SetOn(b, on);
        if (onClick is not null) b.MouseLeftButtonUp += (_, e) => { e.Handled = true; onClick(); };
        return b;
    }

    public static void SetOn(Border round, bool on)
    {
        round.SetResourceReference(Border.BackgroundProperty, on ? "Ui.Accent" : "Ui.ControlHover");
        if (round.Child is TextBlock icon)
        {
            if (on) icon.Foreground = Brushes.White;
            else icon.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        }
    }

    public static Slider Slider(double value, Action<double> onChange)
    {
        var s = new Slider { Minimum = 0, Maximum = 1, Value = value, Style = (Style)Application.Current.Resources["Ui.Slider"] };
        s.ValueChanged += (_, e) => onChange(e.NewValue);
        return s;
    }

    /// <summary>Hover highlight for clickable borders.</summary>
    public static T Hoverable<T>(T element, string normal = "Ui.Control") where T : Border
    {
        element.SetResourceReference(Border.BackgroundProperty, normal);
        element.MouseEnter += (_, _) => element.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
        element.MouseLeave += (_, _) => element.SetResourceReference(Border.BackgroundProperty, normal);
        element.Cursor = Cursors.Hand;
        return element;
    }

    public static Rectangle Gap(double h) => new() { Height = h };
}
