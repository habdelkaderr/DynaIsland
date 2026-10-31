using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DynaIsland.Services;

namespace DynaIsland;

// System activity -> island alerts and cards.
public partial class MainWindow
{
    private readonly DispatcherTimer _clipboardDebounce;
    private readonly DispatcherTimer _clipImageTimer;
    private IntPtr _brightnessNotification;
    private float _lastVolume = -1;
    private bool _lastMuted;
    private bool? _lastMicMuted;
    private int _lastBrightness = -1;
    private bool? _lastCaps;
    private bool? _lastDnd;
    private IntPtr _keyboardWindow;
    private int _keyboardLanguage;
    private BitmapSource? _pendingClipImage;
    private DateTime _lastScreenshotFile = DateTime.MinValue;

    // ---- Polled inputs: volume, mic mute, caps lock, keyboard layout --------------------------

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

        if (_settings.ShowKeyboardLayout)
        {
            // Only a change within the same window counts; switching windows isn't a layout switch.
            var (window, language) = Native.ForegroundKeyboardLanguage();
            if (window == _keyboardWindow && language != _keyboardLanguage && language != 0 && _keyboardLanguage != 0)
                ShowKeyboardAlert(language);
            _keyboardWindow = window;
            _keyboardLanguage = language;
        }
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

    private void ShowKeyboardAlert(int languageId)
    {
        try
        {
            var culture = CultureInfo.GetCultureInfo(languageId);
            var language = culture.IsNeutralCulture || culture.Parent == CultureInfo.InvariantCulture ? culture : culture.Parent;
            ShowAlert("", language.NativeName, culture.TwoLetterISOLanguageName.ToUpperInvariant(), null, ColorUtil.Neutral);
        }
        catch (CultureNotFoundException)
        {
            ShowAlert("", "Keyboard layout", $"0x{languageId:X4}", null, ColorUtil.Neutral);
        }
    }

    // ---- Brightness, power, Do Not Disturb ---------------------------------------------------

