using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynaIsland.Services;
using Microsoft.Win32;

namespace DynaIsland;

public partial class MainWindow : Window
{
    private enum IslandState { Idle, Clock, Compact, Peek, Expanded, Alert }

    private static readonly Dictionary<IslandState, (double W, double H, double R)> Shapes = new()
    {
        [IslandState.Idle] = (120, 30, 15),
        [IslandState.Clock] = (300, 42, 21),
        [IslandState.Compact] = (280, 34, 17),
        [IslandState.Peek] = (360, 54, 27),
        [IslandState.Expanded] = (400, 176, 40),
        [IslandState.Alert] = (330, 44, 22),
    };

    // How long a paused track keeps the compact island visible before it folds back to idle.
    private static readonly TimeSpan PausedLinger = TimeSpan.FromSeconds(90);

    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Brush PlaceholderArt = Freeze(new SolidColorBrush(Color.FromRgb(0x2C, 0x2C, 0x2E)));

    private readonly Settings _settings = Settings.Load();
    private readonly MediaService _media;
    private readonly PowerService _power;
    private readonly AudioVolume _volume = new();
    private readonly AudioVolume _mic = new(AudioVolume.Flow.Recording);
    private readonly Spring _w, _h, _r;
    private readonly SolidColorBrush _accent = new(ColorUtil.Neutral);
    private readonly DateTime _startedAt = DateTime.Now;

    private readonly DispatcherTimer _fastPoll;
    private readonly DispatcherTimer _slowPoll;
    private readonly DispatcherTimer _progressTimer;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _alertTimer;
    private readonly DispatcherTimer _peekTimer;
    private readonly DispatcherTimer _clipboardDebounce;

    private IntPtr _hwnd;
    private IslandState _state = IslandState.Idle;
    private bool _hovered, _alertActive, _peekActive, _fullscreenHidden, _faded;
    private string? _lastTrackKey;
    private BitmapSource? _shownArt;
    private DateTime _pausedSince = DateTime.MinValue;
    private float _lastVolume = -1;
    private bool _lastMuted;
    private bool? _lastMicMuted;
    private int _lastBrightness = -1;
    private IntPtr _brightnessNotification;
    private bool? _lastCaps;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private int _slowTicks;
    private TrayIcon? _tray;

    public MainWindow()
    {
        InitializeComponent();

        var idle = Shapes[IslandState.Idle];
        _w = new Spring(idle.W, stiffness: 300, damping: 25);
        _h = new Spring(idle.H, stiffness: 260, damping: 24);
        _r = new Spring(idle.R, stiffness: 300, damping: 28);

        foreach (var view in AllViews)
        {
            view.RenderTransformOrigin = new Point(0.5, 0.5);
            view.RenderTransform = new ScaleTransform(1, 1);
        }
        CompactBars.Fill = PeekBars.Fill = ExpBars.Fill = _accent;

        _media = new MediaService(Dispatcher);
        _media.Changed += (_, _) => OnMediaChanged();
        _power = new PowerService(Dispatcher);
        _power.Changed += (_, e) => OnPowerEvent(e);

        _fastPoll = MakeTimer(120, PollInputs, start: true);
        _slowPoll = MakeTimer(1000, SlowTick, start: true);
        _progressTimer = MakeTimer(250, UpdateProgress);
        _leaveTimer = MakeTimer(320, () => { _leaveTimer!.Stop(); _hovered = false; UpdateState(); });
        _alertTimer = MakeTimer(2200, () => { _alertTimer!.Stop(); _alertActive = false; UpdateState(); });
        _peekTimer = MakeTimer(3800, () => { _peekTimer!.Stop(); _peekActive = false; UpdateState(); });
        _clipboardDebounce = MakeTimer(150, () => { _clipboardDebounce!.Stop(); OnClipboardChanged(); });

        Island.MouseEnter += (_, _) => { _leaveTimer.Stop(); _hovered = true; UpdateState(); };
        Island.MouseLeave += (_, _) => _leaveTimer.Start();
        Island.MouseWheel += OnIslandWheel;
        Island.ContextMenu = BuildMenu();
        _tray = new TrayIcon(MenuToggles, ApplyToggle, () => _settings.ShowIsland, SetIslandVisible, Close);
        EnsureAutoStart();

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            PositionWindow();
            ApplyShape();
            UpdateIdleOpacity();
            ApplyVisibility();
            await _media.StartAsync();
        };
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private IEnumerable<FrameworkElement> AllViews => [IdleView, ClockView, CompactView, PeekView, ExpandedView, AlertView];

