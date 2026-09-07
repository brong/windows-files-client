using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using FileNodeClient.Jmap.Auth;
using FileNodeClient.Windows;

namespace FileNodeClient.App;

/// <summary>
/// Orchestrates multiple JMAP logins, discovers FileNode-capable accounts
/// in each, and manages AccountSupervisor lifecycle.
///
/// Every account-lifecycle operation is built from the same three moves:
/// <see cref="StartSupervisorAsync"/>, <see cref="StopSupervisorAsync"/>, and
/// <see cref="PersistSession"/>. Keep it that way.
/// </summary>
sealed class LoginManager : IDisposable
{
    private readonly record struct FailedLoginInfo(string LoginId, LoginCredential Credential,
        HashSet<string>? EnabledAccountIds, string Error);

    private const long DiskFullThresholdBytes = 500L * 1024 * 1024;   // Pause at <500MB
    private const long DiskResumeThresholdBytes = 1024L * 1024 * 1024; // Resume at >1GB

    private readonly bool _debug;
    private readonly string? _iconPath;
    private readonly CredentialStore _credentialStore = new();
    private readonly List<LoginSession> _sessions = new();
    private readonly List<AccountSupervisor> _supervisors = new();
    private readonly List<string> _connectingLoginIds = new();
    private readonly List<FailedLoginInfo> _failedLogins = new();
    private List<AccountCache.CachedAccount> _cachedAccounts = new();
    private readonly object _lock = new();
    private NetworkMonitor? _networkMonitor;
    private System.Threading.Timer? _retryTimer;
    private System.Threading.Timer? _diskCheckTimer;
    private int _retryInProgress;
    private CancellationToken _parentCt;
    private bool _disposed;

    public IReadOnlyList<AccountSupervisor> Supervisors
    {
        get { lock (_lock) return _supervisors.ToList(); }
    }

    /// <summary>Login IDs currently in the process of connecting (not yet in Supervisors).</summary>
    public IReadOnlyList<string> ConnectingLoginIds
    {
        get { lock (_lock) return _connectingLoginIds.ToList(); }
    }

    /// <summary>Logins that failed to connect (credential exists but session fetch failed).</summary>
    public IReadOnlyList<(string LoginId, string Error)> FailedLogins
    {
        get { lock (_lock) return _failedLogins.Select(f => (f.LoginId, f.Error)).ToList(); }
    }

    /// <summary>Login IDs with active sessions (connected, regardless of enabled accounts).</summary>
    public IReadOnlyList<string> ConnectedLoginIds
    {
        get { lock (_lock) return _sessions.Select(s => s.LoginId).ToList(); }
    }

    /// <summary>
    /// Accounts from the cache that haven't been replaced by live supervisors yet.
    /// Used to show accounts immediately on startup before network connects.
    /// </summary>
    public IReadOnlyList<AccountCache.CachedAccount> CachedAccounts
    {
        get { lock (_lock) return _cachedAccounts.ToList(); }
    }

    public event Action? AccountsChanged;
    public event Action<SyncStatus>? AggregateStatusChanged;

    public LoginManager(bool debug, string? iconPath)
    {
        _debug = debug;
        _iconPath = iconPath;
        _conflictStrategy = SettingsStore.ParseStrategy(SettingsStore.Load().ConflictResolution);
    }

    private ConflictResolution _conflictStrategy;

    /// <summary>Current global content-conflict resolution strategy (DESIGN §6).</summary>
    public ConflictResolution ConflictStrategy
    {
        get { lock (_lock) return _conflictStrategy; }
    }

    /// <summary>
    /// Update the global conflict strategy: persist it and apply to every live account so the
    /// change takes effect without a restart. New supervisors pick it up via their constructor.
    /// </summary>
    public void SetConflictStrategy(ConflictResolution strategy)
    {
        List<AccountSupervisor> supervisors;
        lock (_lock)
        {
            _conflictStrategy = strategy;
            supervisors = _supervisors.ToList();
        }
        SettingsStore.Save(new SettingsStore.AppSettings(SettingsStore.FormatStrategy(strategy)));
        foreach (var s in supervisors)
            s.ConflictStrategy = strategy;
        Log.Info($"[LoginManager] Conflict resolution set to {strategy}");
    }

    // ---- Startup ----

