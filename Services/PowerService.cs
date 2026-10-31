using System.Windows.Threading;
using Windows.System.Power;

namespace DynaIsland.Services;

public enum PowerEvent
{
    PluggedIn,
    Unplugged,
    LowBattery,
}

/// <summary>Battery / charger notifications via Windows.System.Power (no-op on desktops without a battery).</summary>
public sealed class PowerService
{
    private const int LowThreshold = 20;

    private readonly Dispatcher _ui;
    private PowerSupplyStatus _supply;
    private bool _lowWarned;

    public event EventHandler<PowerEvent>? Changed;

    public PowerService(Dispatcher ui)
    {
        _ui = ui;
        try
        {
            _supply = PowerManager.PowerSupplyStatus;
            PowerManager.PowerSupplyStatusChanged += (_, _) => _ui.InvokeAsync(OnSupplyChanged);
            PowerManager.RemainingChargePercentChanged += (_, _) => _ui.InvokeAsync(OnPercentChanged);
        }
        catch (Exception ex)
        {
            Log.Error("power", ex);
        }
    }

    public static bool HasBattery
    {
        get
        {
            try { return PowerManager.BatteryStatus != BatteryStatus.NotPresent; }
            catch { return false; }
        }
    }

    public static int Percent
    {
        get
        {
            try { return PowerManager.RemainingChargePercent; }
            catch { return 0; }
        }
    }

    public static bool IsPluggedIn
    {
        get
        {
            try { return PowerManager.PowerSupplyStatus != PowerSupplyStatus.NotPresent; }
            catch { return true; }
        }
    }

    private void OnSupplyChanged()
    {
        var supply = PowerManager.PowerSupplyStatus;
        bool wasPlugged = _supply != PowerSupplyStatus.NotPresent;
        bool isPlugged = supply != PowerSupplyStatus.NotPresent;
        _supply = supply;

        if (!HasBattery || wasPlugged == isPlugged) return;
        if (isPlugged) _lowWarned = false;
        Changed?.Invoke(this, isPlugged ? PowerEvent.PluggedIn : PowerEvent.Unplugged);
    }

    private void OnPercentChanged()
    {
        if (!HasBattery) return;
        int percent = Percent;
        if (percent > LowThreshold + 5) _lowWarned = false;
        if (percent <= LowThreshold && !IsPluggedIn && !_lowWarned)
        {
            _lowWarned = true;
            Changed?.Invoke(this, PowerEvent.LowBattery);
        }
    }
}
