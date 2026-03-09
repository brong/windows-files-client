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

    public IpcCommandHandler(LoginManager loginManager, string? iconPath)
    {
        _loginManager = loginManager;
        IconPath = iconPath;
    }

    public async Task<string> HandleAsync(IpcRequest request, CancellationToken ct)
    {
        try
        {
            return request.Method switch
            {
                "ping" => IpcSerializer.SerializeResponse(request.Id),
                "getStatus" => IpcSerializer.SerializeResponse(request.Id, BuildStatusSnapshot()),
                "addLogin" => await HandleAddLoginAsync(request, ct),
                "discoverAccounts" => await HandleDiscoverAccountsAsync(request, ct),
                "removeLogin" => await HandleRemoveLoginAsync(request),
                "cleanUpAccount" => await HandleCleanUpAccountAsync(request),
                "configureLogin" => await HandleConfigureLoginAsync(request, ct),
                "getOutbox" => HandleGetOutbox(request),
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
                _ => IpcSerializer.SerializeError(request.Id, $"Unknown method: {request.Method}"),
            };
        }
        catch (Exception ex)
        {
            Log.Error($"[IPC] Error handling {request.Method}: {ex.Message}");
            return IpcSerializer.SerializeError(request.Id, ex.Message);
        }
    }

    public StatusSnapshotResult BuildStatusSnapshot()
    {
        var supervisors = _loginManager.Supervisors;
        var accounts = supervisors.Select(BuildAccountInfo).ToList();

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

        return new AccountsChangedPush(
            accounts,
            _loginManager.ConnectingLoginIds.ToList(),
            _loginManager.FailedLogins.Select(f => new FailedLogin(f.LoginId, f.Error)).ToList(),
            _loginManager.ConnectedLoginIds.ToList());
    }

    public AccountStatusPush BuildAccountStatus(AccountSupervisor supervisor)
    {
        return new AccountStatusPush(
            supervisor.AccountId,
            MapStatus(supervisor.Status),
            supervisor.StatusDetail,
            supervisor.PendingCount);
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
            ? downloadSnapshot.Select(d => new ActiveDownloadEntry(d.FileName, d.StartedAt)).ToList()
            : null;

        return IpcSerializer.SerializeResponse(request.Id,
            new OutboxResult(p.AccountId, outboxEntries, activeDownloads));
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
    private record ConfigureLoginParams(string LoginId, HashSet<string> EnabledAccountIds);
    private record EnableAccountParams(string LoginId, string AccountId);

    private record UpdateLoginParams(string LoginId, string SessionUrl, string Token,
        string? RefreshToken = null, string? TokenEndpoint = null,
        string? ClientId = null, long? ExpiresAtUnixSeconds = null);
}
