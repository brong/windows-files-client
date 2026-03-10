using System.Collections.Concurrent;
using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FileNodeClient.Windows;

/// <summary>
/// Static registry bridging the COM ThumbnailHandler to running SyncEngine instances.
/// The COM handler runs in the Service.exe process (ExeServer via MSIX manifest),
/// so it has direct access to JMAP clients and mappings — no IPC needed.
///
/// Thumbnail requests from Explorer arrive concurrently (one per file). Rather than
/// issuing a separate Blob/convert HTTP request per file, we batch them: requests
/// accumulate for up to 20ms, then a single Blob/convert call converts all blobs,
/// and the resulting PNGs are downloaded concurrently.
/// </summary>
public static class ThumbnailService
{
    private record Registration(
        IJmapClient Client,
        Func<string, string?> GetBlobId);

    private static readonly ConcurrentDictionary<string, Registration> _registrations
        = new(StringComparer.OrdinalIgnoreCase);

    // LRU cache keyed by (blobId, cx) → PNG bytes
    private static readonly ConcurrentDictionary<(string BlobId, uint Cx), byte[]> _cache = new();
    private const int MaxCacheEntries = 256;

    // Track blobIds that failed conversion so we don't retry on every Explorer request
    private static readonly ConcurrentDictionary<string, DateTime> _failedBlobIds = new();
    private static readonly TimeSpan FailureCooldown = TimeSpan.FromMinutes(5);

    // Timeout for server requests — Explorer will abandon slow handlers
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

    // Batching: accumulate requests for BatchDelay, then fire one Blob/convert
    private static readonly TimeSpan BatchDelay = TimeSpan.FromMilliseconds(20);

    // Limit concurrent blob downloads to avoid hammering the server
    private static readonly SemaphoreSlim _downloadSemaphore = new(4);

    private record PendingRequest(
        string SyncRootPath,
        string BlobId,
        uint Cx,
        TaskCompletionSource<byte[]?> Tcs);

    private static readonly object _batchLock = new();
    private static List<PendingRequest> _pendingBatch = new();
    private static Timer? _batchTimer;

    public static void Register(string syncRootPath, IJmapClient client,
        Func<string, string?> getBlobId)
    {
        if (!client.HasBlobConvert)
        {
            Log.Info($"ThumbnailService: skipping {syncRootPath} (server has no Blob/convert support)");
            return;
        }
        _registrations[syncRootPath] = new Registration(client, getBlobId);
        Log.Info($"ThumbnailService: registered {syncRootPath}");
    }

    public static void Unregister(string syncRootPath)
    {
        _registrations.TryRemove(syncRootPath, out _);
        Log.Info($"ThumbnailService: unregistered {syncRootPath}");
    }

    /// <summary>
    /// Find the sync root path that contains the given file path.
    /// </summary>
    public static string? FindSyncRoot(string filePath)
    {
        foreach (var syncRootPath in _registrations.Keys)
        {
            if (filePath.StartsWith(syncRootPath, StringComparison.OrdinalIgnoreCase)
                && filePath.Length > syncRootPath.Length
                && filePath[syncRootPath.Length] == Path.DirectorySeparatorChar)
                return syncRootPath;
        }
        return null;
    }

