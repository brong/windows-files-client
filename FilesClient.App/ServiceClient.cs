using FilesClient.Ipc;

namespace FilesClient.App;

/// <summary>
/// Wraps IpcPipeClient and maintains a local mirror of service state.
/// Provides the same surface area that TrayIcon/ManageAccountsForm/etc
/// previously got from LoginManager, but via IPC to the service process.
/// </summary>
sealed class ServiceClient : IDisposable
{
    private readonly IpcPipeClient _client;
    private readonly object _lock = new();
    private List<AccountInfo> _accounts = new();
    private List<string> _connectingLoginIds = new();
    private AccountStatus _aggregateStatus = AccountStatus.Idle;
    private int _aggregatePendingCount;
    private bool _connected;
    private bool _disposed;

    // Pending response waiters (one at a time per command type)
    private TaskCompletionSource<AddLoginResultEvent>? _addLoginTcs;
    private TaskCompletionSource<DiscoverAccountsResultEvent>? _discoverTcs;
    private TaskCompletionSource<CommandResultEvent>? _commandTcs;
    private TaskCompletionSource<OutboxSnapshotEvent>? _outboxTcs;
    private TaskCompletionSource<LoginAccountsResultEvent>? _loginAccountsTcs;

    public IReadOnlyList<AccountInfo> Accounts
    {
        get { lock (_lock) return _accounts.ToList(); }
    }

    public IReadOnlyList<string> ConnectingLoginIds
    {
        get { lock (_lock) return _connectingLoginIds.ToList(); }
    }

    public AccountStatus AggregateStatus
    {
        get { lock (_lock) return _aggregateStatus; }
    }

    public int AggregatePendingCount
    {
        get { lock (_lock) return _aggregatePendingCount; }
    }

    public bool IsConnected
    {
        get { lock (_lock) return _connected; }
    }

    public event Action? AccountsChanged;
    public event Action? StatusChanged;
    public event Action<bool>? ConnectionChanged;

    public ServiceClient()
    {
        _client = new IpcPipeClient();
        _client.EventReceived += OnEventReceived;
        _client.ConnectionChanged += OnConnectionChanged;
    }

    public void Start(CancellationToken ct)
    {
        _client.Start(ct);
    }

    /// <summary>
    /// Send a ping to detect broken pipe. If the write fails,
    /// transitions to disconnected state (unless the pipe client
    /// has already reconnected by the time the continuation runs).
    /// </summary>
    public async Task CheckConnectionAsync()
    {
        try
        {
            if (_connected)
                await _client.SendCommandAsync(new GetStatusCommand());
        }
        catch
        {
            // Only fire disconnect if the pipe client hasn't already
            // reconnected — avoids overwriting a successful reconnection
            // with a stale disconnect from this earlier failed write.
            if (!_client.IsConnected)
                OnConnectionChanged(false);
        }
    }

    public async Task StopAsync()
    {
        await _client.StopAsync();
    }

