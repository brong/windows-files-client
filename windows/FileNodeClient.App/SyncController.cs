using FileNodeClient.Logging;
using FileNodeClient.Windows;

namespace FileNodeClient.App;

/// <summary>
/// The UI's view of the sync engine. Wraps <see cref="LoginManager"/>, turns
/// its per-supervisor events into UI-shaped snapshots, and throttles the
/// activity feed so a burst of outbox changes becomes one rebuild.
/// </summary>
sealed class SyncController : IDisposable
{
    private readonly LoginManager _loginManager;
    private readonly object _lock = new();
    private readonly HashSet<AccountSupervisor> _subscribed = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<string, int> _rejectedByAccount = new();

    // Activity throttle: coalesce per-account activity events into one
    // snapshot rebuild every 100ms (thousands of outbox additions arrive when
    // the user drags in a large folder).
    private readonly HashSet<string> _dirtyActivity = new();
    private System.Threading.Timer? _activityTimer;
    private const int ActivityIntervalMs = 100;

    // Activity snapshot stabilisation: only rebuild the entry list once per
    // second so the list doesn't flicker as many small files complete;
    // progress on already-visible entries flows through immediately.
    private readonly Dictionary<string, (ActivitySnapshot Snapshot, DateTime BuiltAt)> _lastActivity = new();

    // Completion tracking: detect items that left the outbox between snapshots.
    private readonly Dictionary<string, Dictionary<Guid, (string FileName, string Action, long? FileSize)>> _previousEntryInfo = new();
    private readonly Dictionary<string, HashSet<string>> _previousDownloadNames = new();
    private readonly Dictionary<string, List<CompletedEntry>> _recentlyCompleted = new();

    public event Action? AccountsChanged;
    public event Action? StatusChanged;
    public event Action<ActivitySnapshot>? ActivityChanged;

    public SyncController(LoginManager loginManager)
    {
        _loginManager = loginManager;
        _loginManager.AccountsChanged += OnAccountsChanged;
        _loginManager.AggregateStatusChanged += _ => Log.SafeInvoke(() => StatusChanged?.Invoke(), "SyncController.StatusChanged");
    }

    // ---- State snapshots ----

    public IReadOnlyList<AccountInfo> Accounts
    {
        get
        {
            var accounts = _loginManager.Supervisors.Select(BuildAccountInfo).ToList();
            // Cached (not-yet-connected) accounts appear immediately on startup
            // with Disconnected status until the real supervisor replaces them.
            var liveIds = accounts.Select(a => a.AccountId).ToHashSet();
            foreach (var cached in _loginManager.CachedAccounts)
            {
                if (liveIds.Contains(cached.AccountId)) continue;
                accounts.Add(new AccountInfo(
                    cached.AccountId, cached.LoginId, cached.DisplayName,
                    cached.SyncRootPath, cached.Username,
                    AccountStatus.Disconnected, "Connecting...", 0));
            }
            return accounts;
        }
    }

    public IReadOnlyList<string> ConnectingLoginIds => _loginManager.ConnectingLoginIds;
    public IReadOnlyList<string> ConnectedLoginIds => _loginManager.ConnectedLoginIds;
    public IReadOnlyList<FailedLogin> FailedLogins =>
        _loginManager.FailedLogins.Select(f => new FailedLogin(f.LoginId, f.Error)).ToList();
    public AccountStatus AggregateStatus => MapStatus(_loginManager.GetAggregateStatus());
    public int AggregatePendingCount => _loginManager.GetAggregatePendingCount();

    public int RejectedFileCount
    {
        get { lock (_lock) return _rejectedByAccount.Values.Sum(); }
    }

    public int GetRejectedCount(string accountId)
    {
        lock (_lock) return _rejectedByAccount.GetValueOrDefault(accountId);
    }

    public ActivitySnapshot GetActivity(string accountId)
    {
        var supervisor = FindSupervisor(accountId);
        return (supervisor != null ? BuildActivitySnapshot(supervisor) : null)
            ?? new ActivitySnapshot(accountId, "", new(), new(), new(), new(), null, 0, 0);
    }

    // ---- Commands ----

