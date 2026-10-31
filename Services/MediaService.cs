using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;
using PlaybackStatus = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionPlaybackStatus;

namespace DynaIsland.Services;

public sealed class MediaSnapshot
{
    public string Title { get; init; } = "";
    public string Artist { get; init; } = "";
    public string Album { get; init; } = "";
    public string SourceApp { get; init; } = "";
    public bool IsPlaying { get; init; }
    public bool CanSkipNext { get; init; }
    public bool CanSkipPrevious { get; init; }
    public bool CanSeek { get; init; }
    public TimeSpan Position { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTimeOffset PositionUpdated { get; init; }
    public BitmapSource? Art { get; init; }
    public Color Accent { get; init; } = ColorUtil.Neutral;

    public string TrackKey => $"{Title}\u001f{Artist}\u001f{Album}";

    /// <summary>Position extrapolated from the last timeline update while playing.</summary>
    public TimeSpan EstimatedPosition
    {
        get
        {
            var p = Position;
            if (IsPlaying) p += DateTimeOffset.Now - PositionUpdated;
            if (Duration > TimeSpan.Zero && p > Duration) p = Duration;
            return p < TimeSpan.Zero ? TimeSpan.Zero : p;
        }
    }
}

/// <summary>
/// Watches the Windows media sessions (Global System Media Transport Controls).
/// Works with any app that integrates with the Windows media overlay: Spotify,
/// browsers (YouTube, etc.), Media Player, Apple Music, VLC 4, ...
/// Everything runs on the UI dispatcher; nothing leaves the machine.
/// </summary>
public sealed class MediaService
{
    private readonly Dispatcher _ui;
    private readonly DispatcherTimer _debounce;
    private readonly HashSet<Session> _watched = new(ReferenceEqualityComparer.Instance);
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private Session? _session;
    private int _version;

    private string? _artKey;
    private BitmapSource? _art;
    private Color _accent = ColorUtil.Neutral;

    public MediaSnapshot? Current { get; private set; }

    /// <summary>Raised on the UI thread whenever <see cref="Current"/> changes.</summary>
    public event EventHandler? Changed;

