using System.Threading.Channels;
using FilesClient.Jmap;
using FilesClient.Windows;

namespace FilesClient.Service;

/// <summary>
/// Manages the sync lifecycle for a single JMAP account:
/// register sync root, populate placeholders, run push/poll loop.
/// </summary>
sealed class AccountSupervisor : IDisposable
{
    private readonly IJmapClient _jmapClient;
    private readonly string _syncRootPath;
    private readonly string _displayName;
    private readonly bool _debug;
    private readonly Channel<string> _stateChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    private JmapQueue? _queue;
    private SyncEngine? _engine;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public string SyncRootPath => _syncRootPath;
    public string DisplayName => _displayName;
    public string AccountId => _jmapClient.AccountId;
    public string Username => _jmapClient.Username;

    public SyncStatus Status { get; private set; }
    public string? StatusDetail { get; private set; }
    public int PendingCount { get; private set; }
    public SyncOutbox? Outbox => _engine?.Outbox;

    /// <summary>
    /// When false, background sync polling is suppressed (metered connection).
    /// On-demand hydration and outbox uploads still work.
    /// </summary>
    public bool BackgroundSyncEnabled { get; set; } = true;

    public event Action<AccountSupervisor>? StatusChanged;
    public event Action<AccountSupervisor>? StatusDetailChanged;
    public event Action<AccountSupervisor>? PendingCountChanged;

    public AccountSupervisor(IJmapClient jmapClient, string syncRootPath, string displayName, bool debug)
    {
        _jmapClient = jmapClient;
        _syncRootPath = syncRootPath;
        _displayName = displayName;
        _debug = debug;
    }

    public async Task StartAsync(string? iconPath, bool clean, CancellationToken ct)
    {
        _queue = new JmapQueue();
        Console.WriteLine($"[{_displayName}] Account: {_jmapClient.AccountId}");
        Console.WriteLine($"[{_displayName}] Sync root: {_syncRootPath}");

        if (clean)
        {
            Console.WriteLine($"[{_displayName}] Cleaning previous sync state...");
            SyncEngine.Clean(_syncRootPath, _jmapClient.Context.AccountId);
        }

        _engine = new SyncEngine(_syncRootPath, _jmapClient, _queue, _jmapClient.Context.ScopeKey);
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.StatusDetailChanged += OnEngineStatusDetailChanged;
        _engine.PendingCountChanged += OnEnginePendingCountChanged;

        // Register sync root
        Console.WriteLine($"[{_displayName}] Registering sync root...");
        await _engine.RegisterAsync(_displayName, _jmapClient.Context.AccountId, iconPath);

        // Populate placeholders
        Console.WriteLine($"[{_displayName}] Populating placeholders...");
        var state = await _engine.PopulateAsync(ct);
        Console.WriteLine($"[{_displayName}] Initial sync complete. State: {state}");

        // Reconcile local changes made while offline
        _engine.ReconcileLocalChanges();

        // Connect callbacks
        _engine.Connect();

        // Start push/poll loop
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => SyncLoopAsync(state, _loopCts.Token));
    }

    /// <summary>
    /// Push a state change from the shared push watcher.
    /// Empty string = "poll now regardless of state".
    /// </summary>
    internal void PushState(string state) => _stateChannel.Writer.TryWrite(state);

    internal void NotifyConnectivityLost() => _engine?.ReportConnectivityLost();
    internal void NotifyConnectivityRestored() => _engine?.ReportConnectivityRestored();

    public async Task StopAsync()
    {
        if (_loopCts != null)
        {
            _loopCts.Cancel();
            if (_loopTask != null)
            {
                try { await _loopTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Console.Error.WriteLine($"[{_displayName}] Loop stop error: {ex.Message}"); }
            }
            _loopCts.Dispose();
            _loopCts = null;
        }
    }

    private async Task SyncLoopAsync(string initialState, CancellationToken ct)
    {
        string currentState = initialState;

        Console.WriteLine($"[{_displayName}] Waiting for state changes...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var newState = await _stateChannel.Reader.ReadAsync(ct);

                if (!BackgroundSyncEnabled)
                {
                    if (_debug)
                        Console.WriteLine($"[{_displayName}] Metered connection, skipping background sync");
                    continue;
                }

                // Empty string = forced poll (e.g. after push reconnect)
                if (newState.Length > 0 && string.Equals(newState, currentState, StringComparison.Ordinal))
                {
                    Console.WriteLine($"[{_displayName}] State unchanged ({currentState}), skipping poll");
                    continue;
                }

                try
                {
                    currentState = await _engine!.PollChangesAsync(currentState, ct);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"[{_displayName}] Change poll error: {ex.Message}");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }
        }
    }

    private void OnEngineStatusChanged(SyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this);
    }

    private void OnEngineStatusDetailChanged(string? detail)
    {
        StatusDetail = detail;
        StatusDetailChanged?.Invoke(this);
    }

    private void OnEnginePendingCountChanged(int count)
    {
        PendingCount = count;
        PendingCountChanged?.Invoke(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _loopCts?.Cancel();
        try { _loopTask?.Wait(3000); } catch { }
        _loopCts?.Dispose();

        _engine?.Dispose();
        _queue?.Dispose();
    }
}
