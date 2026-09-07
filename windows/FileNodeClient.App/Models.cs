using System.Reflection;

namespace FileNodeClient.App;

// Read-only snapshots handed from the sync side (LoginManager / AccountSupervisor)
// to the UI. Records so the UI can hold them across renders without locking.

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
    long? UploadedBytes,
    long? FileSize = null,
    bool IsRejected = false,
    string? RejectionReason = null);

public record DiscoveredAccount(string AccountId, string Name, bool IsPrimary);

public record FailedLogin(string LoginId, string Error);

public record ActiveDownloadEntry(string FileName, DateTime StartedAt,
    int? Progress = null, long? TotalSize = null, bool IsPending = false);

public record CompletedEntry(
    string FileName,
    string Action,
    bool Succeeded,
    string? Error,
    long? FileSize,
    DateTime CompletedAt);

public record SyncProgressInfo(
    string Phase,
    int? ProcessedCount,
    int? TotalCount);

public record LoginAccountsResult(string LoginId, List<DiscoveredAccount>? Accounts,
    HashSet<string>? ActiveAccountIds);

public record ActivitySnapshot(
    string AccountId,
    string DisplayName,
    List<OutboxEntry> ActiveEntries,
    List<OutboxEntry> ErrorEntries,
    List<OutboxEntry> PendingEntries,
    List<OutboxEntry> RejectedEntries,
    List<ActiveDownloadEntry>? ActiveDownloads,
    int TotalPendingCount,
    int TotalDownloadCount,
    List<CompletedEntry>? RecentlyCompleted = null,
    SyncProgressInfo? SyncProgress = null);

public record VersionInfo(string Version, string BuildDate)
{
    public static VersionInfo Current()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        // InformationalVersion format: "1.0.26.0+2026-03-10T12:00:00Z"
        if (infoVersion != null && infoVersion.Contains('+'))
        {
            var parts = infoVersion.Split('+', 2);
            return new VersionInfo(parts[0], parts[1]);
        }
        return new VersionInfo(
            asm.GetName().Version?.ToString() ?? "unknown",
            File.GetLastWriteTimeUtc(asm.Location).ToString("yyyy-MM-ddTHH:mm:ssZ"));
    }
}
