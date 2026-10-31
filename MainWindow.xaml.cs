using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DynaIsland.Services;
using Microsoft.Win32;
using Forms = System.Windows.Forms;

namespace DynaIsland;

public partial class MainWindow : Window
{
    private enum IslandState { Idle, Clock, Compact, Peek, Expanded, Timer, TimerExpanded, Alert, Notify, Shot }

    private static readonly Dictionary<IslandState, (double W, double H, double R)> Shapes = new()
    {
        [IslandState.Idle] = (120, 30, 15),
        [IslandState.Clock] = (300, 42, 21),
        [IslandState.Compact] = (280, 34, 17),
        [IslandState.Peek] = (360, 54, 27),
        [IslandState.Expanded] = (400, 176, 40),
        [IslandState.Timer] = (280, 34, 17),
        [IslandState.TimerExpanded] = (380, 84, 34),
        [IslandState.Alert] = (330, 44, 22),
        [IslandState.Notify] = (400, 68, 30),
        [IslandState.Shot] = (360, 84, 30),
    };

    private const double BaseWidth = 640;
    private const double BaseHeight = 240;
    private const double SideGap = 6;

    // How long a paused track keeps the compact island visible before it folds back to idle.
    private static readonly TimeSpan PausedLinger = TimeSpan.FromSeconds(90);

    private static readonly Color Green = Color.FromRgb(0x30, 0xD1, 0x58);
    private static readonly Color Red = Color.FromRgb(0xFF, 0x45, 0x3A);
    private static readonly Color Orange = Color.FromRgb(0xFF, 0x9F, 0x0A);
    private static readonly Color Indigo = Color.FromRgb(0x5E, 0x5C, 0xE6);
    private static readonly Color Blue = Color.FromRgb(0x0A, 0x84, 0xFF);

    private readonly Settings _settings;
    private readonly MediaService _media;
    private readonly PowerService _power;
    private readonly AudioVolume _volume = new();
    private readonly AudioVolume _mic = new(AudioVolume.Flow.Recording);
    private readonly TimerService _timer = new();
    private readonly PrivacyService _privacy;
    private readonly BluetoothService _bluetooth;
    private readonly NetworkService _network;
    private readonly FolderWatchService _folders;
    private readonly NotificationService _notifications;
    private readonly CalendarService _calendar;
    private readonly Spring _w, _h, _r;
    private readonly DateTime _startedAt = DateTime.Now;
    private readonly Forms.ContextMenuStrip _menu;

    private readonly DispatcherTimer _fastPoll;
    private readonly DispatcherTimer _slowPoll;
    private readonly DispatcherTimer _uiTick;
    private readonly DispatcherTimer _leaveTimer;
    private readonly DispatcherTimer _transientTimer;
    private readonly DispatcherTimer _peekTimer;

    private IntPtr _hwnd;
    private IslandState _state = IslandState.Idle;
    private IslandState? _transient;
    private Action? _transientClick;
    private bool _hovered, _pinned, _peekActive, _fullscreenHidden, _faded, _timerFocused, _bubbleShown;
    private TimeSpan? _lastFrame;
    private bool _animating;
    private int _slowTicks;
    private TrayIcon? _tray;

    // Gestures
    private Point _pressPoint;
    private bool _pressed, _gestureUsed;