    private FrameworkElement ViewFor(IslandState state) => state switch
    {
        IslandState.Clock => ClockView,
        IslandState.Compact => CompactView,
        IslandState.Peek => PeekView,
        IslandState.Expanded => ExpandedView,
        IslandState.Alert => AlertView,
        _ => IdleView,
    };

    // ---- Window plumbing ---------------------------------------------------------------------

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _hwnd = new WindowInteropHelper(this).Handle;

        // Tool window (no Alt+Tab entry) that never steals focus from the app you're using.
        long ex = Native.GetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE).ToInt64();
        Native.SetWindowLongPtr(_hwnd, Native.GWL_EXSTYLE, new IntPtr(ex | Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE));

        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);
        Native.AddClipboardFormatListener(_hwnd);

        // Event-driven brightness (built-in displays): Windows sends the current level right away, then on every change.
        var brightness = Native.GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS;
        _brightnessNotification = Native.RegisterPowerSettingNotification(_hwnd, ref brightness, Native.DEVICE_NOTIFY_WINDOW_HANDLE);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Native.WM_CLIPBOARDUPDATE)
        {
            _clipboardDebounce.Stop();
            _clipboardDebounce.Start();
        }
        else if (msg == Native.WM_POWERBROADCAST && wParam.ToInt32() == Native.PBT_POWERSETTINGCHANGE &&
                 Native.TryReadPowerSetting(lParam, out var setting, out int value) &&
                 setting == Native.GUID_VIDEO_CURRENT_MONITOR_BRIGHTNESS)
        {
            OnBrightnessChanged(value);
        }
        return IntPtr.Zero;
    }

    private void OnBrightnessChanged(int level)
    {
        bool first = _lastBrightness < 0;
        bool changed = level != _lastBrightness;
        _lastBrightness = level;
        if (first || !changed || !_settings.ShowBrightness) return;

        ShowAlert("", "Brightness", $"{level}", level / 100.0, ColorUtil.Neutral);
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_hwnd != IntPtr.Zero) Native.RemoveClipboardFormatListener(_hwnd);
        if (_brightnessNotification != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_brightnessNotification);
        _tray?.Dispose();
        _mic.Dispose();
        _volume.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(PositionWindow);

    private void PositionWindow()
    {
        Left = (SystemParameters.PrimaryScreenWidth - Width) / 2;
        Top = 0;
    }

    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || Island.ContextMenu?.IsOpen == true) return;
        Native.SetWindowPos(_hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
            Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE | Native.SWP_NOOWNERZORDER);
    }

    private DispatcherTimer MakeTimer(int ms, Action tick, bool start = false)
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
        timer.Tick += (_, _) => tick();
        if (start) timer.Start();
        return timer;
    }

    // ---- State machine -----------------------------------------------------------------------

    private void UpdateState()
    {
        var media = _settings.ShowMedia ? _media.Current : null;
        bool showCompact = media is not null && (media.IsPlaying || DateTime.Now - _pausedSince < PausedLinger);

        var target =
            _alertActive ? IslandState.Alert :
            _hovered && media is not null ? IslandState.Expanded :
            _hovered ? IslandState.Clock :
            _peekActive && media is not null ? IslandState.Peek :
            showCompact ? IslandState.Compact :
            IslandState.Idle;

        if (target != _state) Transition(target);
        UpdateVisualizers();
        UpdateIdleOpacity();
    }

    private void Transition(IslandState next)
    {
        var from = ViewFor(_state);
        var to = ViewFor(next);
        _state = next;

        if (from != to)
        {
            FadeOut(from);
            FadeIn(to);
        }

        var shape = Shapes[next];
        _w.Target = shape.W;
        _h.Target = shape.H;
        _r.Target = shape.R;
        StartAnimating();

        if (next == IslandState.Expanded)
        {
            UpdateProgress();
            _progressTimer.Start();
        }
        else
        {
            _progressTimer.Stop();
        }

        if (next == IslandState.Clock) UpdateClock();
    }

    private void FadeIn(FrameworkElement view)
    {
        view.Visibility = Visibility.Visible;

        var fade = new DoubleAnimationUsingKeyFrames();
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        fade.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(70))));
        fade.KeyFrames.Add(new EasingDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(300)),
            new CubicEase { EasingMode = EasingMode.EaseOut }));
        view.BeginAnimation(OpacityProperty, fade);

        var scale = (ScaleTransform)view.RenderTransform;
        var grow = new DoubleAnimation(0.9, 1, TimeSpan.FromMilliseconds(420))
        {
            EasingFunction = new BackEase { Amplitude = 0.35, EasingMode = EasingMode.EaseOut },
        };
        scale.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        scale.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
    }

    private void FadeOut(FrameworkElement view)
    {
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(110));
        fade.Completed += (_, _) =>
        {
            if (ViewFor(_state) != view) view.Visibility = Visibility.Collapsed;
        };
        view.BeginAnimation(OpacityProperty, fade);
    }

    private void UpdateIdleOpacity()
    {
        bool fade = _settings.HideWhenIdle && _state == IslandState.Idle && !_hovered;
        if (fade == _faded) return;
        _faded = fade;

        // Not fully transparent so the faded pill can still be hovered (alpha 0 pixels are click-through).
        var anim = new DoubleAnimation(fade ? 0.02 : 1, TimeSpan.FromMilliseconds(fade ? 600 : 150));
        Island.BeginAnimation(OpacityProperty, anim);
        Shadow.BeginAnimation(OpacityProperty, new DoubleAnimation(fade ? 0 : 0.55, anim.Duration));
    }

    // ---- Spring-driven shape animation -------------------------------------------------------

    private void StartAnimating()
    {
        if (_animating) return;
        _animating = true;
        _lastFrame = null;
        CompositionTarget.Rendering += OnRendering;
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        var now = ((RenderingEventArgs)e).RenderingTime;
        if (_lastFrame == now) return; // Rendering can fire more than once per frame.
        double dt = _lastFrame is { } last ? (now - last).TotalSeconds : 1 / 60.0;
        _lastFrame = now;
        dt = Math.Clamp(dt, 0, 1 / 20.0);

        const double step = 1 / 480.0;
        for (double t = 0; t < dt; t += step)
        {
            double h = Math.Min(step, dt - t);
            _w.Step(h);
            _h.Step(h);
            _r.Step(h);
        }

        bool settled = _w.IsSettled && _h.IsSettled && _r.IsSettled;
        if (settled)
        {
            _w.Snap();
            _h.Snap();
            _r.Snap();
        }

        ApplyShape();

        if (settled)
        {
            CompositionTarget.Rendering -= OnRendering;
            _animating = false;
        }
    }

    private void ApplyShape()
    {
        double w = Math.Max(1, _w.Value);
        double h = Math.Max(1, _h.Value);
        double r = Math.Clamp(_r.Value, 0, Math.Min(w, h) / 2);
        var corner = new CornerRadius(r);

        Island.Width = w;
        Island.Height = h;
        Island.CornerRadius = corner;
        Shadow.Width = w;
        Shadow.Height = h;
        Shadow.CornerRadius = corner;
        Stage.Clip = new RectangleGeometry(new Rect(0, 0, w, h), r, r);
    }

    // ---- Media -------------------------------------------------------------------------------

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
        if (newTrack && media.IsPlaying && !_hovered && DateTime.Now - _startedAt > TimeSpan.FromSeconds(3))
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

    // ---- System activity ---------------------------------------------------------------------

    private void PollInputs()
    {
        if (_settings.ShowVolume && _volume.TryRead(out float level, out bool muted))
        {
            if (_lastVolume < 0 || _volume.DeviceChanged)
            {
                _lastVolume = level;
                _lastMuted = muted;
            }
            else if (Math.Abs(level - _lastVolume) > 0.004 || muted != _lastMuted)
            {
                _lastVolume = level;
                _lastMuted = muted;
                ShowVolumeAlert(level, muted);
            }
        }

        if (_settings.ShowVolume && _mic.TryRead(out _, out bool micMuted))
        {
            if (_lastMicMuted is bool previous && previous != micMuted && !_mic.DeviceChanged)
            {
                ShowAlert(micMuted ? "" : "", micMuted ? "Microphone muted" : "Microphone on", "", null,
                    micMuted ? Red : ColorUtil.Neutral);
            }
            _lastMicMuted = micMuted;
        }
        else
        {
            _lastMicMuted = null;
        }

        if (_settings.ShowCapsLock)
        {
            bool caps = Native.IsCapsLockOn();
            if (_lastCaps is bool previous && previous != caps)
                ShowAlert("", caps ? "Caps Lock on" : "Caps Lock off", "", null, caps ? Green : ColorUtil.Neutral);
            _lastCaps = caps;
        }
        else
        {
            _lastCaps = null;
        }
    }

    private void SlowTick()
    {
        if (_state == IslandState.Clock) UpdateClock();

        bool fullscreen = _settings.HideInFullscreen && Native.IsFullscreenAppRunning();
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            ApplyVisibility();
        }

        if (++_slowTicks % 3 == 0) KeepOnTop();
        UpdateState(); // lets a long-paused track fold back to idle
    }

    private void OnIslandWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_volume.TryRead(out float level, out _))
            _volume.SetLevel(level + Math.Sign(e.Delta) * 0.02f);
    }

    private void ShowVolumeAlert(float level, bool muted)
    {
        string glyph = muted || level < 0.005f ? "" : level < 0.34f ? "" : level < 0.67f ? "" : "";
        ShowAlert(glyph, muted ? "Muted" : "Volume", muted ? "" : $"{Math.Round(level * 100)}",
            muted ? 0 : level, ColorUtil.Neutral);
    }

    private void OnPowerEvent(PowerEvent e)
    {
        if (!_settings.ShowBattery) return;
        int pct = PowerService.Percent;
        switch (e)
        {
            case PowerEvent.PluggedIn:
                ShowAlert(BatteryGlyph(pct, charging: true), "Charging", $"{pct}%", pct / 100.0, Green, Green);
                break;
            case PowerEvent.Unplugged:
                ShowAlert(BatteryGlyph(pct, charging: false), "On battery", $"{pct}%", pct / 100.0, ColorUtil.Neutral);
                break;
            case PowerEvent.LowBattery:
                ShowAlert(BatteryGlyph(pct, charging: false), "Low battery", $"{pct}%", pct / 100.0, Red, Red);
                break;
        }
    }

    private static string BatteryGlyph(int percent, bool charging)
    {
        int step = Math.Clamp(percent / 10, 0, 10);
        return char.ConvertFromUtf32((charging ? 0xEBAB : 0xEBA0) + step);
    }

    private void OnClipboardChanged()
    {
        if (!_settings.ShowClipboard) return;
        string detail;
        try
        {
            // Respect apps (password managers) that ask clipboard monitors to stay away.
            bool sensitive = Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")
                          || Clipboard.ContainsData("CanIncludeInClipboardHistory");

            if (sensitive) detail = "";
            else if (Clipboard.ContainsText()) detail = Clipboard.GetText().Trim().ReplaceLineEndings(" ");
            else if (Clipboard.ContainsImage()) detail = "Image";
            else if (Clipboard.ContainsFileDropList())
            {
                var files = Clipboard.GetFileDropList();
                detail = files.Count == 1 ? Path.GetFileName(files[0] ?? "") : $"{files.Count} files";
            }
            else return;
        }
        catch
        {
            return; // clipboard locked by another process
        }

        if (detail.Length > 40) detail = detail[..40] + "…";
        ShowAlert("", "Copied", detail, null, ColorUtil.Neutral);
    }

    private void ShowAlert(string glyph, string text, string value, double? bar, Color iconColor, Color? barColor = null)
    {
        AlertIcon.Text = glyph;
        AlertIcon.Foreground = Freeze(new SolidColorBrush(iconColor));
        AlertText.Text = text;
        AlertValue.Text = value;
        AlertBar.Visibility = bar.HasValue ? Visibility.Visible : Visibility.Collapsed;
        if (bar is double fraction)
        {
            AlertBarFill.Background = Freeze(new SolidColorBrush(barColor ?? Colors.White));
            AlertBarScale.BeginAnimation(ScaleTransform.ScaleXProperty,
                new DoubleAnimation(Math.Clamp(fraction, 0, 1), TimeSpan.FromMilliseconds(140)));
        }

        _alertActive = true;
        _alertTimer.Stop();
        _alertTimer.Start();
        UpdateState();
    }

    private void UpdateClock()
    {
        var now = DateTime.Now;
        ClockTime.Text = now.ToString("t");
        ClockDate.Text = now.ToString("ddd, MMM d");

        if (!PowerService.HasBattery)
        {
            ClockBattery.Visibility = Visibility.Collapsed;
            return;
        }

        int pct = PowerService.Percent;
        bool plugged = PowerService.IsPluggedIn;
        ClockBattery.Visibility = Visibility.Visible;
        ClockBatteryText.Text = $"{pct}%";
        ClockBatteryIcon.Text = BatteryGlyph(pct, plugged);
        ClockBatteryIcon.Foreground = Freeze(new SolidColorBrush(plugged ? Green : pct <= 20 ? Red : ColorUtil.Neutral));
    }

    // ---- Settings menu -----------------------------------------------------------------------

    private IReadOnlyList<MenuToggle> MenuToggles =>
    [
        new("Now playing", () => _settings.ShowMedia, v => _settings.ShowMedia = v),
        new("Volume & mute", () => _settings.ShowVolume, v => _settings.ShowVolume = v),
        new("Brightness", () => _settings.ShowBrightness, v => _settings.ShowBrightness = v),
        new("Battery & charging", () => _settings.ShowBattery, v => _settings.ShowBattery = v),
        new("Caps Lock", () => _settings.ShowCapsLock, v => _settings.ShowCapsLock = v),
        new("Clipboard copies", () => _settings.ShowClipboard, v => _settings.ShowClipboard = v),
        new("Hide in full-screen apps", () => _settings.HideInFullscreen, v => _settings.HideInFullscreen = v, SeparatorBefore: true),
        new("Fade out when idle", () => _settings.HideWhenIdle, v => _settings.HideWhenIdle = v),
        new("Start with Windows", () => Settings.StartWithWindows, v => Settings.StartWithWindows = v),
    ];

    private void ApplyToggle(MenuToggle toggle, bool value)
    {
        try
        {
            toggle.Set(value);
        }
        catch (Exception ex)
        {
            Log.Error($"toggle {toggle.Header}", ex);
        }
        _settings.Save();
        _lastVolume = -1;
        UpdateState();
    }

    private ContextMenu BuildMenu()
    {
        var menu = new ContextMenu();
        var items = new List<(MenuItem Item, MenuToggle Toggle)>();
        foreach (var toggle in MenuToggles)
        {
            if (toggle.SeparatorBefore) menu.Items.Add(new Separator());
            var item = new MenuItem { Header = toggle.Header, IsCheckable = true, StaysOpenOnClick = true };
            item.Click += (_, _) =>
            {
                ApplyToggle(toggle, item.IsChecked);
                item.IsChecked = toggle.Get();
            };
            menu.Items.Add(item);
            items.Add((item, toggle));
        }

        // Keep the ticks in sync with changes made from the tray menu.
        menu.Opened += (_, _) =>
        {
            foreach (var (item, toggle) in items) item.IsChecked = toggle.Get();
        };

        menu.Items.Add(new Separator());
        var hide = new MenuItem { Header = "Hide island (restore from tray)" };
        hide.Click += (_, _) => SetIslandVisible(false);
        menu.Items.Add(hide);
        var quit = new MenuItem { Header = "Quit DynaIsland" };
        quit.Click += (_, _) => Close();
        menu.Items.Add(quit);
        return menu;
    }

    private void SetIslandVisible(bool visible)
    {
        _settings.ShowIsland = visible;
        _settings.Save();
        ApplyVisibility();
    }

    private void ApplyVisibility()
    {
        bool show = _settings.ShowIsland && !_fullscreenHidden;
        if (show == IsVisible) return;
        if (show)
        {
            Show();
            KeepOnTop();
        }
        else
        {
            Hide();
        }
    }

    /// <summary>
    /// Turns auto-start on the first time the app runs, and keeps the Run entry pointing at
    /// this exe if it was moved (for example after reinstalling to a new folder).
    /// </summary>
    private void EnsureAutoStart()
    {
        try
        {
            if (!_settings.AutoStartConfigured)
            {
                Settings.StartWithWindows = true;
                _settings.AutoStartConfigured = true;
                _settings.Save();
            }
            else if (Settings.StartWithWindows)
            {
                Settings.StartWithWindows = true;
            }
        }
        catch (Exception ex)
        {
            Log.Error("auto-start", ex);
        }
    }

    private static T Freeze<T>(T freezable) where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
