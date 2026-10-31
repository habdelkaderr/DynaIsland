using System.Windows.Threading;
using Windows.Devices.Bluetooth;
using Windows.Devices.Enumeration;

namespace DynaIsland.Services;

public sealed record BluetoothEvent(string Name, bool Connected, int? Battery);

/// <summary>Watches paired Bluetooth (Classic and LE) devices for connect / disconnect, with battery where reported.</summary>
public sealed class BluetoothService : IDisposable
{
    private const string IsConnectedKey = "System.Devices.Aep.IsConnected";
    private const string ContainerIdKey = "System.Devices.Aep.ContainerId";
    private const string BatteryKey = "{104EA319-6EE2-4701-BD47-8DDBF425BBE5} 2"; // DEVPKEY_Bluetooth_Battery

    private readonly Dispatcher _ui;
    private readonly List<DeviceWatcher> _watchers = [];
    private readonly Dictionary<string, (string Name, bool Connected, Guid? Container)> _devices = [];
    private readonly Dictionary<string, DateTime> _recent = [];
    private int _completed;

    /// <summary>Raised on the UI thread.</summary>
    public event EventHandler<BluetoothEvent>? Changed;

    public BluetoothService(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            string[] props = [IsConnectedKey, ContainerIdKey];
            foreach (var selector in new[]
                     {
                         BluetoothDevice.GetDeviceSelectorFromPairingState(true),
                         BluetoothLEDevice.GetDeviceSelectorFromPairingState(true),
                     })
            {
                var watcher = DeviceInformation.CreateWatcher(selector, props, DeviceInformationKind.AssociationEndpoint);
                watcher.Added += (_, d) => _ui.InvokeAsync(() => OnAdded(d));
                watcher.Updated += (_, u) => _ui.InvokeAsync(() => OnUpdated(u));
                watcher.Removed += (_, u) => _ui.InvokeAsync(() => _devices.Remove(u.Id));
                watcher.EnumerationCompleted += (_, _) => _ui.InvokeAsync(() => _completed++);
                watcher.Start();
                _watchers.Add(watcher);
            }
        }
        catch (Exception ex)
        {
            Log.Error("bluetooth", ex);
        }
    }

    private bool Ready => _completed >= _watchers.Count;

    private void OnAdded(DeviceInformation device)
    {
        bool connected = device.Properties.TryGetValue(IsConnectedKey, out var c) && c is true;
        Guid? container = device.Properties.TryGetValue(ContainerIdKey, out var id) && id is Guid g ? g : null;
        _devices[device.Id] = (device.Name, connected, container);
    }

    private void OnUpdated(DeviceInformationUpdate update)
    {
        if (!_devices.TryGetValue(update.Id, out var known)) return;
        if (!update.Properties.TryGetValue(IsConnectedKey, out var c) || c is not bool connected) return;
        if (connected == known.Connected) return;

        _devices[update.Id] = known with { Connected = connected };
        if (!Ready || string.IsNullOrWhiteSpace(known.Name)) return;

        // Dual-mode devices show up as both Classic and LE; report each change once.
        string key = $"{known.Name}|{connected}";
        if (_recent.TryGetValue(key, out var last) && DateTime.UtcNow - last < TimeSpan.FromSeconds(5)) return;
        _recent[key] = DateTime.UtcNow;

        if (!connected)
        {
            Changed?.Invoke(this, new BluetoothEvent(known.Name, false, null));
            return;
        }

        _ = ReportConnectedAsync(known.Name, known.Container);
    }

    private async Task ReportConnectedAsync(string name, Guid? container)
    {
        int? battery = null;
        if (container is Guid id)
        {
            // Battery is published a moment after the connection comes up.
            await Task.Delay(2500);
            battery = await ReadBatteryAsync(id);
        }
        Changed?.Invoke(this, new BluetoothEvent(name, true, battery));
    }

    private static async Task<int?> ReadBatteryAsync(Guid container)
    {
        try
        {
            var devices = await DeviceInformation.FindAllAsync(
                $"System.Devices.ContainerId:=\"{{{container}}}\"", [BatteryKey], DeviceInformationKind.Device);
            foreach (var d in devices)
            {
                if (d.Properties.TryGetValue(BatteryKey, out var value) && value is byte level) return level;
            }
        }
        catch (Exception ex)
        {
            Log.Error("bluetooth battery", ex);
        }
        return null;
    }

    public static string GlyphFor(string name)
    {
        string n = name.ToLowerInvariant();
        if (n.Contains("controller") || n.Contains("dualsense") || n.Contains("xbox") || n.Contains("gamepad")) return "";
        if (n.Contains("mouse")) return "";
        if (n.Contains("keyboard")) return "";
        if (n.Contains("bud") || n.Contains("pod") || n.Contains("liberty") || n.Contains("head") || n.Contains("ear") ||
            n.Contains("soundcore") || n.Contains("jabra") || n.Contains("beats") || n.Contains("wh-") || n.Contains("wf-"))
            return "";
        if (n.Contains("speaker") || n.Contains("boom") || n.Contains("flip") || n.Contains("charge")) return "";
        return "";
    }

    public void Dispose()
    {
        foreach (var w in _watchers)
        {
            try { w.Stop(); } catch { /* already stopped */ }
        }
    }
}
