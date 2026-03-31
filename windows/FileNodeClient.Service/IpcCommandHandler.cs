using System.Text.Json;
using FileNodeClient.Ipc;
using FileNodeClient.Logging;
using FileNodeClient.Windows;

namespace FileNodeClient.Service;

/// <summary>
/// Dispatches IPC requests from the tray app to the LoginManager,
/// and converts results back to IPC responses.
/// </summary>
sealed class IpcCommandHandler
{
    private readonly LoginManager _loginManager;
    public string? IconPath { get; set; }

    // Activity snapshot stabilization: only rebuild the entry list once per second
    // to avoid flickering when many small files complete in rapid succession.
    // Progress updates on already-visible entries flow through immediately.
    private readonly Dictionary<string, (ActivitySnapshot Snapshot, DateTime BuiltAt)> _lastActivity = new();

    // Completion tracking: detect items that leave the outbox between snapshots
    private readonly Dictionary<string, HashSet<Guid>> _previousEntryIds = new();
    private readonly Dictionary<string, HashSet<string>> _previousDownloadNames = new();
    private readonly Dictionary<string, List<CompletedEntry>> _recentlyCompleted = new();

    // Snapshot of outbox entries for completion detection (need filename/action)
    private readonly Dictionary<string, Dictionary<Guid, (string FileName, string Action, long? FileSize)>> _previousEntryInfo = new();

    public IpcCommandHandler(LoginManager loginManager, string? iconPath)
    {
        _loginManager = loginManager;
        IconPath = iconPath;
    }