    public async Task<AddLoginResultEvent> AddLoginAsync(string sessionUrl, string token,
        HashSet<string>? enabledAccountIds = null, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<AddLoginResultEvent>();
        lock (_lock) _addLoginTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new AddLoginCommand(sessionUrl, token, enabledAccountIds), ct);
        return await tcs.Task;
    }

    public async Task<DiscoverAccountsResultEvent> DiscoverAccountsAsync(string sessionUrl, string token,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<DiscoverAccountsResultEvent>();
        lock (_lock) _discoverTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new DiscoverAccountsCommand(sessionUrl, token), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> RemoveLoginAsync(string loginId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new RemoveLoginCommand(loginId), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> CleanUpAccountAsync(string accountId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new CleanUpAccountCommand(accountId), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> ConfigureLoginAsync(string loginId,
        HashSet<string> enabledAccountIds, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new ConfigureLoginCommand(loginId, enabledAccountIds), ct);
        return await tcs.Task;
    }

    public async Task<OutboxSnapshotEvent> GetOutboxAsync(string accountId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<OutboxSnapshotEvent>();
        lock (_lock) _outboxTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new GetOutboxCommand(accountId), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> UpdateLoginAsync(string loginId, string sessionUrl,
        string token, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new UpdateLoginCommand(loginId, sessionUrl, token), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> DetachAccountAsync(string accountId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new DetachAccountCommand(accountId), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> RefreshAccountAsync(string accountId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new RefreshAccountCommand(accountId), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> CleanAccountAsync(string accountId, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new CleanAccountCommand(accountId), ct);
        return await tcs.Task;
    }

    public async Task<CommandResultEvent> EnableAccountAsync(string loginId, string accountId,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CommandResultEvent>();
        lock (_lock) _commandTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new EnableAccountCommand(loginId, accountId), ct);
        return await tcs.Task;
    }

    public async Task<LoginAccountsResultEvent> RefreshLoginAccountsAsync(string loginId,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<LoginAccountsResultEvent>();
        lock (_lock) _loginAccountsTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new RefreshLoginAccountsCommand(loginId), ct);
        return await tcs.Task;
    }

    public async Task<LoginAccountsResultEvent> GetLoginAccountsAsync(string loginId,
        CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<LoginAccountsResultEvent>();
        lock (_lock) _loginAccountsTcs = tcs;

        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await _client.SendCommandAsync(new GetLoginAccountsCommand(loginId), ct);
        return await tcs.Task;
    }

    private void OnConnectionChanged(bool connected)
    {
        lock (_lock)
        {
            _connected = connected;
            if (!connected)
            {
                _accounts.Clear();
                _connectingLoginIds.Clear();
                _aggregateStatus = AccountStatus.Idle;
                _aggregatePendingCount = 0;
            }
        }

        ConnectionChanged?.Invoke(connected);
        AccountsChanged?.Invoke();
        StatusChanged?.Invoke();
    }

    private void OnEventReceived(IpcEvent evt)
    {
        switch (evt)
        {
            case StatusSnapshotEvent snapshot:
                lock (_lock)
                {
                    _accounts = snapshot.Accounts;
                    _connectingLoginIds = snapshot.ConnectingLoginIds;
                    _aggregateStatus = snapshot.AggregateStatus;
                    _aggregatePendingCount = snapshot.AggregatePendingCount;
                }
                AccountsChanged?.Invoke();
                StatusChanged?.Invoke();
                break;

            case AccountStatusChangedEvent statusEvt:
                lock (_lock)
                {
                    var idx = _accounts.FindIndex(a => a.AccountId == statusEvt.AccountId);
                    if (idx >= 0)
                    {
                        var old = _accounts[idx];
                        _accounts[idx] = old with
                        {
                            Status = statusEvt.Status,
                            StatusDetail = statusEvt.StatusDetail,
                            PendingCount = statusEvt.PendingCount,
                        };
                    }
                    // Recalculate aggregate
                    RecalcAggregate();
                }
                StatusChanged?.Invoke();
                break;

            case AccountsChangedEvent accountsEvt:
                lock (_lock)
                {
                    _accounts = accountsEvt.Accounts;
                    _connectingLoginIds = accountsEvt.ConnectingLoginIds;
                    RecalcAggregate();
                }
                AccountsChanged?.Invoke();
                StatusChanged?.Invoke();
                break;

            case AddLoginResultEvent result:
                lock (_lock)
                {
                    _addLoginTcs?.TrySetResult(result);
                    _addLoginTcs = null;
                }
                break;

            case DiscoverAccountsResultEvent result:
                lock (_lock)
                {
                    _discoverTcs?.TrySetResult(result);
                    _discoverTcs = null;
                }
                break;

            case CommandResultEvent result:
                lock (_lock)
                {
                    _commandTcs?.TrySetResult(result);
                    _commandTcs = null;
                }
                break;

            case OutboxSnapshotEvent result:
                lock (_lock)
                {
                    _outboxTcs?.TrySetResult(result);
                    _outboxTcs = null;
                }
                break;

            case LoginAccountsResultEvent result:
                lock (_lock)
                {
                    _loginAccountsTcs?.TrySetResult(result);
                    _loginAccountsTcs = null;
                }
                break;
        }
    }

    // Must be called under _lock
    private void RecalcAggregate()
    {
        if (_accounts.Count == 0)
        {
            _aggregateStatus = AccountStatus.Idle;
            _aggregatePendingCount = 0;
            return;
        }

        _aggregatePendingCount = _accounts.Sum(a => a.PendingCount);

        if (_accounts.Any(a => a.Status == AccountStatus.Error))
            _aggregateStatus = AccountStatus.Error;
        else if (_accounts.Any(a => a.Status == AccountStatus.Disconnected))
            _aggregateStatus = AccountStatus.Disconnected;
        else if (_accounts.Any(a => a.Status == AccountStatus.Syncing))
            _aggregateStatus = AccountStatus.Syncing;
        else
            _aggregateStatus = AccountStatus.Idle;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _client.Dispose();
    }
}
