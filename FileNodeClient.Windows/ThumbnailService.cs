using System.Collections.Concurrent;
using FileNodeClient.Ipc;
using FileNodeClient.Jmap;

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
    /// Called by the COM ThumbnailHandler on the STA thread.
    /// </summary>
    public static byte[]? GetThumbnail(string syncRootPath, string nodeId, uint cx)
    {
        if (!_registrations.TryGetValue(syncRootPath, out var reg))
            return null;

        var blobId = reg.GetBlobId(nodeId);
        if (blobId == null)
            return null;

        // Check cache
        var cacheKey = (blobId, cx);
        if (_cache.TryGetValue(cacheKey, out var cached))
            return cached;

        try
        {
            // Blob/convert to get a resized thumbnail blobId
            var thumbnailBlobId = reg.Client.ConvertImageAsync(
                blobId, cx, cx, "image/png", CancellationToken.None)
                .GetAwaiter().GetResult();

            // Download the thumbnail blob
            using var stream = reg.Client.DownloadBlobAsync(
                thumbnailBlobId, "image/png", null, CancellationToken.None)
                .GetAwaiter().GetResult();

            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            var pngBytes = ms.ToArray();

            // Evict oldest entries if cache is full
            if (_cache.Count >= MaxCacheEntries)
            {
                // Simple eviction: clear half the cache
                int toRemove = _cache.Count / 2;
                foreach (var key in _cache.Keys.Take(toRemove))
                    _cache.TryRemove(key, out _);
            }

            _cache[cacheKey] = pngBytes;
            return pngBytes;
        }
        catch (Exception ex)
        {
            Log.Error($"ThumbnailService: failed for node {nodeId}: {ex.Message}");
            return null;
        }
    }
}
