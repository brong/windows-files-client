using FilesClient.Jmap;
using FilesClient.Windows;

namespace FilesClient.Service;

/// <summary>
/// Orchestrates multiple JMAP logins, discovers FileNode-capable accounts
/// in each, and manages AccountSupervisor lifecycle.
/// </summary>
sealed class LoginManager : IDisposable
{
    private readonly bool _debug;
    private readonly CredentialStore _credentialStore = new();
    private readonly List<LoginSession> _sessions = new();
    private readonly List<AccountSupervisor> _supervisors = new();
    private readonly List<string> _connectingLoginIds = new();
    private readonly object _lock = new();
    private NetworkMonitor? _networkMonitor;
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
        _networkMonitor = new NetworkMonitor();
        _networkMonitor.NetworkStateChanged += OnNetworkStateChanged;
        Console.WriteLine($"[NetworkMonitor] Initial state: connected={_networkMonitor.IsConnected}, metered={_networkMonitor.IsMetered}");

        var storedLogins = _credentialStore.LoadAll();

        // Show all stored logins as "Connecting..." in the UI immediately
        lock (_lock)
        {
            foreach (var login in storedLogins)
                _connectingLoginIds.Add(login.LoginId);
        }
        AccountsChanged?.Invoke();

        foreach (var login in storedLogins)
        {
            try
            {
                Console.WriteLine($"Loading login: {login.LoginId}");
                await ConnectAndStartAsync(login.SessionUrl, login.Token, login.LoginId,
                    enabledAccountIds: login.EnabledAccountIds,
                    persist: false, iconPath: iconPath, clean: clean, ct: ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to load login {login.LoginId}: {ex.Message}");
            }
            finally
            {
                lock (_lock)
                    _connectingLoginIds.Remove(login.LoginId);
                AccountsChanged?.Invoke();
            }
        }

        // Audit for orphaned sync roots (accounts removed server-side between runs)
        AuditOrphanedSyncRoots();
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
        HashSet<string>? enabledAccountIds = null, CancellationToken ct = default)
    {
        var loginId = await ConnectAndStartAsync(sessionUrl, token, loginId: null,
            enabledAccountIds: enabledAccountIds,
            persist: persist, iconPath: iconPath, clean: clean, ct: ct);
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
                Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
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
        }

        _credentialStore.Remove(loginId);
        AccountsChanged?.Invoke();
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
            Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        CleanSupervisorSyncRoot(supervisor);
        supervisor.Dispose();

        lock (_lock)
            _supervisors.Remove(supervisor);

        // Update or remove the stored credential
        if (session != null)
        {
            var remaining = GetActiveAccountIds(session.LoginId);
            if (remaining.Count == 0)
            {
                StopPushWatcher(session);
                _credentialStore.Remove(session.LoginId);
                lock (_lock)
                    _sessions.Remove(session);
            }
            else
            {
                _credentialStore.Save(session.LoginId, session.Token, session.SessionUrl, remaining);
            }
        }

