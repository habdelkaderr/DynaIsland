using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using DynaIsland.Services;

namespace DynaIsland;

// Now playing: compact / peek / expanded media views.
public partial class MainWindow
{
    private static readonly Brush PlaceholderArt = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));

    private readonly SolidColorBrush _accent = new(ColorUtil.Neutral);
    private string? _lastTrackKey;
    private BitmapSource? _shownArt;
    private DateTime _pausedSince = DateTime.MinValue;

    private void OnMediaChanged()
    {
        var media = _media.Current;
        if (media is null)
        {
            _lastTrackKey = null;
            _peekActive = false;
            UpdateState();
            return;
        }

        if (media.IsPlaying) _pausedSince = DateTime.MaxValue;
        else if (_pausedSince == DateTime.MaxValue) _pausedSince = DateTime.Now;

        bool newTrack = media.TrackKey != _lastTrackKey;
        _lastTrackKey = media.TrackKey;
        if (newTrack && media.IsPlaying && !Engaged && DateTime.Now - _startedAt > TimeSpan.FromSeconds(3))
        {
            _peekActive = true;
            _peekTimer.Stop();
            _peekTimer.Start();
        }

        ApplyMedia(media);
        UpdateState();
    }

    private void ApplyMedia(MediaSnapshot media)
    {
        string title = string.IsNullOrWhiteSpace(media.Title) ? "Unknown title" : media.Title;
        ExpTitle.Text = PeekTitle.Text = title;
        ExpArtist.Text = PeekArtist.Text = media.Artist;
        ExpSource.Text = media.SourceApp;

        if (!ReferenceEquals(media.Art, _shownArt))
        {
            _shownArt = media.Art;
            var brush = media.Art is null ? PlaceholderArt : Freeze(new ImageBrush(media.Art) { Stretch = Stretch.UniformToFill });
            var glyph = media.Art is null ? Visibility.Visible : Visibility.Collapsed;
            CompactArt.Background = PeekArt.Background = ExpArt.Background = brush;
            CompactArtGlyph.Visibility = PeekArtGlyph.Visibility = ExpArtGlyph.Visibility = glyph;
        }

        _accent.BeginAnimation(SolidColorBrush.ColorProperty, new ColorAnimation(media.Accent, TimeSpan.FromMilliseconds(450)));

        PlayButton.Content = media.IsPlaying ? "" : "";
        PrevButton.IsEnabled = media.CanSkipPrevious;
        NextButton.IsEnabled = media.CanSkipNext;
        PrevButton.Opacity = media.CanSkipPrevious ? 1 : 0.35;
        NextButton.Opacity = media.CanSkipNext ? 1 : 0.35;
        ExpTimeline.Visibility = media.Duration > TimeSpan.FromSeconds(1) ? Visibility.Visible : Visibility.Hidden;
        ExpTrack.Cursor = media.CanSeek ? Cursors.Hand : Cursors.Arrow;

        UpdateProgress();
    }

    private void UpdateVisualizers()
    {
        bool playing = _media.Current?.IsPlaying == true;
        CompactBars.SetActive(playing && _state == IslandState.Compact);
        PeekBars.SetActive(playing && _state == IslandState.Peek);
        ExpBars.SetActive(playing && _state == IslandState.Expanded);
    }

    private void UpdateProgress()
    {
        if (_state != IslandState.Expanded || _media.Current is not { } media) return;
        var duration = media.Duration;
        if (duration <= TimeSpan.FromSeconds(1)) return;

        var position = media.EstimatedPosition;
        ExpElapsed.Text = FormatTime(position);
        ExpRemaining.Text = "-" + FormatTime(duration - position);
        ExpFillScale.ScaleX = Math.Clamp(position / duration, 0, 1);
    }

    private static string FormatTime(TimeSpan t) =>
        t.TotalHours >= 1 ? t.ToString(@"h\:mm\:ss") : t.ToString(@"m\:ss");

    private async void OnPlayPause(object sender, RoutedEventArgs e) => await _media.TogglePlayPauseAsync();
    private async void OnNext(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void OnPrevious(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void OnTrackClicked(object sender, MouseButtonEventArgs e)
    {
        if (_media.Current is not { CanSeek: true } media || media.Duration <= TimeSpan.Zero) return;
        e.Handled = true;
        double fraction = Math.Clamp(e.GetPosition(ExpTrack).X / ExpTrack.ActualWidth, 0, 1);
        ExpFillScale.ScaleX = fraction;
        await _media.SeekAsync(media.Duration * fraction);
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
