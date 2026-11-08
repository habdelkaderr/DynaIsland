using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynaIsland;

/// <summary>
/// Volume / brightness pop-up: a notch-shaped pill attached to the left or right screen edge that slides
/// in, with curved "ears" blending into the edge. Click-through; never takes focus.
/// </summary>
internal sealed class SideHud : Window
{
    public enum Edge { Left, Right }

    private const double PillWidth = 58;
    private const double PillHeight = 212;
    private const double Ear = 14;
    private const double TrackHeight = 116;

    private readonly Edge _edge;
    private readonly TranslateTransform _slide = new();
    private readonly ScaleTransform _fill = new(1, 0);
    private readonly TextBlock _icon;
    private readonly TextBlock _value;
    private readonly Path _shape;
    private readonly DispatcherTimer _hide;
    private IntPtr _hwnd;
    private bool _shown;

    public SideHud(Edge edge)
    {
        _edge = edge;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        Focusable = false;
        IsHitTestVisible = false;
        Width = PillWidth;
        Height = PillHeight + 2 * Ear;
        Title = "DynaIsland pop-up";

        _shape = new Path
        {
            Data = Outline(),
            Fill = Brushes.Black,
            RenderTransform = edge == Edge.Left ? new ScaleTransform(-1, 1, PillWidth / 2, 0) : Transform.Identity,
        };

        var font = (FontFamily)Application.Current.Resources["IconFont"];
        _icon = new TextBlock { FontFamily = font, FontSize = 18, Foreground = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center };
        _value = new TextBlock
        {
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["SubtleText"],
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var track = new Grid { Width = 6, Height = TrackHeight, Margin = new Thickness(0, 14, 0, 12) };
        track.Children.Add(new Border { CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF)) });
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(3),
            Background = Brushes.White,
            RenderTransformOrigin = new Point(0.5, 1),
            RenderTransform = _fill,
        });

        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(_icon);
        content.Children.Add(track);
        content.Children.Add(_value);

        var root = new Grid { RenderTransform = _slide };
        root.Children.Add(_shape);
        root.Children.Add(content);
        Content = root;

        _hide = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1600) };
        _hide.Tick += (_, _) => SlideOut();

        _hwnd = new WindowInteropHelper(this).EnsureHandle();
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE,
            new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE | Native.WS_EX_TRANSPARENT));
    }

    public void SetFill(Brush fill) => _shape.Fill = fill;

    /// <summary>Shows (or updates) the pop-up on the given monitor.</summary>
    public void ShowLevel(string glyph, double level, string text, IntPtr monitor)
    {
        _icon.Text = glyph;
        _value.Text = text;
        _fill.BeginAnimation(ScaleTransform.ScaleYProperty,
            new DoubleAnimation(Math.Clamp(level, 0, 1), TimeSpan.FromMilliseconds(140)) { EasingFunction = new CubicEase() });

        if (!_shown)
        {
            _shown = true;
            Place(monitor);
            Show();
            double from = _edge == Edge.Right ? PillWidth : -PillWidth;
            _slide.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(from, 0, TimeSpan.FromMilliseconds(380))
            {
                EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
            });
        }
        _hide.Stop();
        _hide.Start();
    }

    public void HideNow()
    {
        _hide.Stop();
        _shown = false;
        Hide();
    }

    private void SlideOut()
    {
        _hide.Stop();
        double to = _edge == Edge.Right ? PillWidth : -PillWidth;
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(240)) { EasingFunction = new CubicEase { EasingMode = EasingMode.EaseIn } };
        anim.Completed += (_, _) =>
        {
            if (_hide.IsEnabled) return; // shown again meanwhile
            _shown = false;
            Hide();
        };
        _slide.BeginAnimation(TranslateTransform.XProperty, anim);
    }

    private void Place(IntPtr monitor)
    {
        var info = new Native.MONITORINFO { cbSize = Marshal.SizeOf<Native.MONITORINFO>() };
        if (!Native.GetMonitorInfo(monitor, ref info)) return;
        uint dpi = Native.GetDpiForMonitor(monitor, 0, out uint dpiX, out _) == 0 ? dpiX : 96;
        int w = (int)Math.Round(Width * dpi / 96.0), h = (int)Math.Round(Height * dpi / 96.0);
        var rc = info.rcMonitor;
        int x = _edge == Edge.Right ? rc.Right - w : rc.Left;
        int y = rc.Top + (rc.Bottom - rc.Top - h) / 2;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, x, y, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
    }

    /// <summary>Right-edge outline: flat against the edge, rounded inner corners, concave ears top and bottom.</summary>
    private static Geometry Outline()
    {
        double x0 = 0, x1 = PillWidth, y0 = Ear, y1 = Ear + PillHeight, r = PillWidth / 2 - 2;
        var f = new PathFigure { StartPoint = new Point(x1, 0), IsClosed = true };
        f.Segments.Add(new ArcSegment(new Point(x1 - Ear, y0), new Size(Ear, Ear), 0, false, SweepDirection.Clockwise, true));
        f.Segments.Add(new LineSegment(new Point(x0 + r, y0), true));
        f.Segments.Add(new ArcSegment(new Point(x0, y0 + r), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
        f.Segments.Add(new LineSegment(new Point(x0, y1 - r), true));
        f.Segments.Add(new ArcSegment(new Point(x0 + r, y1), new Size(r, r), 0, false, SweepDirection.Counterclockwise, true));
        f.Segments.Add(new LineSegment(new Point(x1 - Ear, y1), true));
        f.Segments.Add(new ArcSegment(new Point(x1, y1 + Ear), new Size(Ear, Ear), 0, false, SweepDirection.Clockwise, true));
        var g = new PathGeometry([f]);
        g.Freeze();
        return g;
    }
}
