using FileNodeClient.Ipc;
using FileNodeClient.Windows;
using Microsoft.Extensions.Hosting;

namespace FileNodeClient.Service;

/// <summary>
/// BackgroundService that runs the sync engine and IPC server.
/// Hooks LoginManager events to push status updates to connected UI clients.
/// </summary>
sealed class SyncHostedService : BackgroundService
{
    private readonly bool _debug;
    private readonly string? _token;
    private readonly string _sessionUrl;
    private readonly bool _clean;
    private LoginManager? _loginManager;
    private IpcPipeServer? _ipcServer;
    private IpcCommandHandler? _handler;
    private string? _iconPath;

    public SyncHostedService(ServiceOptions options)
    {
        _debug = options.Debug;
        _token = options.Token;
        _sessionUrl = options.SessionUrl;
        _clean = options.Clean;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            Log.Info("FileNodeClient service starting...");

            // Start IPC server first so the tray app can connect immediately
            _loginManager = new LoginManager(_debug);
            _handler = new IpcCommandHandler(_loginManager, null);
            _ipcServer = new IpcPipeServer(_handler.HandleAsync);
            _ipcServer.Start(stoppingToken);
            Log.Info($"IPC server listening on pipe: {IpcConstants.PipeName}");

            // Download icon for sync root (can be slow on first run)
            _iconPath = await DownloadIconAsync(stoppingToken);
            _handler.IconPath = _iconPath;

            // Hook LoginManager events to broadcast IPC updates
            _loginManager.AccountsChanged += OnAccountsChanged;
            _loginManager.AggregateStatusChanged += OnAggregateStatusChanged;

            // Hook per-supervisor status changes (subscribe on existing and future supervisors)
            _loginManager.AccountsChanged += SubscribeSupervisorEvents;

            // Dev: --token adds a transient (non-persisted) login
            if (_token != null)
            {
                try
                {
                    await _loginManager.AddLoginAsync(_sessionUrl, _token,
                        persist: false, iconPath: _iconPath, clean: _clean, ct: stoppingToken);
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to connect: {ex.Message}");
                }
            }

            // Load stored credentials
            await _loginManager.StartAsync(_iconPath, _clean, stoppingToken);

            Log.Info("FileNodeClient service running");

            // Wait until stopped
            try { await Task.Delay(Timeout.Infinite, stoppingToken); }
            catch (OperationCanceledException) { }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"Fatal error in service: {ex}");
            throw;
        }

        Log.Info("FileNodeClient service stopping...");

        try
        {
            if (_loginManager != null)
                await _loginManager.StopAllAsync();
            if (_ipcServer != null)
                await _ipcServer.StopAsync();

            _loginManager?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Error($"Error during service cleanup: {ex.Message}");
        }

        Log.Info("FileNodeClient service stopped");
    }

    private HashSet<string> _subscribedAccountIds = new();

    private void SubscribeSupervisorEvents()
    {
        if (_loginManager == null) return;

        foreach (var supervisor in _loginManager.Supervisors)
        {
            if (_subscribedAccountIds.Add(supervisor.AccountId))
            {
                supervisor.StatusChanged += OnSupervisorStatusChanged;
                supervisor.StatusDetailChanged += OnSupervisorStatusChanged;
                supervisor.PendingCountChanged += OnSupervisorPendingCountChanged;
            }
        }
    }

    private void OnAccountsChanged()
    {
        if (_handler == null || _ipcServer == null) return;
        var evt = _handler.BuildAccountsChanged();
        _ = _ipcServer.BroadcastAsync(evt);
    }

    private void OnAggregateStatusChanged(SyncStatus status)
    {
        if (_handler == null || _ipcServer == null) return;
        var snapshot = _handler.BuildStatusSnapshot();
        _ = _ipcServer.BroadcastAsync(snapshot);
    }

    private void OnSupervisorStatusChanged(AccountSupervisor supervisor)
    {
        if (_handler == null || _ipcServer == null) return;
        var evt = _handler.BuildAccountStatus(supervisor);
        _ = _ipcServer.BroadcastAsync(evt);
    }

    private void OnSupervisorPendingCountChanged(AccountSupervisor supervisor)
    {
        if (_handler == null || _ipcServer == null) return;
        var evt = _handler.BuildAccountStatus(supervisor);
        _ = _ipcServer.BroadcastAsync(evt);
    }

    private static async Task<string?> DownloadIconAsync(CancellationToken ct)
    {
        const string FaviconUrl = "https://www.fastmail.com/favicon.ico";
        try
        {
            var iconDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "FileNodeClient");
            Directory.CreateDirectory(iconDir);
            var iconPath = Path.Combine(iconDir, "icon.ico");

            if (File.Exists(iconPath))
                return iconPath;

            using var http = new HttpClient();
            var data = await http.GetByteArrayAsync(FaviconUrl, ct);
            await File.WriteAllBytesAsync(iconPath, data, ct);
            Log.Info($"Downloaded icon to {iconPath}");
            return iconPath;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not download icon: {ex.Message}");
            return null;
        }
    }
}

class ServiceOptions
{
    public bool Debug { get; set; }
    public string? Token { get; set; }
    public string SessionUrl { get; set; } = "https://api.fastmail.com/jmap/session";
    public bool Clean { get; set; }
}
