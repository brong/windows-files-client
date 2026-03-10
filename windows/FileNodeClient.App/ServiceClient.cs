using FileNodeClient.Ipc;
using FileNodeClient.Logging;

namespace FileNodeClient.App;

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
    private List<FailedLogin> _failedLogins = new();
    private List<string> _connectedLoginIds = new();
    private AccountStatus _aggregateStatus = AccountStatus.Idle;
    private int _aggregatePendingCount;
    private int _rejectedFileCount;
    private readonly Dictionary<string, int> _rejectedByAccount = new();
    private bool _connected;
    private bool _disposed;

    public IReadOnlyList<AccountInfo> Accounts
    {
        get { lock (_lock) return _accounts.ToList(); }
    }

    public IReadOnlyList<string> ConnectingLoginIds
    {
        get { lock (_lock) return _connectingLoginIds.ToList(); }
    }

    public IReadOnlyList<FailedLogin> FailedLogins
    {
        get { lock (_lock) return _failedLogins.ToList(); }
    }

    public IReadOnlyList<string> ConnectedLoginIds
    {
        get { lock (_lock) return _connectedLoginIds.ToList(); }
    }

    public AccountStatus AggregateStatus
    {
        get { lock (_lock) return _aggregateStatus; }
    }

    public int AggregatePendingCount
    {
        get { lock (_lock) return _aggregatePendingCount; }
    }

    public int RejectedFileCount
    {
        get { lock (_lock) return _rejectedFileCount; }
    }

    public bool IsConnected
    {
        get { lock (_lock) return _connected; }
    }

    public event Action? AccountsChanged;
    public event Action? StatusChanged;
    public event Action<bool>? ConnectionChanged;
    public event Action<ActivitySnapshot>? ActivityChanged;

    public ServiceClient()
    {
        _client = new IpcPipeClient();
        _client.PushReceived += OnPushReceived;
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
                await _client.CallAsync("ping");
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

    public async Task<AddLoginResult> AddLoginAsync(string sessionUrl, string token,
        HashSet<string>? enabledAccountIds = null,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null,
        CancellationToken ct = default)
    {
        return await CallAsync<AddLoginResult>("addLogin", new
        {
            sessionUrl, token, enabledAccountIds,
            refreshToken, tokenEndpoint, clientId, expiresAtUnixSeconds,
        }, ct);
    }

    public async Task<DiscoverAccountsResult> DiscoverAccountsAsync(string sessionUrl, string token,
        CancellationToken ct = default)
    {
        return await CallAsync<DiscoverAccountsResult>("discoverAccounts", new
        {
            sessionUrl, token,
        }, ct);
    }

    public async Task RemoveLoginAsync(string loginId, CancellationToken ct = default)
    {
        await CallVoidAsync("removeLogin", new { loginId }, ct);
    }

    public async Task CleanUpAccountAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("cleanUpAccount", new { accountId }, ct);
    }

    public async Task ConfigureLoginAsync(string loginId,
        HashSet<string> enabledAccountIds, CancellationToken ct = default)
    {
        await CallVoidAsync("configureLogin", new { loginId, enabledAccountIds }, ct);
    }

    public async Task<OutboxResult> GetOutboxAsync(string accountId, CancellationToken ct = default)
    {
        return await CallAsync<OutboxResult>("getOutbox", new { accountId }, ct);
    }

    public async Task<ActivitySnapshot> GetActivityAsync(string accountId, CancellationToken ct = default)
    {
        return await CallAsync<ActivitySnapshot>("getActivity", new { accountId }, ct);
    }

    public async Task UpdateLoginAsync(string loginId, string sessionUrl,
        string token, string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null,
        CancellationToken ct = default)
    {
        await CallVoidAsync("updateLogin", new
        {
            loginId, sessionUrl, token,
            refreshToken, tokenEndpoint, clientId, expiresAtUnixSeconds,
        }, ct);
    }

    public async Task DetachAccountAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("detachAccount", new { accountId }, ct);
    }

    public async Task RefreshAccountAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("refreshAccount", new { accountId }, ct);
    }

    public async Task CleanAccountAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("cleanAccount", new { accountId }, ct);
    }

    public async Task EnableAccountAsync(string loginId, string accountId,
        CancellationToken ct = default)
    {
        await CallVoidAsync("enableAccount", new { loginId, accountId }, ct);
    }

    public async Task<LoginAccountsResult> RefreshLoginAccountsAsync(string loginId,
        CancellationToken ct = default)
    {
        return await CallAsync<LoginAccountsResult>("refreshLoginAccounts", new { loginId }, ct);
    }

    public async Task PauseAccountAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("pauseAccount", new { accountId }, ct);
    }

    public async Task ResumeAccountAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("resumeAccount", new { accountId }, ct);
    }

    public async Task SyncNowAsync(string accountId, CancellationToken ct = default)
    {
        await CallVoidAsync("syncNow", new { accountId }, ct);
    }

    public async Task<LoginAccountsResult> GetLoginAccountsAsync(string loginId,
        CancellationToken ct = default)
    {
        return await CallAsync<LoginAccountsResult>("getLoginAccounts", new { loginId }, ct);
    }

    private async Task<T> CallAsync<T>(string method, object? @params, CancellationToken ct)
    {
        var response = await _client.CallAsync(method, @params, ct);
        if (response is IpcError error)
            throw new IpcCallException(error.Message);
        if (response is IpcResponse success)
            return IpcSerializer.Deserialize<T>(success.Result);
        throw new IpcCallException("Unexpected response type");
    }

    private async Task CallVoidAsync(string method, object? @params, CancellationToken ct)
    {
        var response = await _client.CallAsync(method, @params, ct);
        if (response is IpcError error)
            throw new IpcCallException(error.Message);
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
                _failedLogins.Clear();
                _connectedLoginIds.Clear();
                _aggregateStatus = AccountStatus.Idle;
                _aggregatePendingCount = 0;
                _rejectedFileCount = 0;
                _rejectedByAccount.Clear();
            }
        }

        Log.SafeInvoke(() => ConnectionChanged?.Invoke(connected), "ServiceClient.ConnectionChanged");
        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "ServiceClient.ConnectionChanged.Accounts");
        Log.SafeInvoke(() => StatusChanged?.Invoke(), "ServiceClient.ConnectionChanged.Status");
    }

    private void OnPushReceived(IpcPush push)
    {
        try
        {
            switch (push.Method)
            {
                case "statusSnapshot":
                {
                    var snapshot = IpcSerializer.Deserialize<StatusSnapshotResult>(push.Params);
                    lock (_lock)
                    {
                        _accounts = snapshot.Accounts;
                        _connectingLoginIds = snapshot.ConnectingLoginIds;
                        _failedLogins = snapshot.FailedLogins;
                        _connectedLoginIds = snapshot.ConnectedLoginIds ?? new();
                        _aggregateStatus = snapshot.AggregateStatus;
                        _aggregatePendingCount = snapshot.AggregatePendingCount;
                    }
                    Log.SafeInvoke(() => AccountsChanged?.Invoke(), "ServiceClient.StatusSnapshot.Accounts");
                    Log.SafeInvoke(() => StatusChanged?.Invoke(), "ServiceClient.StatusSnapshot.Status");
                    // Fetch initial activity for all accounts after status is populated
                    Log.FireAndForget(FetchInitialActivityAsync(snapshot.Accounts), "FetchInitialActivity");
                    break;
                }

                case "accountStatusChanged":
                {
                    var statusEvt = IpcSerializer.Deserialize<AccountStatusPush>(push.Params);
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
                                QuotaUsed = statusEvt.QuotaUsed ?? old.QuotaUsed,
                                QuotaLimit = statusEvt.QuotaLimit ?? old.QuotaLimit,
                                PauseReason = statusEvt.PauseReason,
                            };
                        }
                        RecalcAggregate();
                    }
                    Log.SafeInvoke(() => StatusChanged?.Invoke(), "ServiceClient.AccountStatusChanged");
                    break;
                }

                case "activityChanged":
                {
                    var activity = IpcSerializer.Deserialize<ActivitySnapshot>(push.Params);
                    lock (_lock)
                    {
                        _rejectedByAccount[activity.AccountId] = activity.RejectedEntries.Count;
                        _rejectedFileCount = _rejectedByAccount.Values.Sum();
                    }
                    Log.SafeInvoke(() => ActivityChanged?.Invoke(activity), "ServiceClient.ActivityChanged");
                    Log.SafeInvoke(() => StatusChanged?.Invoke(), "ServiceClient.ActivityRejectedStatus");
                    break;
                }

                case "accountsChanged":
                {
                    var accountsEvt = IpcSerializer.Deserialize<AccountsChangedPush>(push.Params);
                    lock (_lock)
                    {
                        _accounts = accountsEvt.Accounts;
                        _connectingLoginIds = accountsEvt.ConnectingLoginIds;
                        _failedLogins = accountsEvt.FailedLogins;
                        _connectedLoginIds = accountsEvt.ConnectedLoginIds ?? new();
                        RecalcAggregate();
                    }
                    Log.SafeInvoke(() => AccountsChanged?.Invoke(), "ServiceClient.AccountsChanged.Accounts");
                    Log.SafeInvoke(() => StatusChanged?.Invoke(), "ServiceClient.AccountsChanged.Status");
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"[ServiceClient] Failed to handle push '{push.Method}': {ex.Message}");
        }
    }

    private async Task FetchInitialActivityAsync(List<AccountInfo> accounts)
    {
        foreach (var acct in accounts)
        {
            var snapshot = await GetActivityAsync(acct.AccountId);
            lock (_lock)
            {
                _rejectedByAccount[snapshot.AccountId] = snapshot.RejectedEntries.Count;
                _rejectedFileCount = _rejectedByAccount.Values.Sum();
            }
            Log.SafeInvoke(() => ActivityChanged?.Invoke(snapshot), "ServiceClient.FetchInitialActivity");
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
