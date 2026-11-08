using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DynaIsland.Services;
using Drawing = System.Drawing;
using Forms = System.Windows.Forms;

namespace DynaIsland;

/// <summary>
/// macOS-style dock at the bottom of the screen: pinned apps, running apps, Recycle Bin, hover
/// magnification, running dots, launch bounce. Registers as an app bar so maximized windows stop above it.
/// </summary>
internal sealed class DockWindow : Window
{
    private const double BaseIcon = 48;
    private const double MaxIcon = 78;
    private const double ItemGap = 6;
    private const double PlateHeight = 66;
    private const double WindowHeight = 160;
    private const double BottomMargin = 6;
    private const double ReserveDip = PlateHeight + BottomMargin + 4;
    private static readonly string PinFolder = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DynaIsland", "DockPins");
    private const string RecycleBin = "::{645FF040-5081-101B-9F08-00AA002F954E}";

    private sealed class Item
    {
        public required string Key;
        public required string Name;
        public DockPin? Pin;
        public bool IsSeparator, IsTrash;
        public List<AppWindow> Windows = [];
        public string? LaunchTarget;
        public string? Aumid, Exe;
        public FrameworkElement Visual = null!;
        public Border IconBox = null!;
        public Ellipse Dot = null!;
        public TranslateTransform Lift = new();
        public double Size = BaseIcon, Target = BaseIcon;
        public int CycleIndex;
    }

    private readonly Settings _settings;
    private readonly Action _saveSettings;
    private readonly Border _plate;
    private readonly StackPanel _row;
    private readonly Border _label;
    private readonly TextBlock _labelText;
    private readonly Canvas _overlay;
    private readonly DispatcherTimer _refresh;
    private readonly List<Item> _items = [];
    private readonly List<string> _runningOrder = [];
    private Item? _hoverItem;
    private IntPtr _hwnd;
    private IntPtr _monitor;
    private uint _appBarMessage;
    private bool _appBarRegistered;
    private bool _animating;
    private double? _mouseX;
    private string _signature = "";

