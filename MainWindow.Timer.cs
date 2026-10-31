using System.IO;
using System.Media;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DynaIsland.Services;

namespace DynaIsland;

// Timer, focus session and stopwatch.
public partial class MainWindow
{
    private void StartTimer(TimerKind kind, TimeSpan duration = default)
    {
        _timer.Start(kind, duration);
        var accent = new SolidColorBrush(kind == TimerKind.Focus ? Indigo : Orange);
        accent.Freeze();
        string glyph = kind == TimerKind.Focus ? "" : "";
        TimerGlyph.Text = TimerRingGlyph.Text = SideGlyph.Text = glyph;
        TimerGlyph.Foreground = TimerCompactText.Foreground = TimerBigText.Foreground = TimerRingGlyph.Foreground = accent;
        TimerRing.Stroke = SideRing.Stroke = SideGlyph.Foreground = accent;
        TimerAddButton.Visibility = kind == TimerKind.Stopwatch ? Visibility.Collapsed : Visibility.Visible;
        UpdateTimerUi();
        _uiTick.Start();
        UpdateState();
    }

    private void UpdateTimerUi()
    {
        if (!_timer.IsActive) return;
        string text = TimerService.Format(_timer.Display, roundUp: _timer.Kind != TimerKind.Stopwatch);
        TimerCompactText.Text = TimerBigText.Text = text;
        TimerLabel.Text = _timer.IsPaused ? $"{_timer.Label} · paused" : _timer.Label;
        TimerPauseButton.Content = _timer.IsPaused ? "" : "";

        double progress = _timer.Kind == TimerKind.Stopwatch ? _timer.Progress : 1 - _timer.Progress;
        // Radii match the track ellipses: (diameter - stroke) / 2.
        TimerRing.Data = Arc(25, 21, progress);
        SideRing.Data = Arc(17, 11.75, progress);
    }

    private void OnTimerFinished()
    {
        string title = _timer.Kind == TimerKind.Focus ? "Focus session complete" : "Timer done";
        string detail = _timer.Kind == TimerKind.Focus ? "Time for a break" : TimerService.Format(_timer.Duration, roundUp: true);
        PlayChime();
        ShowNotice(_timer.Kind == TimerKind.Focus ? "" : "", null, _timer.Label, title, detail,
            _timer.Kind == TimerKind.Focus ? Indigo : Orange, 6000, null);
    }

    private static void PlayChime()
    {
        try
        {
            string alarm = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");
            if (File.Exists(alarm)) new SoundPlayer(alarm).Play();
            else SystemSounds.Exclamation.Play();
        }
        catch
        {
            // sound is optional
        }
    }

    private void OnTimerPauseResume(object sender, RoutedEventArgs e)
    {
        if (_timer.IsPaused) _timer.Resume();
        else _timer.Pause();
        UpdateTimerUi();
    }

    private void OnTimerAdd(object sender, RoutedEventArgs e)
    {
        _timer.AddMinute();
        UpdateTimerUi();
    }

    private void OnTimerStop(object sender, RoutedEventArgs e) => StopTimer();

    private void StopTimer()
    {
        _timer.Stop();
        _timerFocused = false;
        UpdateState();
    }

    private void OnSideBubbleClick(object sender, MouseButtonEventArgs e)
    {
        // With music and a timer running, the bubble opens the timer controls.
        e.Handled = true;
        _timerFocused = true;
        if (_settings.ExpandOnHover) _hovered = true;
        else _pinned = true;
        _leaveTimer.Stop();
        UpdateState();

        // The expanded island usually grows under the cursor; if it doesn't, collapse after a moment.
        var check = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(4) };
        check.Tick += (_, _) =>
        {
            check.Stop();
            if (_settings.ExpandOnHover && !Island.IsMouseOver) _leaveTimer.Start();
        };
        check.Start();
    }

    /// <summary>Clockwise arc from 12 o'clock covering <paramref name="fraction"/> of a circle.</summary>
    private static Geometry Arc(double center, double radius, double fraction)
    {
        fraction = Math.Clamp(fraction, 0, 0.9999);
        if (fraction <= 0.0001) return Geometry.Empty;
        double angle = fraction * 2 * Math.PI;
        var start = new Point(center, center - radius);
        var end = new Point(center + radius * Math.Sin(angle), center - radius * Math.Cos(angle));
        var figure = new PathFigure { StartPoint = start, IsClosed = false };
        figure.Segments.Add(new ArcSegment(end, new Size(radius, radius), 0, fraction > 0.5, SweepDirection.Clockwise, true));
        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        return geometry;
    }
}