    public MediaService(Dispatcher ui)
    {
        _ui = ui;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(90), DispatcherPriority.Normal,
            async (_, _) => { _debounce!.Stop(); await RefreshAsync(); }, ui);
        _debounce.Stop();
    }

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex)
        {
            Log.Error("media manager", ex);
            return;
        }

        _manager.SessionsChanged += (_, _) => _ui.InvokeAsync(() => { WatchSessions(); Schedule(); });
        _manager.CurrentSessionChanged += (_, _) => _ui.InvokeAsync(Schedule);
        WatchSessions();
        await RefreshAsync();
    }

    public Task TogglePlayPauseAsync() => Run(s => s.TryTogglePlayPauseAsync());
    public Task NextAsync() => Run(s => s.TrySkipNextAsync());
    public Task PreviousAsync() => Run(s => s.TrySkipPreviousAsync());
    public Task SeekAsync(TimeSpan position) => Run(s => s.TryChangePlaybackPositionAsync(position.Ticks));

    private async Task Run(Func<Session, global::Windows.Foundation.IAsyncOperation<bool>> op)
    {
        if (_session is not { } session) return;
        try
        {
            await op(session);
        }
        catch (Exception ex)
        {
            Log.Error("media command", ex);
        }
    }

    private void Schedule()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void WatchSessions()
    {
        if (_manager is null) return;
        foreach (var s in _manager.GetSessions())
        {
            if (!_watched.Add(s)) continue;
            s.PlaybackInfoChanged += (_, _) => _ui.InvokeAsync(Schedule);
            s.MediaPropertiesChanged += (_, _) => _ui.InvokeAsync(Schedule);
            s.TimelinePropertiesChanged += (_, _) => _ui.InvokeAsync(Schedule);
        }
    }

    private Session? PickSession()
    {
        if (_manager is null) return null;
        var sessions = _manager.GetSessions().ToList();

        // Stick with the current session while it is playing, otherwise prefer any playing session.
        if (_session is not null && sessions.Contains(_session) && IsPlaying(_session)) return _session;
        var playing = sessions.FirstOrDefault(IsPlaying);
        if (playing is not null) return playing;

        return _manager.GetCurrentSession() ?? sessions.FirstOrDefault();

        static bool IsPlaying(Session s)
        {
            try { return s.GetPlaybackInfo().PlaybackStatus == PlaybackStatus.Playing; }
            catch { return false; }
        }
    }

    private async Task RefreshAsync()
    {
        int version = ++_version;
        try
        {
            var session = PickSession();
            _session = session;
            if (session is null)
            {
                Publish(null);
                return;
            }

            var props = await session.TryGetMediaPropertiesAsync();
            if (version != _version) return;

            var playback = session.GetPlaybackInfo();
            var timeline = session.GetTimelineProperties();
            string title = props?.Title ?? "";
            string artist = props?.Artist ?? "";
            if (string.IsNullOrEmpty(artist)) artist = props?.AlbumArtist ?? "";
            bool playing = playback.PlaybackStatus == PlaybackStatus.Playing;

            if (string.IsNullOrWhiteSpace(title) && !playing)
            {
                Publish(null);
                return;
            }

            string key = $"{title}\u001f{artist}\u001f{props?.AlbumTitle}";
            if (key != _artKey)
            {
                if (props?.Thumbnail is { } thumb)
                {
                    var (art, accent) = await LoadArtAsync(thumb);
                    if (art is not null)
                    {
                        _artKey = key;
                        _art = art;
                        _accent = accent;
                    }
                    if (version != _version) return;
                }
                else
                {
                    // Some players publish the thumbnail a moment after the title; retry on the next update.
                    _art = null;
                    _accent = ColorUtil.Neutral;
                }
            }

            var updated = timeline.LastUpdatedTime;
            if (updated.Year < 2000) updated = DateTimeOffset.Now;

            Publish(new MediaSnapshot
            {
                Title = title,
                Artist = artist,
                Album = props?.AlbumTitle ?? "",
                SourceApp = FriendlyAppName(session.SourceAppUserModelId),
                IsPlaying = playing,
                CanSkipNext = playback.Controls.IsNextEnabled,
                CanSkipPrevious = playback.Controls.IsPreviousEnabled,
                CanSeek = playback.Controls.IsPlaybackPositionEnabled,
                Position = timeline.Position - timeline.StartTime,
                Duration = timeline.EndTime - timeline.StartTime,
                PositionUpdated = updated,
                Art = key == _artKey ? _art : null,
                Accent = key == _artKey ? _accent : ColorUtil.Neutral,
            });
        }
        catch (Exception ex)
        {
            // Sessions can vanish mid-query when an app closes.
            Log.Error("media refresh", ex);
            if (version == _version) Publish(null);
        }
    }

    private void Publish(MediaSnapshot? snapshot)
    {
        Current = snapshot;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static async Task<(BitmapSource?, Color)> LoadArtAsync(IRandomAccessStreamReference thumb)
    {
        try
        {
            var buffer = new MemoryStream();
            using (var ras = await thumb.OpenReadAsync())
            using (var stream = ras.AsStreamForRead())
            {
                await stream.CopyToAsync(buffer);
            }
            buffer.Position = 0;

            return await Task.Run(() =>
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.DecodePixelWidth = 192;
                bmp.StreamSource = buffer;
                bmp.EndInit();
                bmp.Freeze();
                return ((BitmapSource?)bmp, ColorUtil.AccentFrom(bmp));
            });
        }
        catch (Exception ex)
        {
            Log.Error("album art", ex);
            return (null, ColorUtil.Neutral);
        }
    }

    private static string FriendlyAppName(string aumid)
    {
        if (string.IsNullOrEmpty(aumid)) return "";
        string id = aumid.ToLowerInvariant();
        if (id.Contains("spotify")) return "Spotify";
        if (id.Contains("zunemusic")) return "Media Player";
        if (id.Contains("applemusic")) return "Apple Music";
        if (id.Contains("msedge")) return "Edge";
        if (id.Contains("chrome")) return "Chrome";
        if (id.Contains("firefox") || id == "308046b0af4a39cb") return "Firefox";
        if (id.Contains("opera")) return "Opera";
        if (id.Contains("brave")) return "Brave";
        if (id.Contains("vlc")) return "VLC";
        if (id.Contains("foobar")) return "foobar2000";

        string name = aumid;
        int bang = name.IndexOf('!');
        if (bang > 0) name = name[..bang];
        int underscore = name.IndexOf('_');
        if (underscore > 0) name = name[..underscore];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        int dot = name.LastIndexOf('.');
        if (dot >= 0 && dot < name.Length - 1) name = name[(dot + 1)..];
        return name.Length > 0 ? char.ToUpperInvariant(name[0]) + name[1..] : "";
    }
}