    public async Task<string> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        Log.Debug($"[IPC] Request id={request.Id} method={request.Method}");
        try
        {
            var response = request.Method switch
            {
                "ping" => IpcSerializer.SerializeResponse(request.Id),
                "getVersion" => HandleGetVersion(request),
                "getStatus" => IpcSerializer.SerializeResponse(request.Id, BuildStatusSnapshot()),
                "addLogin" => await HandleAddLoginAsync(request, ct),
                "discoverAccounts" => await HandleDiscoverAccountsAsync(request, ct),
                "removeLogin" => await HandleRemoveLoginAsync(request),
                "cleanUpAccount" => await HandleCleanUpAccountAsync(request),
                "configureLogin" => await HandleConfigureLoginAsync(request, ct),
                "getOutbox" => HandleGetOutbox(request),
                "getActivity" => HandleGetActivity(request),
                "getLoginAccounts" => HandleGetLoginAccounts(request),
                "refreshLoginAccounts" => await HandleRefreshLoginAccountsAsync(request, ct),
                "updateLogin" => await HandleUpdateLoginAsync(request, ct),
                "detachAccount" => await HandleDetachAccountAsync(request),
                "refreshAccount" => await HandleRefreshAccountAsync(request, ct),
                "cleanAccount" => await HandleCleanAccountAsync(request, ct),
                "enableAccount" => await HandleEnableAccountAsync(request, ct),
                "pauseAccount" => HandlePauseAccount(request),
                "resumeAccount" => HandleResumeAccount(request),
                "syncNow" => HandleSyncNow(request),
                "retryRejected" => HandleRetryRejected(request),
                "dismissRejected" => HandleDismissRejected(request),
                _ => IpcSerializer.SerializeError(request.Id, $"Unknown method: {request.Method}"),
            };
            Log.Debug($"[IPC] Response id={request.Id} method={request.Method} ok");
            return response;
        }
        catch (Exception ex)
        {
            Log.Error($"[IPC] Error id={request.Id} method={request.Method}: {ex.Message}");
            return IpcSerializer.SerializeError(request.Id, ex.Message);
        }
    }

    private string HandleGetVersion(IpcRequest request)
    {
        return IpcSerializer.SerializeResponse(request.Id, VersionHelper.GetVersionInfo());
    }

    public StatusSnapshotResult BuildStatusSnapshot()
    {
        var supervisors = _loginManager.Supervisors;
        var accounts = supervisors.Select(BuildAccountInfo).ToList();
        AppendCachedAccounts(accounts);

        return new StatusSnapshotResult(
            accounts,
            _loginManager.ConnectingLoginIds.ToList(),
            _loginManager.FailedLogins.Select(f => new FailedLogin(f.LoginId, f.Error)).ToList(),
            MapStatus(_loginManager.GetAggregateStatus()),
            _loginManager.GetAggregatePendingCount(),
            _loginManager.ConnectedLoginIds.ToList());
    }

    public AccountsChangedPush BuildAccountsChanged()
    {
        var supervisors = _loginManager.Supervisors;
        var accounts = supervisors.Select(BuildAccountInfo).ToList();
        AppendCachedAccounts(accounts);

        return new AccountsChangedPush(
            accounts,
            _loginManager.ConnectingLoginIds.ToList(),
            _loginManager.FailedLogins.Select(f => new FailedLogin(f.LoginId, f.Error)).ToList(),
            _loginManager.ConnectedLoginIds.ToList());
    }

    /// <summary>
    /// Append cached (not-yet-connected) accounts so the UI shows them
    /// immediately on startup. They appear with Disconnected status until
    /// the real supervisor replaces them.
    /// </summary>
    private void AppendCachedAccounts(List<AccountInfo> accounts)
    {
        var liveIds = accounts.Select(a => a.AccountId).ToHashSet();
        foreach (var cached in _loginManager.CachedAccounts)
        {
            if (liveIds.Contains(cached.AccountId)) continue;
            accounts.Add(new AccountInfo(
                cached.AccountId, cached.LoginId, cached.DisplayName,
                cached.SyncRootPath, cached.Username,
                AccountStatus.Disconnected, "Connecting...", 0));
        }
    }

    public AccountStatusPush BuildAccountStatus(AccountSupervisor supervisor)
    {
        // Apply the same download-count override as BuildAccountInfo so the
        // tray icon goes blue while downloads are active.
        var status = MapStatus(supervisor.Status);
        if (status == AccountStatus.Idle && supervisor.ActiveDownloadCount > 0)
            status = AccountStatus.Syncing;

        return new AccountStatusPush(
            supervisor.AccountId,
            status,
            supervisor.StatusDetail,
            supervisor.PendingCount,
            supervisor.QuotaUsed,
            supervisor.QuotaLimit,
            supervisor.PauseReason != SyncPauseReason.None ? supervisor.PauseReason.ToString() : null);
    }

    private async Task<string> HandleAddLoginAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<AddLoginParams>(request.Params);
        var loginId = await _loginManager.AddLoginAsync(
            p.SessionUrl, p.Token,
            persist: true, iconPath: IconPath,
            enabledAccountIds: p.EnabledAccountIds, ct: ct,
            refreshToken: p.RefreshToken, tokenEndpoint: p.TokenEndpoint,
            clientId: p.ClientId, expiresAtUnixSeconds: p.ExpiresAtUnixSeconds);
        return IpcSerializer.SerializeResponse(request.Id, new AddLoginResult(loginId));
    }

    private async Task<string> HandleDiscoverAccountsAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<DiscoverParams>(request.Params);
        var accounts = await _loginManager.DiscoverAccountsAsync(p.SessionUrl, p.Token, ct);
        var discovered = accounts.Select(a =>
            new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
        return IpcSerializer.SerializeResponse(request.Id, new DiscoverAccountsResult(discovered));
    }

    private async Task<string> HandleRemoveLoginAsync(IpcRequest request)
    {
        var p = Deserialize<LoginIdParams>(request.Params);
        await _loginManager.RemoveLoginAsync(p.LoginId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private async Task<string> HandleCleanUpAccountAsync(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        await _loginManager.CleanUpAccountAsync(p.AccountId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private async Task<string> HandleConfigureLoginAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<ConfigureLoginParams>(request.Params);
        await _loginManager.ConfigureLoginAsync(p.LoginId, p.EnabledAccountIds, IconPath, ct: ct);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private string HandleGetOutbox(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        var supervisors = _loginManager.Supervisors;
        var supervisor = supervisors.FirstOrDefault(s => s.AccountId == p.AccountId);
        if (supervisor?.Outbox == null)
            return IpcSerializer.SerializeResponse(request.Id,
                new OutboxResult(p.AccountId, new List<OutboxEntry>()));

        var (entries, processingIds) = supervisor.Outbox.GetSnapshot();
        var outboxEntries = entries.Select(e => new OutboxEntry(
            e.Id,
            e.LocalPath,
            e.NodeId,
            e.IsFolder,
            e.IsDirtyContent,
            e.IsDirtyLocation,
            e.IsDeleted,
            e.CreatedAt,
            e.UpdatedAt,
            e.AttemptCount,
            e.LastError,
            e.NextRetryAfter,
            processingIds.Contains(e.Id),
            supervisor.Outbox.GetProgress(e.Id))).ToList();

        var downloadSnapshot = supervisor.GetActiveDownloadSnapshot();
        var activeDownloads = downloadSnapshot.Count > 0
            ? downloadSnapshot.Select(d => new ActiveDownloadEntry(
                d.FileName, d.StartedAt, d.Progress, d.TotalSize, d.IsPending)).ToList()
            : null;

        return IpcSerializer.SerializeResponse(request.Id,
            new OutboxResult(p.AccountId, outboxEntries, activeDownloads));
    }

    private string HandleGetActivity(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        var supervisor = _loginManager.Supervisors.FirstOrDefault(s => s.AccountId == p.AccountId);
        var snapshot = supervisor != null ? BuildActivitySnapshot(supervisor) : null;
        return IpcSerializer.SerializeResponse(request.Id,
            snapshot ?? new ActivitySnapshot(p.AccountId, "", new(), new(), new(), new(), null, 0, 0));
    }

    private string HandleGetLoginAccounts(IpcRequest request)
    {
        var p = Deserialize<LoginIdParams>(request.Params);
        var accounts = _loginManager.GetLoginAccounts(p.LoginId);
        if (accounts == null)
            return IpcSerializer.SerializeError(request.Id, "Login not found");

        var discovered = accounts.Select(a =>
            new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
        var active = _loginManager.GetActiveAccountIds(p.LoginId);
        return IpcSerializer.SerializeResponse(request.Id,
            new LoginAccountsResult(p.LoginId, discovered, active));
    }

    private async Task<string> HandleRefreshLoginAccountsAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<LoginIdParams>(request.Params);
        var accounts = await _loginManager.RefreshLoginAccountsAsync(p.LoginId, ct);
        var discovered = accounts.Select(a =>
            new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
        var active = _loginManager.GetActiveAccountIds(p.LoginId);
        return IpcSerializer.SerializeResponse(request.Id,
            new LoginAccountsResult(p.LoginId, discovered, active));
    }

    private async Task<string> HandleUpdateLoginAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<UpdateLoginParams>(request.Params);
        await _loginManager.UpdateLoginAsync(p.LoginId, p.SessionUrl, p.Token, IconPath, ct,
            refreshToken: p.RefreshToken, tokenEndpoint: p.TokenEndpoint,
            clientId: p.ClientId, expiresAtUnixSeconds: p.ExpiresAtUnixSeconds);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private async Task<string> HandleDetachAccountAsync(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        await _loginManager.DetachAccountAsync(p.AccountId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private async Task<string> HandleRefreshAccountAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        await _loginManager.RefreshAccountAsync(p.AccountId, IconPath, ct);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private async Task<string> HandleCleanAccountAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        await _loginManager.CleanAccountAsync(p.AccountId, IconPath, ct);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private async Task<string> HandleEnableAccountAsync(IpcRequest request, CancellationToken ct)
    {
        var p = Deserialize<EnableAccountParams>(request.Params);
        await _loginManager.EnableAccountAsync(p.LoginId, p.AccountId, IconPath, ct);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private string HandlePauseAccount(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        _loginManager.PauseAccount(p.AccountId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private string HandleResumeAccount(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        _loginManager.ResumeAccount(p.AccountId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private string HandleSyncNow(IpcRequest request)
    {
        var p = Deserialize<AccountIdParams>(request.Params);
        _loginManager.SyncNow(p.AccountId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private string HandleRetryRejected(IpcRequest request)
    {
        var p = Deserialize<OutboxEntryParams>(request.Params);
        var supervisor = _loginManager.Supervisors.FirstOrDefault(s => s.AccountId == p.AccountId);
        supervisor?.Outbox?.RetryRejected(p.EntryId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    private string HandleDismissRejected(IpcRequest request)
    {
        var p = Deserialize<OutboxEntryParams>(request.Params);
        var supervisor = _loginManager.Supervisors.FirstOrDefault(s => s.AccountId == p.AccountId);
        supervisor?.Outbox?.DismissRejected(p.EntryId);
        return IpcSerializer.SerializeResponse(request.Id);
    }

    public ActivitySnapshot? BuildActivitySnapshot(AccountSupervisor supervisor)
    {
        if (supervisor.Outbox == null) return null;

        var now = DateTime.UtcNow;
        var accountId = supervisor.AccountId;
        var fullRebuild = true;

        // Check if we can reuse the previous snapshot's entry list (< 1s old)
        // and just update progress on already-visible entries.
        if (_lastActivity.TryGetValue(accountId, out var cached)
            && (now - cached.BuiltAt).TotalMilliseconds < 1000)
        {
            fullRebuild = false;
        }

        var (entries, processingIds) = supervisor.Outbox.GetSnapshot();

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

        // --- Completion tracking: detect items that left the outbox ---
        var currentEntryIds = new HashSet<Guid>(entries.Select(e => e.Id));
        var currentDownloadNames = new HashSet<string>(downloadSnapshot.Where(d => !d.IsPending).Select(d => d.FileName));

        if (!_recentlyCompleted.ContainsKey(accountId))
            _recentlyCompleted[accountId] = new();

        // Detect completed outbox entries
        if (_previousEntryIds.TryGetValue(accountId, out var prevIds))
        {
            _previousEntryInfo.TryGetValue(accountId, out var prevInfo);
            foreach (var id in prevIds)
            {
                if (!currentEntryIds.Contains(id) && prevInfo != null && prevInfo.TryGetValue(id, out var info))
                {
                    _recentlyCompleted[accountId].Add(new CompletedEntry(
                        info.FileName, info.Action, true, null, info.FileSize, now));
                }
            }
        }

        // Detect completed downloads
        if (_previousDownloadNames.TryGetValue(accountId, out var prevDlNames))
        {
            foreach (var name in prevDlNames)
            {
                if (!currentDownloadNames.Contains(name))
                {
                    _recentlyCompleted[accountId].Add(new CompletedEntry(
                        name, "Download", true, null, null, now));
                }
            }
        }

        // Purge entries older than 60 seconds
        _recentlyCompleted[accountId].RemoveAll(c => (now - c.CompletedAt).TotalSeconds > 60);

        // Update previous state for next diff
        _previousEntryIds[accountId] = currentEntryIds;
        _previousDownloadNames[accountId] = currentDownloadNames;

        // Build entry info map for next completion detection
        var entryInfo = new Dictionary<Guid, (string FileName, string Action, long? FileSize)>();
        foreach (var e in entries)
        {
            var fileName = e.LocalPath != null ? Path.GetFileName(e.LocalPath) : e.NodeId ?? "(unknown)";
            var action = e.IsDeleted ? "Delete" : e.IsFolder && e.NodeId == null ? "Create folder"
                : e.IsDirtyContent ? "Upload" : e.IsDirtyLocation ? "Move" : "Sync";
            long? fileSize = null;
            if (e.LocalPath != null && !e.IsFolder && !e.IsDeleted)
            {
                try { fileSize = new FileInfo(e.LocalPath).Length; } catch { }
            }
            entryInfo[e.Id] = (fileName, action, fileSize);
        }
        _previousEntryInfo[accountId] = entryInfo;

        // Build SyncProgress from supervisor
        SyncProgressInfo? syncProgress = null;
        if (supervisor.SyncProgress is var sp && sp != null)
            syncProgress = new SyncProgressInfo(sp.Value.Phase, sp.Value.Processed, sp.Value.Total);

        var recentlyCompleted = _recentlyCompleted[accountId].Count > 0
            ? _recentlyCompleted[accountId].ToList()
            : null;

        if (!fullRebuild)
        {
            // Quick update: reuse previous entry lists but refresh progress
            // on already-visible active entries.
            var prev = cached.Snapshot;
            var updatedActive = prev.ActiveEntries.Select(ae =>
            {
                var progress = supervisor.Outbox.GetProgress(ae.Id);
                return ae with { UploadedBytes = progress, IsProcessing = processingIds.Contains(ae.Id) };
            }).ToList();

            return new ActivitySnapshot(
                accountId,
                supervisor.DisplayName,
                updatedActive,
                prev.ErrorEntries,
                prev.PendingEntries,
                prev.RejectedEntries,
                downloads,
                entries.Length,
                downloadSnapshot.Count,
                recentlyCompleted,
                syncProgress);
        }

        // Full rebuild: categorize all entries
        var active = new List<OutboxEntry>();
        var errors = new List<OutboxEntry>();
        var pending = new List<OutboxEntry>();
        foreach (var e in entries)
        {
            var info = entryInfo[e.Id];

            var oe = new OutboxEntry(
                e.Id, e.LocalPath, e.NodeId, e.IsFolder,
                e.IsDirtyContent, e.IsDirtyLocation, e.IsDeleted,
                e.CreatedAt, e.UpdatedAt, e.AttemptCount,
                e.LastError, e.NextRetryAfter,
                processingIds.Contains(e.Id),
                supervisor.Outbox.GetProgress(e.Id),
                info.FileSize);

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

        // Rejected entries (kept separately, not in normal processing queue)
        var rejectedEntries = supervisor.Outbox.GetRejectedSnapshot();
        var rejected = rejectedEntries.Select(e =>
        {
            long? fs = null;
            if (e.LocalPath != null && !e.IsFolder && !e.IsDeleted)
            {
                try { fs = new FileInfo(e.LocalPath).Length; } catch { }
            }
            return new OutboxEntry(
                e.Id, e.LocalPath, e.NodeId, e.IsFolder,
                e.IsDirtyContent, e.IsDirtyLocation, e.IsDeleted,
                e.CreatedAt, e.UpdatedAt, e.AttemptCount,
                e.LastError, e.NextRetryAfter,
                false, null, fs, true, e.RejectionReason);
        }).ToList();

        var snapshot = new ActivitySnapshot(
            accountId,
            supervisor.DisplayName,
            active,
            errors,
            pending.Take(10).ToList(),
            rejected,
            downloads,
            entries.Length,
            downloadSnapshot.Count,
            recentlyCompleted,
            syncProgress);

        _lastActivity[accountId] = (snapshot, now);
        return snapshot;
    }

    private static T Deserialize<T>(JsonElement? element) => IpcSerializer.Deserialize<T>(element);

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

    // Internal param records for deserialization
    private record AddLoginParams(string SessionUrl, string Token,
        HashSet<string>? EnabledAccountIds = null,
        string? RefreshToken = null, string? TokenEndpoint = null,
        string? ClientId = null, long? ExpiresAtUnixSeconds = null);

    private record DiscoverParams(string SessionUrl, string Token);
    private record LoginIdParams(string LoginId);
    private record AccountIdParams(string AccountId);
    private record OutboxEntryParams(string AccountId, Guid EntryId);
    private record ConfigureLoginParams(string LoginId, HashSet<string> EnabledAccountIds);
    private record EnableAccountParams(string LoginId, string AccountId);

    private record UpdateLoginParams(string LoginId, string SessionUrl, string Token,
        string? RefreshToken = null, string? TokenEndpoint = null,
        string? ClientId = null, long? ExpiresAtUnixSeconds = null);
}
