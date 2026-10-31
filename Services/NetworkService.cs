using System.Net.NetworkInformation;
using System.Windows.Threading;
using Windows.Networking.Connectivity;
using NetworkInterface = System.Net.NetworkInformation.NetworkInterface;

namespace DynaIsland.Services;

public sealed record NetworkState(bool Online, bool Limited, bool IsWifi, string Name, string? Vpn);

/// <summary>Reports Wi-Fi / Ethernet / offline / VPN changes (debounced; Windows raises bursts of events).</summary>
public sealed class NetworkService
{
    private static readonly string[] VpnHints =
        ["vpn", "wireguard", "tap-windows", "wintun", "openvpn", "anyconnect", "fortinet", "globalprotect", "nordlynx", "tailscale", "zerotier", "proton"];

    private readonly DispatcherTimer _debounce;

    public NetworkState? Current { get; private set; }

    /// <summary>Raised on the UI thread with (previous, current).</summary>
    public event EventHandler<(NetworkState Previous, NetworkState Current)>? Changed;

    public NetworkService(Dispatcher ui)
    {
        _debounce = new DispatcherTimer(TimeSpan.FromSeconds(2), DispatcherPriority.Normal,
            (_, _) => { _debounce!.Stop(); Evaluate(); }, ui);
        _debounce.Stop();

        NetworkChange.NetworkAddressChanged += (_, _) => ui.InvokeAsync(Restart);
        NetworkChange.NetworkAvailabilityChanged += (_, _) => ui.InvokeAsync(Restart);
        Current = Read();
    }

    private void Restart()
    {
        _debounce.Stop();
        _debounce.Start();
    }

    private void Evaluate()
    {
        var next = Read();
        var previous = Current;
        Current = next;
        if (previous is not null && next is not null && previous != next) Changed?.Invoke(this, (previous, next));
    }

    private static NetworkState? Read()
    {
        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            var level = profile?.GetNetworkConnectivityLevel() ?? NetworkConnectivityLevel.None;
            bool online = level is NetworkConnectivityLevel.InternetAccess or NetworkConnectivityLevel.ConstrainedInternetAccess;
            bool limited = level == NetworkConnectivityLevel.ConstrainedInternetAccess;

            string? vpn = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                            (n.NetworkInterfaceType == NetworkInterfaceType.Ppp ||
                             VpnHints.Any(h => n.Description.Contains(h, StringComparison.OrdinalIgnoreCase) ||
                                               n.Name.Contains(h, StringComparison.OrdinalIgnoreCase))))
                .Select(n => n.Name)
                .FirstOrDefault();

            return new NetworkState(online, limited, profile?.IsWlanConnectionProfile == true, profile?.ProfileName ?? "", vpn);
        }
        catch (Exception ex)
        {
            Log.Error("network", ex);
            return null;
        }
    }
}
