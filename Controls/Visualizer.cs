using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DynaIsland.Controls;

/// <summary>Animated "now playing" bars. Purely decorative (no audio capture).</summary>
public sealed class Visualizer : StackPanel
{
    private static readonly Random Rng = new();
    private readonly List<ScaleTransform> _scales = [];
    private readonly DispatcherTimer _timer;
    private bool _active;

    public Visualizer()
    {
        Orientation = Orientation.Horizontal;
        VerticalAlignment = VerticalAlignment.Center;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(170) };
        _timer.Tick += (_, _) => Shuffle();
        Loaded += (_, _) => Build();
    }

    public int BarCount { get; set; } = 4;
    public double BarWidth { get; set; } = 3;
    public double BarGap { get; set; } = 2;

    public Brush Fill
    {
        get => (Brush)GetValue(FillProperty);
        set => SetValue(FillProperty, value);
    }

    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(
        nameof(Fill), typeof(Brush), typeof(Visualizer), new PropertyMetadata(Brushes.White));

    /// <summary>Animate while playing; collapse to small dots when paused / not visible.</summary>
    public void SetActive(bool active)
    {
        if (_active == active) return;
        _active = active;
        if (active)
        {
            Shuffle();
            _timer.Start();
        }
        else
        {
            _timer.Stop();
            foreach (var s in _scales) AnimateTo(s, 0.22, 260);
        }
    }

    private void Build()
    {
        if (_scales.Count > 0) return;
        for (int i = 0; i < BarCount; i++)
        {
            var scale = new ScaleTransform(1, 0.22);
            var bar = new Rectangle
            {
                Width = BarWidth,
                RadiusX = BarWidth / 2,
                RadiusY = BarWidth / 2,
                Margin = new Thickness(i == 0 ? 0 : BarGap, 0, 0, 0),
                RenderTransformOrigin = new Point(0.5, 0.5),
                RenderTransform = scale,
            };
            bar.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Fill)) { Source = this });
            Children.Add(bar);
            _scales.Add(scale);
        }
        if (_active) Shuffle();
    }

    private void Shuffle()
    {
        foreach (var s in _scales) AnimateTo(s, 0.25 + Rng.NextDouble() * 0.75, 160);
    }

    private static void AnimateTo(ScaleTransform s, double value, int ms)
    {
        s.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(value, TimeSpan.FromMilliseconds(ms))
        {
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
    }
}
