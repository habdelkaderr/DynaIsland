using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynaIsland.Services;
using DynaIsland.Ui;

namespace DynaIsland;

/// <summary>
/// Full-screen app launcher: every installed app (from Windows' Start list) in pages, with search,
/// keyboard navigation, categories, frequently used apps, drag-to-reorder and drag-to-Dock.
/// Built when opened and released when closed; icons load page by page in the background.
/// </summary>
internal sealed class LaunchpadWindow : Window
{
    private const double CellWidth = 132;
    private const double CellHeight = 124;
    private const double IconSize = 64;
    private const int IconPixels = 128; // cache key distinct from the dock's, released on close

    private readonly DesktopHost _host;
    private readonly Func<DockWindow?> _dock;
    private readonly IntPtr _hwnd;
    private readonly Grid _root = new();
    private readonly ScaleTransform _zoom = new(1, 1);
    private readonly TextBox _search;
    private readonly TextBlock _searchHint;
    private readonly WrapPanel _chips = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 14, 0, 0) };
    private readonly Grid _pageHost = new() { ClipToBounds = true };
    private readonly StackPanel _dots = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 10, 0, 24) };
    private readonly TranslateTransform _pageSlide = new();

    private List<InstalledApp> _shown = [];
    private string _category = "All";
    private int _page, _columns = 7, _rows = 4, _selected = -1;
    private readonly List<(Border Cell, InstalledApp App)> _cells = [];
    private Point _pressPoint;
    private InstalledApp? _pressApp;
    private bool _open;

    private int PerPage => _columns * _rows;
    private int PageCount => Math.Max(1, (int)Math.Ceiling(_shown.Count / (double)PerPage));

    public LaunchpadWindow(DesktopHost host, Func<DockWindow?> dock)
    {
        _host = host;
        _dock = dock;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = false;
        ShowInTaskbar = false;
        Topmost = true;
        Title = "Launchpad";
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = -10000;
        Top = -10000;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        SetResourceReference(ForegroundProperty, "Ui.Text");

        // Search field
        _searchHint = Ux.Text("Search", 14, "Ui.TextFaint");
        _searchHint.IsHitTestVisible = false;
        _search = new TextBox
        {
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            FontSize = 14,
            Padding = new Thickness(0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        _search.SetResourceReference(ForegroundProperty, "Ui.Text");
        _search.SetResourceReference(TextBox.CaretBrushProperty, "Ui.Text");
        _search.TextChanged += (_, _) =>
        {
            _searchHint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            _page = 0;
            _selected = _search.Text.Length > 0 ? 0 : -1;
            Filter();
        };
        var searchIcon = Icons.Glyph("", 13);
        searchIcon.SetResourceReference(TextBlock.ForegroundProperty, "Ui.TextSecondary");
        searchIcon.Margin = new Thickness(0, 0, 8, 0);
        var field = new Grid { Width = 240, Children = { _searchHint, _search } };
        var searchBox = new Border
        {
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(10, 6, 10, 6),
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 48, 0, 0),
            Child = new StackPanel { Orientation = Orientation.Horizontal, Children = { searchIcon, field } },
            BorderThickness = new Thickness(0.6),
        };
        searchBox.SetResourceReference(Border.BackgroundProperty, "Ui.Control");
        searchBox.SetResourceReference(Border.BorderBrushProperty, "Ui.Stroke");

        _pageHost.Margin = new Thickness(0, 24, 0, 0);
        var layout = new Grid();
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.RowDefinitions.Add(new RowDefinition());
        layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        layout.Children.Add(searchBox);
        Grid.SetRow(_chips, 1);
        layout.Children.Add(_chips);
        Grid.SetRow(_pageHost, 2);
        layout.Children.Add(_pageHost);
        Grid.SetRow(_dots, 3);
        layout.Children.Add(_dots);

        // Clicking empty space closes, like macOS.
        _root.Background = new SolidColorBrush(Color.FromArgb(1, 0, 0, 0));
        _root.MouseLeftButtonUp += (_, e) => { if (e.OriginalSource == _root || e.OriginalSource == _pageHost) Close(); };
        _root.RenderTransformOrigin = new Point(0.5, 0.5);
        _root.RenderTransform = _zoom;
        _root.Children.Add(layout);
        Content = _root;

        PreviewKeyDown += OnKey;
        PreviewMouseWheel += (_, e) => Turn(e.Delta < 0 ? 1 : -1);
        Deactivated += (_, _) => { if (_open) Close(); };
        SizeChanged += (_, _) => { if (_open) Relayout(); };

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW));
        Glass.Apply(this, Darker(Theme.SurfaceTint), host.Settings.GlassBlur, round: false, Theme.IsDark);
        Theme.Changed += (_, _) => Glass.SetTint(_hwnd, Darker(Theme.SurfaceTint), _host.Settings.GlassBlur);
    }

    /// <summary>A little more opaque than other glass, so the desktop recedes behind the grid.</summary>
    private static Color Darker(Color tint) => Color.FromArgb((byte)Math.Min(240, tint.A + 40), tint.R, tint.G, tint.B);

    public bool IsOpen => _open;

    // ---- Open / close ------------------------------------------------------------------------

    public void Open(IntPtr monitor)
    {
        _open = true;
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        Native.GetMonitorInfo(monitor, ref info);
        var rc = info.rcMonitor;
        double scale = Native.GetDpiForMonitor(monitor, 0, out uint dpi, out _) == 0 ? dpi / 96.0 : 1;
        Width = (rc.Right - rc.Left) / scale;
        Height = (rc.Bottom - rc.Top) / scale;

        Glass.SetTint(_hwnd, Darker(Theme.SurfaceTint), _host.Settings.GlassBlur);
        _search.Text = "";
        _category = "All";
        _page = 0;
        _selected = -1;
        _root.Opacity = 0; // window opacity doesn't apply to blurred (non-layered) windows
        Show();
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, rc.Left, rc.Top, rc.Right - rc.Left, rc.Bottom - rc.Top, 0);
        Native.SetForegroundWindow(_hwnd);
        Activate();
        _dock()?.BringToFront(); // so apps can be dragged onto the dock
        BuildChips();
        Relayout();
        _search.Focus();

        double ms = 260 / Motion.Speed;
        _root.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
        var zoom = new DoubleAnimation(1.06, 1, TimeSpan.FromMilliseconds(ms * 1.3)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut } };
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    /// <summary>Closes with a reverse zoom and releases the window and its icons.</summary>
    public new void Close()
    {
        if (!_open) return;
        _open = false;
        double ms = 170 / Motion.Speed;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(ms));
        fade.Completed += (_, _) =>
        {
            ShellApps.ForgetIcons(IconPixels);
            base.Close();
        };
        _root.BeginAnimation(OpacityProperty, fade);
        var zoom = new DoubleAnimation(1.05, TimeSpan.FromMilliseconds(ms)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        _zoom.BeginAnimation(ScaleTransform.ScaleXProperty, zoom);
        _zoom.BeginAnimation(ScaleTransform.ScaleYProperty, zoom);
    }

    // ---- Content -----------------------------------------------------------------------------

    private void BuildChips()
    {
        _chips.Children.Clear();
        var all = AppCatalog.All;
        var names = new List<string> { "All", "Frequently Used", "In Dock" };
        names.AddRange(AppCatalog.CategoryNames.Where(c => all.Any(a => a.Category == c)));
        foreach (var name in names)
        {
            var text = Ux.Text(name, 12);
            var chip = new Border { CornerRadius = new CornerRadius(12), Padding = new Thickness(11, 4, 11, 5), Margin = new Thickness(3), Child = text, Cursor = Cursors.Hand };
            Paint(chip, name == _category);
            chip.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                _category = name;
                _page = 0;
                foreach (Border c in _chips.Children) Paint(c, c == chip);
                Filter();
            };
            _chips.Children.Add(chip);
        }

        static void Paint(Border chip, bool on)
        {
            if (on) chip.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
            else chip.Background = Brushes.Transparent;
        }
    }

    private void Relayout()
    {
        double w = Math.Max(400, ActualWidth - 160);
        double h = Math.Max(200, ActualHeight - 230);
        _columns = Math.Clamp((int)(w / CellWidth), 3, 9);
        _rows = Math.Clamp((int)(h / CellHeight), 2, 6);
        Filter();
    }

    private void Filter()
    {
        var settings = _host.Settings;
        IEnumerable<InstalledApp> apps = AppCatalog.All;
        string query = _search.Text.Trim();
        if (query.Length > 0)
        {
            // Name starts-with first, then word starts-with, then contains.
            apps = apps.Where(a => a.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase))
                       .OrderBy(a => a.Name.StartsWith(query, StringComparison.CurrentCultureIgnoreCase) ? 0 :
                                     a.Name.Split(' ').Any(w => w.StartsWith(query, StringComparison.CurrentCultureIgnoreCase)) ? 1 : 2)
                       .ThenBy(a => a.Name);
        }
        else
        {
            apps = _category switch
            {
                "All" => Ordered(apps),
                "Frequently Used" => apps.Where(a => settings.LaunchCounts.GetValueOrDefault(a.Id) > 0)
                                         .OrderByDescending(a => settings.LaunchCounts[a.Id]).Take(PerPage),
                "In Dock" => apps.Where(a => _dock()?.IsPinned(a.Id) == true),
                _ => apps.Where(a => a.Category == _category),
            };
        }
        _shown = apps.ToList();
        _page = Math.Clamp(_page, 0, PageCount - 1);
        ShowPage(0);
    }

    /// <summary>Custom order from drag-and-drop first, then the rest alphabetically.</summary>
    private IEnumerable<InstalledApp> Ordered(IEnumerable<InstalledApp> apps)
    {
        var order = _host.Settings.LaunchpadOrder;
        var index = order.Select((id, i) => (id, i)).ToDictionary(x => x.id, x => x.i, StringComparer.OrdinalIgnoreCase);
        return apps.OrderBy(a => index.TryGetValue(a.Id, out int i) ? i : int.MaxValue).ThenBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase);
    }

    private void ShowPage(int direction)
    {
        var grid = new UniformGrid { Columns = _columns, Rows = _rows, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top };
        grid.Width = _columns * CellWidth;
        _cells.Clear();
        foreach (var app in _shown.Skip(_page * PerPage).Take(PerPage)) grid.Children.Add(Cell(app));
        _pageHost.Children.Clear();
        grid.RenderTransform = _pageSlide;
        _pageHost.Children.Add(grid);
        if (_shown.Count == 0)
            _pageHost.Children.Add(new TextBlock { Text = "No apps found", FontSize = 15, Opacity = 0.6, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) });

        if (direction != 0)
        {
            _pageSlide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(direction * 120, 0, TimeSpan.FromMilliseconds(260 / Motion.Speed))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            });
        }
        UpdateDots();
        UpdateSelection();
        LoadIconsAsync();
    }

    private FrameworkElement Cell(InstalledApp app)
    {
        var icon = new Border { Width = IconSize, Height = IconSize, HorizontalAlignment = HorizontalAlignment.Center };
        RenderOptions.SetBitmapScalingMode(icon, BitmapScalingMode.HighQuality);
        var name = Ux.Text(app.Name, 12);
        name.TextAlignment = TextAlignment.Center;
        name.HorizontalAlignment = HorizontalAlignment.Center;
        name.MaxWidth = CellWidth - 16;
        name.Margin = new Thickness(0, 8, 0, 0);
        name.SetResourceReference(TextBlock.ForegroundProperty, "Ui.Text");
        var stack = new StackPanel { Children = { icon, name }, VerticalAlignment = VerticalAlignment.Center };
        var cell = new Border
        {
            Width = CellWidth - 8,
            Height = CellHeight - 8,
            CornerRadius = new CornerRadius(14),
            Child = stack,
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            AllowDrop = true,
            Tag = icon,
        };
        cell.MouseEnter += (_, _) => cell.SetResourceReference(Border.BackgroundProperty, "Ui.Control");
        cell.MouseLeave += (_, _) => { if (_cells.FindIndex(c => c.Cell == cell) != _selected) cell.Background = Brushes.Transparent; };
        cell.MouseLeftButtonDown += (_, e) => { _pressPoint = e.GetPosition(this); _pressApp = app; e.Handled = true; };
        cell.MouseMove += (_, e) =>
        {
            if (_pressApp != app || e.LeftButton != MouseButtonState.Pressed || (e.GetPosition(this) - _pressPoint).Length < 8) return;
            _pressApp = null;
            // One drag serves both: drop on another app to reorder, or on the dock to pin.
            DragDrop.DoDragDrop(cell, new DataObject(DockWindow.DragFormat, app.Id), DragDropEffects.Copy | DragDropEffects.Move);
        };
        cell.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (_pressApp == app) Launch(app);
            _pressApp = null;
        };
        cell.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenu(app, cell); };
        cell.DragOver += (_, e) => { e.Effects = DragDropEffects.Move; e.Handled = true; };
        cell.Drop += (_, e) =>
        {
            if (e.Data.GetData(DockWindow.DragFormat) is string id && !string.Equals(id, app.Id, StringComparison.OrdinalIgnoreCase))
                MoveBefore(id, app.Id);
            e.Handled = true;
        };
        _cells.Add((cell, app));
        return cell;
    }

    /// <summary>Loads icons for the current page in small batches, so opening never stalls.</summary>
    private void LoadIconsAsync()
    {
        foreach (var (cell, app) in _cells.ToList())
        {
            Dispatcher.InvokeAsync(() =>
            {
                if (cell.Tag is not Border box || box.Background is not null) return;
                var image = app.Icon(IconPixels);
                box.Background = image is null ? new SolidColorBrush(Color.FromArgb(0x40, 0x80, 0x80, 0x80)) : new ImageBrush(image) { Stretch = Stretch.Uniform };
                if (image is null) box.CornerRadius = new CornerRadius(14);
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateDots()
    {
        _dots.Children.Clear();
        if (PageCount <= 1) return;
        for (int i = 0; i < PageCount; i++)
        {
            int page = i;
            var dot = new Border { Width = 8, Height = 8, CornerRadius = new CornerRadius(4), Margin = new Thickness(5), Cursor = Cursors.Hand, Opacity = i == _page ? 1 : 0.35 };
            dot.SetResourceReference(Border.BackgroundProperty, "Ui.Text");
            dot.MouseLeftButtonUp += (_, e) => { e.Handled = true; int d = page.CompareTo(_page); _page = page; ShowPage(d); };
            _dots.Children.Add(dot);
        }
    }

    private void MoveBefore(string movedId, string targetId)
    {
        // Materialise the current full order, then move.
        var order = Ordered(AppCatalog.All).Select(a => a.Id).ToList();
        order.RemoveAll(i => string.Equals(i, movedId, StringComparison.OrdinalIgnoreCase));
        int at = order.FindIndex(i => string.Equals(i, targetId, StringComparison.OrdinalIgnoreCase));
        order.Insert(at < 0 ? order.Count : at, movedId);
        _host.Settings.LaunchpadOrder = order;
        _host.Settings.Save();
        _category = "All";
        Filter();
    }

    // ---- Input -------------------------------------------------------------------------------

    private void Turn(int direction)
    {
        int next = Math.Clamp(_page + direction, 0, PageCount - 1);
        if (next == _page) return;
        _page = next;
        _selected = _selected >= 0 ? 0 : -1;
        ShowPage(direction);
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Escape:
                e.Handled = true;
                if (_search.Text.Length > 0) _search.Text = "";
                else Close();
                break;
            case Key.Enter:
                e.Handled = true;
                int index = _selected >= 0 ? _selected : 0;
                if (index < _cells.Count) Launch(_cells[index].App);
                break;
            case Key.Right: Move(1); e.Handled = true; break;
            case Key.Left: Move(-1); e.Handled = true; break;
            case Key.Down: Move(_columns); e.Handled = true; break;
            case Key.Up: Move(-_columns); e.Handled = true; break;
            case Key.PageDown: Turn(1); e.Handled = true; break;
            case Key.PageUp: Turn(-1); e.Handled = true; break;
        }
    }

    private void Move(int delta)
    {
        if (_cells.Count == 0) return;
        int next = (_selected < 0 ? 0 : _selected + delta);
        if (next >= _cells.Count && _page < PageCount - 1) { Turn(1); return; }
        if (next < 0 && _page > 0) { Turn(-1); return; }
        _selected = Math.Clamp(next, 0, _cells.Count - 1);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        for (int i = 0; i < _cells.Count; i++)
        {
            if (i == _selected) _cells[i].Cell.SetResourceReference(Border.BackgroundProperty, "Ui.ControlHover");
            else _cells[i].Cell.Background = Brushes.Transparent;
        }
    }

    private void Launch(InstalledApp app)
    {
        AppCatalog.Launch(app, _host.Settings);
        Close();
    }

    private void ShowMenu(InstalledApp app, Border cell)
    {
        var dock = _dock();
        var entries = new List<GlassMenu.Entry>
        {
            GlassMenu.Entry.Header(app.Name),
            new("Open", () => Launch(app)),
        };
        if (dock is not null)
            entries.Add(dock.IsPinned(app.Id) ? new("Remove from Dock", () => dock.Unpin(app.Id)) : new("Keep in Dock", () => dock.PinApp(app.Id)));
        if (app.Exe is { } exe && File.Exists(exe))
            entries.Add(new("Show in File Explorer", () => ShellApps.Start("explorer.exe", $"/select,\"{exe}\"")));
        entries.Add(GlassMenu.Entry.Separator);
        entries.Add(new("Uninstall or Modify…", () => { Close(); SystemActions.OpenSettings("appsfeatures"); }));

        var menu = new GlassMenu(entries, 210) { Blur = _host.Settings.GlassBlur, CloseOnDeactivate = true };
        var p = cell.PointToScreen(new Point(cell.ActualWidth / 2, cell.ActualHeight - 10));
        _suppressDeactivate = true;
        menu.Closed += (_, _) => { _suppressDeactivate = false; if (_open) Activate(); };
        menu.ShowAt((int)p.X, (int)p.Y, HorizontalAlignment.Center);
    }

    private bool _suppressDeactivate;

    protected override void OnDeactivated(EventArgs e)
    {
        if (_suppressDeactivate) return; // our own context menu took focus
        base.OnDeactivated(e);
    }
}
