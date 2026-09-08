using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Windows;

internal class SyncCallbacks
{
    private readonly IJmapClient _jmapClient;
    private readonly JmapQueue _queue;
    private readonly string _logPrefix;

    /// <summary>
    /// Node IDs that were recently hydrated by cfapi → LastWriteTimeUtc at
    /// hydration completion. SyncEngine checks this (with mtime comparison)
    /// to avoid re-uploading a file that was just downloaded while still
    /// detecting real edits.
    /// </summary>
    public ConcurrentDictionary<string, (DateTime Mtime, DateTime AddedAt)> RecentlyHydrated { get; } = new();

    /// <summary>
    /// In-flight hydration requests keyed by TransferKey, so CANCEL_FETCH_DATA
    /// can cancel the corresponding download.
    /// </summary>
    private readonly ConcurrentDictionary<long, (CancellationTokenSource Cts, string? NodeId, DateTime StartedAt)> _inFlightFetches = new();

    /// <summary>
    /// Whether the server supports HTTP Range requests. Set to false after
    /// a non-206 response or error, disabling range requests for the session.
    /// </summary>
    private volatile bool _rangeRequestsSupported = true;

    private const long BlobGetMaxSize = 16384;
    private string? _digestAlgorithm;
    private bool _digestAlgorithmResolved;

    /// <summary>
    /// The downloaded bytes could not be proven to match the server's blob: the
    /// digest differed, or the server supports digests but we could not fetch one.
    /// Never served to the caller (RELIABILITY I1, DESIGN pitfall #34).
    /// </summary>
    public sealed class DownloadIntegrityException(string message) : IOException(message);

    // Streaming hands cfapi each chunk as it arrives, so a mismatch found at the end
    // leaves unverified bytes in the placeholder. We fail the read, remember the node,
    // and dehydrate it when the last handle closes so the next open fetches afresh.
    private readonly ConcurrentDictionary<string, string> _rejectedHydrations = new();

    /// <summary>
    /// Called before a delete completes. Return true to allow, false to veto.
    /// Parameters: (nodeId, fullPath)
    /// </summary>
    public Func<string?, string, Task<bool>>? OnDeleteRequested;

    /// <summary>
    /// Called before a rename completes. Return true to allow, false to veto.
    /// Parameters: (nodeId, sourcePath, targetPath, targetInScope)
    /// </summary>
    public Func<string?, string, string, bool, Task<bool>>? OnRenameRequested;

    /// <summary>
    /// Called when the OS requests dehydration (Storage Sense, low disk).
    /// Return true to allow, false to veto. Parameters: (nodeId, fullPath)
    /// </summary>
    public Func<string?, string, Task<bool>>? OnDehydrateRequested;

    public event Action<long, string, long?, string?>? OnDownloadStarted;   // transferKey, fileName, totalSize, fullPath
    public event Action<long, int>? OnDownloadProgress;     // transferKey, percent
    public event Action<long>? OnDownloadCompleted;          // transferKey

    /// <summary>
    /// Returns true if a FETCH_DATA is currently in flight for the given node ID.
    /// Used to suppress FSW echo during streaming hydration.
    /// </summary>
    public bool IsHydrating(string nodeId)
    {
        foreach (var (_, (_, nid, _)) in _inFlightFetches)
        {
            if (nid == nodeId)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Tracks open file state: open count and LastWriteTimeUtc at first open.
    /// Used to detect whether the file was actually modified between open and close.
    /// </summary>
    private record OpenFileState(int Count, DateTime LastWriteTimeAtOpen);
    private readonly ConcurrentDictionary<string, OpenFileState> _openFiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fired when an external process closes a placeholder file.
    /// Parameters: (nodeId, fullPath)
    /// </summary>
    public event Action<string?, string>? OnFileCloseCompleted;

    /// <summary>
    /// When set and returns non-null, hydration requests are rejected with the
    /// returned message. Used to block downloads when disk is full or sync is paused.
    /// </summary>
    public Func<string?>? HydrationBlockedReason;

    public record DirectoryPopulatedInfo(string DirectoryPath);
    public event Action<DirectoryPopulatedInfo>? OnDirectoryPopulated;

    /// <summary>
    /// Remove stale entries from unbounded collections. Called periodically by SyncEngine.
    /// </summary>
    public void CleanupStaleEntries()
    {
        var cutoff = DateTime.UtcNow.AddMinutes(-10);
        var fetchCutoff = DateTime.UtcNow.AddMinutes(-30);

        // Clean RecentlyHydrated entries older than 10 minutes
        foreach (var kvp in RecentlyHydrated)
        {
            if (kvp.Value.AddedAt < cutoff)
                RecentlyHydrated.TryRemove(kvp.Key, out _);
        }

        // Clean stale in-flight fetches older than 30 minutes (cancelled + removed)
        foreach (var kvp in _inFlightFetches)
        {
            if (kvp.Value.StartedAt < fetchCutoff)
            {
                if (_inFlightFetches.TryRemove(kvp.Key, out var entry))
                {
                    try { entry.Cts.Cancel(); } catch { }
                    entry.Cts.Dispose();
                }
            }
        }

        // Clean stale open file tracking entries older than 24 hours
        var openCutoff = DateTime.UtcNow.AddHours(-24);
        foreach (var kvp in _openFiles)
        {
            if (kvp.Value.LastWriteTimeAtOpen < openCutoff)
                _openFiles.TryRemove(kvp.Key, out _);
        }
    }

    public SyncCallbacks(IJmapClient jmapClient, JmapQueue queue, string logPrefix)
    {
        _jmapClient = jmapClient;
        _queue = queue;
        _logPrefix = logPrefix;
    }

    public unsafe (CF_CALLBACK_REGISTRATION[] registrations, CF_CALLBACK[] delegates) CreateCallbackRegistrations()
    {
        // Must keep delegate references alive to prevent GC
        CF_CALLBACK fetchPlaceholdersDelegate = new(FetchPlaceholdersCallback);
        CF_CALLBACK fetchDataDelegate = new(FetchDataCallback);
        CF_CALLBACK notifyDeleteDelegate = new(NotifyDeleteCallback);
        CF_CALLBACK notifyRenameDelegate = new(NotifyRenameCallback);
        CF_CALLBACK cancelFetchDataDelegate = new(CancelFetchDataCallback);
        CF_CALLBACK notifyDehydrateDelegate = new(NotifyDehydrateCallback);
        CF_CALLBACK notifyDehydrateCompletionDelegate = new(NotifyDehydrateCompletionCallback);
        CF_CALLBACK notifyFileOpenCompletionDelegate = new(NotifyFileOpenCompletionCallback);
        CF_CALLBACK notifyFileCloseCompletionDelegate = new(NotifyFileCloseCompletionCallback);

        var delegates = new CF_CALLBACK[] { fetchPlaceholdersDelegate, fetchDataDelegate, notifyDeleteDelegate, notifyRenameDelegate, cancelFetchDataDelegate, notifyDehydrateDelegate, notifyDehydrateCompletionDelegate, notifyFileOpenCompletionDelegate, notifyFileCloseCompletionDelegate };

        var registrations = new CF_CALLBACK_REGISTRATION[]
        {
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_PLACEHOLDERS,
                Callback = fetchPlaceholdersDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = fetchDataDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_CANCEL_FETCH_DATA,
                Callback = cancelFetchDataDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DELETE,
                Callback = notifyDeleteDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_RENAME,
                Callback = notifyRenameDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE,
                Callback = notifyDehydrateDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_DEHYDRATE_COMPLETION,
                Callback = notifyDehydrateCompletionDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_OPEN_COMPLETION,
                Callback = notifyFileOpenCompletionDelegate,
            },
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NOTIFY_FILE_CLOSE_COMPLETION,
                Callback = notifyFileCloseCompletionDelegate,
            },
            // Sentinel entry to mark end of array
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
            },
        };