    /// <summary>Load stored credentials and start all accounts.</summary>
    public async Task StartAsync(bool clean, CancellationToken ct)
    {
        _parentCt = ct;

        _networkMonitor = new NetworkMonitor();
        _networkMonitor.NetworkStateChanged += OnNetworkStateChanged;
        Log.Info($"[NetworkMonitor] Initial state: connected={_networkMonitor.IsConnected}, metered={_networkMonitor.IsMetered}");

        // Periodic disk space check as a safety net (5 minutes). Primary checks
        // happen on demand: before hydration and after sync polls.
        _diskCheckTimer = new System.Threading.Timer(_ => CheckDiskSpace(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

        var storedLogins = _credentialStore.LoadAll();

        // Load cached account metadata so accounts appear in the UI immediately
        lock (_lock)
        {
            _cachedAccounts = AccountCache.Load();
            foreach (var login in storedLogins)
                _connectingLoginIds.Add(login.LoginId);
        }
        if (_cachedAccounts.Count > 0)
            Log.Info($"Loaded {_cachedAccounts.Count} cached account(s) for instant display");
        NotifyAccountsChanged();

        foreach (var login in storedLogins)
        {
            Log.Info($"Loading login: {login.LoginId}");
            await ConnectLoginAsync(login.LoginId, login.Credential, login.EnabledAccountIds,
                persist: false, clean: clean, ct: ct);
        }

        AuditOrphanedSyncRoots();

        // Periodically retry failed logins (e.g. server briefly unreachable at startup)
        _retryTimer = new System.Threading.Timer(_ => Log.FireAndForget(RetryFailedLoginsAsync(), "RetryFailedLogins"), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Connect a known login, recording it as failed (for periodic retry) if
    /// the connection fails. Used for startup and retries; never throws.
    /// </summary>
    private async Task ConnectLoginAsync(string loginId, LoginCredential credential,
        HashSet<string>? enabledAccountIds, bool persist, bool clean, CancellationToken ct)
    {
        try
        {
            await ConnectAndStartAsync(credential, loginId, enabledAccountIds, persist, clean, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to connect login {loginId}: {ex.Message}");
            lock (_lock)
                _failedLogins.Add(new FailedLoginInfo(loginId, credential, enabledAccountIds, ex.Message));
        }
        finally
        {
            lock (_lock)
                _connectingLoginIds.Remove(loginId);
            NotifyAccountsChanged();
        }
    }

    // ---- Public operations ----

    /// <summary>
    /// Discover accounts for a credential without starting anything.
    /// </summary>
    public async Task<List<(string AccountId, string Name, bool IsPrimary)>> DiscoverAccountsAsync(
        LoginCredential credential, CancellationToken ct = default)
    {
        using var client = new JmapClient(credential.Token, _debug);
        await client.ConnectAsync(credential.SessionUrl, ct);
        return client.GetFileNodeAccounts();
    }

    /// <summary>Add a new login: connect, discover accounts, start supervisors.</summary>
    public Task<string> AddLoginAsync(LoginCredential credential, HashSet<string>? enabledAccountIds = null,
        bool persist = true, bool clean = false, CancellationToken ct = default)
        => ConnectAndStartAsync(credential, loginId: null, enabledAccountIds, persist, clean, ct);

    /// <summary>Remove a login: stop all its supervisors, clean sync roots, remove credential.</summary>
    public async Task RemoveLoginAsync(string loginId)
    {
        var session = FindSession(loginId);
        if (session != null)
        {
            StopPushWatcher(session);
            foreach (var supervisor in SupervisorsOf(session))
            {
                await StopSupervisorAsync(supervisor);
                CleanSyncRoot(supervisor);
            }
            lock (_lock) _sessions.Remove(session);
            session.Client.Dispose();
        }

        lock (_lock)
        {
            _failedLogins.RemoveAll(f => f.LoginId == loginId);
            _cachedAccounts.RemoveAll(c => c.LoginId == loginId);
        }
        _credentialStore.Remove(loginId);
        SaveAccountCache();
        AuditOrphanedSyncRoots();
        NotifyAccountsChanged();
    }

    /// <summary>
    /// Stop syncing one account and delete all its local files. The login stays
    /// connected (even with zero accounts) so it can be re-enabled without re-adding.
    /// </summary>
    public Task CleanUpAccountAsync(string accountId) => RemoveAccountAsync(accountId, CleanSyncRoot);

    /// <summary>
    /// Stop syncing one account, delete dehydrated placeholders and empty
    /// folders, and unregister the sync root — but leave hydrated files in place.
    /// </summary>
    public Task DetachAccountAsync(string accountId) => RemoveAccountAsync(accountId, DetachSyncRoot);

    private async Task RemoveAccountAsync(string accountId, Action<AccountSupervisor> removeSyncRoot)
    {
        var supervisor = FindSupervisor(accountId);
        if (supervisor == null) return;
        var session = FindSessionForAccount(accountId);

        await StopSupervisorAsync(supervisor);
        removeSyncRoot(supervisor);

        if (session != null)
            PersistSession(session);
        AuditOrphanedSyncRoots();
        NotifyAccountsChanged();
    }

    /// <summary>
    /// Force a full re-sync: drop the node cache and restart the supervisor.
    /// Existing sync root and local files are preserved.
    /// </summary>
    public Task RefreshAccountAsync(string accountId, CancellationToken ct = default)
        => RestartSupervisorAsync(accountId, s => NodeCache.Delete(s.ScopeKey), ct);

    /// <summary>
    /// Start over: delete all local files and the sync root, then re-create from scratch.
    /// </summary>
    public Task CleanAccountAsync(string accountId, CancellationToken ct = default)
        => RestartSupervisorAsync(accountId, s => { CleanSyncRoot(s); NodeCache.Delete(s.ScopeKey); }, ct);

    private async Task RestartSupervisorAsync(string accountId, Action<AccountSupervisor> afterStop, CancellationToken ct)
    {
        var supervisor = FindSupervisor(accountId);
        var session = FindSessionForAccount(accountId);
        if (supervisor == null || session == null)
            throw new InvalidOperationException($"Account {accountId} not found");
        var account = session.Account(accountId)
            ?? throw new InvalidOperationException($"Account {accountId} not found on server");

        await StopSupervisorAsync(supervisor);
        afterStop(supervisor);
        // Keep the existing folder even if the account has since been renamed.
        await StartSupervisorAsync(session, account, supervisor.DisplayName, supervisor.SyncRootPath, clean: false, ct);
        NotifyAccountsChanged();
    }

    /// <summary>Enable (start syncing) a previously non-synced account for a given login.</summary>
    public async Task EnableAccountAsync(string loginId, string accountId, CancellationToken ct = default)
    {
        var session = FindSession(loginId)
            ?? throw new InvalidOperationException($"Login {loginId} not found");
        if (FindSupervisor(accountId) != null)
            throw new InvalidOperationException($"Account {accountId} is already syncing");
        var account = session.Account(accountId)
            ?? throw new InvalidOperationException($"Account {accountId} not found on server");

        await StartSupervisorAsync(session, account, clean: false, ct);

        // Restart push if it was stopped (e.g. all accounts were removed then one re-enabled)
        if (session.PushTask == null)
            StartPushWatcher(session);

        PersistSession(session);
        SaveAccountCache();
        NotifyAccountsChanged();
    }

    /// <summary>
    /// Replace a login's credentials: reconnect, then restart the accounts that
    /// were syncing (detaching any that no longer exist on the server).
    /// </summary>
    public async Task UpdateLoginAsync(string loginId, LoginCredential credential, CancellationToken ct = default)
    {
        var oldSession = FindSession(loginId);
        if (oldSession == null)
        {
            bool known;
            lock (_lock)
            {
                known = _failedLogins.RemoveAll(f => f.LoginId == loginId) > 0
                    || _connectingLoginIds.Contains(loginId);
                if (known && !_connectingLoginIds.Contains(loginId))
                    _connectingLoginIds.Add(loginId);
            }
            if (!known)
                throw new InvalidOperationException($"Login {loginId} not found");
            // Failed login: nothing to tear down — connect fresh with the new credentials.
            NotifyAccountsChanged();
            try
            {
                await ConnectAndStartAsync(credential, loginId, enabledAccountIds: null, persist: true, clean: false, ct);
            }
            finally
            {
                lock (_lock) _connectingLoginIds.Remove(loginId);
                NotifyAccountsChanged();
            }
            return;
        }

        // Connect with the new credentials first, so a bad token changes nothing.
        var (client, current) = await ConnectClientAsync(credential, loginId, ct);
        var newAccounts = client.GetFileNodeAccounts();

        var oldSupervisors = SupervisorsOf(oldSession);
        var previouslyActive = oldSupervisors.Select(s => s.AccountId).ToHashSet();

        StopPushWatcher(oldSession);
        foreach (var supervisor in oldSupervisors)
        {
            await StopSupervisorAsync(supervisor);
            if (newAccounts.All(a => a.AccountId != supervisor.AccountId))
            {
                Log.Info($"Account {supervisor.AccountId} removed from server, detaching...");
                DetachSyncRoot(supervisor);
            }
        }
        lock (_lock) _sessions.Remove(oldSession);
        oldSession.Client.Dispose();

        var session = new LoginSession(loginId, client, current, newAccounts, _parentCt);
        lock (_lock) _sessions.Add(session);

        foreach (var account in newAccounts.Where(a => previouslyActive.Contains(a.AccountId)))
            await TryStartSupervisorAsync(session, account, clean: false, ct);

        StartPushWatcher(session);
        PersistSession(session);
        NotifyAccountsChanged();
    }

    /// <summary>Stop all supervisors and push watchers gracefully.</summary>
    public async Task StopAllAsync()
    {
        List<LoginSession> sessions;
        List<AccountSupervisor> all;
        lock (_lock)
        {
            sessions = _sessions.ToList();
            all = _supervisors.ToList();
        }
        foreach (var session in sessions)
            StopPushWatcher(session);
        foreach (var supervisor in all)
        {
            try { await supervisor.StopAsync(); }
            catch (Exception ex) { Log.Error($"Error stopping {supervisor.DisplayName}: {ex.Message}"); }
        }
    }

    public string? GetLoginIdForAccount(string accountId) => FindSessionForAccount(accountId)?.LoginId;

    /// <summary>All FileNode accounts for a login (from its JMAP session), or null if unknown.</summary>
    public List<(string AccountId, string Name, bool IsPrimary)>? GetLoginAccounts(string loginId)
        => FindSession(loginId)?.Accounts;

    /// <summary>The set of currently-syncing account IDs for a login.</summary>
    public HashSet<string> GetActiveAccountIds(string loginId)
    {
        var session = FindSession(loginId);
        return session == null ? new() : SupervisorsOf(session).Select(s => s.AccountId).ToHashSet();
    }

    /// <summary>
    /// Fetch fresh accounts from the server for an existing login without
    /// disturbing its session or supervisors.
    /// </summary>
    public async Task<List<(string AccountId, string Name, bool IsPrimary)>> RefreshLoginAccountsAsync(
        string loginId, CancellationToken ct = default)
    {
        var session = FindSession(loginId)
            ?? throw new InvalidOperationException($"Login {loginId} not found");
        Log.Info($"Refreshing accounts for login {loginId}...");
        var accounts = await DiscoverAccountsAsync(session.Credential, ct);
        Log.Info($"Refreshed login {loginId}: {accounts.Count} account(s) found");
        return accounts;
    }

    public void PauseAccount(string accountId)
    {
        var supervisor = FindSupervisor(accountId);
        if (supervisor == null) return;
        supervisor.Pause(SyncPauseReason.UserRequested);
        RaiseAggregateStatus();
    }

    public void ResumeAccount(string accountId)
    {
        var supervisor = FindSupervisor(accountId);
        if (supervisor == null) return;
        supervisor.Resume(SyncPauseReason.UserRequested);
        supervisor.PushState(""); // catch-up poll
        RaiseAggregateStatus();
    }

    public void SyncNow(string accountId) => FindSupervisor(accountId)?.SyncNow();

    public SyncStatus GetAggregateStatus()
    {
        lock (_lock)
        {
            // Worst-of-all: Error > Disconnected > Syncing > Idle
            if (_supervisors.Any(s => s.Status == SyncStatus.Error)) return SyncStatus.Error;
            if (_supervisors.Any(s => s.Status == SyncStatus.Disconnected)) return SyncStatus.Disconnected;
            if (_supervisors.Any(s => s.Status == SyncStatus.Syncing)) return SyncStatus.Syncing;
            return SyncStatus.Idle;
        }
    }

    public int GetAggregatePendingCount()
    {
        lock (_lock) return _supervisors.Sum(s => s.PendingCount);
    }

    // ---- The three primitives ----

    /// <summary>
    /// Create, register and start a supervisor for one account. Throws if it
    /// fails to start (after cleaning up after itself).
    /// </summary>
    private Task<AccountSupervisor> StartSupervisorAsync(LoginSession session,
        (string AccountId, string Name, bool IsPrimary) account, bool clean, CancellationToken ct)
    {
        var displayName = $"{account.Name} Files";
        return StartSupervisorAsync(session, account, displayName, SyncRootPathFor(displayName), clean, ct);
    }

    private async Task<AccountSupervisor> StartSupervisorAsync(LoginSession session,
        (string AccountId, string Name, bool IsPrimary) account,
        string displayName, string syncRootPath, bool clean, CancellationToken ct)
    {
        IJmapClient client = account.IsPrimary ? session.Client : session.Client.ForAccount(account.AccountId);
        var supervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug, ConflictStrategy);
        // Wire status before StartAsync so the UI sees each account's
        // Discovering → Idle transition as it happens.
        supervisor.StatusChanged += _ => RaiseAggregateStatus();
        supervisor.PendingCountChanged += _ => RaiseAggregateStatus();
        lock (_lock) _supervisors.Add(supervisor);
        try
        {
            await supervisor.StartAsync(_iconPath, clean, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start supervisor for {displayName}: {ex}");
            lock (_lock) _supervisors.Remove(supervisor);
            supervisor.Dispose();
            throw;
        }
        if (_networkMonitor is { IsMetered: true })
            supervisor.Pause(SyncPauseReason.MeteredConnection);
        lock (_lock) _cachedAccounts.RemoveAll(c => c.AccountId == account.AccountId);
        return supervisor;
    }

    /// <summary>Like <see cref="StartSupervisorAsync"/>, but a failure only logs (for multi-account loops).</summary>
    private async Task TryStartSupervisorAsync(LoginSession session,
        (string AccountId, string Name, bool IsPrimary) account, bool clean, CancellationToken ct)
    {
        try { await StartSupervisorAsync(session, account, clean, ct); }
        catch (Exception) { /* already logged */ }
    }

    /// <summary>
    /// Stop a supervisor, disconnect it from cfapi and forget it. Disconnecting
    /// first matters: while connected, the cloud filter driver holds handles on
    /// the sync root and any Clean/Detach fails with ACCESS_DENIED.
    /// </summary>
    private async Task StopSupervisorAsync(AccountSupervisor supervisor)
    {
        try { await supervisor.StopAsync(); }
        catch (Exception ex) { Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}"); }
        supervisor.Dispose();
        lock (_lock) _supervisors.Remove(supervisor);
    }

    /// <summary>
    /// Write a session's current credential and enabled-account set to the
    /// credential store, and stop its push watcher if nothing is left syncing.
    /// </summary>
    private void PersistSession(LoginSession session)
    {
        var active = GetActiveAccountIds(session.LoginId);
        if (active.Count == 0)
            StopPushWatcher(session);
        _credentialStore.Save(session.LoginId, session.Credential, active.Count > 0 ? active : null);
    }

    // ---- Connecting ----

    /// <summary>
    /// Build a JmapClient for a credential and connect it. For OAuth logins,
    /// token refreshes are written back to the store as they happen. Returns
    /// the client and the credential as it stands after connecting (a refresh
    /// may have occurred during the session fetch).
    /// </summary>
    private async Task<(JmapClient Client, LoginCredential Current)> ConnectClientAsync(
        LoginCredential credential, string? loginId, CancellationToken ct)
    {
        var current = credential;
        JmapClient client;
        if (credential.IsOAuth)
        {
            var expiresAt = credential.ExpiresAtUnixSeconds.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(credential.ExpiresAtUnixSeconds.Value)
                : DateTimeOffset.UtcNow.AddSeconds(3600);
            var handler = new OAuthTokenHandler(credential.Token, credential.RefreshToken!,
                credential.TokenEndpoint!, credential.ClientId!, expiresAt);
            handler.TokenRefreshed += h =>
            {
                current = current with
                {
                    Token = h.AccessToken,
                    RefreshToken = h.RefreshToken ?? current.RefreshToken,
                    ExpiresAtUnixSeconds = h.ExpiresAt.ToUnixTimeSeconds(),
                };
                // Once the session exists it owns the credential; until then
                // the caller persists `current` after connecting.
                var session = loginId != null ? FindSession(loginId) : null;
                if (session != null)
                {
                    session.Credential = current;
                    PersistSession(session);
                }
            };
            client = new JmapClient(handler, _debug);
        }
        else
        {
            client = new JmapClient(credential.Token, _debug);
        }

        try
        {
            Log.Info("Connecting to JMAP...");
            await client.ConnectAsync(credential.SessionUrl, ct);
            Log.Info($"Connected as {client.Session.Username}");
        }
        catch
        {
            client.Dispose();
            throw;
        }
        return (client, current);
    }

    private async Task<string> ConnectAndStartAsync(LoginCredential credential, string? loginId,
        HashSet<string>? enabledAccountIds, bool persist, bool clean, CancellationToken ct)
    {
        var (client, current) = await ConnectClientAsync(credential, loginId, ct);
        loginId ??= CredentialStore.DeriveLoginId(client.Session.Username, credential.SessionUrl);

        // Re-adding an existing login (e.g. with a new token) replaces it and starts clean.
        if (FindSession(loginId) != null)
        {
            Log.Info($"Replacing existing login {loginId}");
            await RemoveLoginAsync(loginId);
            clean = true;
        }

        var accounts = client.GetFileNodeAccounts();
        if (accounts.Count == 0)
        {
            client.Dispose();
            throw new InvalidOperationException("No accounts with FileNode capability found");
        }

        var session = new LoginSession(loginId, client, current, accounts, _parentCt);
        lock (_lock) _sessions.Add(session);

        // Start supervisors sequentially. (Parallel start was tried and caused
        // socket errors when several accounts hit the shared HttpClient at once
        // during cold start.)
        foreach (var account in accounts)
        {
            if (enabledAccountIds != null && !enabledAccountIds.Contains(account.AccountId))
                continue;
            await TryStartSupervisorAsync(session, account, clean, ct);
        }

        StartPushWatcher(session);
        if (persist)
            _credentialStore.Save(loginId, current, enabledAccountIds);
        SaveAccountCache();
        NotifyAccountsChanged();
        return loginId;
    }

    private async Task RetryFailedLoginsAsync()
    {
        if (_networkMonitor is not { IsConnected: true })
            return;
        if (Interlocked.CompareExchange(ref _retryInProgress, 1, 0) != 0)
            return;
        try
        {
            List<FailedLoginInfo> toRetry;
            lock (_lock)
            {
                if (_failedLogins.Count == 0) return;
                toRetry = _failedLogins.ToList();
                _failedLogins.Clear();
                foreach (var f in toRetry)
                    _connectingLoginIds.Add(f.LoginId);
            }
            NotifyAccountsChanged();

            foreach (var f in toRetry)
            {
                Log.Info($"[RetryLogin] Retrying failed login {f.LoginId}...");
                await ConnectLoginAsync(f.LoginId, f.Credential, f.EnabledAccountIds,
                    persist: false, clean: false, ct: _parentCt);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _retryInProgress, 0);
        }
    }

    // ---- Lookups ----

    private LoginSession? FindSession(string loginId)
    {
        lock (_lock) return _sessions.FirstOrDefault(s => s.LoginId == loginId);
    }

    private LoginSession? FindSessionForAccount(string accountId)
    {
        lock (_lock) return _sessions.FirstOrDefault(s => s.AccountIds.Contains(accountId));
    }

    private AccountSupervisor? FindSupervisor(string accountId)
    {
        lock (_lock) return _supervisors.FirstOrDefault(s => s.AccountId == accountId);
    }

    private List<AccountSupervisor> SupervisorsOf(LoginSession session)
    {
        lock (_lock) return _supervisors.Where(s => session.AccountIds.Contains(s.AccountId)).ToList();
    }

    // ---- Sync roots & caches ----

    private static string SyncRootPathFor(string displayName) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), SanitizeFolderName(displayName));

    private static void CleanSyncRoot(AccountSupervisor supervisor)
    {
        try
        {
            Log.Info($"Cleaning sync root for {supervisor.DisplayName}...");
            SyncEngine.Clean(supervisor.SyncRootPath, supervisor.AccountId);
        }
        catch (Exception ex)
        {
            Log.Error($"Error cleaning sync root for {supervisor.DisplayName}: {ex.Message}");
        }
    }

    private static void DetachSyncRoot(AccountSupervisor supervisor)
    {
        try
        {
            Log.Info($"Detaching sync root for {supervisor.DisplayName}...");
            SyncEngine.Detach(supervisor.SyncRootPath, supervisor.AccountId);
            NodeCache.Delete(supervisor.ScopeKey);
        }
        catch (Exception ex)
        {
            Log.Error($"Error detaching sync root for {supervisor.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>Persist current live accounts to cache for instant display on next startup.</summary>
    private void SaveAccountCache()
    {
        List<AccountCache.CachedAccount> toSave;
        lock (_lock)
        {
            toSave = _supervisors.Select(s => new AccountCache.CachedAccount(
                s.AccountId, GetLoginIdForAccount(s.AccountId) ?? "",
                s.DisplayName, s.SyncRootPath, s.Username)).ToList();
        }
        AccountCache.Save(toSave);
    }

    /// <summary>
    /// Detach sync roots registered on this machine that no active supervisor
    /// owns (e.g. account removed server-side between runs, or a partial cleanup).
    /// </summary>
    private void AuditOrphanedSyncRoots()
    {
        try
        {
            HashSet<string> activeAccountIds;
            lock (_lock) activeAccountIds = _supervisors.Select(s => s.AccountId).ToHashSet();

            foreach (var (accountId, path) in SyncEngine.GetRegisteredSyncRoots())
            {
                if (activeAccountIds.Contains(accountId)) continue;
                Log.Info($"Orphaned sync root detected: accountId={accountId}, path={path}");
                try { SyncEngine.Detach(path, accountId); }
                catch (Exception ex) { Log.Error($"Failed to detach orphaned sync root {accountId}: {ex.Message}"); }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Orphaned sync root audit failed: {ex.Message}");
        }
    }

    internal static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.Select(c => Array.IndexOf(invalid, c) >= 0 ? '_' : c).ToArray();
        return new string(chars).TrimEnd(' ', '.');
    }

    // ---- Push (one SSE connection per login, fanned out to its accounts) ----

    private void StartPushWatcher(LoginSession session)
    {
        session.PushCts = CancellationTokenSource.CreateLinkedTokenSource(session.ParentCt);
        session.PushTask = Task.Run(() => RunPushWatcherAsync(session, session.PushCts.Token));
    }

    private static void StopPushWatcher(LoginSession session)
    {
        session.PushCts?.Cancel();
        if (session.PushTask != null)
        {
            try { session.PushTask.Wait(3000); }
            catch { }
        }
        session.PushCts?.Dispose();
        session.PushCts = null;
        session.PushTask = null;
    }

    private async Task RunPushWatcherAsync(LoginSession session, CancellationToken ct)
    {
        var username = session.Client.Session.Username;
        Log.Info($"[Push:{username}] Starting shared push watcher");

        int backoffMs = 30000;
        const int maxBackoffMs = 60000;
        bool wasDisconnected = false;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                await foreach (var (accountId, state) in session.Client.WatchAllAccountChangesAsync(ct))
                {
                    if (wasDisconnected)
                    {
                        Log.Info($"[Push:{username}] Reconnected");
                        NotifyConnectivity(session, restored: true);
                        wasDisconnected = false;
                    }
                    backoffMs = 30000; // Reset after receiving a successful event
                    FindSupervisor(accountId)?.PushState(state);
                }

                // Stream ended normally — reconnect after a short delay
                Log.Info($"[Push:{username}] SSE stream ended, reconnecting in 5s...");
                await Task.Delay(5000, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[Push:{username}] SSE error: {ex.Message}");
                wasDisconnected = true;
                // SSE is only a notification channel — don't mark accounts
                // offline; poll instead so they stay current.
                foreach (var s in SupervisorsOf(session))
                    s.PushState("");
                try { await Task.Delay(backoffMs, ct); }
                catch (OperationCanceledException) { break; }
                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
            }
        }

        Log.Info($"[Push:{username}] Push watcher stopped");
    }

    private void NotifyConnectivity(LoginSession session, bool restored)
    {
        foreach (var s in SupervisorsOf(session))
        {
            if (restored)
            {
                s.NotifyConnectivityRestored();
                s.PushState(""); // Force poll after reconnect
            }
            else
            {
                s.NotifyConnectivityLost();
            }
        }
    }

    // ---- Environment monitoring ----

    private void CheckDiskSpace()
    {
        try
        {
            foreach (var supervisor in Supervisors)
            {
                var freeBytes = supervisor.GetFreeDiskSpace();
                if (freeBytes == null) continue;
                var isPausedForDisk = supervisor.PauseReason.HasFlag(SyncPauseReason.DiskFull);
                if (!isPausedForDisk && freeBytes < DiskFullThresholdBytes)
                {
                    Log.Warn($"[DiskCheck] {supervisor.DisplayName}: {freeBytes / (1024 * 1024)}MB free — pausing sync");
                    supervisor.Pause(SyncPauseReason.DiskFull);
                    RaiseAggregateStatus();
                }
                else if (isPausedForDisk && freeBytes > DiskResumeThresholdBytes)
                {
                    Log.Info($"[DiskCheck] {supervisor.DisplayName}: {freeBytes / (1024 * 1024)}MB free — resuming sync");
                    supervisor.Resume(SyncPauseReason.DiskFull);
                    supervisor.PushState(""); // Catch up
                    RaiseAggregateStatus();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"CheckDiskSpace failed: {ex.Message}");
        }
    }

    private void OnNetworkStateChanged(bool isConnected, bool isMetered)
    {
        List<LoginSession> sessions;
        lock (_lock) sessions = _sessions.ToList();
        var supervisors = Supervisors;

        if (!isConnected)
        {
            Log.Info("[NetworkMonitor] Network offline — marking accounts disconnected");
            foreach (var session in sessions)
            {
                StopPushWatcher(session);
                NotifyConnectivity(session, restored: false);
            }
        }
        else
        {
            Log.Info("[NetworkMonitor] Network online — reconnecting");
            foreach (var session in sessions)
            {
                NotifyConnectivity(session, restored: true);
                StartPushWatcher(session);
            }
            Log.FireAndForget(RetryFailedLoginsAsync(), "RetryFailedLoginsOnRestore");
        }

        // Metered: pause outbox + background sync, but still allow hydration
        if (isMetered)
        {
            Log.Info("[NetworkMonitor] Metered connection — background sync and uploads suppressed");
            foreach (var supervisor in supervisors)
                supervisor.Pause(SyncPauseReason.MeteredConnection);
        }
        else if (isConnected)
        {
            Log.Info("[NetworkMonitor] Unmetered connection — full sync enabled");
            foreach (var supervisor in supervisors)
            {
                supervisor.Resume(SyncPauseReason.MeteredConnection);
                supervisor.PushState(""); // catch-up poll
            }
        }

        RaiseAggregateStatus();
    }

    // ---- Events ----

    private void NotifyAccountsChanged()
    {
        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    private void RaiseAggregateStatus()
    {
        var status = GetAggregateStatus();
        Log.SafeInvoke(() => AggregateStatusChanged?.Invoke(status), "LoginManager.AggregateStatusChanged");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _retryTimer?.Dispose();
        _diskCheckTimer?.Dispose();
        _networkMonitor?.Dispose();

        lock (_lock)
        {
            foreach (var session in _sessions)
                StopPushWatcher(session);
            foreach (var supervisor in _supervisors)
                supervisor.Dispose();
            _supervisors.Clear();
            foreach (var session in _sessions)
                session.Client.Dispose();
            _sessions.Clear();
        }
    }
}

/// <summary>A connected JMAP login and the accounts it exposes.</summary>
sealed class LoginSession
{
    public string LoginId { get; }
    public JmapClient Client { get; }
    /// <summary>Current credential — updated in place when an OAuth token is refreshed.</summary>
    public LoginCredential Credential { get; set; }
    public List<(string AccountId, string Name, bool IsPrimary)> Accounts { get; }
    public HashSet<string> AccountIds { get; }
    /// <summary>Parent CT for the push watcher, kept so push can be restarted after a network drop.</summary>
    public CancellationToken ParentCt { get; }
    public CancellationTokenSource? PushCts { get; set; }
    public Task? PushTask { get; set; }

    public LoginSession(string loginId, JmapClient client, LoginCredential credential,
        List<(string AccountId, string Name, bool IsPrimary)> accounts, CancellationToken parentCt)
    {
        LoginId = loginId;
        Client = client;
        Credential = credential;
        Accounts = accounts;
        AccountIds = accounts.Select(a => a.AccountId).ToHashSet();
        ParentCt = parentCt;
    }

    public (string AccountId, string Name, bool IsPrimary)? Account(string accountId)
    {
        foreach (var a in Accounts)
            if (a.AccountId == accountId) return a;
        return null;
    }
}
