using FileNodeClient.Logging;
using Windows.Networking.Connectivity;

namespace FileNodeClient.Service;

/// <summary>
/// Monitors Windows network connectivity state and metered connection status.
/// Fires events when the state changes so LoginManager can react immediately
/// (e.g. mark accounts offline, suppress background sync on metered).
/// </summary>
sealed class NetworkMonitor : IDisposable
{
    private bool _disposed;

    public bool IsConnected { get; private set; }
    public bool IsMetered { get; private set; }

    /// <summary>
    /// Fired when connectivity or metered state changes.
    /// Parameters: (isConnected, isMetered).
    /// </summary>
    public event Action<bool, bool>? NetworkStateChanged;

    public NetworkMonitor()
    {
        UpdateState();
        NetworkInformation.NetworkStatusChanged += OnNetworkStatusChanged;
    }

    private void OnNetworkStatusChanged(object sender)
    {
        UpdateState();
    }

    private void UpdateState()
    {
        bool connected;
        bool metered;

        try
        {
            var profile = NetworkInformation.GetInternetConnectionProfile();
            connected = profile?.GetNetworkConnectivityLevel()
                == NetworkConnectivityLevel.InternetAccess;

            metered = false;
            if (profile != null)
            {
                var cost = profile.GetConnectionCost();
                metered = cost.NetworkCostType != NetworkCostType.Unrestricted
                       || cost.Roaming
                       || cost.OverDataLimit;
            }
        }
        catch (Exception ex)
        {
            // WinRT API can throw if network subsystem is unavailable
            Log.Error($"[NetworkMonitor] Error reading network state: {ex.Message}");
            connected = true; // Assume connected if we can't tell
            metered = false;
        }

        if (connected == IsConnected && metered == IsMetered)
            return;

        var wasConnected = IsConnected;
        var wasMetered = IsMetered;
        IsConnected = connected;
        IsMetered = metered;

        Log.Info($"[NetworkMonitor] State changed: connected={connected} (was {wasConnected}), metered={metered} (was {wasMetered})");
        try { NetworkStateChanged?.Invoke(connected, metered); }
        catch (Exception ex) { Log.Error($"[NetworkMonitor] NetworkStateChanged handler error: {ex.Message}"); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        NetworkInformation.NetworkStatusChanged -= OnNetworkStatusChanged;
    }
}