        return (registrations, delegates);
    }

    private unsafe void FetchPlaceholdersCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            // All placeholders are created upfront during PopulateAsync and kept
            // in sync by PollChangesAsync.  Just acknowledge so cfapi marks the
            // directory as populated — enabling pin hydration of its children.
            var nodeId = ExtractNodeId(callbackInfo);
            Log.Info($"{_logPrefix} FETCH_PLACEHOLDERS: node={nodeId}, path={callbackInfo->NormalizedPath}");

            var opInfo = new CF_OPERATION_INFO
            {
                StructSize = (uint)sizeof(CF_OPERATION_INFO),
                Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_PLACEHOLDERS,
                ConnectionKey = callbackInfo->ConnectionKey,
                TransferKey = callbackInfo->TransferKey,
                RequestKey = callbackInfo->RequestKey,
            };

            var opParams = new CF_OPERATION_PARAMETERS();
            opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
            opParams.Anonymous.TransferPlaceholders.CompletionStatus = new NTSTATUS(0); // STATUS_SUCCESS
            opParams.Anonymous.TransferPlaceholders.PlaceholderArray = null;
            opParams.Anonymous.TransferPlaceholders.PlaceholderCount = 0;
            opParams.Anonymous.TransferPlaceholders.PlaceholderTotalCount = 0;
            opParams.Anonymous.TransferPlaceholders.Flags = CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAGS.CF_OPERATION_TRANSFER_PLACEHOLDERS_FLAG_NONE;

            PInvoke.CfExecute(in opInfo, ref opParams).ThrowOnFailure();

            // Notify SyncEngine so it can hydrate pinned files in this directory
            var dirPath = ExtractFullPath(callbackInfo);
            Log.SafeInvoke(() => OnDirectoryPopulated?.Invoke(new DirectoryPopulatedInfo(dirPath)), "SyncCallbacks.OnDirectoryPopulated");
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} FETCH_PLACEHOLDERS error: {ex.Message}");
        }
    }

    private unsafe void FetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var transferKey = callbackInfo->TransferKey;
        var cts = new CancellationTokenSource();
        _inFlightFetches[transferKey] = (cts, null, DateTime.UtcNow);

        // Materialize cfapi-owned data synchronously — pointers invalidate after callback returns.
        var fullPath = ExtractFullPath(callbackInfo);   // with the drive; NormalizedPath alone is volume-relative
        var fileName = Path.GetFileName(fullPath);
        var cbInfo = *callbackInfo;
        var fetchParams = callbackParameters->Anonymous.FetchData;
        long requiredOffset = fetchParams.RequiredFileOffset;
        long requiredLength = fetchParams.RequiredLength;
        var nodeId = ExtractNodeId(callbackInfo);

        if (string.IsNullOrEmpty(nodeId))
        {
            Log.Error($"{_logPrefix} FETCH_DATA: No identity for {fileName}");
            _inFlightFetches.TryRemove(transferKey, out _);
            cts.Dispose();
            TransferError(cbInfo, new NTSTATUS(unchecked((int)0xC000000D)), requiredOffset, requiredLength, "File not recognized by sync engine"); // STATUS_INVALID_PARAMETER
            return;
        }

        // Check if hydration is blocked (disk full, user paused, etc.)
        var blockedReason = HydrationBlockedReason?.Invoke();
        if (blockedReason != null)
        {
            Log.Info($"{_logPrefix} FETCH_DATA: blocked for {fileName}: {blockedReason}");
            _inFlightFetches.TryRemove(transferKey, out _);
            cts.Dispose();
            TransferError(cbInfo, new NTSTATUS(unchecked((int)0xC00000CF)), requiredOffset, requiredLength, blockedReason); // STATUS_DEVICE_NOT_READY
            return;
        }

        // Store node ID so CancelFetchesWhere can match by node
        _inFlightFetches[transferKey] = (cts, nodeId, DateTime.UtcNow);

        // Log the requesting process so we can understand what triggers
        // FETCH_DATA (e.g. Explorer thumbnails, Search indexer, etc.)
        var processName = "(unknown)";
        if (callbackInfo->ProcessInfo != null)
        {
            try
            {
                var processId = callbackInfo->ProcessInfo->ProcessId;
                processName = $"PID={processId}";
                if (callbackInfo->ProcessInfo->ImagePath.Length > 0)
                    processName = Path.GetFileName(callbackInfo->ProcessInfo->ImagePath.ToString());
            }
            catch { }
        }
        Log.Info($"{_logPrefix} FETCH_DATA: node={nodeId}, offset={requiredOffset}, len={requiredLength}, caller={processName}");

        // All JMAP/HTTP work happens on a background thread so the cfapi callback
        // returns immediately, allowing cfapi to dispatch further callbacks in parallel.
        Log.FireAndForget(Task.Run(() => FetchDataAsync(
            cbInfo, nodeId, fullPath, fileName, transferKey,
            requiredOffset, requiredLength, cts)), $"{_logPrefix}.FetchDataCallback");
    }

    private async Task FetchDataAsync(
        CF_CALLBACK_INFO cbInfo, string nodeId, string fullPath, string fileName,
        long transferKey, long requiredOffset, long requiredLength,
        CancellationTokenSource cts)
    {
        bool cleanupHere = true;
        try
        {
            // Fetch node metadata first (shared by both paths)
            var node = await FetchNodeAsync(nodeId, cts.Token);
            var nodeSize = node.Size ?? 0;
            bool isFullHydration = requiredOffset == 0 && requiredLength >= nodeSize;

            // Now that we have size, notify SyncEngine of the download
            try { OnDownloadStarted?.Invoke(transferKey, fileName, nodeSize, fullPath); }
            catch (Exception ex) { Log.Error($"{_logPrefix} OnDownloadStarted handler error: {ex.Message}"); }

            Log.Info($"{_logPrefix} FETCH_DATA: node={nodeId}, size={nodeSize}, isFullHydration={isFullHydration}, path={((isFullHydration && nodeSize > BlobGetMaxSize) ? "streaming" : "buffered")}");

            if (isFullHydration && nodeSize > BlobGetMaxSize)
            {
                // Streaming path owns cleanup of _inFlightFetches/cts.
                cleanupHere = false;
                StreamBlobAsync(cbInfo, node, transferKey, nodeId, fullPath, cts);
                return;
            }

            // Small file, partial request, or Blob/get eligible — use buffered path
            var (data, dataStartOffset, totalSize) = await FetchBlobDataAsync(node, requiredOffset, requiredLength, cts.Token);

            Log.Info($"{_logPrefix} FETCH_DATA: buffered {data.Length} bytes for node={nodeId}, dataStartOffset={dataStartOffset}, totalSize={totalSize}");

            int sourceOffset = (int)(requiredOffset - dataStartOffset);
            TransferData(cbInfo, data, sourceOffset, requiredOffset, requiredLength, totalSize, cts.Token);

            Log.Info($"{_logPrefix} FETCH_DATA: transfer complete for node={nodeId}");

            // Record that we just hydrated this file so SyncEngine doesn't
            // re-upload it when FileSystemWatcher fires a Changed event.
            RecentlyHydrated[nodeId] = (GetLastWriteTimeSafe(fullPath), DateTime.UtcNow);
        }
        catch (OperationCanceledException)
        {
            Log.Info($"{_logPrefix} FETCH_DATA cancelled: transferKey={transferKey}");
        }
        catch (DownloadIntegrityException ex)
        {
            // Nothing was transferred: the placeholder stays dehydrated and the next open retries.
            Log.Error($"{_logPrefix} FETCH_DATA integrity failure for {fileName}: {ex.Message}");
            TransferError(cbInfo, new NTSTATUS(unchecked((int)0xC000003E)), requiredOffset, requiredLength, "File failed integrity check — will retry"); // STATUS_DATA_ERROR
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} FETCH_DATA error: {ex.Message}");
            TransferError(cbInfo, new NTSTATUS(unchecked((int)0xC0000001)), requiredOffset, requiredLength, $"Download failed: {ex.Message}"); // STATUS_UNSUCCESSFUL
        }
        finally
        {
            if (cleanupHere)
            {
                _inFlightFetches.TryRemove(transferKey, out _);
                cts.Dispose();
                try { OnDownloadCompleted?.Invoke(transferKey); }
                catch (Exception ex) { Log.Error($"{_logPrefix} OnDownloadCompleted handler error: {ex.Message}"); }
            }
        }
    }

    private unsafe void NotifyDeleteCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        // Materialize everything cfapi owns (strings, identity) synchronously;
        // PCWSTR + FileIdentity pointers are invalid after the callback returns.
        var nodeId = ExtractNodeId(callbackInfo);
        var fullPath = ExtractFullPath(callbackInfo);
        var cbInfo = *callbackInfo; // scalar fields only used async

        Log.Info($"{_logPrefix} NOTIFY_DELETE: node={nodeId}, path={fullPath}");

        var handler = OnDeleteRequested;
        if (handler == null)
        {
            try { AckDelete(cbInfo, true); }
            catch (Exception ex) { Log.Error($"{_logPrefix} AckDelete failed: {ex.Message}"); }
            return;
        }

        Log.FireAndForget(Task.Run(() => HandleNotifyDeleteAsync(cbInfo, handler, nodeId, fullPath)),
            $"{_logPrefix}.NotifyDeleteCallback");
    }

    private async Task HandleNotifyDeleteAsync(CF_CALLBACK_INFO cbInfo,
        Func<string?, string, Task<bool>> handler, string? nodeId, string fullPath)
    {
        bool allowed;
        try { allowed = await handler(nodeId, fullPath); }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} NOTIFY_DELETE error: {ex.Message}");
            allowed = false;
        }
        try { AckDelete(cbInfo, allowed); }
        catch (Exception ex) { Log.Error($"{_logPrefix} AckDelete failed: {ex.Message}"); }
    }

    private unsafe void NotifyRenameCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var nodeId = ExtractNodeId(callbackInfo);
        var sourcePath = ExtractFullPath(callbackInfo);

        var renameParams = callbackParameters->Anonymous.Rename;
        var targetPath = callbackInfo->VolumeDosName.ToString() + renameParams.TargetPath.ToString();
        if (targetPath.StartsWith(@"\\?\"))
            targetPath = targetPath.Substring(4);

        bool targetInScope = ((uint)renameParams.Flags & 0x4) != 0; // CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE
        var cbInfo = *callbackInfo;

        Log.Info($"{_logPrefix} NOTIFY_RENAME: node={nodeId}, {sourcePath} → {targetPath} (inScope={targetInScope})");

        var handler = OnRenameRequested;
        if (handler == null)
        {
            try { AckRename(cbInfo, true); }
            catch (Exception ex) { Log.Error($"{_logPrefix} AckRename failed: {ex.Message}"); }
            return;
        }

        Log.FireAndForget(Task.Run(() => HandleNotifyRenameAsync(cbInfo, handler, nodeId, sourcePath, targetPath, targetInScope)),
            $"{_logPrefix}.NotifyRenameCallback");
    }

    private async Task HandleNotifyRenameAsync(CF_CALLBACK_INFO cbInfo,
        Func<string?, string, string, bool, Task<bool>> handler,
        string? nodeId, string sourcePath, string targetPath, bool targetInScope)
    {
        bool allowed;
        try { allowed = await handler(nodeId, sourcePath, targetPath, targetInScope); }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} NOTIFY_RENAME error: {ex.Message}");
            allowed = false;
        }
        try { AckRename(cbInfo, allowed); }
        catch (Exception ex) { Log.Error($"{_logPrefix} AckRename failed: {ex.Message}"); }
    }

    private unsafe void CancelFetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            var transferKey = callbackInfo->TransferKey;
            var nodeId = ExtractNodeId(callbackInfo);
            Log.Info($"{_logPrefix} CANCEL_FETCH_DATA: node={nodeId}, transferKey={transferKey}");

            if (_inFlightFetches.TryGetValue(transferKey, out var entry))
                entry.Cts.Cancel();
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} CANCEL_FETCH_DATA error: {ex.Message}");
        }
    }

    /// <summary>
    /// Cancel in-flight downloads whose node ID matches the predicate.
    /// Called when a directory is unpinned to abort hydrations for files
    /// under that directory only.
    /// </summary>
    public void CancelFetchesWhere(Func<string, bool> shouldCancel)
    {
        foreach (var kvp in _inFlightFetches)
        {
            var (cts, nodeId, _) = kvp.Value;
            if (nodeId != null && shouldCancel(nodeId))
            {
                Log.Info($"{_logPrefix} Cancelling in-flight download for node {nodeId}");
                cts.Cancel();
            }
        }
    }

    private unsafe void NotifyDehydrateCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var nodeId = ExtractNodeId(callbackInfo);
        var fullPath = ExtractFullPath(callbackInfo);
        var cbInfo = *callbackInfo;

        Log.Info($"{_logPrefix} NOTIFY_DEHYDRATE: node={nodeId}, path={fullPath}");

        var handler = OnDehydrateRequested;
        if (handler == null)
        {
            try { AckDehydrate(cbInfo, true); }
            catch (Exception ex) { Log.Error($"{_logPrefix} AckDehydrate failed: {ex.Message}"); }
            return;
        }

        Log.FireAndForget(Task.Run(() => HandleNotifyDehydrateAsync(cbInfo, handler, nodeId, fullPath)),
            $"{_logPrefix}.NotifyDehydrateCallback");
    }

    private async Task HandleNotifyDehydrateAsync(CF_CALLBACK_INFO cbInfo,
        Func<string?, string, Task<bool>> handler, string? nodeId, string fullPath)
    {
        bool allowed;
        try { allowed = await handler(nodeId, fullPath); }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} NOTIFY_DEHYDRATE error: {ex.Message}");
            allowed = false;
        }
        try { AckDehydrate(cbInfo, allowed); }
        catch (Exception ex) { Log.Error($"{_logPrefix} AckDehydrate failed: {ex.Message}"); }
    }

    private unsafe void NotifyDehydrateCompletionCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);
            Log.Info($"{_logPrefix} NOTIFY_DEHYDRATE_COMPLETION: node={nodeId}, path={fullPath}");
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} NOTIFY_DEHYDRATE_COMPLETION error: {ex.Message}");
        }
    }

    private unsafe void NotifyFileOpenCompletionCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);

            var processName = "(unknown)";
            if (callbackInfo->ProcessInfo != null)
            {
                try
                {
                    var processId = callbackInfo->ProcessInfo->ProcessId;
                    processName = $"PID={processId}";
                    if (callbackInfo->ProcessInfo->ImagePath.Length > 0)
                        processName = Path.GetFileName(callbackInfo->ProcessInfo->ImagePath.ToString());
                }
                catch { }
            }

            var state = _openFiles.AddOrUpdate(fullPath,
                _ => new OpenFileState(1, GetLastWriteTimeSafe(fullPath)),
                (_, old) => old with { Count = old.Count + 1 });
            Log.Info($"{_logPrefix} NOTIFY_FILE_OPEN_COMPLETION: node={nodeId}, path={fullPath}, caller={processName}, openCount={state.Count}");
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} NOTIFY_FILE_OPEN_COMPLETION error: {ex.Message}");
        }
    }

    private unsafe void NotifyFileCloseCompletionCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);

            DateTime openTime = DateTime.MinValue;
            int newCount = 0;
            _openFiles.AddOrUpdate(fullPath,
                _ => new OpenFileState(0, DateTime.MinValue),
                (_, old) =>
                {
                    openTime = old.LastWriteTimeAtOpen;
                    newCount = Math.Max(0, old.Count - 1);
                    return old with { Count = newCount };
                });
            if (newCount == 0)
                _openFiles.TryRemove(fullPath, out _);

            // Only fire the event if the file was modified while open
            var currentWriteTime = GetLastWriteTimeSafe(fullPath);
            bool wasModified = currentWriteTime != openTime;

            Log.Info($"{_logPrefix} NOTIFY_FILE_CLOSE_COMPLETION: node={nodeId}, path={fullPath}, openCount={newCount}, modified={wasModified}");

            if (newCount == 0 && nodeId != null && _rejectedHydrations.ContainsKey(nodeId))
            {
                Log.FireAndForget(Task.Run(() => TryDiscardNow(nodeId, fullPath)), $"{_logPrefix}.DiscardUnverifiedOnClose");
                return;
            }

            if (wasModified)
                Log.SafeInvoke(() => OnFileCloseCompleted?.Invoke(nodeId, fullPath), "SyncCallbacks.OnFileCloseCompleted");
        }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} NOTIFY_FILE_CLOSE_COMPLETION error: {ex.Message}");
        }
    }

    private static DateTime GetLastWriteTimeSafe(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static unsafe void AckDehydrate(CF_CALLBACK_INFO callbackInfo, bool allow)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DEHYDRATE,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey,
        };

        var opParams = new CF_OPERATION_PARAMETERS();
        opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
        opParams.Anonymous.AckDehydrate.CompletionStatus = allow
            ? new NTSTATUS(0)  // STATUS_SUCCESS
            : new NTSTATUS(unchecked((int)0xC0000001));  // STATUS_UNSUCCESSFUL

        PInvoke.CfExecute(in opInfo, ref opParams).ThrowOnFailure();
    }

    private static unsafe string? ExtractNodeId(CF_CALLBACK_INFO* callbackInfo)
    {
        if (callbackInfo->FileIdentity != null && callbackInfo->FileIdentityLength > 0)
        {
            return Encoding.UTF8.GetString(
                new ReadOnlySpan<byte>(callbackInfo->FileIdentity, (int)callbackInfo->FileIdentityLength));
        }
        return null;
    }

    private static unsafe string ExtractFullPath(CF_CALLBACK_INFO* callbackInfo)
    {
        var path = callbackInfo->VolumeDosName.ToString() + callbackInfo->NormalizedPath.ToString();
        if (path.StartsWith(@"\\?\"))
            path = path.Substring(4);
        return path;
    }

    private static unsafe void AckDelete(CF_CALLBACK_INFO callbackInfo, bool allow)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey,
        };

        var opParams = new CF_OPERATION_PARAMETERS();
        opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
        opParams.Anonymous.AckDelete.CompletionStatus = allow
            ? new NTSTATUS(0)  // STATUS_SUCCESS
            : new NTSTATUS(unchecked((int)0xC0000001));  // STATUS_UNSUCCESSFUL

        PInvoke.CfExecute(in opInfo, ref opParams).ThrowOnFailure();
    }

    private static unsafe void AckRename(CF_CALLBACK_INFO callbackInfo, bool allow)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey,
        };

        var opParams = new CF_OPERATION_PARAMETERS();
        opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
        opParams.Anonymous.AckRename.CompletionStatus = allow
            ? new NTSTATUS(0)  // STATUS_SUCCESS
            : new NTSTATUS(unchecked((int)0xC0000001));  // STATUS_UNSUCCESSFUL

        PInvoke.CfExecute(in opInfo, ref opParams).ThrowOnFailure();
    }

    private string? GetDigestAlgorithm()
    {
        if (!_digestAlgorithmResolved)
        {
            _digestAlgorithm = _jmapClient.PreferredDigestAlgorithm;
            _digestAlgorithmResolved = true;
        }
        return _digestAlgorithm;
    }

    private static string ComputeDigest(string algorithm, byte[] data)
    {
        byte[] hash = algorithm switch
        {
            "sha" => SHA1.HashData(data),
            "sha-256" => SHA256.HashData(data),
            _ => throw new ArgumentException($"Unsupported digest algorithm: {algorithm}"),
        };
        return Convert.ToBase64String(hash);
    }

    private static string? GetDigestFromItem(BlobDataItem item, string algorithm)
    {
        return algorithm switch
        {
            "sha" => item.DigestSha,
            "sha-256" => item.DigestSha256,
            _ => null,
        };
    }

    private async Task<BlobDataItem?> GetBlobDigestAsync(string blobId, string algo,
        long? offset, long? length, CancellationToken ct)
    {
        // Use Background queue to avoid deadlock: download+digest run concurrently,
        // and if all 4 interactive slots hold downloads waiting for digests, deadlock.
        return await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.GetBlobAsync(blobId, [$"digest:{algo}"], offset, length, ct), ct);
    }

    /// <summary>Throws <see cref="DownloadIntegrityException"/> if the data does not match the server's digest.</summary>
    private static void VerifyDigest(string algorithm, byte[] data, BlobDataItem item, string context)
    {
        var expected = GetDigestFromItem(item, algorithm);
        if (expected == null)
            throw new DownloadIntegrityException($"Server returned no {algorithm} digest ({context})");
        var actual = ComputeDigest(algorithm, data);
        if (actual != expected)
        {
            Log.Error($"Digest mismatch ({context}): expected {expected}, got {actual} — rejecting download");
            throw new DownloadIntegrityException($"Digest mismatch ({context})");
        }
        Log.Debug($"Digest OK ({context}): {algorithm}={actual}");
    }

    /// <summary>
    /// Fetch a FileNode by ID. Throws if not found or has no blob.
    /// </summary>
    private async Task<FileNode> FetchNodeAsync(string nodeId, CancellationToken ct)
    {
        var nodes = await _queue.EnqueueAsync(QueuePriority.Interactive,
            () => _jmapClient.GetFileNodesAsync([nodeId], ct), ct);
        if (nodes.Length == 0 || nodes[0].BlobId == null)
            throw new FileNotFoundException($"Node {nodeId} not found or has no blob");
        return nodes[0];
    }

    /// <summary>
    /// Download blob data asynchronously (can run on any thread).
    /// Returns the data bytes, the file offset where data starts, and total file size.
    /// Uses Blob/get for small files, HTTP Range for partial requests, and full HTTP as fallback.
    /// All paths verify content digests when the server supports Blob/get.
    /// </summary>
    private async Task<(byte[] data, long dataStartOffset, long totalSize)> FetchBlobDataAsync(
        FileNode node, long requiredOffset, long requiredLength, CancellationToken ct)
    {
        var algo = GetDigestAlgorithm();
        var nodeSize = node.Size ?? 0;
        bool isPartialRequest = requiredOffset > 0 || requiredLength < nodeSize;

        // Path A — Small file via Blob/get (full file, ≤ 16KB)
        if (!isPartialRequest && nodeSize <= BlobGetMaxSize && algo != null)
        {
            try
            {
                var props = new[] { "data:asBase64", "size", $"digest:{algo}" };
                var item = await _queue.EnqueueAsync(QueuePriority.Interactive,
                    () => _jmapClient.GetBlobAsync(node.BlobId!, props, ct: ct), ct);
                if (item.DataAsBase64 != null && !item.IsTruncated)
                {
                    var data = Convert.FromBase64String(item.DataAsBase64);
                    VerifyDigest(algo, data, item, $"Blob/get {node.BlobId}");
                    Log.Info($"{_logPrefix} FETCH_DATA: small file via Blob/get, {data.Length} bytes");
                    return (data, 0, nodeSize);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                Log.Error($"{_logPrefix} Blob/get failed for small file, falling back to HTTP: {ex.Message}");
            }
        }

        // Path B — Range download via HTTP with concurrent digest verification
        if (_rangeRequestsSupported && isPartialRequest)
        {
            try
            {
                // Launch HTTP download and digest fetch concurrently
                var downloadTask = _queue.EnqueueAsync(QueuePriority.Interactive,
                    () => _jmapClient.DownloadBlobRangeAsync(
                        node.BlobId!, requiredOffset, requiredLength, node.Type, node.Name, ct), ct);
                Task<BlobDataItem?> digestTask = algo != null
                    ? GetBlobDigestAsync(node.BlobId!, algo, requiredOffset, requiredLength, ct)
                    : Task.FromResult<BlobDataItem?>(null);

                var (rangeStream, isPartial) = await downloadTask;
                using (rangeStream)
                {
                    using var ms = new MemoryStream();
                    await rangeStream.CopyToAsync(ms, ct);
                    var rangeBytes = ms.ToArray();

                    if (isPartial)
                    {
                        if (algo != null)
                        {
                            var digestItem = await AwaitDigestAsync(digestTask, $"range {node.BlobId}");
                            VerifyDigest(algo, rangeBytes, digestItem, $"range {node.BlobId} @{requiredOffset}+{requiredLength}");
                        }
                        return (rangeBytes, requiredOffset, nodeSize);
                    }

                    // Server returned 200 (full content) — disable range requests
                    _rangeRequestsSupported = false;
                    Log.Info($"{_logPrefix} Range requests not supported by server, falling back to full downloads");

                    if (algo != null)
                    {
                        // Re-fetch digest for full file since range digest doesn't apply
                        var fullDigestItem = await AwaitDigestAsync(
                            GetBlobDigestAsync(node.BlobId!, algo, null, null, ct), $"full fallback {node.BlobId}");
                        VerifyDigest(algo, rangeBytes, fullDigestItem, $"full fallback {node.BlobId}");
                    }
                    return (rangeBytes, 0, nodeSize);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (DownloadIntegrityException) { throw; }   // not a range-support problem
            catch (Exception ex)
            {
                _rangeRequestsSupported = false;
                Log.Error($"{_logPrefix} Range request failed, falling back to full download: {ex.Message}");
            }
        }

        // Path C — Full HTTP download with concurrent digest verification
        {
            var downloadTask = _queue.EnqueueAsync(QueuePriority.Interactive,
                () => _jmapClient.DownloadBlobAsync(node.BlobId!, node.Type, node.Name, ct), ct);
            Task<BlobDataItem?> digestTask = algo != null
                ? GetBlobDigestAsync(node.BlobId!, algo, null, null, ct)
                : Task.FromResult<BlobDataItem?>(null);

            using var stream = await downloadTask;
            using var fullMs = new MemoryStream();
            await stream.CopyToAsync(fullMs, ct);
            var fullBytes = fullMs.ToArray();

            if (algo != null)
            {
                var digestItem = await AwaitDigestAsync(digestTask, $"full download {node.BlobId}");
                VerifyDigest(algo, fullBytes, digestItem, $"full download {node.BlobId}");
            }

            return (fullBytes, 0, nodeSize);
        }
    }

    /// <summary>
    /// The server advertises digests, so failing to obtain one is a failed download,
    /// not a reason to serve unverified bytes. Cancellation passes through.
    /// </summary>
    private async Task<BlobDataItem> AwaitDigestAsync(Task<BlobDataItem?> digestTask, string context)
    {
        try
        {
            return await digestTask ?? throw new DownloadIntegrityException($"No digest returned ({context})");
        }
        catch (OperationCanceledException) { throw; }
        catch (DownloadIntegrityException) { throw; }
        catch (Exception ex)
        {
            Log.Error($"{_logPrefix} Digest fetch failed ({context}): {ex.Message}");
            throw new DownloadIntegrityException($"Digest unavailable ({context}): {ex.Message}");
        }
    }

    /// <summary>
    /// A hydration whose bytes were already handed to cfapi failed verification:
    /// throw the content away (dehydrate) so the next open fetches it again. The
    /// reader that triggered the fetch may still hold the file open, so retry in
    /// the background for a while; the close notification (for handles cfapi tells
    /// us about) is a second trigger via <see cref="_rejectedHydrations"/>.
    /// </summary>
    private void DiscardUnverifiedContent(string nodeId, string fullPath)
    {
        _rejectedHydrations[nodeId] = fullPath;
        Log.Warn($"{_logPrefix} Unverified content in {Path.GetFileName(fullPath)} will be discarded");
        Log.FireAndForget(Task.Run(async () =>
        {
            for (int attempt = 0; attempt < 120 && _rejectedHydrations.ContainsKey(nodeId); attempt++)
            {
                if (attempt > 0) await Task.Delay(1000);
                if (TryDiscardNow(nodeId, fullPath)) return;
            }
        }), $"{_logPrefix}.DiscardUnverified");
    }

    private bool TryDiscardNow(string nodeId, string fullPath)
    {
        try
        {
            SyncEngine.DehydratePlaceholder(fullPath);
            _rejectedHydrations.TryRemove(nodeId, out _);
            Log.Warn($"{_logPrefix} Discarded unverified content of {Path.GetFileName(fullPath)} (dehydrated); it will be fetched again on next open");
            return true;
        }
        catch (Exception ex)
        {
            Log.Debug($"{_logPrefix} Discard of unverified content deferred for {Path.GetFileName(fullPath)}: {ex.Message}");
            return false;   // typically still open by the reader — try again later
        }
    }

    /// <summary>
    /// Launch async streaming of a blob to a cfapi placeholder. Runs the download
    /// and chunk transfer on a thread pool thread so the FETCH_DATA callback can
    /// return immediately — cfapi then releases data to the reading application
    /// (e.g. video player) as each chunk arrives via CfExecute.
    /// Owns the CancellationTokenSource and handles all cleanup.
    /// </summary>
    private void StreamBlobAsync(
        CF_CALLBACK_INFO callbackInfo, FileNode node,
        long transferKey, string nodeId, string fullPath, CancellationTokenSource cts)
    {
        Task.Run(async () =>
        {
            var totalSize = node.Size ?? 0;
            long totalTransferred = 0;   // bytes handed to cfapi (the catch fails the rest)
            try
            {
                var blobId = node.BlobId!;
                const int chunkSize = 128 * 1024; // 128KB — small enough for responsive progressive playback
                var algo = GetDigestAlgorithm();
                var ct = cts.Token;

                Log.Info($"{_logPrefix} FETCH_DATA: streaming {totalSize} bytes in {chunkSize} byte chunks for blob {blobId}");

                // Digest in flight alongside the download; awaited before the last chunk is released.
                Task<BlobDataItem?> digestTask = algo != null
                    ? GetBlobDigestAsync(blobId, algo, null, null, ct)
                    : Task.FromResult<BlobDataItem?>(null);

                var stream = await _queue.EnqueueAsync(QueuePriority.Interactive,
                    () => _jmapClient.DownloadBlobAsync(blobId, node.Type, node.Name, ct), ct);

                using (stream)
                {
                    var buffer = new byte[chunkSize];
                    long totalRead = 0;          // bytes read from the server
                    int heldBack = 0;            // the final chunk, kept until the digest checks out
                    using var hash = algo != null
                        ? IncrementalHash.CreateHash(algo == "sha" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256)
                        : null;

                    while (totalRead < totalSize)
                    {
                        ct.ThrowIfCancellationRequested();

                        var toRead = (int)Math.Min(chunkSize, totalSize - totalRead);
                        var bytesRead = await ReadFullAsync(stream, buffer, toRead, ct);
                        if (bytesRead == 0)
                            break;

                        hash?.AppendData(buffer, 0, bytesRead);
                        totalRead += bytesRead;
                        var isFinal = totalRead >= totalSize;

                        if (!isFinal && bytesRead < toRead)
                        {
                            // The stream ended early. A partial mid-file chunk cannot be
                            // transferred (cfapi wants 4 KB alignment except at EOF) and the
                            // size check below fails the download.
                            Log.Warn($"{_logPrefix} FETCH_DATA: short read from server for {blobId}: {totalRead}/{totalSize} bytes");
                            break;
                        }

                        if (isFinal && hash != null)
                        {
                            // Last chunk: the reader cannot complete until we release it, and we
                            // only release it once the whole stream has been verified.
                            heldBack = bytesRead;
                            break;
                        }

                        TransferChunk(callbackInfo, buffer, 0, totalTransferred, bytesRead);
                        totalTransferred += bytesRead;

                        // Log progress every ~1MB to avoid spam
                        if (totalTransferred == bytesRead || totalTransferred % (1024 * 1024) < chunkSize)
                            Log.Info($"{_logPrefix} FETCH_DATA: stream progress node={nodeId}, {totalTransferred}/{totalSize} bytes ({totalTransferred * 100 / totalSize}%)");

                        if (totalSize > 0)
                        {
                            var percent = (int)(totalTransferred * 100 / totalSize);
                            try { OnDownloadProgress?.Invoke(transferKey, percent); }
                            catch { /* progress is best-effort */ }

                            try
                            {
                                PInvoke.CfReportProviderProgress(
                                    callbackInfo.ConnectionKey,
                                    callbackInfo.TransferKey,
                                    totalSize,
                                    totalTransferred);
                            }
                            catch { /* progress is best-effort */ }
                        }
                    }

                    Log.Info($"{_logPrefix} FETCH_DATA: streamed {totalRead} bytes total for blob {blobId}");

                    if (totalRead != totalSize)
                        throw new DownloadIntegrityException($"Short download: {totalRead}/{totalSize} bytes (stream {blobId})");

                    // Verify the whole stream against the server's digest, then release the
                    // held-back final chunk.
                    if (hash != null && algo != null)
                    {
                        var actual = Convert.ToBase64String(hash.GetHashAndReset());
                        var digestItem = await AwaitDigestAsync(digestTask, $"stream {blobId}");
                        var expected = GetDigestFromItem(digestItem, algo)
                            ?? throw new DownloadIntegrityException($"Server returned no {algo} digest (stream {blobId})");
                        if (actual != expected)
                        {
                            Log.Error($"{_logPrefix} Digest mismatch (stream {blobId}): expected {expected}, got {actual} — rejecting download");
                            throw new DownloadIntegrityException($"Digest mismatch (stream {blobId})");
                        }
                        Log.Info($"{_logPrefix} Digest OK (stream {blobId}): {algo}={actual}");

                        if (heldBack > 0)
                        {
                            TransferChunk(callbackInfo, buffer, 0, totalTransferred, heldBack);
                            totalTransferred += heldBack;
                            try { PInvoke.CfReportProviderProgress(callbackInfo.ConnectionKey, callbackInfo.TransferKey, totalSize, totalTransferred); }
                            catch { /* progress is best-effort */ }
                        }
                    }
                }

                if (nodeId != null)
                {
                    RecentlyHydrated[nodeId] = (GetLastWriteTimeSafe(fullPath), DateTime.UtcNow);
                }
            }
            catch (OperationCanceledException)
            {
                Log.Info($"{_logPrefix} FETCH_DATA cancelled: transferKey={transferKey}");
            }
            catch (DownloadIntegrityException ex)
            {
                Log.Error($"{_logPrefix} FETCH_DATA integrity failure (stream) for {Path.GetFileName(fullPath)}: {ex.Message}");
                TransferError(callbackInfo, new NTSTATUS(unchecked((int)0xC000003E)), totalTransferred, totalSize - totalTransferred, "File failed integrity check — will retry"); // STATUS_DATA_ERROR
                if (nodeId != null && totalTransferred > 0)
                    DiscardUnverifiedContent(nodeId, fullPath);
            }
            catch (Exception ex)
            {
                Log.Error($"{_logPrefix} FETCH_DATA streaming error: {ex.Message}");
                TransferError(callbackInfo, new NTSTATUS(unchecked((int)0xC0000001)), totalTransferred, totalSize - totalTransferred, $"Download failed: {ex.Message}"); // STATUS_UNSUCCESSFUL
                if (nodeId != null && totalTransferred > 0)
                    DiscardUnverifiedContent(nodeId, fullPath);
            }
            finally
            {
                _inFlightFetches.TryRemove(transferKey, out _);
                cts.Dispose();
                try { OnDownloadCompleted?.Invoke(transferKey); }
                catch (Exception ex) { Log.Error($"{_logPrefix} OnDownloadCompleted handler error: {ex.Message}"); }
            }
        });
    }

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes from <paramref name="stream"/>,
    /// or fewer if the stream ends.
    /// </summary>
    private static async Task<int> ReadFullAsync(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            ct.ThrowIfCancellationRequested();
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                break;
            offset += read;
        }
        return offset;
    }

    /// <summary>
    /// Transfer downloaded data to cfapi. Must run on the callback thread.
    /// <paramref name="sourceOffset"/> is the index into <paramref name="data"/> where the requested range starts.
    /// <paramref name="fileOffset"/> is the offset within the cloud file where data should be written.
    /// </summary>
    private static void TransferData(CF_CALLBACK_INFO callbackInfo, byte[] data,
        int sourceOffset, long fileOffset, long length, long totalSize, CancellationToken ct)
    {
        const int chunkSize = 4 * 1024 * 1024; // 4MB
        long totalTransferred = 0;

        long remaining = Math.Min(length, data.Length - sourceOffset);
        while (remaining > 0)
        {
            ct.ThrowIfCancellationRequested();

            int chunkLen = (int)Math.Min(chunkSize, remaining);
            TransferChunk(callbackInfo, data, (int)(sourceOffset + totalTransferred), fileOffset + totalTransferred, chunkLen);
            totalTransferred += chunkLen;
            remaining -= chunkLen;

            if (totalSize > 0)
            {
                try
                {
                    PInvoke.CfReportProviderProgress(
                        callbackInfo.ConnectionKey,
                        callbackInfo.TransferKey,
                        totalSize,
                        totalTransferred);
                }
                catch { /* progress is best-effort */ }
            }
        }
    }

    private static unsafe void TransferChunk(CF_CALLBACK_INFO callbackInfo, byte[] data, int sourceOffset, long fileOffset, int length)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey,
        };

        fixed (byte* pData = &data[sourceOffset])
        {
            var opParams = new CF_OPERATION_PARAMETERS();
            opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
            opParams.Anonymous.TransferData.CompletionStatus = new NTSTATUS(0); // STATUS_SUCCESS
            opParams.Anonymous.TransferData.Buffer = pData;
            opParams.Anonymous.TransferData.Offset = fileOffset;
            opParams.Anonymous.TransferData.Length = length;

            PInvoke.CfExecute(in opInfo, ref opParams).ThrowOnFailure();
        }
    }

    /// <summary>
    /// Fail the outstanding part of a FETCH_DATA request. cfapi needs the failed
    /// range: a zero-length failure is ignored and the request only ends when the
    /// driver times it out (~60 s), during which the reader sits blocked.
    /// </summary>
    private unsafe void TransferError(CF_CALLBACK_INFO callbackInfo, NTSTATUS status, long offset, long length, string? message = null)
    {
        Log.Error($"{_logPrefix} TransferError: status=0x{(uint)status.Value:X8}, range={offset}+{length}, transferKey={callbackInfo.TransferKey} (marks placeholder NOT in-sync)");
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey,
        };

        var opParams = new CF_OPERATION_PARAMETERS();
        opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
        opParams.Anonymous.TransferData.CompletionStatus = status;
        opParams.Anonymous.TransferData.Buffer = null;
        opParams.Anonymous.TransferData.Offset = offset;
        opParams.Anonymous.TransferData.Length = length;

        if (message != null && CfApiCapabilities.HasSyncStatus)
        {
            if (message.Length > 200)
                message = message[..200];

            Span<byte> syncBuf = stackalloc byte[SyncRoot.SyncStatusSize(message)];
            SyncRoot.FillSyncStatus(syncBuf, 0x80000000u, message);

            fixed (byte* pSync = syncBuf)
            {
                opInfo.SyncStatus = (CF_SYNC_STATUS*)pSync;
                PInvoke.CfExecute(in opInfo, ref opParams);
            }
        }
        else
        {
            PInvoke.CfExecute(in opInfo, ref opParams);
        }
    }
}