    public MainWindow(Settings settings)
    {
        _settings = settings;
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
        _privacy = new PrivacyService(Dispatcher);
        _privacy.Started += (_, e) => OnSensorStarted(e.Sensor, e.App);
        _privacy.Changed += (_, _) => UpdateSideItems();
        _bluetooth = new BluetoothService(Dispatcher);
        _bluetooth.Changed += (_, e) => OnBluetooth(e);
        _network = new NetworkService(Dispatcher);
        _network.Changed += (_, e) => OnNetworkChanged(e.Previous, e.Current);
        _folders = new FolderWatchService(Dispatcher);
        _folders.DownloadStarted += (_, name) => OnDownloadStarted(name);
        _folders.DownloadCompleted += (_, path) => OnDownloadCompleted(path);
        _folders.ScreenshotSaved += (_, path) => OnScreenshotSaved(path);
        _notifications = new NotificationService(Dispatcher);
        _notifications.Received += (_, toast) => OnToast(toast);
        _calendar = new CalendarService(Dispatcher);
        _calendar.Upcoming += (_, meeting) => OnMeeting(meeting);

        _fastPoll = MakeTimer(120, PollInputs, start: true);
        _slowPoll = MakeTimer(1000, SlowTick, start: true);
        _uiTick = MakeTimer(250, OnUiTick);
        _leaveTimer = MakeTimer(320, () => { _leaveTimer!.Stop(); _hovered = false; _timerFocused = false; UpdateState(); });
        _transientTimer = MakeTimer(2200, EndTransient);
        _peekTimer = MakeTimer(3800, () => { _peekTimer!.Stop(); _peekActive = false; UpdateState(); });
        _clipboardDebounce = MakeTimer(150, () => { _clipboardDebounce!.Stop(); OnClipboardChanged(); });
        _clipImageTimer = MakeTimer(1200, ShowPendingClipboardImage);

        Island.MouseEnter += (_, _) => { _leaveTimer.Stop(); _hovered = true; UpdateState(); };
        Island.MouseLeave += (_, _) => _leaveTimer.Start();
        Island.MouseWheel += OnIslandWheel;
        Island.MouseLeftButtonDown += OnIslandPress;
        Island.MouseMove += OnIslandDrag;
        Island.MouseLeftButtonUp += OnIslandRelease;
        Island.MouseRightButtonUp += (_, e) => { e.Handled = true; ShowMenuAtCursor(); };

        _menu = CreateMenu();
        _tray = new TrayIcon(_menu, () => SetIslandVisible(!_settings.ShowIsland));
        EnsureAutoStart();

        SourceInitialized += OnSourceInitialized;
        Loaded += async (_, _) =>
        {
            ApplyAppearance();
            ApplyShape();
            UpdateIdleOpacity();
            ApplyVisibility();
            await _media.StartAsync();
        };
        SizeChanged += (_, _) => PositionWindow();
        DpiChanged += (_, _) => Dispatcher.InvokeAsync(PositionWindow, DispatcherPriority.Background);
        Closed += OnClosed;
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private IEnumerable<FrameworkElement> AllViews =>
        [IdleView, ClockView, CompactView, PeekView, ExpandedView, TimerView, TimerExpandedView, AlertView, NotifyView, ShotView];

    private FrameworkElement ViewFor(IslandState state) => state switch
    {
        IslandState.Clock => ClockView,
        IslandState.Compact => CompactView,
        IslandState.Peek => PeekView,
        IslandState.Expanded => ExpandedView,
        IslandState.Timer => TimerView,
        IslandState.TimerExpanded => TimerExpandedView,
        IslandState.Alert => AlertView,
        IslandState.Notify => NotifyView,
        IslandState.Shot => ShotView,
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
        PositionWindow();
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

    private void OnClosed(object? sender, EventArgs e)
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (_hwnd != IntPtr.Zero) Native.RemoveClipboardFormatListener(_hwnd);
        if (_brightnessNotification != IntPtr.Zero) Native.UnregisterPowerSettingNotification(_brightnessNotification);
        _tray?.Dispose();
        _menu.Dispose();
        _volume.Dispose();
        _mic.Dispose();
        _privacy.Dispose();
        _bluetooth.Dispose();
        _folders.Dispose();
        _notifications.Dispose();
        _calendar.Dispose();
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Dispatcher.InvokeAsync(PositionWindow);

    private void KeepOnTop()
    {
        if (_hwnd == IntPtr.Zero || _menu.Visible) return;
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

    private void SlowTick()
    {
        if (_state == IslandState.Clock) UpdateClock();

        bool fullscreen = _settings.HideInFullscreen && Native.IsFullscreenAppRunning();
        if (fullscreen != _fullscreenHidden)
        {
            _fullscreenHidden = fullscreen;
            ApplyVisibility();
        }

        CheckDoNotDisturb();
        if (_settings.Screen == "Follow" && TargetMonitor() != _currentMonitor) PositionWindow();
        if (++_slowTicks % 3 == 0) KeepOnTop();
        UpdateState(); // lets a long-paused track fold back to idle
    }

    private void OnUiTick()
    {
        if (_timer.IsActive)
        {
            if (_timer.CheckFinished()) OnTimerFinished();
            UpdateTimerUi();
        }
        if (_state == IslandState.Expanded) UpdateProgress();
        if (!_timer.IsActive && _state != IslandState.Expanded) _uiTick.Stop();
    }

    // ---- State machine -----------------------------------------------------------------------

    private bool Engaged => _settings.ExpandOnHover ? _hovered : _pinned;

    private void UpdateState()
    {
        var media = _settings.ShowMedia ? _media.Current : null;
        bool showCompact = media is not null && (media.IsPlaying || DateTime.Now - _pausedSince < PausedLinger);
        bool timer = _timer.IsActive;

        var target =
            _transient is { } t ? t :
            Engaged && timer && (_timerFocused || media is null) ? IslandState.TimerExpanded :
            Engaged && media is not null ? IslandState.Expanded :
            Engaged ? IslandState.Clock :
            _peekActive && media is not null ? IslandState.Peek :
            showCompact ? IslandState.Compact :
            timer ? IslandState.Timer :
            IslandState.Idle;

        if (target != _state) Transition(target);
        UpdateVisualizers();
        UpdateSideItems();
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

        if (next == IslandState.Expanded) UpdateProgress();
        if (next is IslandState.Timer or IslandState.TimerExpanded) UpdateTimerUi();
        if (next == IslandState.Expanded || _timer.IsActive) _uiTick.Start();
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
        bool fade = _settings.HideWhenIdle && _state == IslandState.Idle && !_hovered && !_bubbleShown;
        if (fade == _faded) return;
        _faded = fade;

        // Not fully transparent so the faded pill can still be hovered (alpha 0 pixels are click-through).
        var anim = new DoubleAnimation(fade ? 0.02 : 1, TimeSpan.FromMilliseconds(fade ? 600 : 150));
        Island.BeginAnimation(OpacityProperty, anim);
        Shadow.BeginAnimation(OpacityProperty, new DoubleAnimation(fade ? 0 : ShadowOpacity, anim.Duration));
    }

    // ---- Transient cards (alerts, notifications, screenshots) --------------------------------

    private void ShowTransient(IslandState kind, int milliseconds, Action? onClick)
    {
        _transient = kind;
        _transientClick = onClick;
        _transientTimer.Stop();
        _transientTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        _transientTimer.Start();
        UpdateState();
    }

    private void EndTransient()
    {
        _transientTimer.Stop();
        _transient = null;
        _transientClick = null;
        UpdateState();
    }

    // ---- Gestures ----------------------------------------------------------------------------

    private void OnIslandPress(object sender, MouseButtonEventArgs e)
    {
        _pressPoint = e.GetPosition(this);
        _pressed = true;
        _gestureUsed = false;
        Island.CaptureMouse();
    }

    private void OnIslandDrag(object sender, MouseEventArgs e)
    {
        if (!_pressed || _gestureUsed) return;
        var delta = e.GetPosition(this) - _pressPoint;

        // Swipe left / right on the music island to skip tracks.
        bool onMedia = _state is IslandState.Compact or IslandState.Peek or IslandState.Expanded;
        if (onMedia && Math.Abs(delta.X) > 40 && Math.Abs(delta.X) > Math.Abs(delta.Y) * 1.5)
        {
            _gestureUsed = true;
            bool next = delta.X < 0;
            Nudge(next ? -1 : 1);
            _ = next ? _media.NextAsync() : _media.PreviousAsync();
        }
    }

    private void OnIslandRelease(object sender, MouseButtonEventArgs e)
    {
        if (!_pressed) return;
        _pressed = false;
        Island.ReleaseMouseCapture();
        if (_gestureUsed || (e.GetPosition(this) - _pressPoint).Length > 6) return;

        if (_transient is not null)
        {
            var click = _transientClick;
            EndTransient();
            click?.Invoke();
        }
        else if (!_settings.ExpandOnHover)
        {
            _pinned = !_pinned;
            UpdateState();
        }
    }

    private void Nudge(int direction)
    {
        var anim = new DoubleAnimation(0, 14 * direction, TimeSpan.FromMilliseconds(110))
        {
            AutoReverse = true,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseOut },
        };
        StageNudge.BeginAnimation(TranslateTransform.XProperty, anim);
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
        PlaceSideItems(w);
    }

    /// <summary>Keeps the timer bubble and privacy dots just to the right of the island as it morphs.</summary>
    private void PlaceSideItems(double islandWidth)
    {
        double x = islandWidth / 2 + SideGap;
        SideBubbleShift.X = x + SideBubble.Width / 2;
        if (_bubbleShown) x += SideBubble.Width + SideGap;
        double dotsWidth = PrivacyDots.ActualWidth > 0 ? PrivacyDots.ActualWidth : 8;
        PrivacyShift.X = x + dotsWidth / 2 + 2;
    }
}
