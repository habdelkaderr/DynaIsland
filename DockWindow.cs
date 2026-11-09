using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynaIsland.Services;
using DynaIsland.Ui;

namespace DynaIsland;

/// <summary>
/// macOS-style dock: Launchpad, pinned apps, running apps and the Recycle Bin, with magnification,
/// running dots, launch bounce, drag-to-reorder (drag off to remove), drops from Launchpad, glass
/// behind it (a separate blurred backdrop window) and optional auto-hide. Bottom, left or right.
/// </summary>
internal sealed class DockWindow : Window
{
    public const string DragFormat = "DynaIsland.AppId";
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";
    private const double EdgeMargin = 6;
    private const double PlatePadding = 7;
    // 256 px source icons, scaled down: sharp even when magnified on high-DPI screens.
    private const int IconPixels = 256;

    private enum Kind { Launchpad, Pinned, Running, Trash, Separator }

    private sealed class Item
    {
        public required Kind Kind;
        public required string Key;
        public string Name = "";
        public InstalledApp? App;
        public string? LaunchTarget;
        public List<AppWindow> Windows = [];
        public FrameworkElement Visual = null!;
        public Border IconBox = null!;
        public Ellipse Dot = null!;
        public TranslateTransform Shift = new();
        public double Size, Target;
    }

    private readonly Settings _settings;
    private readonly Action _openLaunchpad;
    private readonly IntPtr _hwnd;
    private readonly AppBar _appBar;
    private readonly GlassBackdrop _backdrop = new();
    private readonly EdgeTrigger _trigger = new();
    private readonly Grid _root = new();
    private readonly Border _plate;
    private readonly StackPanel _row;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly Canvas _overlay = new() { IsHitTestVisible = false };
    private readonly TranslateTransform _slide = new();
    private readonly DispatcherTimer _hideTimer;
    private readonly List<Item> _items = [];
    private readonly List<string> _runningOrder = [];

    private IntPtr _monitor;
    private bool _visibleWanted;
    private bool _hidden;          // auto-hidden (slid away)
    private bool _menuOpen;
    private bool _animating;
    private double? _pointer;      // pointer position along the dock, for magnification
    private string _signature = "";
    private Item? _hover;

    // drag-to-reorder
    private Item? _dragItem;
    private Point _dragStart;
    private bool _dragging;

    private bool Vertical => _settings.DockPosition is "Left" or "Right";
    private double IconSize => Math.Clamp(_settings.DockIconSize, 32, 80);
    private double MaxIcon => IconSize * (1 + 0.8 * Math.Clamp(_settings.DockMagnification, 0, 1));
    private double Gap => Math.Clamp(_settings.DockSpacing, 0, 20);
    private double Thickness => IconSize + 2 * PlatePadding + 6; // plate depth incl. running dot

    public DockWindow(Settings settings, Action openLaunchpad)
    {
        _settings = settings;
        _openLaunchpad = openLaunchpad;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Title = "DynaIsland dock";
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        AllowDrop = true;

        _plate = new Border
        {
            CornerRadius = new CornerRadius(9), // matches Windows' rounded corners on the glass behind
            BorderThickness = new Thickness(0.8),
            Background = new SolidColorBrush(Color.FromArgb(0x02, 0x80, 0x80, 0x80)), // hit-testable, visually clear
        };
        _plate.SetResourceReference(Border.BorderBrushProperty, "Ui.Stroke");
        _row = new StackPanel();
        _row.SizeChanged += (_, _) => FitPlate();

        _labelText = new TextBlock { FontSize = 12.5 };
        _labelText.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        _label = new Border
        {
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 4, 10, 5),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            BorderThickness = new Thickness(0.6),
        };
        _label.SetResourceReference(Border.BackgroundProperty, "Ui.Window");
        _label.SetResourceReference(Border.BorderBrushProperty, "Ui.Stroke");
        _overlay.Children.Add(_label);

        _root.RenderTransform = _slide;
        _root.Children.Add(_plate);
        _root.Children.Add(_row);
        Content = new Grid { Children = { _root, _overlay } };

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _hideTimer.Tick += (_, _) => { _hideTimer.Stop(); if (!IsMouseOver && !_menuOpen) SlideAway(); };
        _trigger.Touched += Reveal;

