using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using FileNodeClient.Windows;

namespace FileNodeClient.Service;

/// <summary>
/// Orchestrates multiple JMAP logins, discovers FileNode-capable accounts
/// in each, and manages AccountSupervisor lifecycle.
/// </summary>
sealed class LoginManager : IDisposable
{
    private readonly record struct FailedLoginInfo(string LoginId, string SessionUrl, string Token,
        HashSet<string>? EnabledAccountIds, string Error,
        string? RefreshToken = null, string? TokenEndpoint = null,
        string? ClientId = null, long? ExpiresAtUnixSeconds = null);

    private const long DiskFullThresholdBytes = 500L * 1024 * 1024;   // Pause at <500MB
    private const long DiskResumeThresholdBytes = 1024L * 1024 * 1024; // Resume at >1GB

    private readonly bool _debug;
    private readonly CredentialStore _credentialStore = new();
    private readonly List<LoginSession> _sessions = new();
    private readonly List<AccountSupervisor> _supervisors = new();
    private readonly List<string> _connectingLoginIds = new();
    private readonly List<FailedLoginInfo> _failedLogins = new();
    private List<AccountCache.CachedAccount> _cachedAccounts = new();
    private readonly object _lock = new();
    private NetworkMonitor? _networkMonitor;
    private Timer? _retryTimer;
    private Timer? _diskCheckTimer;
    private int _retryInProgress;
    private string? _iconPath;
    private CancellationToken _parentCt;
    private bool _disposed;

    public IReadOnlyList<AccountSupervisor> Supervisors
    {
        get { lock (_lock) return _supervisors.ToList(); }
    }

    /// <summary>
    /// Login IDs currently in the process of connecting (not yet in Supervisors).
    /// </summary>
    public IReadOnlyList<string> ConnectingLoginIds
    {
        get { lock (_lock) return _connectingLoginIds.ToList(); }
    }

    /// <summary>
    /// Logins that failed to connect at startup (credential exists but session fetch failed).
    /// </summary>
    public IReadOnlyList<(string LoginId, string Error)> FailedLogins
    {
        get { lock (_lock) return _failedLogins.Select(f => (f.LoginId, f.Error)).ToList(); }
    }

    /// <summary>
    /// Login IDs with active sessions (connected, regardless of enabled accounts).
    /// </summary>
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

    public LoginManager(bool debug)
    {
        _debug = debug;
    }

