using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using FilesClient.Jmap;
using FilesClient.Jmap.Models;

namespace FilesClient.Windows;

internal class SyncCallbacks
{
    private readonly IJmapClient _jmapClient;
    private readonly JmapQueue _queue;
    private readonly string _logPrefix;

    private void Log(string msg) => Console.WriteLine($"{_logPrefix} {msg}");
    private void LogError(string msg) => Console.Error.WriteLine($"{_logPrefix} {msg}");

    /// <summary>
    /// Node IDs that were recently hydrated by cfapi. SyncEngine checks this
    /// to avoid re-uploading a file that was just downloaded.
    /// </summary>
    public ConcurrentDictionary<string, byte> RecentlyHydrated { get; } = new();

    /// <summary>
    /// In-flight hydration requests keyed by TransferKey, so CANCEL_FETCH_DATA
    /// can cancel the corresponding download.
    /// </summary>
    private readonly ConcurrentDictionary<long, (CancellationTokenSource Cts, string? NodeId)> _inFlightFetches = new();

    /// <summary>
    /// Whether the server supports HTTP Range requests. Set to false after
    /// a non-206 response or error, disabling range requests for the session.
    /// </summary>
    private volatile bool _rangeRequestsSupported = true;

    private const long BlobGetMaxSize = 16384;
    private string? _digestAlgorithm;
    private bool _digestAlgorithmResolved;

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

    public event Action<long, string>? OnDownloadStarted;   // transferKey, fileName
    public event Action<long>? OnDownloadCompleted;          // transferKey

    public record DirectoryPopulatedInfo(string DirectoryPath);
    public event Action<DirectoryPopulatedInfo>? OnDirectoryPopulated;

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