        AccountsChanged?.Invoke();
        RaiseAggregateStatus();
    }

    /// <summary>
    /// Check for sync roots registered on this machine that don't correspond
    /// to any active supervisor (e.g. account removed server-side between runs).
    /// Detach orphans so the Explorer nav pane entry is removed.
    /// </summary>
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

                Console.WriteLine($"Orphaned sync root detected: accountId={accountId}, path={path}");
                try
                {
                    SyncEngine.Detach(path, accountId);
                    Console.WriteLine($"Detached orphaned sync root: {accountId}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to detach orphaned sync root {accountId}: {ex.Message}");
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Orphaned sync root audit failed: {ex.Message}");
        }
    }

    private static void CleanSupervisorSyncRoot(AccountSupervisor supervisor)
    {
        try
        {
            Console.WriteLine($"Cleaning sync root for {supervisor.DisplayName}...");
            SyncEngine.Clean(supervisor.SyncRootPath, supervisor.AccountId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error cleaning sync root for {supervisor.DisplayName}: {ex.Message}");
        }
    }

    /// <summary>
    /// Update credentials for an existing login: reconnect with new token/URL,
    /// rediscover accounts, restart existing supervisors, detach removed accounts.
    /// </summary>
    public async Task UpdateLoginAsync(string loginId, string sessionUrl, string token,
        string? iconPath = null, CancellationToken ct = default)
    {
        LoginSession? oldSession;
        List<AccountSupervisor> oldSupervisors;

        lock (_lock)
        {
            oldSession = _sessions.FirstOrDefault(s => s.LoginId == loginId);
            if (oldSession == null)
                throw new InvalidOperationException($"Login {loginId} not found");
            oldSupervisors = _supervisors.Where(s => oldSession.AccountIds.Contains(s.AccountId)).ToList();
        }

        // Stop push watcher
        StopPushWatcher(oldSession);

        // Stop all supervisors (but don't clean sync roots)
        foreach (var supervisor in oldSupervisors)
        {
            try { await supervisor.StopAsync(); }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
            }
        }

        // Connect with new credentials
        var jmapClient = new JmapClient(token, _debug);
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
            _sessions.Remove(oldSession);
        }

        oldSession.Client.Dispose();

        // Detach accounts no longer on server
        foreach (var accountId in previouslyActive.Where(id => !newAccountIds.Contains(id)))
        {
            Console.WriteLine($"Account {accountId} removed from server, detaching...");
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
                Console.Error.WriteLine($"Failed to detach {accountId}: {ex.Message}");
            }
        }

        // Create new session
        var newSession = new LoginSession(loginId, jmapClient, sessionUrl, token,
            newAccounts.Select(a => a.AccountId).ToList());
        lock (_lock)
            _sessions.Add(newSession);

        // Restart supervisors for accounts still on server that were previously synced
        var enabledAccountIds = new HashSet<string>();
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
            supervisor.StatusChanged += _ => RaiseAggregateStatus();
            supervisor.PendingCountChanged += _ => RaiseAggregateStatus();

            lock (_lock)
                _supervisors.Add(supervisor);

            try
            {
                await supervisor.StartAsync(iconPath, clean: false, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to restart supervisor for {displayName}: {ex.Message}");
                lock (_lock)
                    _supervisors.Remove(supervisor);
                supervisor.Dispose();
            }
        }

        // Restart push watcher
        StartPushWatcher(newSession, ct);

        // Update credential store
        _credentialStore.Save(loginId, token, sessionUrl, enabledAccountIds);

        AccountsChanged?.Invoke();
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
            Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        // Detach: delete dehydrated files, empty dirs, unregister sync root
        try
        {
            Console.WriteLine($"Detaching sync root for {supervisor.DisplayName}...");
            SyncEngine.Detach(supervisor.SyncRootPath, supervisor.AccountId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Error detaching sync root for {supervisor.DisplayName}: {ex.Message}");
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
            {
                StopPushWatcher(session);
                _credentialStore.Remove(session.LoginId);
                lock (_lock)
                    _sessions.Remove(session);
            }
            else
            {
                _credentialStore.Save(session.LoginId, session.Token, session.SessionUrl, remaining);
            }
        }

        AccountsChanged?.Invoke();
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
            Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
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
            Console.Error.WriteLine($"Failed to restart supervisor for {displayName}: {ex.Message}");
            lock (_lock)
                _supervisors.Remove(newSupervisor);
            newSupervisor.Dispose();
            throw;
        }

        AccountsChanged?.Invoke();
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
            Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
        }

        // Clean: unregister + delete all files
        CleanSupervisorSyncRoot(supervisor);

        var displayName = supervisor.DisplayName;
        var syncRootPath = supervisor.SyncRootPath;
        var username = supervisor.Username;
        var accounts = session.Client.GetFileNodeAccounts();
        var isPrimary = accounts.FirstOrDefault(a => a.AccountId == accountId).IsPrimary;

        supervisor.Dispose();
        lock (_lock)
            _supervisors.Remove(supervisor);

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
            Console.Error.WriteLine($"Failed to restart supervisor for {displayName}: {ex.Message}");
            lock (_lock)
                _supervisors.Remove(newSupervisor);
            newSupervisor.Dispose();
            throw;
        }

        AccountsChanged?.Invoke();
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
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to start supervisor for {displayName}: {ex.Message}");
            lock (_lock)
                _supervisors.Remove(supervisor);
            supervisor.Dispose();
            throw;
        }

        // Update credential store with new enabled set
        var enabled = GetActiveAccountIds(loginId);
        _credentialStore.Save(loginId, session.Token, session.SessionUrl, enabled);

        AccountsChanged?.Invoke();
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
            catch (Exception ex) { Console.Error.WriteLine($"Error stopping {supervisor.DisplayName}: {ex.Message}"); }
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
        Console.WriteLine($"Refreshing accounts for login {loginId} from {sessionUrl}...");
        var accounts = await DiscoverAccountsAsync(sessionUrl, token, ct);
        Console.WriteLine($"Refreshed login {loginId}: {accounts.Count} account(s) found");
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
            catch (Exception ex) { Console.Error.WriteLine($"Error stopping {supervisor.DisplayName}: {ex.Message}"); }
            supervisor.Dispose();
        }

        lock (_lock)
        {
            foreach (var supervisor in toStop)
                _supervisors.Remove(supervisor);
        }

        // Start supervisors for newly enabled accounts
        var currentActive = GetActiveAccountIds(loginId);
        var accounts = session.Client.GetFileNodeAccounts();
        foreach (var (accountId, accountName, isPrimary) in accounts)
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
            supervisor.StatusChanged += _ => RaiseAggregateStatus();
            supervisor.PendingCountChanged += _ => RaiseAggregateStatus();

            lock (_lock)
                _supervisors.Add(supervisor);

            try
            {
                await supervisor.StartAsync(iconPath, clean, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to start supervisor for {displayName}: {ex.Message}");
                lock (_lock)
                    _supervisors.Remove(supervisor);
                supervisor.Dispose();
            }
        }

        // Update stored credential with new selection
        _credentialStore.Save(loginId, session.Token, session.SessionUrl, enabledAccountIds);

        AccountsChanged?.Invoke();
        RaiseAggregateStatus();
    }

    private async Task<string> ConnectAndStartAsync(string sessionUrl, string token, string? loginId,
        HashSet<string>? enabledAccountIds, bool persist, string? iconPath, bool clean, CancellationToken ct)
    {
        var jmapClient = new JmapClient(token, _debug);
        Console.WriteLine("Connecting to JMAP...");
        await jmapClient.ConnectAsync(sessionUrl, ct);
        Console.WriteLine($"Connected as {jmapClient.Session.Username}");

        loginId ??= CredentialStore.DeriveLoginId(jmapClient.Session.Username, sessionUrl);

        // Remove existing login with same ID (e.g. re-adding with a new token)
        bool existing;
        lock (_lock)
            existing = _sessions.Any(s => s.LoginId == loginId);
        if (existing)
        {
            Console.WriteLine($"Replacing existing login {loginId}");
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

        var session = new LoginSession(loginId, jmapClient, sessionUrl, token,
            accounts.Select(a => a.AccountId).ToList());

        lock (_lock)
            _sessions.Add(session);

        // Create a supervisor for each enabled account
        foreach (var (accountId, accountName, isPrimary) in accounts)
        {
            // Skip accounts not in the enabled set (null = all enabled)
            if (enabledAccountIds != null && !enabledAccountIds.Contains(accountId))
                continue;

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
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Failed to start supervisor for {displayName}: {ex.Message}");
                lock (_lock)
                    _supervisors.Remove(supervisor);
                supervisor.Dispose();
            }
        }

        // Start shared push watcher for this session
        StartPushWatcher(session, ct);

        if (persist)
            _credentialStore.Save(loginId, token, sessionUrl, enabledAccountIds);

        AccountsChanged?.Invoke();
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
        Console.WriteLine($"[Push:{username}] Starting shared push watcher");

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
                        Console.WriteLine($"[Push:{username}] Reconnected");
                        NotifySupervisorsConnectivity(session, restored: true);
                        wasDisconnected = false;
                    }

                    backoffMs = 30000; // Reset after receiving a successful event

                    AccountSupervisor? supervisor;
                    lock (_lock)
                        supervisor = _supervisors.FirstOrDefault(s => s.AccountId == accountId);
                    supervisor?.PushState(state);
                }

                // Stream ended normally — reconnect
                Console.WriteLine($"[Push:{username}] SSE stream ended, reconnecting...");
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[Push:{username}] SSE error: {ex.Message}");
                wasDisconnected = true;

                // SSE is just a notification channel — don't mark accounts
                // offline. Instead, trigger a poll so accounts stay current.
                TriggerPollForAllSupervisors(session);

                try { await Task.Delay(backoffMs, ct); }
                catch (OperationCanceledException) { break; }

                backoffMs = Math.Min(backoffMs * 2, maxBackoffMs);
            }
        }

        Console.WriteLine($"[Push:{username}] Push watcher stopped");
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
            Console.WriteLine("[NetworkMonitor] Network offline — marking accounts disconnected");
            foreach (var session in sessions)
                StopPushWatcher(session);
            foreach (var session in sessions)
                NotifySupervisorsConnectivity(session, restored: false);
        }
        else
        {
            // Network came online: restart push watchers, notify supervisors
            Console.WriteLine("[NetworkMonitor] Network online — reconnecting");
            foreach (var session in sessions)
            {
                NotifySupervisorsConnectivity(session, restored: true);
                StartPushWatcher(session, session.ParentCt);
            }
        }

        // Handle metered state changes
        foreach (var supervisor in supervisors)
            supervisor.BackgroundSyncEnabled = !isMetered;

        if (isMetered)
            Console.WriteLine("[NetworkMonitor] Metered connection — background sync suppressed");
        else if (isConnected)
        {
            Console.WriteLine("[NetworkMonitor] Unmetered connection — background sync enabled");
            // Trigger catch-up poll on transition from metered to unmetered
            foreach (var supervisor in supervisors)
                supervisor.PushState("");
        }

        RaiseAggregateStatus();
    }

    private void RaiseAggregateStatus()
    {
        var status = GetAggregateStatus();
        AggregateStatusChanged?.Invoke(status);
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
    public string Token { get; }
    public List<string> AccountIds { get; }
    public CancellationTokenSource? PushCts { get; set; }
    public Task? PushTask { get; set; }
    /// <summary>Parent CT used to create linked push CTS, stored so push can be restarted.</summary>
    public CancellationToken ParentCt { get; set; }

    public LoginSession(string loginId, JmapClient client, string sessionUrl, string token, List<string> accountIds)
    {
        LoginId = loginId;
        Client = client;
        SessionUrl = sessionUrl;
        Token = token;
        AccountIds = accountIds;
    }
}
