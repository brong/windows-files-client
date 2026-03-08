using System.Collections.Concurrent;
using FileNodeClient.Ipc;
using FileNodeClient.Jmap;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FileNodeClient.Windows;

/// <summary>
/// Static registry bridging the COM ThumbnailHandler to running SyncEngine instances.
/// The COM handler runs in the Service.exe process (ExeServer via MSIX manifest),
/// so it has direct access to JMAP clients and mappings — no IPC needed.
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

    public static void Register(string syncRootPath, IJmapClient client,
        Func<string, string?> getBlobId)
    {
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
    /// Called by the COM ThumbnailHandler on the STA thread — all async work
    /// is dispatched to the thread pool to avoid STA deadlocks.
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

        Log.Debug($"ThumbnailService: requesting thumbnail for blob {blobId} at {cx}px");

        // Check cache
        var cacheKey = (blobId, cx);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        // Skip blobIds that recently failed (server may not support Blob/convert yet)
        if (_failedBlobIds.TryGetValue(blobId, out var failedAt)
            && DateTime.UtcNow - failedAt < FailureCooldown)
            return null;

        try
        {
            // Run async work on thread pool to avoid STA deadlock
            var task = Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(RequestTimeout);

                // Blob/convert to get a resized thumbnail blobId
                var thumbnailBlobId = await reg.Client.ConvertImageAsync(
                    blobId, cx, cx, "image/png", cts.Token);

                // Download the thumbnail blob
                using var stream = await reg.Client.DownloadBlobAsync(
                    thumbnailBlobId, "image/png", null, cts.Token);

                using var ms = new MemoryStream();
                await stream.CopyToAsync(ms, cts.Token);
                return ms.ToArray();
            });

            var pngBytes = task.GetAwaiter().GetResult();

            // Evict oldest entries if cache is full
            if (_cache.Count >= MaxCacheEntries)
            {
                int toRemove = _cache.Count / 2;
                foreach (var key in _cache.Keys.Take(toRemove))
                    _cache.TryRemove(key, out _);
            }

            _cache[cacheKey] = pngBytes;
            // Clear any previous failure record on success
            _failedBlobIds.TryRemove(blobId, out _);
            return pngBytes;
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailService: failed for node {nodeId}: {ex.Message}");
            _failedBlobIds[blobId] = DateTime.UtcNow;
            return null;
        }
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