        var delegates = new CF_CALLBACK[] { fetchPlaceholdersDelegate, fetchDataDelegate, notifyDeleteDelegate, notifyRenameDelegate, cancelFetchDataDelegate, notifyDehydrateDelegate, notifyDehydrateCompletionDelegate };

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
            Log($"FETCH_PLACEHOLDERS: node={nodeId}, path={callbackInfo->NormalizedPath}");

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
            OnDirectoryPopulated?.Invoke(new DirectoryPopulatedInfo(dirPath));
        }
        catch (Exception ex)
        {
            LogError($"FETCH_PLACEHOLDERS error: {ex.Message}");
        }
    }

    private unsafe void FetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var transferKey = callbackInfo->TransferKey;
        var cts = new CancellationTokenSource();
        _inFlightFetches[transferKey] = (cts, null);

        var fileName = Path.GetFileName(callbackInfo->NormalizedPath.ToString());
        OnDownloadStarted?.Invoke(transferKey, fileName);

        try
        {
            var fetchParams = callbackParameters->Anonymous.FetchData;
            long requiredOffset = fetchParams.RequiredFileOffset;
            long requiredLength = fetchParams.RequiredLength;

            // Extract the FileNode ID from the file identity blob
            var nodeId = ExtractNodeId(callbackInfo);

            if (string.IsNullOrEmpty(nodeId))
            {
                LogError($"FETCH_DATA: No identity for {fileName}");
                TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC000000D))); // STATUS_INVALID_PARAMETER
                return;
            }

            // Store node ID so CancelFetchesWhere can match by node
            _inFlightFetches[transferKey] = (cts, nodeId);

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
            Log($"FETCH_DATA: node={nodeId}, offset={requiredOffset}, len={requiredLength}, caller={processName}");

            // Fetch node metadata first (shared by both paths)
            var node = FetchNodeAsync(nodeId, cts.Token).GetAwaiter().GetResult();
            var nodeSize = node.Size ?? 0;
            bool isFullHydration = requiredOffset == 0 && requiredLength >= nodeSize;

            if (isFullHydration && nodeSize > BlobGetMaxSize)
            {
                // Large full hydration — stream directly to cfapi in chunks
                StreamBlobToPlaceholder(*callbackInfo, node, cts.Token);
            }
            else
            {
                // Small file, partial request, or Blob/get eligible — use buffered path
                var (data, dataStartOffset, totalSize) = FetchBlobDataAsync(node, requiredOffset, requiredLength, cts.Token)
                    .GetAwaiter().GetResult();

                int sourceOffset = (int)(requiredOffset - dataStartOffset);
                TransferData(*callbackInfo, data, sourceOffset, requiredOffset, requiredLength, totalSize, cts.Token);
            }

            // Record that we just hydrated this file so SyncEngine doesn't
            // re-upload it when FileSystemWatcher fires a Changed event.
            if (nodeId != null)
                RecentlyHydrated[nodeId] = 0;
        }
        catch (OperationCanceledException)
        {
            Log($"FETCH_DATA cancelled: transferKey={transferKey}");
        }
        catch (Exception ex)
        {
            LogError($"FETCH_DATA error: {ex.Message}");
            TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC0000001))); // STATUS_UNSUCCESSFUL
        }
        finally
        {
            _inFlightFetches.TryRemove(transferKey, out _);
            cts.Dispose();
            OnDownloadCompleted?.Invoke(transferKey);
        }
    }

    private unsafe void NotifyDeleteCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        bool allowed = true;
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);

            Log($"NOTIFY_DELETE: node={nodeId}, path={fullPath}");

            if (OnDeleteRequested != null)
                allowed = OnDeleteRequested(nodeId, fullPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogError($"NOTIFY_DELETE error: {ex.Message}");
            allowed = false;
        }
        AckDelete(callbackInfo, allowed);
    }

    private unsafe void NotifyRenameCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        bool allowed = true;
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var sourcePath = ExtractFullPath(callbackInfo);

            var renameParams = callbackParameters->Anonymous.Rename;
            var targetPath = callbackInfo->VolumeDosName.ToString() + renameParams.TargetPath.ToString();
            if (targetPath.StartsWith(@"\\?\"))
                targetPath = targetPath.Substring(4);

            bool targetInScope = ((uint)renameParams.Flags & 0x4) != 0; // CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE

            Log($"NOTIFY_RENAME: node={nodeId}, {sourcePath} → {targetPath} (inScope={targetInScope})");

            if (OnRenameRequested != null)
                allowed = OnRenameRequested(nodeId, sourcePath, targetPath, targetInScope).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogError($"NOTIFY_RENAME error: {ex.Message}");
            allowed = false;
        }
        AckRename(callbackInfo, allowed);
    }

    private unsafe void CancelFetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var transferKey = callbackInfo->TransferKey;
        var nodeId = ExtractNodeId(callbackInfo);
        Log($"CANCEL_FETCH_DATA: node={nodeId}, transferKey={transferKey}");

        if (_inFlightFetches.TryGetValue(transferKey, out var entry))
            entry.Cts.Cancel();
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
            var (cts, nodeId) = kvp.Value;
            if (nodeId != null && shouldCancel(nodeId))
            {
                Log($"Cancelling in-flight download for node {nodeId}");
                cts.Cancel();
            }
        }
    }

    private unsafe void NotifyDehydrateCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        bool allowed = true;
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);

            Log($"NOTIFY_DEHYDRATE: node={nodeId}, path={fullPath}");

            if (OnDehydrateRequested != null)
                allowed = OnDehydrateRequested(nodeId, fullPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            LogError($"NOTIFY_DEHYDRATE error: {ex.Message}");
            allowed = false;
        }
        AckDehydrate(callbackInfo, allowed);
    }

    private unsafe void NotifyDehydrateCompletionCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);
            Log($"NOTIFY_DEHYDRATE_COMPLETION: node={nodeId}, path={fullPath}");
        }
        catch (Exception ex)
        {
            LogError($"NOTIFY_DEHYDRATE_COMPLETION error: {ex.Message}");
        }
    }

    private static unsafe void AckDehydrate(CF_CALLBACK_INFO* callbackInfo, bool allow)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DEHYDRATE,
            ConnectionKey = callbackInfo->ConnectionKey,
            TransferKey = callbackInfo->TransferKey,
            RequestKey = callbackInfo->RequestKey,
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

    private static unsafe void AckDelete(CF_CALLBACK_INFO* callbackInfo, bool allow)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_DELETE,
            ConnectionKey = callbackInfo->ConnectionKey,
            TransferKey = callbackInfo->TransferKey,
            RequestKey = callbackInfo->RequestKey,
        };

        var opParams = new CF_OPERATION_PARAMETERS();
        opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
        opParams.Anonymous.AckDelete.CompletionStatus = allow
            ? new NTSTATUS(0)  // STATUS_SUCCESS
            : new NTSTATUS(unchecked((int)0xC0000001));  // STATUS_UNSUCCESSFUL

        PInvoke.CfExecute(in opInfo, ref opParams).ThrowOnFailure();
    }

    private static unsafe void AckRename(CF_CALLBACK_INFO* callbackInfo, bool allow)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_ACK_RENAME,
            ConnectionKey = callbackInfo->ConnectionKey,
            TransferKey = callbackInfo->TransferKey,
            RequestKey = callbackInfo->RequestKey,
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

    private static void VerifyDigest(string algorithm, byte[] data, BlobDataItem item, string context)
    {
        var expected = GetDigestFromItem(item, algorithm);
        if (expected == null)
            return;
        var actual = ComputeDigest(algorithm, data);
        if (actual != expected)
            Console.Error.WriteLine($"Digest mismatch ({context}): expected {expected}, got {actual}");
        else
            Console.WriteLine($"Digest OK ({context}): {algorithm}={actual}");
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
                    Log($"FETCH_DATA: small file via Blob/get, {data.Length} bytes");
                    return (data, 0, nodeSize);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogError($"Blob/get failed for small file, falling back to HTTP: {ex.Message}");
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
                            try
                            {
                                var digestItem = await digestTask;
                                if (digestItem != null)
                                    VerifyDigest(algo, rangeBytes, digestItem, $"range {node.BlobId} @{requiredOffset}+{requiredLength}");
                            }
                            catch (Exception ex) when (ex is not OperationCanceledException)
                            {
                                LogError($"Digest fetch failed for range request: {ex.Message}");
                            }
                        }
                        return (rangeBytes, requiredOffset, nodeSize);
                    }

                    // Server returned 200 (full content) — disable range requests
                    _rangeRequestsSupported = false;
                    Log("Range requests not supported by server, falling back to full downloads");

                    if (algo != null)
                    {
                        try
                        {
                            // Re-fetch digest for full file since range digest doesn't apply
                            var fullDigestItem = await _queue.EnqueueAsync(QueuePriority.Interactive,
                                () => _jmapClient.GetBlobAsync(node.BlobId!, [$"digest:{algo}"], ct: ct), ct);
                            VerifyDigest(algo, rangeBytes, fullDigestItem, $"full fallback {node.BlobId}");
                        }
                        catch (Exception ex) when (ex is not OperationCanceledException)
                        {
                            LogError($"Digest fetch failed for full fallback: {ex.Message}");
                        }
                    }
                    return (rangeBytes, 0, nodeSize);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _rangeRequestsSupported = false;
                LogError($"Range request failed, falling back to full download: {ex.Message}");
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
                try
                {
                    var digestItem = await digestTask;
                    if (digestItem != null)
                        VerifyDigest(algo, fullBytes, digestItem, $"full download {node.BlobId}");
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogError($"Digest fetch failed for full download: {ex.Message}");
                }
            }

            return (fullBytes, 0, nodeSize);
        }
    }

    /// <summary>
    /// Stream a blob directly to a cfapi placeholder in chunks. Must run on the
    /// callback thread. Opens a single HTTP GET, reads chunks synchronously, and
    /// transfers each chunk to cfapi immediately — so the application (e.g. video
    /// player) receives data progressively.
    /// </summary>
    private unsafe void StreamBlobToPlaceholder(
        CF_CALLBACK_INFO callbackInfo, FileNode node, CancellationToken ct)
    {
        var blobId = node.BlobId!;
        var totalSize = node.Size ?? 0;
        var chunkSize = (int)Math.Min(_jmapClient.ChunkSize ?? 4 * 1024 * 1024, int.MaxValue);
        var algo = GetDigestAlgorithm();

        Log($"FETCH_DATA: streaming {totalSize} bytes in {chunkSize} byte chunks for blob {blobId}");

        // Open HTTP stream (async → .GetAwaiter().GetResult() to get stream on callback thread)
        var stream = _queue.EnqueueAsync(QueuePriority.Interactive,
            () => _jmapClient.DownloadBlobAsync(blobId, node.Type, node.Name, ct), ct)
            .GetAwaiter().GetResult();

        using (stream)
        {
            var buffer = new byte[chunkSize];
            long totalTransferred = 0;
            using var hash = algo != null
                ? IncrementalHash.CreateHash(algo == "sha" ? HashAlgorithmName.SHA1 : HashAlgorithmName.SHA256)
                : null;

            while (totalTransferred < totalSize)
            {
                ct.ThrowIfCancellationRequested();

                var toRead = (int)Math.Min(chunkSize, totalSize - totalTransferred);
                var bytesRead = ReadFull(stream, buffer, toRead, ct);
                if (bytesRead == 0)
                    break;

                hash?.AppendData(buffer, 0, bytesRead);
                TransferChunk(callbackInfo, buffer, 0, totalTransferred, bytesRead);
                totalTransferred += bytesRead;

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

            Log($"FETCH_DATA: streamed {totalTransferred} bytes total for blob {blobId}");

            // Verify digest against server
            if (hash != null && algo != null)
            {
                var actual = Convert.ToBase64String(hash.GetHashAndReset());
                try
                {
                    var digestItem = GetBlobDigestAsync(blobId, algo, null, null, ct)
                        .GetAwaiter().GetResult();
                    if (digestItem != null)
                    {
                        var expected = GetDigestFromItem(digestItem, algo);
                        if (expected != null)
                        {
                            if (actual != expected)
                                LogError($"Digest mismatch (stream {blobId}): expected {expected}, got {actual}");
                            else
                                Log($"Digest OK (stream {blobId}): {algo}={actual}");
                        }
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    LogError($"Digest fetch failed for streaming download: {ex.Message}");
                }
            }
        }
    }

    /// <summary>
    /// Read exactly <paramref name="count"/> bytes from <paramref name="stream"/>,
    /// or fewer if the stream ends. Uses synchronous reads so this can block the
    /// callback thread while waiting for network data.
    /// </summary>
    private static int ReadFull(Stream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int offset = 0;
        while (offset < count)
        {
            ct.ThrowIfCancellationRequested();
            int read = stream.Read(buffer, offset, count - offset);
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

    private static unsafe void TransferError(CF_CALLBACK_INFO callbackInfo, NTSTATUS status)
    {
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
        opParams.Anonymous.TransferData.Offset = 0;
        opParams.Anonymous.TransferData.Length = 0;

        PInvoke.CfExecute(in opInfo, ref opParams);
    }
}