    /// <summary>
    /// Load stored credentials and start all accounts.
    /// </summary>
    public async Task StartAsync(string? iconPath, bool clean, CancellationToken ct)
    {
        _iconPath = iconPath;
        _parentCt = ct;

        _networkMonitor = new NetworkMonitor();
        _networkMonitor.NetworkStateChanged += OnNetworkStateChanged;
        Log.Info($"[NetworkMonitor] Initial state: connected={_networkMonitor.IsConnected}, metered={_networkMonitor.IsMetered}");

        // Periodic disk space check as a safety net (5 minutes).
        // Primary checks happen on-demand: before hydration (via HydrationBlockedReason)
        // and after sync polls (via CheckDiskSpaceForAccount).
        _diskCheckTimer = new Timer(_ => CheckDiskSpace(), null, TimeSpan.FromSeconds(10), TimeSpan.FromMinutes(5));

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
        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");

        foreach (var login in storedLogins)
        {
            try
            {
                Log.Info($"Loading login: {login.LoginId}");
                await ConnectAndStartAsync(login.SessionUrl, login.Token, login.LoginId,
                    enabledAccountIds: login.EnabledAccountIds,
                    persist: false, iconPath: iconPath, clean: clean, ct: ct,
                    refreshToken: login.RefreshToken, tokenEndpoint: login.TokenEndpoint,
                    clientId: login.ClientId, expiresAtUnixSeconds: login.ExpiresAtUnixSeconds);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to load login {login.LoginId}: {ex.Message}");
                lock (_lock)
                    _failedLogins.Add(new FailedLoginInfo(login.LoginId, login.SessionUrl, login.Token,
                        login.EnabledAccountIds, ex.Message,
                        login.RefreshToken, login.TokenEndpoint, login.ClientId, login.ExpiresAtUnixSeconds));
            }
            finally
            {
                lock (_lock)
                    _connectingLoginIds.Remove(login.LoginId);
                Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
            }
        }

        // Audit for orphaned sync roots (accounts removed server-side between runs)
        AuditOrphanedSyncRoots();

        // Periodically retry failed logins (e.g. server briefly unreachable at startup)
        _retryTimer = new Timer(_ => Log.FireAndForget(RetryFailedLoginsAsync(), "RetryFailedLogins"), null,
            TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// Discover accounts for a given session URL and token without starting supervisors.
    /// Returns the list of FileNode-capable accounts.
    /// </summary>
    public async Task<List<(string AccountId, string Name, bool IsPrimary)>> DiscoverAccountsAsync(
        string sessionUrl, string token, CancellationToken ct = default)
    {
        var jmapClient = new JmapClient(token, _debug);
        try
        {
            await jmapClient.ConnectAsync(sessionUrl, ct);
            return jmapClient.GetFileNodeAccounts();
        }
        finally
        {
            jmapClient.Dispose();
        }
    }

    /// <summary>
    /// Add a new login, connect, discover accounts, start supervisors.
    /// </summary>
    public async Task<string> AddLoginAsync(string sessionUrl, string token,
        bool persist = true, string? iconPath = null, bool clean = false,
        HashSet<string>? enabledAccountIds = null, CancellationToken ct = default,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null)
    {
        var loginId = await ConnectAndStartAsync(sessionUrl, token, loginId: null,
            enabledAccountIds: enabledAccountIds,
            persist: persist, iconPath: iconPath, clean: clean, ct: ct,
            refreshToken: refreshToken, tokenEndpoint: tokenEndpoint,
            clientId: clientId, expiresAtUnixSeconds: expiresAtUnixSeconds);
        return loginId;
    }

    /// <summary>
    /// Remove a login: stop all its supervisors, clean sync roots, remove credential.
    /// </summary>
    public async Task RemoveLoginAsync(string loginId)
    {
        List<AccountSupervisor> toStop;
        LoginSession? session;

        lock (_lock)
        {
            session = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            toStop = _supervisors.Where(s => session != null &&
                session.AccountIds.Contains(s.AccountId)).ToList();
        }

        // Stop push watcher first
        if (session != null)
            StopPushWatcher(session);

        foreach (var supervisor in toStop)
        {
            try { await supervisor.StopAsync(); }
            catch (Exception ex)
            {
                Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
            }

            // Dispose first to disconnect cfapi, then clean up the sync root.
            // If clean runs while cfapi is still connected, re-registration fails
            // with "unauthorized operation".
            supervisor.Dispose();
            CleanSupervisorSyncRoot(supervisor);
        }

        lock (_lock)
        {
            foreach (var supervisor in toStop)
                _supervisors.Remove(supervisor);
            if (session != null)
                _sessions.Remove(session);
            _failedLogins.RemoveAll(f => f.LoginId == loginId);
        }

        _credentialStore.Remove(loginId);

        // Remove cached entries for this login
        lock (_lock)
            _cachedAccounts.RemoveAll(c => c.LoginId == loginId);
        SaveAccountCache();

        // Clean up any orphaned sync root registrations left behind by
        // partial cleanup failures (e.g. error 5 from cfapi).
        AuditOrphanedSyncRoots();

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Clean up a single account: stop supervisor, unregister sync root, delete local files.
    /// If other accounts remain under the same login, update the credential's enabled set.
    /// If no accounts remain, remove the credential entirely.
    /// </summary>
    public async Task CleanUpAccountAsync(string accountId)
    {
        AccountSupervisor? supervisor;
        LoginSession? session;

        lock (_lock)
        {
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
            session = _sessions.FirstOrDefault(s => s.AccountIds.Contains(accountId));
        }

        if (supervisor == null)
            return;

        try { await supervisor.StopAsync(); }
        catch (Exception ex)
        {
            Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        // Dispose first to disconnect from the cloud filter driver — otherwise
        // CfConnectSyncRoot holds handles on directories and deletion fails
        // with ACCESS_DENIED (error 5).
        supervisor.Dispose();

        CleanSupervisorSyncRoot(supervisor);

        lock (_lock)
            _supervisors.Remove(supervisor);

        // Update the stored credential — keep session alive even with 0 accounts
        // so the user can re-enable accounts without re-adding the login.
        if (session != null)
        {
            var remaining = GetActiveAccountIds(session.LoginId);
            if (remaining.Count == 0)
                StopPushWatcher(session);
            _credentialStore.Save(session.LoginId, session.Token, session.SessionUrl, remaining,
                session.RefreshToken, session.TokenEndpoint, session.ClientId, session.ExpiresAtUnixSeconds);
        }

        // Clean up any orphaned sync root registrations left behind by
        // partial cleanup failures (e.g. error 5 from cfapi).
        AuditOrphanedSyncRoots();

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Check for sync roots registered on this machine that don't correspond
    /// to any active supervisor (e.g. account removed server-side between runs).
    /// Detach orphans so the Explorer nav pane entry is removed.
    /// </summary>
    /// <summary>
    /// Persist current live accounts to cache for instant display on next startup.
    /// </summary>
    private void SaveAccountCache()
    {
        List<AccountCache.CachedAccount> toSave;
        lock (_lock)
        {
            toSave = _supervisors.Select(s => new AccountCache.CachedAccount(
                s.AccountId,
                GetLoginIdForAccount(s.AccountId) ?? "",
                s.DisplayName,
                s.SyncRootPath,
                s.Username)).ToList();
        }
        AccountCache.Save(toSave);
    }

    private void AuditOrphanedSyncRoots()
    {
        try
        {
            var registeredRoots = SyncEngine.GetRegisteredSyncRoots();
            if (registeredRoots.Count == 0)
                return;

            HashSet<string> activeAccountIds;
            lock (_lock)
                activeAccountIds = _supervisors.Select(s => s.AccountId).ToHashSet();

            foreach (var (accountId, path) in registeredRoots)
            {
                if (activeAccountIds.Contains(accountId))
                    continue;

                Log.Info($"Orphaned sync root detected: accountId={accountId}, path={path}");
                try
                {
                    SyncEngine.Detach(path, accountId);
                    Log.Info($"Detached orphaned sync root: {accountId}");
                }
                catch (Exception ex)
                {
                    Log.Error($"Failed to detach orphaned sync root {accountId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Orphaned sync root audit failed: {ex.Message}");
        }
    }

    private static void CleanSupervisorSyncRoot(AccountSupervisor supervisor)
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

    /// <summary>
    /// Update credentials for an existing login: reconnect with new token/URL,
    /// rediscover accounts, restart existing supervisors, detach removed accounts.
    /// </summary>
    public async Task UpdateLoginAsync(string loginId, string sessionUrl, string token,
        string? iconPath = null, CancellationToken ct = default,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null)
    {
        LoginSession? oldSession;
        List<AccountSupervisor> oldSupervisors;
        bool wasFailedLogin = false;

        lock (_lock)
        {
            oldSession = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            if (oldSession == null)
            {
                if (_failedLogins.Any(f => f.LoginId == loginId))
                {
                    _failedLogins.RemoveAll(f => f.LoginId == loginId);
                    wasFailedLogin = true;
                }
                else if (_connectingLoginIds.Contains(loginId))
                {
                    // Login is currently being retried — treat as failed login
                    // so we replace with the new credentials.
                    wasFailedLogin = true;
                }
                else
                {
                    throw new InvalidOperationException($"Login {loginId} not found");
                }
            }
            oldSupervisors = oldSession != null
                ? _supervisors.Where(s => oldSession.AccountIds.Contains(s.AccountId)).ToList()
                : new();
        }

        // Failed login: no existing session/supervisors — just connect fresh
        if (wasFailedLogin)
        {
            lock (_lock)
                _connectingLoginIds.Add(loginId);
            Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");

            try
            {
                await ConnectAndStartAsync(sessionUrl, token, loginId,
                    enabledAccountIds: null, persist: true,
                    iconPath: iconPath ?? _iconPath, clean: false, ct: ct,
                    refreshToken: refreshToken, tokenEndpoint: tokenEndpoint,
                    clientId: clientId, expiresAtUnixSeconds: expiresAtUnixSeconds);
            }
            finally
            {
                lock (_lock)
                    _connectingLoginIds.Remove(loginId);
                Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
            }
            return;
        }

        // Stop push watcher (oldSession is non-null here: wasFailedLogin returned early, else threw)
        StopPushWatcher(oldSession!);

        // Stop all supervisors (but don't clean sync roots)
        foreach (var supervisor in oldSupervisors)
        {
            try { await supervisor.StopAsync(); }
            catch (Exception ex)
            {
                Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
            }
        }

        // Connect with new credentials
        JmapClient jmapClient;
        Jmap.Auth.OAuthTokenHandler? oauthHandler = null;
        if (refreshToken != null && tokenEndpoint != null && clientId != null)
        {
            var expiresAt = expiresAtUnixSeconds.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds.Value)
                : DateTimeOffset.UtcNow.AddSeconds(3600);
            oauthHandler = new Jmap.Auth.OAuthTokenHandler(token, refreshToken,
                tokenEndpoint, clientId, expiresAt);
            jmapClient = new JmapClient(oauthHandler, _debug);
        }
        else
        {
            jmapClient = new JmapClient(token, _debug);
        }

        // Subscribe to token refresh events BEFORE ConnectAsync so refreshes
        // during the initial session fetch are persisted.
        LoginSession? newSession = null;
        if (oauthHandler != null)
        {
            oauthHandler.TokenRefreshed += handler =>
            {
                var s = newSession;
                if (s != null)
                {
                    s.Token = handler.AccessToken;
                    if (handler.RefreshToken != null)
                        s.RefreshToken = handler.RefreshToken;
                    s.ExpiresAtUnixSeconds = handler.ExpiresAt.ToUnixTimeSeconds();
                }

                var activeIds = s != null ? GetActiveAccountIds(loginId) : null;
                _credentialStore.Save(loginId, handler.AccessToken, sessionUrl,
                    activeIds is { Count: > 0 } ? activeIds : null,
                    handler.RefreshToken, handler.TokenEndpoint, handler.ClientId,
                    handler.ExpiresAt.ToUnixTimeSeconds());
            };
        }

        await jmapClient.ConnectAsync(sessionUrl, ct);

        var newAccounts = jmapClient.GetFileNodeAccounts();
        var newAccountIds = newAccounts.Select(a => a.AccountId).ToHashSet();

        // Determine previously-synced accounts
        var previouslyActive = oldSupervisors.Select(s => s.AccountId).ToHashSet();

        // Dispose old supervisors
        foreach (var supervisor in oldSupervisors)
            supervisor.Dispose();

        lock (_lock)
        {
            foreach (var supervisor in oldSupervisors)
                _supervisors.Remove(supervisor);
            _sessions.Remove(oldSession!);
        }

        oldSession!.Client.Dispose();

        // Detach accounts no longer on server
        foreach (var accountId in previouslyActive.Where(id => !newAccountIds.Contains(id)))
        {
            Log.Info($"Account {accountId} removed from server, detaching...");
            var displayName = oldSupervisors.FirstOrDefault(s => s.AccountId == accountId)?.DisplayName ?? accountId;
            var syncRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SanitizeFolderName(displayName));
            try
            {
                SyncEngine.Detach(syncRootPath, accountId);
                var scopeKey = $"{oldSession.Client.Session.Username}/{accountId}";
                NodeCache.Delete(scopeKey);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to detach {accountId}: {ex.Message}");
            }
        }

        // Create new session (use handler's current values in case token was refreshed during ConnectAsync)
        newSession = new LoginSession(loginId, jmapClient, sessionUrl,
            oauthHandler?.AccessToken ?? token,
            newAccounts.Select(a => a.AccountId).ToList(),
            oauthHandler?.RefreshToken ?? refreshToken, tokenEndpoint, clientId,
            oauthHandler != null ? oauthHandler.ExpiresAt.ToUnixTimeSeconds() : expiresAtUnixSeconds);
        lock (_lock)
            _sessions.Add(newSession);

        // Restart supervisors for accounts still on server that were previously synced
        var enabledAccountIds = new HashSet<string>();
        var updateStarted = new List<AccountSupervisor>();
        foreach (var (accountId, accountName, isPrimary) in newAccounts)
        {
            if (!previouslyActive.Contains(accountId))
                continue;

            enabledAccountIds.Add(accountId);

            IJmapClient client = isPrimary
                ? jmapClient
                : jmapClient.ForAccount(accountId);

            var displayName = $"{accountName} Files";
            var syncRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SanitizeFolderName(displayName));

            var supervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug);

            lock (_lock)
                _supervisors.Add(supervisor);

            try
            {
                await supervisor.StartAsync(iconPath, clean: false, ct);
                ApplyInitialPauseState(supervisor);
                updateStarted.Add(supervisor);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to restart supervisor for {displayName}: {ex.Message}");
                lock (_lock)
                    _supervisors.Remove(supervisor);
                supervisor.Dispose();
            }
        }

        foreach (var supervisor in updateStarted)
        {
            supervisor.StatusChanged += _ => RaiseAggregateStatus();
            supervisor.PendingCountChanged += _ => RaiseAggregateStatus();
        }

        // Restart push watcher
        StartPushWatcher(newSession, ct);

        // Update credential store with current (possibly refreshed) values
        _credentialStore.Save(loginId, oauthHandler?.AccessToken ?? token, sessionUrl, enabledAccountIds,
            oauthHandler?.RefreshToken ?? refreshToken, tokenEndpoint, clientId,
            oauthHandler != null ? oauthHandler.ExpiresAt.ToUnixTimeSeconds() : expiresAtUnixSeconds);

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Detach a single account: stop syncing, delete dehydrated placeholders,
    /// remove empty folders, unregister sync root, leave hydrated files.
    /// </summary>
    public async Task DetachAccountAsync(string accountId)
    {
        AccountSupervisor? supervisor;
        LoginSession? session;

        lock (_lock)
        {
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
            session = _sessions.FirstOrDefault(s => s.AccountIds.Contains(accountId));
        }

        if (supervisor == null)
            return;

        try { await supervisor.StopAsync(); }
        catch (Exception ex)
        {
            Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        // Detach: delete dehydrated files, empty dirs, unregister sync root
        try
        {
            Log.Info($"Detaching sync root for {supervisor.DisplayName}...");
            SyncEngine.Detach(supervisor.SyncRootPath, supervisor.AccountId);
        }
        catch (Exception ex)
        {
            Log.Error($"Error detaching sync root for {supervisor.DisplayName}: {ex.Message}");
        }

        supervisor.Dispose();

        lock (_lock)
            _supervisors.Remove(supervisor);

        // Delete node cache
        var scopeKey = $"{supervisor.Username}/{supervisor.AccountId}";
        NodeCache.Delete(scopeKey);

        // Update or remove the stored credential
        if (session != null)
        {
            var remaining = GetActiveAccountIds(session.LoginId);
            if (remaining.Count == 0)
                StopPushWatcher(session);
            _credentialStore.Save(session.LoginId, session.Token, session.SessionUrl, remaining,
                session.RefreshToken, session.TokenEndpoint, session.ClientId, session.ExpiresAtUnixSeconds);
        }

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Force full re-sync of an account: delete node cache, stop and restart supervisor.
    /// Preserves existing sync root and local files.
    /// </summary>
    public async Task RefreshAccountAsync(string accountId, string? iconPath = null, CancellationToken ct = default)
    {
        AccountSupervisor? supervisor;
        LoginSession? session;

        lock (_lock)
        {
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
            session = _sessions.FirstOrDefault(s => s.AccountIds.Contains(accountId));
        }

        if (supervisor == null || session == null)
            throw new InvalidOperationException($"Account {accountId} not found");

        // Stop supervisor
        try { await supervisor.StopAsync(); }
        catch (Exception ex)
        {
            Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        // Delete node cache so next start does a full fetch
        var scopeKey = $"{supervisor.Username}/{supervisor.AccountId}";
        NodeCache.Delete(scopeKey);

        var displayName = supervisor.DisplayName;
        var syncRootPath = supervisor.SyncRootPath;
        var accounts = session.Client.GetFileNodeAccounts();
        var isPrimary = accounts.FirstOrDefault(a => a.AccountId == accountId).IsPrimary;

        supervisor.Dispose();
        lock (_lock)
            _supervisors.Remove(supervisor);

        // Create new supervisor
        IJmapClient client = isPrimary
            ? session.Client
            : session.Client.ForAccount(accountId);

        var newSupervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug);
        newSupervisor.StatusChanged += _ => RaiseAggregateStatus();
        newSupervisor.PendingCountChanged += _ => RaiseAggregateStatus();

        lock (_lock)
            _supervisors.Add(newSupervisor);

        try
        {
            await newSupervisor.StartAsync(iconPath, clean: false, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to restart supervisor for {displayName}: {ex.Message}");
            lock (_lock)
                _supervisors.Remove(newSupervisor);
            newSupervisor.Dispose();
            throw;
        }

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Clean an account: stop supervisor, delete all local files and sync root,
    /// then recreate from scratch.
    /// </summary>
    public async Task CleanAccountAsync(string accountId, string? iconPath = null, CancellationToken ct = default)
    {
        AccountSupervisor? supervisor;
        LoginSession? session;

        lock (_lock)
        {
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
            session = _sessions.FirstOrDefault(s => s.AccountIds.Contains(accountId));
        }

        if (supervisor == null || session == null)
            throw new InvalidOperationException($"Account {accountId} not found");

        // Stop supervisor
        try { await supervisor.StopAsync(); }
        catch (Exception ex)
        {
            Log.Error($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        var displayName = supervisor.DisplayName;
        var syncRootPath = supervisor.SyncRootPath;
        var username = supervisor.Username;
        var accounts = session.Client.GetFileNodeAccounts();
        var isPrimary = accounts.FirstOrDefault(a => a.AccountId == accountId).IsPrimary;

        // Dispose first to disconnect from the cloud filter driver — otherwise
        // CfConnectSyncRoot holds handles on directories and deletion fails
        // with ACCESS_DENIED (error 5).
        supervisor.Dispose();
        lock (_lock)
            _supervisors.Remove(supervisor);

        // Clean: unregister + delete all files
        CleanSupervisorSyncRoot(supervisor);

        // Delete node cache
        var scopeKey = $"{username}/{accountId}";
        NodeCache.Delete(scopeKey);

        // Create new supervisor (fresh start, empty dir)
        IJmapClient client = isPrimary
            ? session.Client
            : session.Client.ForAccount(accountId);

        var newSupervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug);
        newSupervisor.StatusChanged += _ => RaiseAggregateStatus();
        newSupervisor.PendingCountChanged += _ => RaiseAggregateStatus();

        lock (_lock)
            _supervisors.Add(newSupervisor);

        try
        {
            await newSupervisor.StartAsync(iconPath, clean: false, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to restart supervisor for {displayName}: {ex.Message}");
            lock (_lock)
                _supervisors.Remove(newSupervisor);
            newSupervisor.Dispose();
            throw;
        }

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Enable (start syncing) a previously non-synced account for a given login.
    /// </summary>
    public async Task EnableAccountAsync(string loginId, string accountId,
        string? iconPath = null, CancellationToken ct = default)
    {
        LoginSession? session;
        lock (_lock)
        {
            session = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            if (session == null)
                throw new InvalidOperationException($"Login {loginId} not found");

            // Check not already active
            if (_supervisors.Any(s => s.AccountId == accountId))
                throw new InvalidOperationException($"Account {accountId} is already syncing");
        }

        var accounts = session.Client.GetFileNodeAccounts();
        var account = accounts.FirstOrDefault(a => a.AccountId == accountId);
        if (account.AccountId == null)
            throw new InvalidOperationException($"Account {accountId} not found on server");

        IJmapClient client = account.IsPrimary
            ? session.Client
            : session.Client.ForAccount(accountId);

        var displayName = $"{account.Name} Files";
        var syncRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            SanitizeFolderName(displayName));

        var supervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug);
        supervisor.StatusChanged += _ => RaiseAggregateStatus();
        supervisor.PendingCountChanged += _ => RaiseAggregateStatus();

        lock (_lock)
            _supervisors.Add(supervisor);

        try
        {
            await supervisor.StartAsync(iconPath, clean: false, ct);
            ApplyInitialPauseState(supervisor);
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to start supervisor for {displayName}: {ex}");
            lock (_lock)
                _supervisors.Remove(supervisor);
            supervisor.Dispose();
            throw;
        }

        // Restart push watcher if it was stopped (e.g. all accounts were removed then one re-enabled)
        if (session.PushTask == null)
            StartPushWatcher(session, session.ParentCt);

        // Update credential store with new enabled set
        var enabled = GetActiveAccountIds(loginId);
        _credentialStore.Save(loginId, session.Token, session.SessionUrl, enabled,
            session.RefreshToken, session.TokenEndpoint, session.ClientId, session.ExpiresAtUnixSeconds);

        SaveAccountCache();
        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Stop all supervisors and push watchers gracefully.
    /// </summary>
    public async Task StopAllAsync()
    {
        List<LoginSession> sessions;
        List<AccountSupervisor> all;
        lock (_lock)
        {
            sessions = _sessions.ToList();
            all = _supervisors.ToList();
        }

        // Stop push watchers first
        foreach (var session in sessions)
            StopPushWatcher(session);

        foreach (var supervisor in all)
        {
            try { await supervisor.StopAsync(); }
            catch (Exception ex) { Log.Error($"Error stopping {supervisor.DisplayName}: {ex.Message}"); }
        }
    }

    /// <summary>
    /// Find which loginId owns a given accountId.
    /// </summary>
    public string? GetLoginIdForAccount(string accountId)
    {
        lock (_lock)
            return _sessions.FirstOrDefault(s => s.AccountIds.Contains(accountId))?.LoginId;
    }

    /// <summary>
    /// Get all accounts for a given login (from the JMAP session).
    /// </summary>
    public List<(string AccountId, string Name, bool IsPrimary)>? GetLoginAccounts(string loginId)
    {
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            return session?.Client.GetFileNodeAccounts();
        }
    }

    /// <summary>
    /// Get the set of currently-running account IDs for a login.
    /// </summary>
    public HashSet<string> GetActiveAccountIds(string loginId)
    {
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            if (session == null) return new HashSet<string>();
            return _supervisors
                .Where(s => session.AccountIds.Contains(s.AccountId))
                .Select(s => s.AccountId)
                .ToHashSet();
        }
    }

    /// <summary>
    /// Fetch fresh accounts from the server for an existing login without
    /// modifying the cached session or disrupting active supervisors.
    /// </summary>
    public async Task<List<(string AccountId, string Name, bool IsPrimary)>> RefreshLoginAccountsAsync(
        string loginId, CancellationToken ct = default)
    {
        string sessionUrl, token;
        lock (_lock)
        {
            var session = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            if (session == null)
                throw new InvalidOperationException($"Login {loginId} not found");
            sessionUrl = session.SessionUrl;
            token = session.Token;
        }
        Log.Info($"Refreshing accounts for login {loginId} from {sessionUrl}...");
        var accounts = await DiscoverAccountsAsync(sessionUrl, token, ct);
        Log.Info($"Refreshed login {loginId}: {accounts.Count} account(s) found");
        return accounts;
    }

    /// <summary>
    /// Reconfigure which accounts are synced for a given login.
    /// Stops disabled supervisors, starts newly enabled ones, updates credential.
    /// </summary>
    public async Task ConfigureLoginAsync(string loginId, HashSet<string> enabledAccountIds,
        string? iconPath = null, bool clean = false, CancellationToken ct = default)
    {
        LoginSession? session;
        lock (_lock)
            session = _sessions.FirstOrDefault(s => s.LoginId == loginId);

        if (session == null)
            throw new InvalidOperationException($"Login {loginId} not found");

        // Stop supervisors for accounts no longer enabled
        List<AccountSupervisor> toStop;
        lock (_lock)
        {
            toStop = _supervisors
                .Where(s => session.AccountIds.Contains(s.AccountId) && !enabledAccountIds.Contains(s.AccountId))
                .ToList();
        }

        foreach (var supervisor in toStop)
        {
            try { await supervisor.StopAsync(); }
            catch (Exception ex) { Log.Error($"Error stopping {supervisor.DisplayName}: {ex.Message}"); }
            supervisor.Dispose();
        }

        lock (_lock)
        {
            foreach (var supervisor in toStop)
                _supervisors.Remove(supervisor);
        }

        // Start supervisors for newly enabled accounts
        var currentActive = GetActiveAccountIds(loginId);
        var configAccounts = session.Client.GetFileNodeAccounts();
        var configStarted = new List<AccountSupervisor>();
        foreach (var (accountId, accountName, isPrimary) in configAccounts)
        {
            if (!enabledAccountIds.Contains(accountId) || currentActive.Contains(accountId))
                continue;

            IJmapClient client = isPrimary
                ? session.Client
                : session.Client.ForAccount(accountId);

            var displayName = $"{accountName} Files";
            var syncRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SanitizeFolderName(displayName));

            var supervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug);

            lock (_lock)
                _supervisors.Add(supervisor);

            try
            {
                await supervisor.StartAsync(iconPath, clean, ct);
                ApplyInitialPauseState(supervisor);
                configStarted.Add(supervisor);
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start supervisor for {displayName}: {ex}");
                lock (_lock)
                    _supervisors.Remove(supervisor);
                supervisor.Dispose();
            }
        }

        foreach (var supervisor in configStarted)
        {
            supervisor.StatusChanged += _ => RaiseAggregateStatus();
            supervisor.PendingCountChanged += _ => RaiseAggregateStatus();
        }

        // Update stored credential with new selection
        _credentialStore.Save(loginId, session.Token, session.SessionUrl, enabledAccountIds,
            session.RefreshToken, session.TokenEndpoint, session.ClientId, session.ExpiresAtUnixSeconds);

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
    }

    private async Task<string> ConnectAndStartAsync(string sessionUrl, string token, string? loginId,
        HashSet<string>? enabledAccountIds, bool persist, string? iconPath, bool clean, CancellationToken ct,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null)
    {
        JmapClient jmapClient;
        Jmap.Auth.OAuthTokenHandler? oauthHandler = null;
        if (refreshToken != null && tokenEndpoint != null && clientId != null)
        {
            var expiresAt = expiresAtUnixSeconds.HasValue
                ? DateTimeOffset.FromUnixTimeSeconds(expiresAtUnixSeconds.Value)
                : DateTimeOffset.UtcNow.AddSeconds(3600);
            oauthHandler = new Jmap.Auth.OAuthTokenHandler(token, refreshToken,
                tokenEndpoint, clientId, expiresAt);
            jmapClient = new JmapClient(oauthHandler, _debug);
        }
        else
        {
            jmapClient = new JmapClient(token, _debug);
        }
        // Subscribe to token refresh events BEFORE ConnectAsync so that any
        // proactive refresh during the initial session fetch is captured.
        // We use a local holder that the event updates; once we have a LoginSession
        // we switch the handler to update the session directly.
        LoginSession? session = null;
        if (oauthHandler != null)
        {
            oauthHandler.TokenRefreshed += handler =>
            {
                var s = session;
                if (s != null)
                {
                    s.Token = handler.AccessToken;
                    if (handler.RefreshToken != null)
                        s.RefreshToken = handler.RefreshToken;
                    s.ExpiresAtUnixSeconds = handler.ExpiresAt.ToUnixTimeSeconds();
                }

                var lid = loginId ?? "unknown";
                var activeIds = s != null ? GetActiveAccountIds(lid) : enabledAccountIds;
                _credentialStore.Save(lid, handler.AccessToken, sessionUrl,
                    activeIds is { Count: > 0 } ? activeIds : null,
                    handler.RefreshToken, handler.TokenEndpoint, handler.ClientId,
                    handler.ExpiresAt.ToUnixTimeSeconds());
            };
        }

        Log.Info("Connecting to JMAP...");
        await jmapClient.ConnectAsync(sessionUrl, ct);
        Log.Info($"Connected as {jmapClient.Session.Username}");

        loginId ??= CredentialStore.DeriveLoginId(jmapClient.Session.Username, sessionUrl);

        // Remove existing login with same ID (e.g. re-adding with a new token)
        bool existing;
        lock (_lock)
            existing = _sessions.Any(s => s.LoginId == loginId);
        if (existing)
        {
            Log.Info($"Replacing existing login {loginId}");
            await RemoveLoginAsync(loginId);
            clean = true; // Force clean start after removing old login
        }

        // Discover all accounts with FileNode capability
        var accounts = jmapClient.GetFileNodeAccounts();
        if (accounts.Count == 0)
        {
            jmapClient.Dispose();
            throw new InvalidOperationException("No accounts with FileNode capability found");
        }

        // Use the handler's current values in case token was refreshed during ConnectAsync
        session = new LoginSession(loginId, jmapClient, sessionUrl,
            oauthHandler?.AccessToken ?? token,
            accounts.Select(a => a.AccountId).ToList(),
            oauthHandler?.RefreshToken ?? refreshToken, tokenEndpoint, clientId,
            oauthHandler != null ? oauthHandler.ExpiresAt.ToUnixTimeSeconds() : expiresAtUnixSeconds);

        lock (_lock)
            _sessions.Add(session);

        // Create and start supervisors for each enabled account concurrently.
        // Each account has its own JmapClient/JmapQueue/SyncEngine, so populates
        // don't contend; only the shared HttpClient + OAuthTokenHandler are reused,
        // both of which are internally thread-safe. Wire status events BEFORE
        // StartAsync so the UI sees each account's Discovering → Idle transition
        // as it happens, not gated on its siblings completing.
        var accountsToStart = accounts
            .Where(a => enabledAccountIds == null || enabledAccountIds.Contains(a.AccountId))
            .ToList();

        var startTasks = accountsToStart.Select(async acct =>
        {
            var (accountId, accountName, isPrimary) = acct;
            IJmapClient client = isPrimary
                ? jmapClient
                : jmapClient.ForAccount(accountId);

            var displayName = $"{accountName} Files";
            var syncRootPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                SanitizeFolderName(displayName));

            var supervisor = new AccountSupervisor(client, syncRootPath, displayName, _debug);
            supervisor.StatusChanged += _ => RaiseAggregateStatus();
            supervisor.PendingCountChanged += _ => RaiseAggregateStatus();

            lock (_lock)
                _supervisors.Add(supervisor);

            try
            {
                await supervisor.StartAsync(iconPath, clean, ct);
                ApplyInitialPauseState(supervisor);
                return supervisor;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to start supervisor for {displayName}: {ex}");
                lock (_lock)
                    _supervisors.Remove(supervisor);
                supervisor.Dispose();
                return null;
            }
        }).ToList();

        var startedSupervisors = (await Task.WhenAll(startTasks))
            .Where(s => s != null)
            .Select(s => s!)
            .ToList();

        // Start shared push watcher for this session
        StartPushWatcher(session, ct);

        if (persist)
            _credentialStore.Save(loginId, token, sessionUrl, enabledAccountIds,
                refreshToken, tokenEndpoint, clientId, expiresAtUnixSeconds);

        // Remove cached placeholders for accounts that are now live
        lock (_lock)
        {
            var liveIds = startedSupervisors.Select(s => s.AccountId).ToHashSet();
            _cachedAccounts.RemoveAll(c => liveIds.Contains(c.AccountId));
        }
        SaveAccountCache();

        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
        RaiseAggregateStatus();
        return loginId;
    }

    private void StartPushWatcher(LoginSession session, CancellationToken parentCt)
    {
        session.ParentCt = parentCt;
        session.PushCts = CancellationTokenSource.CreateLinkedTokenSource(parentCt);
        session.PushTask = Task.Run(() => RunPushWatcherAsync(session, session.PushCts.Token));
    }

    private void StopPushWatcher(LoginSession session)
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
                        NotifySupervisorsConnectivity(session, restored: true);
                        wasDisconnected = false;
                    }

                    backoffMs = 30000; // Reset after receiving a successful event

                    AccountSupervisor? supervisor;
                    lock (_lock)
                        supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
                    supervisor?.PushState(state);
                }

                // Stream ended normally — reconnect after a short delay
                // to avoid tight-looping if the server immediately closes
                Log.Info($"[Push:{username}] SSE stream ended, reconnecting in 5s...");
                try { await Task.Delay(5000, ct); }
                catch (OperationCanceledException) { break; }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error($"[Push:{username}] SSE error: {ex.Message}");
                wasDisconnected = true;

                // SSE is just a notification channel — don't mark accounts
                // offline. Instead, trigger a poll so accounts stay current.
                TriggerPollForAllSupervisors(session);

                try { await Task.Delay(backoffMs, ct); }
                catch (OperationCanceledException) { break; }

                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
            }
        }

        Log.Info($"[Push:{username}] Push watcher stopped");
    }

