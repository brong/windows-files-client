using FilesClient.Jmap;
using FilesClient.Windows;

namespace FilesClient.App;

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
    private readonly object _lock = new();
    private bool _disposed;

    public IReadOnlyList<AccountSupervisor> Supervisors
    {
        get { lock (_lock) return _supervisors.ToList(); }
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
        var storedLogins = _credentialStore.LoadAll();
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
        }
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
    /// Remove a login: stop its supervisors, remove credential.
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

        foreach (var supervisor in toStop)
        {
            try
            {
                await supervisor.StopAsync();
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Error stopping supervisor {supervisor.DisplayName}: {ex.Message}");
            }
            supervisor.Dispose();
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
    /// Stop all supervisors gracefully.
    /// </summary>
    public async Task StopAllAsync()
    {
        List<AccountSupervisor> all;
        lock (_lock)
            all = _supervisors.ToList();

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

        // Check for duplicate login
        lock (_lock)
        {
            if (_sessions.Any(s => s.LoginId == loginId))
            {
                jmapClient.Dispose();
                throw new InvalidOperationException($"Login {loginId} is already active");
            }
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

        if (persist)
            _credentialStore.Save(loginId, token, sessionUrl, enabledAccountIds);

        AccountsChanged?.Invoke();
        RaiseAggregateStatus();
        return loginId;
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

    private static string SanitizeFolderName(string name)
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

        lock (_lock)
        {
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

    public LoginSession(string loginId, JmapClient client, string sessionUrl, string token, List<string> accountIds)
    {
        LoginId = loginId;
        Client = client;
        SessionUrl = sessionUrl;
        Token = token;
        AccountIds = accountIds;
    }
}
