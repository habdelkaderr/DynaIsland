using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using Microsoft.Win32;

namespace DynaIsland.Services;

public enum SensorKind
{
    Microphone,
    Camera,
}

/// <summary>
/// Detects which apps are using the microphone or camera, from the same local registry data that
/// drives Windows' own privacy indicators. Event-driven (registry change notifications), no polling.
/// </summary>
public sealed class PrivacyService : IDisposable
{
    private const string ConsentStore = @"Software\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore";

    private readonly Dispatcher _ui;
    private readonly RegistryKey? _root;
    private readonly AutoResetEvent _signal = new(false);
    private readonly DispatcherTimer _debounce;
    private RegisteredWaitHandle? _wait;
    private bool _disposed;

    public IReadOnlyList<string> MicrophoneApps { get; private set; } = [];
    public IReadOnlyList<string> CameraApps { get; private set; } = [];

    /// <summary>Raised on the UI thread when an app starts using a sensor.</summary>
    public event EventHandler<(SensorKind Sensor, string App)>? Started;

    /// <summary>Raised on the UI thread whenever the in-use lists change.</summary>
    public event EventHandler? Changed;

    public PrivacyService(Dispatcher ui)
    {
        _ui = ui;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(250), DispatcherPriority.Normal,
            (_, _) => { _debounce!.Stop(); Scan(notify: true); }, ui);
        _debounce.Stop();

        try
        {
            _root = Registry.CurrentUser.OpenSubKey(ConsentStore);
            Scan(notify: false);
            Arm();
        }
        catch (Exception ex)
        {
            Log.Error("privacy", ex);
        }
    }

    private void Arm()
    {
        if (_root is null || _disposed) return;
        int filter = Native.REG_NOTIFY_CHANGE_NAME | Native.REG_NOTIFY_CHANGE_LAST_SET | Native.REG_NOTIFY_THREAD_AGNOSTIC;
        if (Native.RegNotifyChangeKeyValue(_root.Handle, true, filter, _signal.SafeWaitHandle, true) != 0) return;
        _wait = ThreadPool.RegisterWaitForSingleObject(_signal, (_, _) =>
        {
            if (_disposed) return;
            _ui.InvokeAsync(() => { _debounce.Stop(); _debounce.Start(); });
            Arm();
        }, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    private void Scan(bool notify)
    {
        var mic = InUse("microphone");
        var cam = InUse("webcam");

        if (notify)
        {
            foreach (var app in mic.Except(MicrophoneApps)) Started?.Invoke(this, (SensorKind.Microphone, app));
            foreach (var app in cam.Except(CameraApps)) Started?.Invoke(this, (SensorKind.Camera, app));
        }

        bool changed = !mic.SequenceEqual(MicrophoneApps) || !cam.SequenceEqual(CameraApps);
        MicrophoneApps = mic;
        CameraApps = cam;
        if (changed && notify) Changed?.Invoke(this, EventArgs.Empty);
    }

    private List<string> InUse(string capability)
    {
        var result = new List<string>();
        try
        {
            using var key = _root?.OpenSubKey(capability);
            if (key is null) return result;
            Collect(key, packaged: true);
            using var nonPackaged = key.OpenSubKey("NonPackaged");
            if (nonPackaged is not null) Collect(nonPackaged, packaged: false);
        }
        catch (Exception ex)
        {
            Log.Error("privacy scan", ex);
        }
        return result.Distinct().OrderBy(x => x).ToList();

        void Collect(RegistryKey parent, bool packaged)
        {
            foreach (var name in parent.GetSubKeyNames())
            {
                if (packaged && name == "NonPackaged") continue;
                using var app = parent.OpenSubKey(name);
                if (app?.GetValue("LastUsedTimeStart") is long start && start != 0 &&
                    app.GetValue("LastUsedTimeStop") is long stop && stop == 0)
                {
                    result.Add(FriendlyName(name, packaged));
                }
            }
        }
    }

    private static string FriendlyName(string key, bool packaged)
    {
        if (packaged)
        {
            string family = key.Split('_')[0];
            string last = family.Split('.').Last();
            return last.Length > 0 ? last : family;
        }

        string path = key.Replace('#', '\\');
        try
        {
            if (File.Exists(path))
            {
                string? description = FileVersionInfo.GetVersionInfo(path).FileDescription;
                if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
            }
        }
        catch
        {
            // fall back to the file name
        }
        return Path.GetFileNameWithoutExtension(path);
    }

    public void Dispose()
    {
        _disposed = true;
        _wait?.Unregister(null);
        _root?.Dispose();
        _signal.Dispose();
    }
}
