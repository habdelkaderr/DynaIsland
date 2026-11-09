using System.Windows.Threading;
using Windows.Devices.Radios;

namespace DynaIsland.Services;

/// <summary>
/// Wi-Fi and Bluetooth radios through Windows.Devices.Radios: real on/off state and control.
/// If Windows denies control, <see cref="CanControl"/> is false and callers offer Settings instead.
/// </summary>
public sealed class RadioService
{
    private readonly Dispatcher _ui;
    private Radio? _wifi, _bluetooth;

    public bool CanControl { get; private set; }
    public bool? WifiOn => _wifi is null ? null : _wifi.State == RadioState.On;
    public bool? BluetoothOn => _bluetooth is null ? null : _bluetooth.State == RadioState.On;

    /// <summary>Raised on the UI thread when a radio turns on or off.</summary>
    public event EventHandler? Changed;

    public RadioService(Dispatcher ui) => _ui = ui;

    private bool _started;

    /// <summary>Starts watching (idempotent). Only runs while the menu bar or Control Center is on.</summary>
    public async Task StartAsync()
    {
        if (_started) return;
        _started = true;
        try
        {
            CanControl = await Radio.RequestAccessAsync() == RadioAccessStatus.Allowed;
            foreach (var radio in await Radio.GetRadiosAsync())
            {
                if (!_started) return;
                if (radio.Kind == RadioKind.WiFi && _wifi is null) _wifi = radio;
                else if (radio.Kind == RadioKind.Bluetooth && _bluetooth is null) _bluetooth = radio;
                else continue;
                radio.StateChanged += OnStateChanged;
            }
            Changed?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            Log.Error("radios", ex);
        }
    }

    public void Stop()
    {
        if (!_started) return;
        _started = false;
        if (_wifi is not null) _wifi.StateChanged -= OnStateChanged;
        if (_bluetooth is not null) _bluetooth.StateChanged -= OnStateChanged;
        _wifi = _bluetooth = null;
    }

    private void OnStateChanged(Radio sender, object args) => _ui.InvokeAsync(() => Changed?.Invoke(this, EventArgs.Empty));

    /// <summary>Turns a radio on/off. Returns false if Windows refused (open Settings instead).</summary>
    public async Task<bool> SetAsync(RadioKind kind, bool on)
    {
        var radio = kind == RadioKind.WiFi ? _wifi : _bluetooth;
        if (radio is null || !CanControl) return false;
        try
        {
            return await radio.SetStateAsync(on ? RadioState.On : RadioState.Off) == RadioAccessStatus.Allowed;
        }
        catch (Exception ex)
        {
            Log.Error("radio set", ex);
            return false;
        }
    }
}
