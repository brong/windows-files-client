using System.Text.Json;

namespace FileNodeClient.Ipc;

// ---- Shared data types ----

public enum AccountStatus { Idle, Syncing, Error, Disconnected, Paused }

public record AccountInfo(
    string AccountId,
    string LoginId,
    string DisplayName,
    string SyncRootPath,
    string Username,
    AccountStatus Status,
    string? StatusDetail,
    int PendingCount,
    long? QuotaUsed = null,
    long? QuotaLimit = null,
    string? PauseReason = null);

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

public record FailedLogin(string LoginId, string Error);

public record ActiveDownloadEntry(string FileName, DateTime StartedAt,
    int? Progress = null, long? TotalSize = null, bool IsPending = false);

// ---- Wire-format envelope types ----

public abstract record IpcMessage;
public record IpcRequest(string Id, string Method, JsonElement? Params) : IpcMessage;
public record IpcResponse(string Id, JsonElement Result) : IpcMessage;
public record IpcError(string Id, string Message) : IpcMessage;
public record IpcPush(string Method, JsonElement? Params) : IpcMessage;

// ---- Result records for methods returning structured data ----

public record AddLoginResult(string LoginId);

public record DiscoverAccountsResult(List<DiscoveredAccount> Accounts);

public record OutboxResult(string AccountId, List<OutboxEntry> Entries,
    List<ActiveDownloadEntry>? ActiveDownloads = null);

public record LoginAccountsResult(string LoginId, List<DiscoveredAccount>? Accounts,
    HashSet<string>? ActiveAccountIds);

public record StatusSnapshotResult(List<AccountInfo> Accounts, List<string> ConnectingLoginIds,
    List<FailedLogin> FailedLogins, AccountStatus AggregateStatus, int AggregatePendingCount,
    List<string>? ConnectedLoginIds = null);

// ---- Push payload records ----

public record AccountStatusPush(string AccountId, AccountStatus Status,
    string? StatusDetail, int PendingCount);

public record AccountsChangedPush(List<AccountInfo> Accounts, List<string> ConnectingLoginIds,
    List<FailedLogin> FailedLogins, List<string>? ConnectedLoginIds = null);

// ---- Exception type ----

public class IpcCallException : Exception
{
    public IpcCallException(string message) : base(message) { }
}