    public DockWindow(Settings settings, Action saveSettings)
    {
        _settings = settings;
        _saveSettings = saveSettings;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        Title = "DynaIsland dock";
        Height = WindowHeight;
        Width = 1200;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");

        _plate = new Border
        {
            Height = PlateHeight,
            CornerRadius = new CornerRadius(20),
            Background = new SolidColorBrush(Color.FromArgb(0xC8, 0x1E, 0x1E, 0x22)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.8),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, BottomMargin),
            Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.45, Color = Colors.Black },
        };
        _row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, 0, BottomMargin + 5),
        };
        _row.SizeChanged += (_, _) => _plate.Width = _row.ActualWidth + 20;

        _labelText = new TextBlock { Foreground = Brushes.White, FontSize = 12.5 };
        _label = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x2A, 0x2A, 0x2E)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(0.6),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(10, 4, 10, 5),
            Child = _labelText,
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
        };
        _overlay = new Canvas { IsHitTestVisible = false };
        _overlay.Children.Add(_label);

        var root = new Grid();
        root.Children.Add(_plate);
        root.Children.Add(_row);
        root.Children.Add(_overlay);
        Content = root;

        MouseMove += (_, e) =>
        {
            if (!_settings.DockMagnify) return;
            _mouseX = e.GetPosition(_row).X;
            StartAnimating();
        };
        MouseLeave += (_, _) =>
        {
            _mouseX = null;
            SetHover(null);
            StartAnimating();
        };

        _refresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _refresh.Tick += (_, _) => Refresh();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));
        _appBarMessage = Native.RegisterWindowMessage("DynaIslandDockAppBar");
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Closed += (_, _) => RemoveAppBar();
    }

    // ---- Visibility & placement --------------------------------------------------------------

    /// <summary>Shows or hides the dock according to the settings (and full-screen suppression).</summary>
    public void Apply(IntPtr monitor, bool suppressed)
    {
        _monitor = monitor;
        bool show = _settings.ShowDock && !suppressed;
        if (show)
        {
            if (_settings.DockPins is null) SeedPins();
            Refresh();
            if (!IsVisible) Show();
            Place();
            _refresh.Start();
        }
        else
        {
            _refresh.Stop();
            RemoveAppBar();
            if (IsVisible) Hide();
        }
    }

    private void Place()
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (_monitor == IntPtr.Zero || !Native.GetMonitorInfo(_monitor, ref info)) return;
        uint dpi = Native.GetDpiForMonitor(_monitor, 0, out uint dpiX, out _) == 0 ? dpiX : 96;
        double scale = dpi / 96.0;
        var rc = info.rcMonitor;
        Width = (rc.Right - rc.Left) / scale;
        int heightPx = (int)Math.Round(WindowHeight * scale);
        int bottom = info.rcWork.Bottom; // above the Windows taskbar (or the screen edge when it auto-hides)

        if (_settings.DockReserveSpace)
        {
            var data = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd, uCallbackMessage = _appBarMessage };
            if (!_appBarRegistered)
            {
                Native.SHAppBarMessage(Native.ABM_NEW, ref data);
                _appBarRegistered = true;
            }
            int reserve = (int)Math.Round(ReserveDip * scale);
            data.uEdge = Native.ABE_BOTTOM;
            data.rc = new Native.RECT { Left = rc.Left, Right = rc.Right, Top = rc.Bottom - reserve, Bottom = rc.Bottom };
            Native.SHAppBarMessage(Native.ABM_QUERYPOS, ref data); // moves it above the Windows taskbar if needed
            data.rc.Top = data.rc.Bottom - reserve;
            Native.SHAppBarMessage(Native.ABM_SETPOS, ref data);
            bottom = data.rc.Bottom;
        }
        else
        {
            RemoveAppBar();
        }

        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rc.Left, bottom - heightPx, (int)(rc.Right - rc.Left), heightPx,
            Native.SWP_NOACTIVATE);
    }

    private void RemoveAppBar()
    {
        if (!_appBarRegistered) return;
        var data = new Native.APPBARDATA { cbSize = Marshal.SizeOf<Native.APPBARDATA>(), hWnd = _hwnd };
        Native.SHAppBarMessage(Native.ABM_REMOVE, ref data);
        _appBarRegistered = false;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _appBarMessage && wParam.ToInt32() == Native.ABN_POSCHANGED && IsVisible) Dispatcher.InvokeAsync(Place);
        return IntPtr.Zero;
    }

    // ---- Model -------------------------------------------------------------------------------

    /// <summary>First run: start with the apps pinned to the Windows taskbar.</summary>
    private void SeedPins()
    {
        var pins = new List<DockPin>();
        try
        {
            Directory.CreateDirectory(PinFolder);
            string taskbar = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                @"Microsoft\Internet Explorer\Quick Launch\User Pinned\TaskBar");
            if (Directory.Exists(taskbar))
            {
                foreach (var lnk in Directory.GetFiles(taskbar, "*.lnk"))
                {
                    // Keep our own copy so unpinning from the Windows taskbar doesn't break the dock.
                    string copy = System.IO.Path.Combine(PinFolder, System.IO.Path.GetFileName(lnk));
                    File.Copy(lnk, copy, overwrite: true);
                    var (target, aumid) = ShellApps.Shortcut(copy);
                    pins.Add(new DockPin(System.IO.Path.GetFileNameWithoutExtension(lnk), copy, aumid, target));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("dock seed", ex);
        }

        // The taskbar's File Explorer pin is a special shortcut without an exe target, so match it by name too.
        if (!pins.Any(p => p.Exe?.EndsWith("explorer.exe", StringComparison.OrdinalIgnoreCase) == true ||
                           string.Equals(p.Name, "File Explorer", StringComparison.OrdinalIgnoreCase)))
        {
            string explorer = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
            pins.Insert(0, new DockPin("File Explorer", explorer, null, explorer));
        }
        _settings.DockPins = pins;
        _saveSettings();
    }

    private void Refresh()
    {
        List<AppWindow> windows;
        try { windows = WindowTracker.Snapshot(); }
        catch (Exception ex) { Log.Error("dock windows", ex); return; }

        var entries = new List<Item>();
        var claimed = new HashSet<IntPtr>();
        foreach (var pin in _settings.DockPins ?? [])
        {
            var mine = windows.Where(w => WindowTracker.Matches(w, pin.Aumid, pin.Exe)).ToList();
            foreach (var w in mine) claimed.Add(w.Handle);
            entries.Add(new Item { Key = "pin|" + pin.Target, Name = pin.Name, Pin = pin, Windows = mine, LaunchTarget = pin.Target, Aumid = pin.Aumid, Exe = pin.Exe });
        }

        var running = windows.Where(w => !claimed.Contains(w.Handle)).GroupBy(w => w.Key).ToList();
        foreach (var g in running) if (!_runningOrder.Contains(g.Key)) _runningOrder.Add(g.Key);
        _runningOrder.RemoveAll(k => running.All(g => g.Key != k));
        if (running.Count > 0) entries.Add(new Item { Key = "sep|running", Name = "", IsSeparator = true });
        foreach (var key in _runningOrder)
        {
            var group = running.First(g => g.Key == key).ToList();
            var first = group[0];
            entries.Add(new Item
            {
                Key = "run|" + key,
                Name = AppName(first),
                Windows = group,
                LaunchTarget = LaunchTargetFor(first),
                Aumid = first.Aumid,
                Exe = first.Exe,
            });
        }
        entries.Add(new Item { Key = "sep|trash", Name = "", IsSeparator = true });
        entries.Add(new Item { Key = "trash", Name = "Recycle Bin", IsTrash = true, LaunchTarget = "shell:RecycleBinFolder" });

        string signature = string.Join("\n", entries.Select(e => e.Key));
        if (signature != _signature)
        {
            _signature = signature;
            Rebuild(entries);
        }
        else
        {
            for (int i = 0; i < entries.Count; i++)
            {
                _items[i].Windows = entries[i].Windows;
                _items[i].Pin = entries[i].Pin;
            }
        }

        foreach (var item in _items.Where(i => !i.IsSeparator))
            item.Dot.Visibility = item.Windows.Count > 0 ? Visibility.Visible : Visibility.Hidden;
    }

    private static string AppName(AppWindow w)
    {
        if (IsPackaged(w)) return ShellApps.Lookup(w.Aumid!).Name;
        try
        {
            if (w.Exe is not null)
            {
                string? description = FileVersionInfo.GetVersionInfo(w.Exe).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
                return System.IO.Path.GetFileNameWithoutExtension(w.Exe);
            }
        }
        catch
        {
            // fall through to the title
        }
        return w.Title;
    }

    private static bool IsPackaged(AppWindow w) =>
        w.Aumid is not null && (w.Exe is null ||
                                w.Exe.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase) ||
                                w.Exe.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase));

    private static string? LaunchTargetFor(AppWindow w) => IsPackaged(w) ? @"shell:AppsFolder\" + w.Aumid : w.Exe;

    // ---- Visuals -----------------------------------------------------------------------------

    private void Rebuild(List<Item> entries)
    {
        _row.Children.Clear();
        _items.Clear();
        foreach (var item in entries)
        {
            item.Visual = item.IsSeparator ? Separator() : AppVisual(item);
            _row.Children.Add(item.Visual);
            _items.Add(item);
        }
        ApplySizes();
    }

    private static FrameworkElement Separator() => new Border
    {
        Width = 1,
        Height = BaseIcon - 6,
        Margin = new Thickness(ItemGap + 2, 0, ItemGap + 2, 10),
        VerticalAlignment = VerticalAlignment.Bottom,
        Background = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF)),
    };

    private FrameworkElement AppVisual(Item item)
    {
        BitmapSource? icon = null;
        try
        {
            icon = item.IsTrash ? ShellApps.Icon(RecycleBin, 96)
                 : item.LaunchTarget is { } target ? ShellApps.Icon(target, 96)
                 : null;
        }
        catch (Exception ex)
        {
            Log.Error("dock icon", ex);
        }

        item.IconBox = new Border
        {
            Width = BaseIcon,
            Height = BaseIcon,
            Background = icon is null ? new SolidColorBrush(Color.FromRgb(0x3A, 0x3A, 0x3E)) : new ImageBrush(icon) { Stretch = Stretch.Uniform },
            CornerRadius = new CornerRadius(icon is null ? 12 : 0),
        };
        RenderOptions.SetBitmapScalingMode(item.IconBox, BitmapScalingMode.HighQuality);
        item.Dot = new Ellipse
        {
            Width = 4,
            Height = 4,
            Fill = new SolidColorBrush(Color.FromArgb(0xD0, 0xFF, 0xFF, 0xFF)),
            Margin = new Thickness(0, 3, 0, 0),
            Visibility = Visibility.Hidden,
        };

        var stack = new StackPanel
        {
            Margin = new Thickness(ItemGap / 2, 0, ItemGap / 2, 0),
            VerticalAlignment = VerticalAlignment.Bottom,
            Background = Brushes.Transparent,
            RenderTransform = item.Lift,
            Cursor = Cursors.Hand,
        };
        stack.Children.Add(item.IconBox);
        stack.Children.Add(item.Dot);
        stack.MouseEnter += (_, _) => SetHover(item);
        stack.MouseLeftButtonUp += (_, e) => { e.Handled = true; OnClick(item); };
        stack.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(item); };
        return stack;
    }

    private void SetHover(Item? item)
    {
        _hoverItem = item;
        if (item is null)
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
        if (_hoverItem is null) return;
        _label.UpdateLayout();
        var top = _hoverItem.IconBox.TranslatePoint(new Point(_hoverItem.IconBox.ActualWidth / 2, 0), _overlay);
        Canvas.SetLeft(_label, top.X - _label.ActualWidth / 2);
        Canvas.SetTop(_label, top.Y - _label.ActualHeight - 10);
    }

    // ---- Magnification -----------------------------------------------------------------------

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        // Targets from the unmagnified layout, so icons don't chase the cursor as the row grows.
        double baseWidth = _items.Sum(i => i.IsSeparator ? 1 + 2 * (ItemGap + 2) : BaseIcon + ItemGap);
        double offset = (_row.ActualWidth - baseWidth) / 2;
        double x = 0;
        foreach (var item in _items)
        {
            double w = item.IsSeparator ? 1 + 2 * (ItemGap + 2) : BaseIcon + ItemGap;
            if (!item.IsSeparator)
            {
                double center = x + w / 2;
                double t = 0;
                if (_mouseX is double mx)
                {
                    double d = Math.Abs(mx - offset - center);
                    double range = BaseIcon * 2.6;
                    if (d < range) t = (1 + Math.Cos(Math.PI * d / range)) / 2;
                }
                item.Target = BaseIcon + (MaxIcon - BaseIcon) * t;
            }
            x += w;
        }

        bool moving = false;
        foreach (var item in _items.Where(i => !i.IsSeparator))
        {
            double delta = item.Target - item.Size;
            if (Math.Abs(delta) > 0.2) moving = true;
            item.Size += delta * 0.28;
        }
        ApplySizes();
        PlaceLabel();

        if (!moving && _mouseX is null)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void ApplySizes()
    {
        foreach (var item in _items.Where(i => !i.IsSeparator))
            item.IconBox.Width = item.IconBox.Height = item.Size;
    }

    // ---- Actions -----------------------------------------------------------------------------

    private void OnClick(Item item)
    {
        if (item.Windows.Count == 0)
        {
            if (item.LaunchTarget is { } target) ShellApps.Start(target);
            if (!item.IsTrash) Bounce(item);
            return;
        }

        var foreground = Native.GetForegroundWindow();
        int frontIndex = item.Windows.FindIndex(w => w.Handle == foreground);
        if (frontIndex < 0)
        {
            WindowTracker.Activate(item.Windows[0].Handle);
        }
        else if (item.Windows.Count > 1)
        {
            item.CycleIndex = (frontIndex + 1) % item.Windows.Count; // cycle through the app's windows
            WindowTracker.Activate(item.Windows[item.CycleIndex].Handle);
        }
        else
        {
            Native.ShowWindow(foreground, Native.SW_MINIMIZE);
        }
    }

    private static void Bounce(Item item)
    {
        var bounce = new DoubleAnimationUsingKeyFrames { Duration = TimeSpan.FromMilliseconds(1100) };
        var ease = new QuadraticEase { EasingMode = EasingMode.EaseOut };
        var fall = new QuadraticEase { EasingMode = EasingMode.EaseIn };
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-20, KeyTime.FromPercent(0.18), ease));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.38), fall));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-12, KeyTime.FromPercent(0.56), ease));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0.74), fall));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(-5, KeyTime.FromPercent(0.86), ease));
        bounce.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1), fall));
        item.Lift.BeginAnimation(TranslateTransform.YProperty, bounce);
    }

    private void ShowMenu(Item item)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false, Font = new Drawing.Font("Segoe UI", 9.5f) };
        menu.Items.Add(new Forms.ToolStripLabel(item.Name) { Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold) });
        menu.Items.Add(new Forms.ToolStripSeparator());

        if (item.IsTrash)
        {
            Add("Open", () => ShellApps.Start("shell:RecycleBinFolder"));
            Add("Empty Recycle Bin", () => SHEmptyRecycleBin(IntPtr.Zero, null, 0)); // Windows asks to confirm
        }
        else
        {
            foreach (var w in item.Windows.Take(8))
            {
                string title = w.Title.Length > 48 ? w.Title[..48] + "…" : w.Title;
                Add(title, () => WindowTracker.Activate(w.Handle));
            }
            if (item.Windows.Count > 0) menu.Items.Add(new Forms.ToolStripSeparator());
            if (item.LaunchTarget is { } target) Add(item.Windows.Count > 0 ? "New window" : "Open", () => { ShellApps.Start(target); Bounce(item); });
            if (item.Pin is { } pin) Add("Remove from Dock", () => Unpin(pin));
            else if (item.LaunchTarget is not null) Add("Keep in Dock", () => KeepInDock(item));
            if (item.Windows.Count > 0)
                Add("Quit", () => { foreach (var w in item.Windows) Native.PostMessage(w.Handle, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero); });
        }

        menu.Closed += (_, _) => Dispatcher.InvokeAsync(menu.Dispose, DispatcherPriority.Background);
        menu.Show(Forms.Cursor.Position);
        Native.SetForegroundWindow(menu.Handle);

        void Add(string text, Action action)
        {
            var mi = new Forms.ToolStripMenuItem(text.Replace("&", "&&"));
            mi.Click += (_, _) => action();
            menu.Items.Add(mi);
        }
    }

    private void KeepInDock(Item item)
    {
        if (item.LaunchTarget is null) return;
        (_settings.DockPins ??= []).Add(new DockPin(item.Name, item.LaunchTarget, item.Aumid, item.Exe));
        _saveSettings();
        Refresh();
    }

    private void Unpin(DockPin pin)
    {
        _settings.DockPins?.Remove(pin);
        _saveSettings();
        Refresh();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? root, uint flags);
}
