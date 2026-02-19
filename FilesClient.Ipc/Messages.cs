namespace FilesClient.Ipc;

// ---- Shared data types ----

public enum AccountStatus { Idle, Syncing, Error, Disconnected }

public record AccountInfo(
    string AccountId,
    string LoginId,
    string DisplayName,
    string SyncRootPath,
    string Username,
    AccountStatus Status,
    string? StatusDetail,
    int PendingCount);

public record OutboxEntry(
    Guid Id,
    string? LocalPath,
    string? NodeId,
    bool IsFolder,
    bool IsDirtyContent,
    bool IsDirtyLocation,
    bool IsDeleted,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    int AttemptCount,
    string? LastError,
    DateTime? NextRetryAfter,
    bool IsProcessing,
    int? UploadProgress);

public record DiscoveredAccount(string AccountId, string Name, bool IsPrimary);

// ---- UI -> Service commands ----

public abstract record IpcCommand;

public sealed record GetStatusCommand : IpcCommand;

public sealed record AddLoginCommand(
    string SessionUrl,
    string Token,
    HashSet<string>? EnabledAccountIds) : IpcCommand;

public sealed record DiscoverAccountsCommand(
    string SessionUrl,
    string Token) : IpcCommand;

public sealed record RemoveLoginCommand(string LoginId) : IpcCommand;

public sealed record CleanUpAccountCommand(string AccountId) : IpcCommand;

public sealed record ConfigureLoginCommand(
    string LoginId,
    HashSet<string> EnabledAccountIds) : IpcCommand;

public sealed record GetOutboxCommand(string AccountId) : IpcCommand;

public sealed record GetLoginAccountsCommand(string LoginId) : IpcCommand;

// ---- Service -> UI events ----

public abstract record IpcEvent;

public sealed record StatusSnapshotEvent(
    List<AccountInfo> Accounts,
    List<string> ConnectingLoginIds,
    AccountStatus AggregateStatus,
    int AggregatePendingCount) : IpcEvent;

public sealed record AccountStatusChangedEvent(
    string AccountId,
    AccountStatus Status,
    string? StatusDetail,
    int PendingCount) : IpcEvent;

public sealed record AccountsChangedEvent(
    List<AccountInfo> Accounts,
    List<string> ConnectingLoginIds) : IpcEvent;

public sealed record AddLoginResultEvent(
    bool Success,
    string? LoginId,
    string? Error) : IpcEvent;

public sealed record DiscoverAccountsResultEvent(
    bool Success,
    List<DiscoveredAccount>? Accounts,
    string? Error) : IpcEvent;

public sealed record CommandResultEvent(
    string CommandType,
    bool Success,
    string? Error) : IpcEvent;

public sealed record OutboxSnapshotEvent(
    string AccountId,
    List<OutboxEntry> Entries) : IpcEvent;

public sealed record LoginAccountsResultEvent(
    string LoginId,
    List<DiscoveredAccount>? Accounts,
    HashSet<string>? ActiveAccountIds,
    string? Error) : IpcEvent;