    private void NotifySupervisorsConnectivity(LoginSession session, bool restored)
    {
        List<AccountSupervisor> supervisors;
        lock (_lock)
            supervisors = _supervisors.Where(s => session.AccountIds.Contains(s.AccountId)).ToList();

        foreach (var s in supervisors)
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

    private void TriggerPollForAllSupervisors(LoginSession session)
    {
        List<AccountSupervisor> supervisors;
        lock (_lock)
            supervisors = _supervisors.Where(s => session.AccountIds.Contains(s.AccountId)).ToList();

        foreach (var s in supervisors)
            s.PushState(""); // Empty state triggers a /changes poll
    }

    public void PauseAccount(string accountId)
    {
        AccountSupervisor? supervisor;
        lock (_lock)
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
        if (supervisor != null)
        {
            supervisor.Pause(SyncPauseReason.UserRequested);
            RaiseAggregateStatus();
        }
    }

    public void ResumeAccount(string accountId)
    {
        AccountSupervisor? supervisor;
        lock (_lock)
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
        if (supervisor != null)
        {
            supervisor.Resume(SyncPauseReason.UserRequested);
            // Trigger a catch-up poll on resume
            supervisor.PushState("");
            RaiseAggregateStatus();
        }
    }

    public void SyncNow(string accountId)
    {
        AccountSupervisor? supervisor;
        lock (_lock)
            supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
        supervisor?.SyncNow();
    }

    private void CheckDiskSpace()
    {
        try
        {
            List<AccountSupervisor> supervisors;
            lock (_lock)
                supervisors = _supervisors.ToList();

            foreach (var supervisor in supervisors)
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

    /// <summary>
    /// Apply initial pause state for a newly started supervisor (e.g. metered network).
    /// </summary>
    private void ApplyInitialPauseState(AccountSupervisor supervisor)
    {
        if (_networkMonitor is { IsMetered: true })
            supervisor.Pause(SyncPauseReason.MeteredConnection);
    }

    private void OnNetworkStateChanged(bool isConnected, bool isMetered)
    {
        List<LoginSession> sessions;
        List<AccountSupervisor> supervisors;
        lock (_lock)
        {
            sessions = _sessions.ToList();
            supervisors = _supervisors.ToList();
        }

        if (!isConnected)
        {
            // Network went offline: notify all supervisors, stop push watchers
            Log.Info("[NetworkMonitor] Network offline — marking accounts disconnected");
            foreach (var session in sessions)
                StopPushWatcher(session);
            foreach (var session in sessions)
                NotifySupervisorsConnectivity(session, restored: false);
        }
        else
        {
            // Network came online: restart push watchers, notify supervisors
            Log.Info("[NetworkMonitor] Network online — reconnecting");
            foreach (var session in sessions)
            {
                NotifySupervisorsConnectivity(session, restored: true);
                StartPushWatcher(session, session.ParentCt);
            }

            // Retry failed logins
            Log.FireAndForget(RetryFailedLoginsAsync(), "RetryFailedLoginsOnRestore");
        }

        // Handle metered state changes — pause outbox + background sync, but allow hydration
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
                supervisor.Resume(SyncPauseReason.MeteredConnection);
            // Trigger catch-up poll on transition from metered to unmetered
            foreach (var supervisor in supervisors)
                supervisor.PushState("");
        }

        RaiseAggregateStatus();
    }

    private async Task RetryFailedLoginsAsync()
    {
        if (_networkMonitor is not { IsConnected: true })
            return;

        // Guard against concurrent retries
        if (Interlocked.CompareExchange(ref _retryInProgress, 1, 0) != 0)
            return;

        try
        {
            List<FailedLoginInfo> toRetry;
            lock (_lock)
            {
                if (_failedLogins.Count == 0)
                    return;
                toRetry = _failedLogins.ToList();
                _failedLogins.Clear();
                foreach (var f in toRetry)
                    _connectingLoginIds.Add(f.LoginId);
            }

            Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");

            foreach (var f in toRetry)
            {
                try
                {
                    Log.Info($"[RetryLogin] Retrying failed login {f.LoginId}...");
                    await ConnectAndStartAsync(f.SessionUrl, f.Token, f.LoginId,
                        enabledAccountIds: f.EnabledAccountIds,
                        persist: false, iconPath: _iconPath, clean: false, ct: _parentCt,
                        refreshToken: f.RefreshToken, tokenEndpoint: f.TokenEndpoint,
                        clientId: f.ClientId, expiresAtUnixSeconds: f.ExpiresAtUnixSeconds);
                }
                catch (Exception ex)
                {
                    Log.Error($"[RetryLogin] Retry failed for {f.LoginId}: {ex.Message}");
                    lock (_lock)
                        _failedLogins.Add(f with { Error = ex.Message });
                }
                finally
                {
                    lock (_lock)
                        _connectingLoginIds.Remove(f.LoginId);
                    Log.SafeInvoke(() => AccountsChanged?.Invoke(), "LoginManager.AccountsChanged");
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _retryInProgress, 0);
        }
    }

    private void RaiseAggregateStatus()
    {
        var status = GetAggregateStatus();
        Log.SafeInvoke(() => AggregateStatusChanged?.Invoke(status), "LoginManager.AggregateStatusChanged");
    }

    public SyncStatus GetAggregateStatus()
    {
        lock (_lock)
        {
            if (_supervisors.Count == 0)
                return SyncStatus.Idle;

            // Worst-of-all: Error > Disconnected > Syncing > Idle
            if (_supervisors.Any(s => s.Status == SyncStatus.Error))
                return SyncStatus.Error;
            if (_supervisors.Any(s => s.Status == SyncStatus.Disconnected))
                return SyncStatus.Disconnected;
            if (_supervisors.Any(s => s.Status == SyncStatus.Syncing))
                return SyncStatus.Syncing;
            return SyncStatus.Idle;
        }
    }

    public int GetAggregatePendingCount()
    {
        lock (_lock)
            return _supervisors.Sum(s => s.PendingCount);
    }

    internal static string SanitizeFolderName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            if (Array.IndexOf(invalid, chars[i]) >= 0)
                chars[i] = '_';
        }
        return new string(chars).TrimEnd(' ', '.');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _retryTimer?.Dispose();
        _retryTimer = null;

        _diskCheckTimer?.Dispose();
        _diskCheckTimer = null;

        _networkMonitor?.Dispose();
        _networkMonitor = null;

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

/// <summary>
/// Represents a single JMAP login session.
/// </summary>
sealed class LoginSession
{
    public string LoginId { get; }
    public JmapClient Client { get; }
    public string SessionUrl { get; }
    public string Token { get; set; }
    public List<string> AccountIds { get; }
    public CancellationTokenSource? PushCts { get; set; }
    public Task? PushTask { get; set; }
    /// <summary>Parent CT used to create linked push CTS, stored so push can be restarted.</summary>
    public CancellationToken ParentCt { get; set; }

    // OAuth fields (null for app-password logins)
    public string? RefreshToken { get; set; }
    public string? TokenEndpoint { get; set; }
    public string? ClientId { get; set; }
    public long? ExpiresAtUnixSeconds { get; set; }
    public bool IsOAuth => RefreshToken != null;

    public LoginSession(string loginId, JmapClient client, string sessionUrl, string token, List<string> accountIds,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null)
    {
        LoginId = loginId;
        Client = client;
        SessionUrl = sessionUrl;
        Token = token;
        AccountIds = accountIds;
        RefreshToken = refreshToken;
        TokenEndpoint = tokenEndpoint;
        ClientId = clientId;
        ExpiresAtUnixSeconds = expiresAtUnixSeconds;
    }
}