    /// <summary>
    /// Get a thumbnail for the given node. Returns PNG bytes or null on failure.
    /// Called from pipe server threads — blocks until the batch completes.
    /// </summary>
    public static byte[]? GetThumbnail(string syncRootPath, string nodeId, uint cx)
    {
        if (!_registrations.TryGetValue(syncRootPath, out var reg))
        {
            Log.Debug($"ThumbnailService: no registration for {syncRootPath}");
            return null;
        }

        var blobId = reg.GetBlobId(nodeId);
        if (blobId == null)
        {
            Log.Debug($"ThumbnailService: no blobId for node {nodeId}");
            return null;
        }

        // Check cache
        var cacheKey = (blobId, cx);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Skip blobIds that recently failed
        if (_failedBlobIds.TryGetValue(blobId, out var failedAt)
            && DateTime.UtcNow - failedAt < FailureCooldown)
            return null;

        Log.Debug($"ThumbnailService: queuing thumbnail for blob {blobId} at {cx}px");

        // Enqueue into the batch and wait for result
        var tcs = new TaskCompletionSource<byte[]?>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_batchLock)
        {
            _pendingBatch.Add(new PendingRequest(syncRootPath, blobId, cx, tcs));

            // Start or reset the batch timer
            if (_batchTimer == null)
                _batchTimer = new Timer(OnBatchTimerFired, null, BatchDelay, Timeout.InfiniteTimeSpan);
            else
                _batchTimer.Change(BatchDelay, Timeout.InfiniteTimeSpan);
        }