        MouseMove += OnMouseMove;
        MouseLeave += (_, _) =>
        {
            _pointer = null;
            SetHover(null);
            StartAnimating();
            if (_settings.DockAutoHide && !_menuOpen && !_dragging) { _hideTimer.Stop(); _hideTimer.Start(); }
        };
        MouseEnter += (_, _) => _hideTimer.Stop();
        DragOver += OnDragOver;
        Drop += OnDrop;

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _appBar = new AppBar(_hwnd, "DynaIslandDockAppBar");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Theme.Changed += (_, _) => _backdrop.SetBlur(_settings.GlassBlur);
        Closed += (_, _) =>
        {
            _appBar.Remove();
            _backdrop.Close();
            _trigger.Close();
        };
    }

    // ---- Visibility, layout & placement ------------------------------------------------------

    /// <summary>(Re)applies settings: position, sizes, auto-hide, visibility. Cheap to call.</summary>
    public void Apply(IntPtr monitor, bool visible)
    {
        _monitor = monitor;
        _visibleWanted = visible;
        if (!visible)
        {
            _appBar.Remove();
            _backdrop.Conceal();
            _trigger.Conceal();
            if (IsVisible) Hide();
            return;
        }

        if (_settings.DockPins is null) SeedPins();
        _backdrop.SetBlur(_settings.GlassBlur);
        ConfigureLayout();
        _signature = "";
        Refresh();
        if (!IsVisible) Show();
        _hidden = false;
        _slide.BeginAnimation(TranslateTransform.XProperty, null);
        _slide.BeginAnimation(TranslateTransform.YProperty, null);
        _slide.X = _slide.Y = 0;
        Place();
        if (_settings.DockAutoHide) { _hideTimer.Stop(); _hideTimer.Start(); }
    }

    private void ConfigureLayout()
    {
        _row.Orientation = Vertical ? Orientation.Vertical : Orientation.Horizontal;
        var (h, v) = _settings.DockPosition switch
        {
            "Left" => (HorizontalAlignment.Left, VerticalAlignment.Center),
            "Right" => (HorizontalAlignment.Right, VerticalAlignment.Center),
            _ => (HorizontalAlignment.Center, VerticalAlignment.Bottom),
        };
        foreach (var el in new FrameworkElement[] { _plate, _row })
        {
            el.HorizontalAlignment = h;
            el.VerticalAlignment = v;
        }
        double m = EdgeMargin;
        _plate.Margin = _settings.DockPosition switch
        {
            "Left" => new Thickness(m, 0, 0, 0),
            "Right" => new Thickness(0, 0, m, 0),
            _ => new Thickness(0, 0, 0, m),
        };
        double inset = m + PlatePadding;
        _row.Margin = _settings.DockPosition switch
        {
            "Left" => new Thickness(inset - 2, 0, 0, 0),
            "Right" => new Thickness(0, 0, inset - 2, 0),
            _ => new Thickness(0, 0, 0, inset - 2),
        };
    }

    private (Native.RECT Monitor, double Scale) MonitorInfo()
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(_monitor, ref info);
        double scale = Native.GetDpiForMonitor(_monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        return (info.rcMonitor, scale);
    }

    /// <summary>Sizes the window along its edge and reserves space (unless auto-hiding).</summary>
    private void Place()
    {
        if (_monitor == IntPtr.Zero) return;
        var (rc, scale) = MonitorInfo();
        int depth = (int)Math.Round((MaxIcon + PlatePadding * 2 + EdgeMargin + (Vertical ? 190 : 56)) * scale);
        int reserve = (int)Math.Round((Thickness + EdgeMargin + 2) * scale);
        uint edge = _settings.DockPosition switch { "Left" => AppBar.ABE_LEFT, "Right" => AppBar.ABE_RIGHT, _ => AppBar.ABE_BOTTOM };

        // The strip the dock sits against: the screen edge, or beside the Windows taskbar when it's visible.
        Native.RECT bar;
        if (_settings.DockReserveSpace && !_settings.DockAutoHide)
        {
            bar = _appBar.Reserve(edge, rc, reserve);
        }
        else
        {
            _appBar.Remove();
            bar = rc;
            var (tb, tbEdge) = AppBar.TaskbarPosition();
            bool sameMonitor = tb.Left >= rc.Left && tb.Right <= rc.Right && tb.Top >= rc.Top && tb.Bottom <= rc.Bottom;
            if (sameMonitor && tbEdge == edge && !AppBar.TaskbarAutoHide)
            {
                if (edge == AppBar.ABE_BOTTOM) bar.Bottom = tb.Top;
                else if (edge == AppBar.ABE_LEFT) bar.Left = tb.Right;
                else if (edge == AppBar.ABE_RIGHT) bar.Right = tb.Left;
            }
        }

        int x, y, w, h;
        switch (_settings.DockPosition)
        {
            case "Left": x = bar.Left; y = rc.Top; w = depth; h = rc.Bottom - rc.Top; break;
            case "Right": x = bar.Right - depth; y = rc.Top; w = depth; h = rc.Bottom - rc.Top; break;
            default: x = rc.Left; y = bar.Bottom - depth; w = rc.Right - rc.Left; h = depth; break;
        }
        Width = w / scale;
        Height = h / scale;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, w, h, Native.SWP_NOACTIVATE);
        Dispatcher.InvokeAsync(PlaceBackdrop, DispatcherPriority.Loaded);

        // Auto-hide trigger strip along the edge (2 px).
        if (_settings.DockAutoHide)
        {
            var r = _settings.DockPosition switch
            {
                "Left" => new Int32Rect(rc.Left, rc.Top, 2, rc.Bottom - rc.Top),
                "Right" => new Int32Rect(rc.Right - 2, rc.Top, 2, rc.Bottom - rc.Top),
                _ => new Int32Rect(rc.Left + (rc.Right - rc.Left) / 4, rc.Bottom - 2, (rc.Right - rc.Left) / 2, 2),
            };
            _trigger.Place(r);
        }
        else
        {
            _trigger.Conceal();
        }
    }

    private void FitPlate()
    {
        double along = (Vertical ? _row.ActualHeight : _row.ActualWidth) + PlatePadding * 2;
        if (Vertical)
        {
            _plate.Height = along;
            _plate.Width = Thickness;
        }
        else
        {
            _plate.Width = along;
            _plate.Height = Thickness;
        }
        PlaceBackdrop();
    }

    /// <summary>Keeps the blurred backdrop exactly under the plate (physical pixels).</summary>
    private void PlaceBackdrop()
    {
        if (!IsVisible || _hidden || _plate.ActualWidth <= 0) { _backdrop.Conceal(); return; }
        try
        {
            var topLeft = _plate.PointToScreen(new Point(0, 0));
            var bottomRight = _plate.PointToScreen(new Point(_plate.ActualWidth, _plate.ActualHeight));
            _backdrop.Place(new Int32Rect((int)topLeft.X, (int)topLeft.Y,
                (int)(bottomRight.X - topLeft.X), (int)(bottomRight.Y - topLeft.Y)), _hwnd);
        }
        catch (InvalidOperationException)
        {
            // not connected to a presentation source yet
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == (int)_appBar.CallbackMessage && wParam.ToInt32() == AppBar.ABN_POSCHANGED && _visibleWanted)
            Dispatcher.InvokeAsync(Place);
        return IntPtr.Zero;
    }

    /// <summary>Raises the dock (and its glass) above other topmost windows, e.g. over Launchpad for drag-to-pin.</summary>
    public void BringToFront()
    {
        if (!IsVisible) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
        PlaceBackdrop();
        Reveal();
    }

    // ---- Auto-hide ---------------------------------------------------------------------------

    private void SlideAway()
    {
        if (_hidden || !_settings.DockAutoHide) return;
        _hidden = true;
        SetHover(null);
        _backdrop.Conceal();
        double d = Thickness + EdgeMargin + 12;
        var (prop, to) = _settings.DockPosition switch
        {
            "Left" => (TranslateTransform.XProperty, -d),
            "Right" => (TranslateTransform.XProperty, d),
            _ => (TranslateTransform.YProperty, d),
        };
        _slide.BeginAnimation(prop, new DoubleAnimation(to, TimeSpan.FromMilliseconds(220 / Motion.Speed)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } });
    }

    private void Reveal()
    {
        if (!_hidden) return;
        _hidden = false;
        var prop = Vertical ? TranslateTransform.XProperty : TranslateTransform.YProperty;
        var anim = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260 / Motion.Speed)) { EasingFunction = new BackEase { Amplitude = 0.25, EasingMode = EasingMode.EaseOut } };
        anim.Completed += (_, _) => PlaceBackdrop();
        _slide.BeginAnimation(prop, anim);
        _hideTimer.Stop();
        _hideTimer.Start(); // hides again if the pointer doesn't move onto the dock
    }

    // ---- Model -------------------------------------------------------------------------------

    /// <summary>First run: File Explorer, the apps pinned to the Windows taskbar, then Settings.</summary>
    private void SeedPins()
    {
        var pins = new List<string>();
        void Add(InstalledApp? app)
        {
            if (app is not null && !pins.Contains(app.Id, StringComparer.OrdinalIgnoreCase)) pins.Add(app.Id);
        }

        Add(AppCatalog.FindByName("File Explorer"));
        try
        {
            string taskbar = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (System.IO.Directory.Exists(taskbar))
                foreach (var lnk in System.IO.Directory.GetFiles(taskbar, "*.lnk"))
                    Add(AppCatalog.FindByName(System.IO.Path.GetFileNameWithoutExtension(lnk)));
        }
        catch (Exception ex)
        {
            Log.Error("dock seed", ex);
        }
        Add(AppCatalog.FindByName("Settings"));
        _settings.DockPins = pins;
        _settings.Save();
    }

    /// <summary>Rebuilds the item list from pins + running windows. Called on window events, not on a timer.</summary>
    public void Refresh()
    {
        if (!_visibleWanted) return;
        List<AppWindow> windows;
        try { windows = WindowTracker.Snapshot(); }
        catch (Exception ex) { Log.Error("dock windows", ex); return; }

        var entries = new List<Item> { new() { Kind = Kind.Launchpad, Key = "launchpad", Name = "Launchpad" } };
        var claimed = new HashSet<IntPtr>();
        foreach (var id in _settings.DockPins ?? [])
        {
            if (AppCatalog.Find(id) is not { } app) continue;
            var mine = windows.Where(w => AppCatalog.Owns(app, w)).ToList();
            foreach (var w in mine) claimed.Add(w.Handle);
            entries.Add(new Item { Kind = Kind.Pinned, Key = "pin|" + id, Name = app.Name, App = app, LaunchTarget = app.LaunchPath, Windows = mine });
        }

        var running = windows.Where(w => !claimed.Contains(w.Handle))
            .GroupBy(w => (AppCatalog.ForWindow(w)?.Id ?? w.Aumid ?? w.Exe ?? w.Handle.ToString()).ToLowerInvariant())
            .ToList();
        foreach (var g in running) if (!_runningOrder.Contains(g.Key)) _runningOrder.Add(g.Key);
        _runningOrder.RemoveAll(k => running.All(g => g.Key != k));
        if (running.Count > 0) entries.Add(new Item { Kind = Kind.Separator, Key = "sep|running" });
        foreach (var key in _runningOrder)
        {
            var group = running.First(g => g.Key == key).ToList();
            var app = AppCatalog.ForWindow(group[0]);
            entries.Add(new Item
            {
                Kind = Kind.Running,
                Key = "run|" + key,
                Name = app?.Name ?? AppName(group[0]),
                App = app,
                LaunchTarget = app?.LaunchPath ?? group[0].Exe,
                Windows = group,
            });
        }
        entries.Add(new Item { Kind = Kind.Separator, Key = "sep|trash" });
        entries.Add(new Item { Kind = Kind.Trash, Key = "trash", Name = "Recycle Bin", LaunchTarget = "shell:RecycleBinFolder" });

        string signature = string.Join("\n", entries.Select(e => e.Key));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(entries);
        }
        else
        {
            for (int i = 0; i < entries.Count; i++) _items[i].Windows = entries[i].Windows;
        }

        foreach (var item in _items.Where(i => i.Kind is Kind.Pinned or Kind.Running))
            item.Dot.Visibility = item.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private static string AppName(AppWindow w)
    {
        try
        {
            if (w.Exe is not null)
            {
                string? description = FileVersionInfo.GetVersionInfo(w.Exe).FileDescription;
                return string.IsNullOrWhiteSpace(description) ? System.IO.Path.GetFileNameWithoutExtension(w.Exe) : description.Trim();
            }
        }
        catch
        {
            // fall through to the title
        }
        return w.Title;
    }

    // ---- Visuals -----------------------------------------------------------------------------

    private void Rebuild(List<Item> entries)
    {
        _row.Children.Clear();
        _items.Clear();
        foreach (var item in entries)
        {
            item.Size = item.Target = IconSize;
            item.Visual = item.Kind == Kind.Separator ? Separator() : AppVisual(item);
            _row.Children.Add(item.Visual);
            _items.Add(item);
        }
        ApplySizes();
    }

    private FrameworkElement Separator()
    {
        var line = new Border
        {
            Width = Vertical ? IconSize - 8 : 1,
            Height = Vertical ? 1 : IconSize - 8,
            Margin = Vertical ? new Thickness(0, Gap + 2, 0, Gap + 2) : new Thickness(Gap + 2, 0, Gap + 2, 0),
        };
        line.SetResourceReference(Border.BackgroundProperty, "Ui.Divider");
        return line;
    }

    private FrameworkElement AppVisual(Item item)
    {
        Brush? fill = null;
        try
        {
            BitmapSource? icon = item.Kind switch
            {
                Kind.Launchpad => null,
                Kind.Trash => ShellApps.Icon(RecycleBin, IconPixels),
                _ => item.App?.Icon(IconPixels) ?? (item.LaunchTarget is { } t ? ShellApps.Icon(t, IconPixels) : null),
            };
            fill = item.Kind == Kind.Launchpad ? LaunchpadIcon() : icon is null ? null : new ImageBrush(icon) { Stretch = Stretch.Uniform };
        }
        catch (Exception ex)
        {
            Log.Error("dock icon", ex);
        }

        item.IconBox = new Border { Width = IconSize, Height = IconSize, CornerRadius = new CornerRadius(fill is null ? IconSize * 0.22 : 0) };
        if (fill is null) item.IconBox.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
        else item.IconBox.Background = fill;
        RenderOptions.SetBitmapScalingMode(item.IconBox, BitmapScalingMode.HighQuality);

        item.Dot = new Ellipse { Width = 4, Height = 4, Visibility = Visibility.Hidden };
        item.Dot.SetResourceReference(Shape.FillProperty, "Ui.Text");

        var panel = new DockPanel { LastChildFill = false, Background = Brushes.Transparent, Cursor = Cursors.Hand, RenderTransform = item.Shift };
        var dotSide = _settings.DockPosition switch { "Left" => Dock.Left, "Right" => Dock.Right, _ => Dock.Bottom };
        DockPanel.SetDock(item.Dot, dotSide);
        item.Dot.Margin = _settings.DockPosition switch
        {
            "Left" => new Thickness(0, 0, 2, 0),
            "Right" => new Thickness(2, 0, 0, 0),
            _ => new Thickness(0, 2, 0, 0),
        };
        item.Dot.HorizontalAlignment = HorizontalAlignment.Center;
        item.Dot.VerticalAlignment = VerticalAlignment.Center;
        DockPanel.SetDock(item.IconBox, dotSide);
        panel.Children.Add(item.Dot);
        panel.Children.Add(item.IconBox);
        panel.Margin = Vertical ? new Thickness(0, Gap / 2, 0, Gap / 2) : new Thickness(Gap / 2, 0, Gap / 2, 0);
        panel.HorizontalAlignment = _settings.DockPosition switch { "Left" => HorizontalAlignment.Left, "Right" => HorizontalAlignment.Right, _ => HorizontalAlignment.Center };
        panel.VerticalAlignment = Vertical ? VerticalAlignment.Center : VerticalAlignment.Bottom;

        panel.MouseEnter += (_, _) => SetHover(item);
        panel.MouseLeftButtonDown += (_, e) => { _dragItem = item; _dragStart = e.GetPosition(this); _dragging = false; panel.CaptureMouse(); e.Handled = true; };
        panel.MouseMove += (_, e) => OnItemDrag(item, e);
        panel.MouseLeftButtonUp += (_, e) =>
        {
            panel.ReleaseMouseCapture();
            e.Handled = true;
            if (_dragging) EndDrag(item, e.GetPosition(this));
            else if (_dragItem == item) OnClick(item);
            _dragItem = null;
            _dragging = false;
        };
        panel.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(item); };
        return panel;
    }

    /// <summary>Launchpad icon: a grid of coloured tiles, drawn (no bundled image).</summary>
    private static Brush LaunchpadIcon()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(new LinearGradientBrush(Color.FromRgb(0x3A, 0x3A, 0x40), Color.FromRgb(0x1C, 0x1C, 0x20), 90), null,
            new RectangleGeometry(new Rect(0, 0, 100, 100), 22, 22)));
        Color[] colors =
        [
            Color.FromRgb(0xFF, 0x45, 0x3A), Color.FromRgb(0xFF, 0x9F, 0x0A), Color.FromRgb(0xFF, 0xD6, 0x0A),
            Color.FromRgb(0x30, 0xD1, 0x58), Color.FromRgb(0x64, 0xD2, 0xFF), Color.FromRgb(0x0A, 0x84, 0xFF),
            Color.FromRgb(0x5E, 0x5C, 0xE6), Color.FromRgb(0xBF, 0x5A, 0xF2), Color.FromRgb(0xFF, 0x37, 0x5F),
        ];
        for (int i = 0; i < 9; i++)
        {
            double x = 19 + (i % 3) * 22, y = 19 + (i / 3) * 22;
            group.Children.Add(new GeometryDrawing(new SolidColorBrush(colors[i]), null, new RectangleGeometry(new Rect(x, y, 18, 18), 5, 5)));
        }
        var brush = new DrawingBrush(group) { Stretch = Stretch.Uniform };
        brush.Freeze();
        return brush;
    }

    private void SetHover(Item? item)
    {
        _hover = item;
        if (item is null || _dragging)
        {
            _label.Visibility = Visibility.Collapsed;
            return;
        }
        _labelText.Text = item.Windows.Count > 1 ? $"{item.Name}  ({item.Windows.Count})" : item.Name;
        _label.Visibility = Visibility.Visible;
        PlaceLabel();
    }

    private void PlaceLabel()
    {
        if (_hover is null || _label.Visibility != Visibility.Visible) return;
        _label.UpdateLayout();
        var box = _hover.IconBox;
        switch (_settings.DockPosition)
        {
            case "Left":
                var l = box.TranslatePoint(new Point(box.ActualWidth, box.ActualHeight / 2), _overlay);
                Canvas.SetLeft(_label, l.X + 14);
                Canvas.SetTop(_label, l.Y - _label.ActualHeight / 2);
                break;
            case "Right":
                var r = box.TranslatePoint(new Point(0, box.ActualHeight / 2), _overlay);
                Canvas.SetLeft(_label, r.X - _label.ActualWidth - 14);
                Canvas.SetTop(_label, r.Y - _label.ActualHeight / 2);
                break;
            default:
                var t = box.TranslatePoint(new Point(box.ActualWidth / 2, 0), _overlay);
                Canvas.SetLeft(_label, t.X - _label.ActualWidth / 2);
                Canvas.SetTop(_label, t.Y - _label.ActualHeight - 10);
                break;
        }
    }

    // ---- Magnification (spring-smoothed) -----------------------------------------------------

    private void OnMouseMove(object sender, MouseEventArgs e)
    {
        if (_dragging || _settings.DockMagnification <= 0.01) return;
        var p = e.GetPosition(_row);
        _pointer = Vertical ? p.Y : p.X;
        StartAnimating();
    }

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Targets from the unmagnified layout so icons don't chase the pointer as the row grows.
        double ItemLength(Item i) => i.Kind == Kind.Separator ? 1 + 2 * (Gap + 2) : IconSize + Gap;
        double baseLength = _items.Sum(ItemLength);
        double current = Vertical ? _row.ActualHeight : _row.ActualWidth;
        double offset = (current - baseLength) / 2;
        double pos = 0;
        double range = IconSize * 2.6;
        foreach (var item in _items)
        {
            double len = ItemLength(item);
            if (item.Kind != Kind.Separator)
            {
                double t = 0;
                if (_pointer is double p)
                {
                    double d = Math.Abs(p - offset - (pos + len / 2));
                    if (d < range) t = (1 + Math.Cos(Math.PI * d / range)) / 2;
                }
                item.Target = IconSize + (MaxIcon - IconSize) * t;
            }
            pos += len;
        }

        bool moving = false;
        double k = Math.Min(1, 0.28 * Motion.Speed);
        foreach (var item in _items.Where(i => i.Kind != Kind.Separator))
        {
            double delta = item.Target - item.Size;
            if (Math.Abs(delta) > 0.2) moving = true;
            item.Size += delta * k;
        }
        ApplySizes();
        PlaceLabel();

        if (!moving && _pointer is null)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void ApplySizes()
    {
        foreach (var item in _items.Where(i => i.Kind != Kind.Separator))
            item.IconBox.Width = item.IconBox.Height = item.Size;
    }

    // ---- Drag to reorder / remove, drops from Launchpad --------------------------------------

    private void OnItemDrag(Item item, MouseEventArgs e)
    {
        if (_dragItem != item || item.Kind != Kind.Pinned || e.LeftButton != MouseButtonState.Pressed) return;
        var delta = e.GetPosition(this) - _dragStart;
        if (!_dragging && delta.Length < 8) return;
        if (!_dragging)
        {
            _dragging = true;
            _pointer = null;
            SetHover(null);
            item.Visual.Opacity = 0.75;
            Panel.SetZIndex(item.Visual, 10);
        }
        item.Shift.X = delta.X;
        item.Shift.Y = delta.Y;
        double away = Perpendicular(delta);
        item.Visual.Opacity = away > IconSize * 1.4 ? 0.35 : 0.75; // hint: release here to remove
    }

    private double Perpendicular(Vector delta) => _settings.DockPosition switch
    {
        "Left" => delta.X,
        "Right" => -delta.X,
        _ => -delta.Y,
    };

    private void EndDrag(Item item, Point drop)
    {
        var delta = drop - _dragStart;
        item.Shift.X = item.Shift.Y = 0;
        item.Visual.Opacity = 1;
        Panel.SetZIndex(item.Visual, 0);
        var pins = _settings.DockPins ??= [];
        if (item.App is null) return;

        if (Perpendicular(delta) > IconSize * 1.4)
        {
            pins.RemoveAll(p => string.Equals(p, item.App.Id, StringComparison.OrdinalIgnoreCase)); // dragged off the dock
        }
        else
        {
            int target = PinIndexAt(drop);
            int from = pins.FindIndex(p => string.Equals(p, item.App.Id, StringComparison.OrdinalIgnoreCase));
            if (from >= 0)
            {
                pins.RemoveAt(from);
                pins.Insert(Math.Clamp(target > from ? target - 1 : target, 0, pins.Count), item.App.Id);
            }
        }
        _settings.Save();
        _signature = "";
        Refresh();
    }

    /// <summary>Pin slot under a point in window coordinates (insertion index).</summary>
    private int PinIndexAt(Point p)
    {
        int index = 0;
        foreach (var item in _items.Where(i => i.Kind == Kind.Pinned))
        {
            var center = item.Visual.TranslatePoint(new Point(item.Visual.ActualWidth / 2, item.Visual.ActualHeight / 2), this);
            if ((Vertical ? p.Y : p.X) > (Vertical ? center.Y : center.X)) index++;
        }
        return index;
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DragFormat) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        Reveal();
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DragFormat) is not string id) return;
        PinApp(id, PinIndexAt(e.GetPosition(this)));
    }

    public void PinApp(string id, int index = int.MaxValue)
    {
        var pins = _settings.DockPins ??= [];
        pins.RemoveAll(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));
        pins.Insert(Math.Clamp(index, 0, pins.Count), id);
        _settings.Save();
        _signature = "";
        Refresh();
    }

    public bool IsPinned(string id) => _settings.DockPins?.Contains(id, StringComparer.OrdinalIgnoreCase) == true;

    public void Unpin(string id)
    {
        _settings.DockPins?.RemoveAll(p => string.Equals(p, id, StringComparison.OrdinalIgnoreCase));
        _settings.Save();
        _signature = "";
        Refresh();
    }

    // ---- Actions -----------------------------------------------------------------------------

    private void OnClick(Item item)
    {
        switch (item.Kind)
        {
            case Kind.Launchpad:
                _openLaunchpad();
                return;
            case Kind.Trash:
                ShellApps.Start("explorer.exe", "shell:RecycleBinFolder");
                return;
        }

        if (item.Windows.Count == 0)
        {
            Launch(item);
            return;
        }

        var foreground = Native.GetForegroundWindow();
        int front = item.Windows.FindIndex(w => w.Handle == foreground);
        if (front < 0)
        {
            // Restore the app: bring all its windows forward, most recent last so it ends on top.
            for (int i = item.Windows.Count - 1; i >= 0; i--) WindowTracker.Activate(item.Windows[i].Handle);
        }
        else if (item.Windows.Count > 1)
        {
            WindowTracker.Activate(item.Windows[(front + 1) % item.Windows.Count].Handle); // cycle windows
        }
        else
        {
            Native.ShowWindow(foreground, 6 /* SW_MINIMIZE */);
        }
    }

    private void Launch(Item item)
    {
        if (item.App is { } app) AppCatalog.Launch(app, _settings);
        else if (item.LaunchTarget is { } target) ShellApps.Start(target);
        Bounce(item);
    }

    private static void Bounce(Item item)
    {
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100 / Motion.Speed) };
        var up = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var down = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-20, KeyTime.FromPercent(0.18), up));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.38), down));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromPercent(0.56), up));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.74), down));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-5, KeyTime.FromPercent(0.86), up));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), down));
        item.Shift.BeginAnimation(TranslateTransform.YProperty, bounce);
    }

    private void ShowMenu(Item item)
    {
        var entries = new List<GlassMenu.Entry> { GlassMenu.Entry.Header(item.Name) };
        switch (item.Kind)
        {
            case Kind.Launchpad:
                entries.Add(new("Open Launchpad", _openLaunchpad));
                break;
            case Kind.Trash:
                entries.Add(new("Open", () => ShellApps.Start("explorer.exe", "shell:RecycleBinFolder")));
                entries.Add(new("Empty Recycle Bin…", () => SHEmptyRecycleBin(IntPtr.Zero, null, 0))); // Windows asks to confirm
                break;
            default:
                if (item.Windows.Count > 0)
                {
                    entries.Add(GlassMenu.Entry.Separator);
                    foreach (var w in item.Windows.Take(10))
                    {
                        var handle = w.Handle;
                        string title = Native.WindowTitle(handle);
                        entries.Add(new(title.Length > 50 ? title[..50] + "…" : title, () => WindowTracker.Activate(handle)));
                    }
                }
                entries.Add(GlassMenu.Entry.Separator);
                if (item.LaunchTarget is not null)
                    entries.Add(new(item.Windows.Count > 0 ? "New Window" : "Open", () => Launch(item)));
                if (item.App is { } app)
                {
                    entries.Add(IsPinned(app.Id)
                        ? new("Remove from Dock", () => Unpin(app.Id))
                        : new("Keep in Dock", () => PinApp(app.Id)));
                }
                if (item.Windows.Count > 0)
                {
                    entries.Add(new("Minimize All", () => { foreach (var w in item.Windows) Native.ShowWindow(w.Handle, 6); }));
                    entries.Add(new("Quit", () => { foreach (var w in item.Windows) PostMessage(w.Handle, 0x0010 /* WM_CLOSE */, IntPtr.Zero, IntPtr.Zero); }));
                }
                break;
        }

        var menu = new GlassMenu(entries, 200) { Blur = _settings.GlassBlur };
        _menuOpen = true;
        _hideTimer.Stop();
        menu.Closed += (_, _) =>
        {
            _menuOpen = false;
            if (_settings.DockAutoHide && !IsMouseOver) _hideTimer.Start();
        };
        var anchor = item.IconBox.PointToScreen(new Point(item.IconBox.ActualWidth / 2, 0));
        switch (_settings.DockPosition)
        {
            case "Left":
                var l = item.IconBox.PointToScreen(new Point(item.IconBox.ActualWidth + 8, item.IconBox.ActualHeight / 2));
                menu.ShowAt((int)l.X, (int)l.Y, HorizontalAlignment.Left, VerticalAlignment.Center);
                break;
            case "Right":
                var r = item.IconBox.PointToScreen(new Point(-8, item.IconBox.ActualHeight / 2));
                menu.ShowAt((int)r.X, (int)r.Y, HorizontalAlignment.Right, VerticalAlignment.Center);
                break;
            default:
                menu.ShowAt((int)anchor.X, (int)anchor.Y - 10, HorizontalAlignment.Center, VerticalAlignment.Bottom);
                break;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
}