    private void OnBrightnessChanged(int level)
    {
        bool first = _lastBrightness < 0;
        bool changed = level != _lastBrightness;
        _lastBrightness = level;
        if (first || !changed || !_settings.ShowBrightness) return;

        ShowAlert("", "Brightness", $"{level}", level / 100.0, ColorUtil.Neutral);
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

    private void CheckDoNotDisturb()
    {
        if (Native.IsDoNotDisturbOn() is not bool on) return;
        if (_lastDnd is bool previous && previous != on && _settings.ShowDoNotDisturb)
            ShowAlert("", on ? "Do Not Disturb on" : "Do Not Disturb off", "", null, on ? Indigo : ColorUtil.Neutral);
        _lastDnd = on;
    }

    // ---- Clipboard & screenshots -------------------------------------------------------------

    private void OnClipboardChanged()
    {
        string detail;
        try
        {
            // Respect apps (password managers) that ask clipboard monitors to stay away.
            bool sensitive = Clipboard.ContainsData("ExcludeClipboardContentFromMonitorProcessing")
                          || Clipboard.ContainsData("CanIncludeInClipboardHistory");

            if (!sensitive && Clipboard.ContainsImage())
            {
                if (_settings.ShowScreenshots && Thumbnail(Clipboard.GetImage()) is { } thumb)
                {
                    // Win+Shift+S also saves a file; wait briefly so the saved file (clickable) wins.
                    _pendingClipImage = thumb;
                    _clipImageTimer.Stop();
                    _clipImageTimer.Start();
                    return;
                }
                detail = "Image";
            }
            else if (sensitive) detail = "";
            else if (Clipboard.ContainsText()) detail = Clipboard.GetText().Trim().ReplaceLineEndings(" ");
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

        if (!_settings.ShowClipboard) return;
        if (detail.Length > 40) detail = detail[..40] + "…";
        ShowAlert("", "Copied", detail, null, ColorUtil.Neutral);
    }

    private void ShowPendingClipboardImage()
    {
        _clipImageTimer.Stop();
        if (_pendingClipImage is not { } thumb) return;
        _pendingClipImage = null;
        if (DateTime.Now - _lastScreenshotFile < TimeSpan.FromSeconds(3)) return;
        ShowShot(thumb, "Image copied", "Ready to paste", null);
    }

    private async void OnScreenshotSaved(string path)
    {
        if (!_settings.ShowScreenshots) return;
        _lastScreenshotFile = DateTime.Now;
        _pendingClipImage = null;
        _clipImageTimer.Stop();

        var thumb = await LoadThumbnailAsync(path);
        if (thumb is null) return;
        ShowShot(thumb, "Screenshot saved", "Click to open", () => OpenPath(path));
    }

    private void ShowShot(BitmapSource thumb, string title, string subtitle, Action? onClick)
    {
        ShotThumb.Background = Freeze(new ImageBrush(thumb) { Stretch = Stretch.UniformToFill });
        ShotTitle.Text = title;
        ShotSubtitle.Text = subtitle;
        ShowTransient(IslandState.Shot, 4000, onClick);
    }

    private static BitmapSource? Thumbnail(BitmapSource? source)
    {
        if (source is null || source.PixelWidth == 0) return null;
        double scale = Math.Min(1, 320.0 / source.PixelWidth);
        var scaled = new TransformedBitmap(source, new ScaleTransform(scale, scale));
        var copy = new WriteableBitmap(scaled);
        copy.Freeze();
        return copy;
    }

    private static async Task<BitmapSource?> LoadThumbnailAsync(string path)
    {
        // The file may still be being written; retry briefly.
        for (int attempt = 0; attempt < 8; attempt++)
        {
            try
            {
                return await Task.Run(() =>
                {
                    using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                    var bmp = new BitmapImage();
                    bmp.BeginInit();
                    bmp.CacheOption = BitmapCacheOption.OnLoad;
                    bmp.DecodePixelWidth = 320;
                    bmp.StreamSource = stream;
                    bmp.EndInit();
                    bmp.Freeze();
                    return (BitmapSource)bmp;
                });
            }
            catch (Exception) when (attempt < 7)
            {
                await Task.Delay(250);
            }
            catch (Exception ex)
            {
                Log.Error("screenshot", ex);
            }
        }
        return null;
    }

    private static void OpenPath(string path, bool select = false)
    {
        try
        {
            var info = select
                ? new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"")
                : new ProcessStartInfo(path) { UseShellExecute = true };
            Process.Start(info);
        }
        catch (Exception ex)
        {
            Log.Error("open", ex);
        }
    }

    // ---- Downloads ---------------------------------------------------------------------------

    private void OnDownloadStarted(string name)
    {
        if (_settings.ShowDownloads) ShowAlert("", "Downloading", name, null, Blue);
    }

    private void OnDownloadCompleted(string path)
    {
        if (!_settings.ShowDownloads) return;
        ShowNotice("", null, "Downloads", "Download complete", Path.GetFileName(path), Green, 5000,
            () => OpenPath(path, select: true));
    }

    // ---- Mic / camera in use -----------------------------------------------------------------

    private void OnSensorStarted(SensorKind sensor, string app)
    {
        if (!_settings.ShowPrivacy) return;
        bool mic = sensor == SensorKind.Microphone;
        ShowAlert(mic ? "" : "", mic ? "Microphone in use" : "Camera in use", app, null, mic ? Orange : Green);
    }

    /// <summary>Timer bubble (when another activity owns the island) and privacy dots.</summary>
    private void UpdateSideItems()
    {
        bool bubble = _timer.IsActive && _state is not (IslandState.Timer or IslandState.TimerExpanded or IslandState.Idle);
        if (bubble != _bubbleShown)
        {
            _bubbleShown = bubble;
            if (bubble) SideBubble.Visibility = Visibility.Visible;
            var fade = new DoubleAnimation(bubble ? 1 : 0, TimeSpan.FromMilliseconds(bubble ? 260 : 140));
            if (!bubble) fade.Completed += (_, _) => { if (!_bubbleShown) SideBubble.Visibility = Visibility.Collapsed; };
            SideBubble.BeginAnimation(OpacityProperty, fade);
        }

        bool camera = _settings.ShowPrivacy && _privacy.CameraApps.Count > 0;
        bool mic = _settings.ShowPrivacy && _privacy.MicrophoneApps.Count > 0;
        CameraDot.Visibility = camera ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Visibility = mic ? Visibility.Visible : Visibility.Collapsed;
        MicDot.Margin = new Thickness(camera ? 4 : 0, 0, 0, 0);
        PrivacyDots.Visibility = camera || mic ? Visibility.Visible : Visibility.Collapsed;

        Dispatcher.InvokeAsync(() => PlaceSideItems(_w.Value), DispatcherPriority.Loaded);
    }

    // ---- Bluetooth & network -----------------------------------------------------------------

    private void OnBluetooth(BluetoothEvent e)
    {
        if (!_settings.ShowBluetooth) return;
        string glyph = BluetoothService.GlyphFor(e.Name);
        if (!e.Connected)
        {
            ShowAlert(glyph, e.Name, "Disconnected", null, ColorUtil.Neutral);
        }
        else if (e.Battery is int battery)
        {
            ShowAlert(glyph, e.Name, $"{battery}%", battery / 100.0, Blue, battery <= 20 ? Red : Green, 3500);
        }
        else
        {
            ShowAlert(glyph, e.Name, "Connected", null, Blue, null, 3000);
        }
    }

    private void OnNetworkChanged(NetworkState previous, NetworkState current)
    {
        if (!_settings.ShowNetwork) return;
        if (previous.Vpn != current.Vpn)
        {
            if (current.Vpn is { } vpn) ShowAlert("", "VPN connected", vpn, null, Green);
            else ShowAlert("", "VPN disconnected", "", null, ColorUtil.Neutral);
            return;
        }

        if (!current.Online)
        {
            if (previous.Online) ShowAlert("", "Offline", "No internet", null, Red);
            return;
        }

        if (previous.Online != current.Online || previous.Name != current.Name || previous.IsWifi != current.IsWifi)
        {
            ShowAlert(current.IsWifi ? "" : "", current.Limited ? "Limited connection" : "Connected",
                current.Name, null, current.Limited ? Orange : Green);
        }
    }

    // ---- Notifications & calendar ------------------------------------------------------------

    private void OnToast(ToastInfo toast)
    {
        if (!_settings.ShowNotifications || _lastDnd == true || _fullscreenHidden) return;
        var (name, icon) = ShellApps.Lookup(toast.AppId);
        ShowNotice("", icon, name, toast.Title, toast.Body, ColorUtil.Neutral, 5000, () => ShellApps.Launch(toast.AppId));
    }

    private void OnMeeting(Meeting meeting)
    {
        if (!_settings.ShowCalendar) return;
        string when = meeting.MinutesAway <= 0 ? "Starting now" : $"In {meeting.MinutesAway} min · {meeting.Start:t}";
        if (!string.IsNullOrWhiteSpace(meeting.Location)) when += $" · {meeting.Location}";
        ShowNotice("", null, "Calendar", meeting.Subject, when, Red, 8000, null);
        if (meeting.MinutesAway <= 0) PlayChime();
    }

    // ---- Shared presenters -------------------------------------------------------------------

    private void ShowAlert(string glyph, string text, string value, double? bar, Color iconColor,
                           Color? barColor = null, int milliseconds = 2200, Action? onClick = null)
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
        ShowTransient(IslandState.Alert, milliseconds, onClick);
    }

    private void ShowNotice(string glyph, BitmapSource? image, string app, string title, string body,
                            Color accent, int milliseconds, Action? onClick)
    {
        NotifyImage.Source = image;
        NotifyImage.Visibility = image is null ? Visibility.Collapsed : Visibility.Visible;
        NotifyIconTile.Visibility = NotifyGlyph.Visibility = image is null ? Visibility.Visible : Visibility.Collapsed;
        NotifyGlyph.Text = glyph;
        NotifyGlyph.Foreground = Freeze(new SolidColorBrush(accent));
        NotifyApp.Text = app;
        NotifyTitle.Text = title;
        NotifyBody.Text = body;
        NotifyBody.Visibility = string.IsNullOrWhiteSpace(body) ? Visibility.Collapsed : Visibility.Visible;
        ShowTransient(IslandState.Notify, milliseconds, onClick);
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
}