        try
        {
            return tcs.Task.GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailService: batch failed for node {nodeId}: {ex.Message}");
            return null;
        }
    }

    private static void OnBatchTimerFired(object? state)
    {
        List<PendingRequest> batch;
        lock (_batchLock)
        {
            batch = _pendingBatch;
            _pendingBatch = new List<PendingRequest>();
        }

        if (batch.Count == 0)
            return;

        // Fire and forget — each TCS will be completed
        Log.FireAndForget(Task.Run(() => ProcessBatchAsync(batch)), "ThumbnailBatchProcess");
    }

    private static async Task ProcessBatchAsync(List<PendingRequest> batch)
    {
        Log.Info($"ThumbnailService: processing batch of {batch.Count} thumbnail(s)");

        // Group by sync root (all should typically be the same)
        var byRoot = batch.GroupBy(r => r.SyncRootPath, StringComparer.OrdinalIgnoreCase);

        foreach (var group in byRoot)
        {
            if (!_registrations.TryGetValue(group.Key, out var reg))
            {
                foreach (var req in group)
                    req.Tcs.TrySetResult(null);
                continue;
            }

            var requests = group.ToList();

            // Deduplicate by (blobId, cx) — multiple requests for the same blob
            // share one conversion
            var unique = requests
                .GroupBy(r => (r.BlobId, r.Cx))
                .Select(g => g.First())
                .ToList();

            try
            {
                using var cts = new CancellationTokenSource(RequestTimeout);

                // Single batched Blob/convert call
                var items = unique
                    .Select(r => (r.BlobId, r.Cx, r.Cx))
                    .ToList();

                var converted = await reg.Client.ConvertImagesAsync(items, "image/png", cts.Token);

                Log.Debug($"ThumbnailService: Blob/convert returned {converted.Count}/{unique.Count} thumbnails");

                // Download all converted blobs concurrently (throttled)
                var downloadTasks = converted.Select(async kv =>
                {
                    var (blobId, thumbBlobId) = kv;
                    await _downloadSemaphore.WaitAsync(cts.Token);
                    try
                    {
                        using var stream = await reg.Client.DownloadBlobAsync(
                            thumbBlobId, "image/png", null, cts.Token);
                        using var ms = new MemoryStream();
                        await stream.CopyToAsync(ms, cts.Token);
                        return (BlobId: blobId, Data: ms.ToArray());
                    }
                    catch (Exception ex)
                    {
                        Log.Error($"ThumbnailService: download failed for blob {blobId}: {ex.Message}");
                        return (BlobId: blobId, Data: (byte[]?)null);
                    }
                    finally
                    {
                        _downloadSemaphore.Release();
                    }
                }).ToList();

                var results = await Task.WhenAll(downloadTasks);

                // Build lookup from blobId → PNG bytes
                var pngByBlobId = new Dictionary<string, byte[]?>();
                foreach (var (blobId, data) in results)
                    pngByBlobId[blobId] = data;

                // Complete all waiting callers
                foreach (var req in requests)
                {
                    byte[]? pngBytes = null;
                    if (pngByBlobId.TryGetValue(req.BlobId, out var data) && data != null)
                    {
                        pngBytes = data;
                        CacheResult(req.BlobId, req.Cx, pngBytes);
                        _failedBlobIds.TryRemove(req.BlobId, out _);
                    }
                    else if (!converted.ContainsKey(req.BlobId))
                    {
                        // Blob/convert didn't return this one — record failure
                        _failedBlobIds[req.BlobId] = DateTime.UtcNow;
                    }
                    req.Tcs.TrySetResult(pngBytes);
                }
            }
            catch (Exception ex)
            {
                Log.Error($"ThumbnailService: batch Blob/convert failed: {ex.Message}");
                foreach (var req in requests)
                {
                    _failedBlobIds[req.BlobId] = DateTime.UtcNow;
                    req.Tcs.TrySetResult(null);
                }
            }
        }
    }

    private static void CacheResult(string blobId, uint cx, byte[] pngBytes)
    {
        // Evict oldest entries if cache is full
        if (_cache.Count >= MaxCacheEntries)
        {
            int toRemove = _cache.Count / 2;
            foreach (var key in _cache.Keys.Take(toRemove))
                _cache.TryRemove(key, out _);
        }
        _cache[(blobId, cx)] = pngBytes;
    }

    /// <summary>
    /// Convenience method for the pipe server: resolves path → sync root + nodeId,
    /// then fetches the thumbnail.
    /// </summary>
    public static byte[]? GetThumbnailForPath(string path, uint cx)
    {
        var syncRootPath = FindSyncRoot(path);
        if (syncRootPath == null)
        {
            Log.Debug($"ThumbnailService: no sync root for {path}");
            return null;
        }

        var nodeId = ReadPlaceholderNodeId(path);
        if (nodeId == null)
        {
            Log.Debug($"ThumbnailService: no nodeId for {path}");
            return null;
        }

        return GetThumbnail(syncRootPath, nodeId, cx);
    }

    /// <summary>
    /// Read the nodeId from a cloud file placeholder's FileIdentity.
    /// Uses FILE_WRITE_ATTRIBUTES (0x100) to avoid triggering FETCH_DATA on dehydrated files.
    /// </summary>
    internal static unsafe string? ReadPlaceholderNodeId(string path)
    {
        try
        {
            using var handle = PInvoke.CreateFile(
                path,
                0x00000100, // FILE_WRITE_ATTRIBUTES
                global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                    | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                    | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
                null,
                global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                default,
                null);

            if (handle.IsInvalid)
                return null;

            var cfHandle = new global::Windows.Win32.Foundation.HANDLE(handle.DangerousGetHandle());

            var buffer = new byte[256];
            fixed (byte* pBuffer = buffer)
            {
                uint returnedLen;
                var hr = PInvoke.CfGetPlaceholderInfo(
                    cfHandle,
                    CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
                    pBuffer,
                    (uint)buffer.Length,
                    &returnedLen);

                if (hr.Failed)
                    return null;

                // CF_PLACEHOLDER_BASIC_INFO layout:
                //   PinState (int, offset 0)
                //   InSyncState (int, offset 4)
                //   FileId (long, offset 8)
                //   SyncRootFileId (long, offset 16)
                //   FileIdentityLength (uint, offset 24)
                //   FileIdentity (byte[], offset 28)
                const int fileIdentityLengthOffset = 24;
                const int fileIdentityOffset = 28;

                if (returnedLen < (uint)fileIdentityOffset)
                    return null;

                var identityLength = *(uint*)(pBuffer + fileIdentityLengthOffset);
                if (identityLength == 0 || returnedLen < (uint)fileIdentityOffset + identityLength)
                    return null;

                return System.Text.Encoding.UTF8.GetString(pBuffer + fileIdentityOffset, (int)identityLength);
            }
        }
        catch
        {
            return null;
        }
    }
}