    public Task<string> AddLoginAsync(string sessionUrl, string token,
        HashSet<string>? enabledAccountIds = null,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null)
    {
        var cred = new LoginCredential(sessionUrl, token, refreshToken, tokenEndpoint, clientId, expiresAtUnixSeconds);
        return _loginManager.AddLoginAsync(cred, enabledAccountIds);
    }

    public async Task<List<DiscoveredAccount>> DiscoverAccountsAsync(string sessionUrl, string token)
    {
        var accounts = await _loginManager.DiscoverAccountsAsync(new LoginCredential(sessionUrl, token));
        return accounts.Select(a => new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
    }

    public Task RemoveLoginAsync(string loginId) => _loginManager.RemoveLoginAsync(loginId);
    public Task CleanUpAccountAsync(string accountId) => _loginManager.CleanUpAccountAsync(accountId);
    public Task DetachAccountAsync(string accountId) => _loginManager.DetachAccountAsync(accountId);
    public Task RefreshAccountAsync(string accountId) => _loginManager.RefreshAccountAsync(accountId);
    public Task CleanAccountAsync(string accountId) => _loginManager.CleanAccountAsync(accountId);
    public Task EnableAccountAsync(string loginId, string accountId) => _loginManager.EnableAccountAsync(loginId, accountId);

    public Task UpdateLoginAsync(string loginId, string sessionUrl, string token,
        string? refreshToken = null, string? tokenEndpoint = null,
        string? clientId = null, long? expiresAtUnixSeconds = null)
    {
        var cred = new LoginCredential(sessionUrl, token, refreshToken, tokenEndpoint, clientId, expiresAtUnixSeconds);
        return _loginManager.UpdateLoginAsync(loginId, cred);
    }

    public LoginAccountsResult? GetLoginAccounts(string loginId)
    {
        var accounts = _loginManager.GetLoginAccounts(loginId);
        if (accounts == null) return null;
        return new LoginAccountsResult(loginId,
            accounts.Select(a => new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList(),
            _loginManager.GetActiveAccountIds(loginId));
    }

    public async Task<LoginAccountsResult> RefreshLoginAccountsAsync(string loginId)
    {
        var accounts = await _loginManager.RefreshLoginAccountsAsync(loginId);
        return new LoginAccountsResult(loginId,
            accounts.Select(a => new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList(),
            _loginManager.GetActiveAccountIds(loginId));
    }

    public void PauseAccount(string accountId) => _loginManager.PauseAccount(accountId);
    public void ResumeAccount(string accountId) => _loginManager.ResumeAccount(accountId);
    public void SyncNow(string accountId) => _loginManager.SyncNow(accountId);
    public void RetryRejected(string accountId, Guid entryId) => FindSupervisor(accountId)?.Outbox?.RetryRejected(entryId);
    public void DismissRejected(string accountId, Guid entryId) => FindSupervisor(accountId)?.Outbox?.DismissRejected(entryId);

    // ---- Event plumbing ----

    private AccountSupervisor? FindSupervisor(string accountId) =>
        _loginManager.Supervisors.FirstOrDefault(s => s.AccountId == accountId);

    private void OnAccountsChanged()
    {
        // Subscribe to any supervisors created since the last change.
        foreach (var supervisor in _loginManager.Supervisors)
        {
            bool added;
            lock (_lock) added = _subscribed.Add(supervisor);
            if (!added) continue;
            supervisor.StatusChanged += OnSupervisorStatusChanged;
            supervisor.StatusDetailChanged += OnSupervisorStatusChanged;
            supervisor.PendingCountChanged += OnSupervisorStatusChanged;
            supervisor.QuotaChanged += OnSupervisorStatusChanged;
            supervisor.ActivityChanged += OnSupervisorActivityChanged;
            OnSupervisorActivityChanged(supervisor); // initial activity snapshot
        }
        Log.SafeInvoke(() => AccountsChanged?.Invoke(), "SyncController.AccountsChanged");
        Log.SafeInvoke(() => StatusChanged?.Invoke(), "SyncController.AccountsChanged.Status");
    }

    private void OnSupervisorStatusChanged(AccountSupervisor _) =>
        Log.SafeInvoke(() => StatusChanged?.Invoke(), "SyncController.StatusChanged");

    private void OnSupervisorActivityChanged(AccountSupervisor supervisor)
    {
        lock (_lock)
        {
            _dirtyActivity.Add(supervisor.AccountId);
            _activityTimer ??= new System.Threading.Timer(FlushActivity, null, ActivityIntervalMs, Timeout.Infinite);
        }
    }

    private void FlushActivity(object? state)
    {
        try
        {
            List<string> accountIds;
            lock (_lock)
            {
                accountIds = _dirtyActivity.ToList();
                _dirtyActivity.Clear();
                _activityTimer?.Dispose();
                _activityTimer = null;
            }

            foreach (var accountId in accountIds)
            {
                var supervisor = FindSupervisor(accountId);
                if (supervisor == null) continue;
                var snapshot = BuildActivitySnapshot(supervisor);
                if (snapshot == null) continue;
                lock (_lock)
                    _rejectedByAccount[accountId] = snapshot.RejectedEntries.Count;
                Log.SafeInvoke(() => ActivityChanged?.Invoke(snapshot), "SyncController.ActivityChanged");
                Log.SafeInvoke(() => StatusChanged?.Invoke(), "SyncController.ActivityChanged.Status");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"FlushActivity failed: {ex.Message}");
        }
    }

    // ---- Snapshot builders ----

    private AccountInfo BuildAccountInfo(AccountSupervisor s)
    {
        // Override Idle → Syncing when downloads are active so tray icon goes blue
        var status = MapStatus(s.Status);
        if (status == AccountStatus.Idle && s.ActiveDownloadCount > 0)
            status = AccountStatus.Syncing;

        return new(
            s.AccountId,
            _loginManager.GetLoginIdForAccount(s.AccountId) ?? "",
            s.DisplayName,
            s.SyncRootPath,
            s.Username,
            status,
            s.StatusDetail,
            s.PendingCount,
            s.QuotaUsed,
            s.QuotaLimit,
            s.PauseReason != SyncPauseReason.None ? s.PauseReason.ToString() : null);
    }

    private static AccountStatus MapStatus(SyncStatus status) => status switch
    {
        SyncStatus.Idle => AccountStatus.Idle,
        SyncStatus.Syncing => AccountStatus.Syncing,
        SyncStatus.Error => AccountStatus.Error,
        SyncStatus.Disconnected => AccountStatus.Disconnected,
        SyncStatus.Paused => AccountStatus.Paused,
        _ => AccountStatus.Idle,
    };

    private static long? FileSizeOf(PendingChange e)
    {
        if (e.LocalPath == null || e.IsFolder || e.IsDeleted) return null;
        try { return new FileInfo(e.LocalPath).Length; } catch { return null; }
    }

    private static OutboxEntry ToOutboxEntry(PendingChange e, bool isProcessing, long? uploadedBytes,
        long? fileSize, bool isRejected = false) =>
        new(e.Id, e.LocalPath, e.NodeId, e.IsFolder,
            e.IsDirtyContent, e.IsDirtyLocation, e.IsDeleted,
            e.CreatedAt, e.UpdatedAt, e.AttemptCount,
            e.LastError, e.NextRetryAfter,
            isProcessing, uploadedBytes, fileSize, isRejected,
            isRejected ? e.RejectionReason : null);

    private ActivitySnapshot? BuildActivitySnapshot(AccountSupervisor supervisor)
    {
        var outbox = supervisor.Outbox;
        if (outbox == null) return null;

        var now = DateTime.UtcNow;
        var accountId = supervisor.AccountId;

        // Reuse the previous entry list if it's < 1s old; just refresh progress.
        var quickUpdate = _lastActivity.TryGetValue(accountId, out var cached)
            && (now - cached.BuiltAt).TotalMilliseconds < 1000;

        var (entries, processingIds) = outbox.GetSnapshot();

        // Downloads always get fresh data (progress changes rapidly)
        var downloadSnapshot = supervisor.GetActiveDownloadSnapshot();
        List<ActiveDownloadEntry>? downloads = null;
        if (downloadSnapshot.Count > 0)
        {
            downloads = new List<ActiveDownloadEntry>();
            int pendingDlCount = 0;
            foreach (var d in downloadSnapshot)
            {
                if (d.IsPending && pendingDlCount >= 10) continue;
                if (d.IsPending) pendingDlCount++;
                downloads.Add(new ActiveDownloadEntry(d.FileName, d.StartedAt, d.Progress, d.TotalSize, d.IsPending));
            }
        }

        // --- Completion tracking: anything that left the outbox / download set completed ---
        var entryInfo = new Dictionary<Guid, (string FileName, string Action, long? FileSize)>();
        foreach (var e in entries)
        {
            var fileName = e.LocalPath != null ? Path.GetFileName(e.LocalPath) : e.NodeId ?? "(unknown)";
            var action = e.IsDeleted ? "Delete" : e.IsFolder && e.NodeId == null ? "Create folder"
                : e.IsDirtyContent ? "Upload" : e.IsDirtyLocation ? "Move" : "Sync";
            entryInfo[e.Id] = (fileName, action, FileSizeOf(e));
        }
        var currentDownloadNames = downloadSnapshot.Where(d => !d.IsPending).Select(d => d.FileName).ToHashSet();

        var completed = _recentlyCompleted.TryGetValue(accountId, out var list) ? list : _recentlyCompleted[accountId] = new();
        if (_previousEntryInfo.TryGetValue(accountId, out var prevInfo))
        {
            foreach (var (id, info) in prevInfo)
                if (!entryInfo.ContainsKey(id))
                    completed.Add(new CompletedEntry(info.FileName, info.Action, true, null, info.FileSize, now));
        }
        if (_previousDownloadNames.TryGetValue(accountId, out var prevDlNames))
        {
            foreach (var name in prevDlNames)
                if (!currentDownloadNames.Contains(name))
                    completed.Add(new CompletedEntry(name, "Download", true, null, null, now));
        }
        completed.RemoveAll(c => (now - c.CompletedAt).TotalSeconds > 60);
        _previousEntryInfo[accountId] = entryInfo;
        _previousDownloadNames[accountId] = currentDownloadNames;
        var recentlyCompleted = completed.Count > 0 ? completed.ToList() : null;

        SyncProgressInfo? syncProgress = supervisor.SyncProgress is { } sp
            ? new SyncProgressInfo(sp.Phase, sp.Processed, sp.Total) : null;

        if (quickUpdate)
        {
            var prev = cached.Snapshot;
            var updatedActive = prev.ActiveEntries.Select(ae =>
                ae with { UploadedBytes = outbox.GetProgress(ae.Id), IsProcessing = processingIds.Contains(ae.Id) }).ToList();
            return new ActivitySnapshot(accountId, supervisor.DisplayName,
                updatedActive, prev.ErrorEntries, prev.PendingEntries, prev.RejectedEntries,
                downloads, entries.Length, downloadSnapshot.Count, recentlyCompleted, syncProgress);
        }

        // Full rebuild: categorise all entries
        var active = new List<OutboxEntry>();
        var errors = new List<OutboxEntry>();
        var pending = new List<OutboxEntry>();
        foreach (var e in entries)
        {
            var oe = ToOutboxEntry(e, processingIds.Contains(e.Id), outbox.GetProgress(e.Id), entryInfo[e.Id].FileSize);
            if (oe.IsProcessing) active.Add(oe);
            else if (oe.LastError != null) errors.Add(oe);
            else pending.Add(oe);
        }

        // Always include a few pending entries in the active list so there's
        // always something visible during rapid small-file processing.
        if (active.Count == 0 && pending.Count > 0)
        {
            active.AddRange(pending.Take(3));
            pending = pending.Skip(3).ToList();
        }

        var rejected = outbox.GetRejectedSnapshot()
            .Select(e => ToOutboxEntry(e, false, null, FileSizeOf(e), isRejected: true))
            .ToList();

        var snapshot = new ActivitySnapshot(accountId, supervisor.DisplayName,
            active, errors, pending.Take(10).ToList(), rejected,
            downloads, entries.Length, downloadSnapshot.Count, recentlyCompleted, syncProgress);

        _lastActivity[accountId] = (snapshot, now);
        return snapshot;
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _activityTimer?.Dispose();
            _activityTimer = null;
        }
    }
}
