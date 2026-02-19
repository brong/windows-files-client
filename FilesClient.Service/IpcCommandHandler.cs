using FilesClient.Ipc;
using FilesClient.Windows;

namespace FilesClient.Service;

/// <summary>
/// Dispatches IPC commands from the tray app to the LoginManager,
/// and converts results back to IPC events.
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

    public async Task<IpcEvent?> HandleAsync(IpcCommand command, CancellationToken ct)
    {
        switch (command)
        {
            case GetStatusCommand:
                return BuildStatusSnapshot();

            case AddLoginCommand cmd:
                return await HandleAddLoginAsync(cmd, ct);

            case DiscoverAccountsCommand cmd:
                return await HandleDiscoverAccountsAsync(cmd, ct);

            case RemoveLoginCommand cmd:
                return await HandleRemoveLoginAsync(cmd);

            case CleanUpAccountCommand cmd:
                return await HandleCleanUpAccountAsync(cmd);

            case ConfigureLoginCommand cmd:
                return await HandleConfigureLoginAsync(cmd, ct);

            case GetOutboxCommand cmd:
                return HandleGetOutbox(cmd);

            case GetLoginAccountsCommand cmd:
                return HandleGetLoginAccounts(cmd);

            case RefreshLoginAccountsCommand cmd:
                return await HandleRefreshLoginAccountsAsync(cmd, ct);

            case UpdateLoginCommand cmd:
                return await HandleUpdateLoginAsync(cmd, ct);

            case DetachAccountCommand cmd:
                return await HandleDetachAccountAsync(cmd);

            case RefreshAccountCommand cmd:
                return await HandleRefreshAccountAsync(cmd, ct);

            case CleanAccountCommand cmd:
                return await HandleCleanAccountAsync(cmd, ct);

            case EnableAccountCommand cmd:
                return await HandleEnableAccountAsync(cmd, ct);

            default:
                Console.Error.WriteLine($"[IPC] Unknown command type: {command.GetType().Name}");
                return null;
        }
    }

    public StatusSnapshotEvent BuildStatusSnapshot()
    {
        var supervisors = _loginManager.Supervisors;
        var accounts = supervisors.Select(s => new AccountInfo(
            s.AccountId,
            _loginManager.GetLoginIdForAccount(s.AccountId) ?? "",
            s.DisplayName,
            s.SyncRootPath,
            s.Username,
            MapStatus(s.Status),
            s.StatusDetail,
            s.PendingCount)).ToList();

        return new StatusSnapshotEvent(
            accounts,
            _loginManager.ConnectingLoginIds.ToList(),
            MapStatus(_loginManager.GetAggregateStatus()),
            _loginManager.GetAggregatePendingCount());
    }

    public AccountsChangedEvent BuildAccountsChanged()
    {
        var supervisors = _loginManager.Supervisors;
        var accounts = supervisors.Select(s => new AccountInfo(
            s.AccountId,
            _loginManager.GetLoginIdForAccount(s.AccountId) ?? "",
            s.DisplayName,
            s.SyncRootPath,
            s.Username,
            MapStatus(s.Status),
            s.StatusDetail,
            s.PendingCount)).ToList();

        return new AccountsChangedEvent(
            accounts,
            _loginManager.ConnectingLoginIds.ToList());
    }

    public AccountStatusChangedEvent BuildAccountStatus(AccountSupervisor supervisor)
    {
        return new AccountStatusChangedEvent(
            supervisor.AccountId,
            MapStatus(supervisor.Status),
            supervisor.StatusDetail,
            supervisor.PendingCount);
    }

    private async Task<IpcEvent> HandleAddLoginAsync(AddLoginCommand cmd, CancellationToken ct)
    {
        try
        {
            var loginId = await _loginManager.AddLoginAsync(
                cmd.SessionUrl, cmd.Token,
                persist: true, iconPath: IconPath,
                enabledAccountIds: cmd.EnabledAccountIds, ct: ct);
            return new AddLoginResultEvent(true, loginId, null);
        }
        catch (Exception ex)
        {
            return new AddLoginResultEvent(false, null, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleDiscoverAccountsAsync(DiscoverAccountsCommand cmd, CancellationToken ct)
    {
        try
        {
            var accounts = await _loginManager.DiscoverAccountsAsync(cmd.SessionUrl, cmd.Token, ct);
            var discovered = accounts.Select(a =>
                new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
            return new DiscoverAccountsResultEvent(true, discovered, null);
        }
        catch (Exception ex)
        {
            return new DiscoverAccountsResultEvent(false, null, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleRemoveLoginAsync(RemoveLoginCommand cmd)
    {
        try
        {
            await _loginManager.RemoveLoginAsync(cmd.LoginId);
            return new CommandResultEvent("removeLogin", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("removeLogin", false, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleCleanUpAccountAsync(CleanUpAccountCommand cmd)
    {
        try
        {
            await _loginManager.CleanUpAccountAsync(cmd.AccountId);
            return new CommandResultEvent("cleanUpAccount", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("cleanUpAccount", false, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleConfigureLoginAsync(ConfigureLoginCommand cmd, CancellationToken ct)
    {
        try
        {
            await _loginManager.ConfigureLoginAsync(cmd.LoginId, cmd.EnabledAccountIds, IconPath, ct: ct);
            return new CommandResultEvent("configureLogin", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("configureLogin", false, ex.Message);
        }
    }

    private IpcEvent HandleGetOutbox(GetOutboxCommand cmd)
    {
        var supervisors = _loginManager.Supervisors;
        var supervisor = supervisors.FirstOrDefault(s => s.AccountId == cmd.AccountId);
        if (supervisor?.Outbox == null)
            return new OutboxSnapshotEvent(cmd.AccountId, new List<OutboxEntry>());

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

        return new OutboxSnapshotEvent(cmd.AccountId, outboxEntries);
    }

    private IpcEvent HandleGetLoginAccounts(GetLoginAccountsCommand cmd)
    {
        var accounts = _loginManager.GetLoginAccounts(cmd.LoginId);
        if (accounts == null)
            return new LoginAccountsResultEvent(cmd.LoginId, null, null, "Login not found");

        var discovered = accounts.Select(a =>
            new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
        var active = _loginManager.GetActiveAccountIds(cmd.LoginId);
        return new LoginAccountsResultEvent(cmd.LoginId, discovered, active, null);
    }

    private async Task<IpcEvent> HandleRefreshLoginAccountsAsync(
        RefreshLoginAccountsCommand cmd, CancellationToken ct)
    {
        try
        {
            var accounts = await _loginManager.RefreshLoginAccountsAsync(cmd.LoginId, ct);
            var discovered = accounts.Select(a =>
                new DiscoveredAccount(a.AccountId, a.Name, a.IsPrimary)).ToList();
            var active = _loginManager.GetActiveAccountIds(cmd.LoginId);
            return new LoginAccountsResultEvent(cmd.LoginId, discovered, active, null);
        }
        catch (Exception ex)
        {
            return new LoginAccountsResultEvent(cmd.LoginId, null, null, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleUpdateLoginAsync(UpdateLoginCommand cmd, CancellationToken ct)
    {
        try
        {
            await _loginManager.UpdateLoginAsync(cmd.LoginId, cmd.SessionUrl, cmd.Token, IconPath, ct);
            return new CommandResultEvent("updateLogin", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("updateLogin", false, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleDetachAccountAsync(DetachAccountCommand cmd)
    {
        try
        {
            await _loginManager.DetachAccountAsync(cmd.AccountId);
            return new CommandResultEvent("detachAccount", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("detachAccount", false, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleRefreshAccountAsync(RefreshAccountCommand cmd, CancellationToken ct)
    {
        try
        {
            await _loginManager.RefreshAccountAsync(cmd.AccountId, IconPath, ct);
            return new CommandResultEvent("refreshAccount", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("refreshAccount", false, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleCleanAccountAsync(CleanAccountCommand cmd, CancellationToken ct)
    {
        try
        {
            await _loginManager.CleanAccountAsync(cmd.AccountId, IconPath, ct);
            return new CommandResultEvent("cleanAccount", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("cleanAccount", false, ex.Message);
        }
    }

    private async Task<IpcEvent> HandleEnableAccountAsync(EnableAccountCommand cmd, CancellationToken ct)
    {
        try
        {
            await _loginManager.EnableAccountAsync(cmd.LoginId, cmd.AccountId, IconPath, ct);
            return new CommandResultEvent("enableAccount", true, null);
        }
        catch (Exception ex)
        {
            return new CommandResultEvent("enableAccount", false, ex.Message);
        }
    }

    private static AccountStatus MapStatus(SyncStatus status) => status switch
    {
        SyncStatus.Idle => AccountStatus.Idle,
        SyncStatus.Syncing => AccountStatus.Syncing,
        SyncStatus.Error => AccountStatus.Error,
        SyncStatus.Disconnected => AccountStatus.Disconnected,
        _ => AccountStatus.Idle,
    };
}
