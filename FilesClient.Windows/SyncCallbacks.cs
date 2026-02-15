using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.CloudFilters;
using FilesClient.Jmap;

namespace FilesClient.Windows;

internal class SyncCallbacks
{
    private readonly IJmapClient _jmapClient;

    /// <summary>
    /// Node IDs that were recently hydrated by cfapi. SyncEngine checks this
    /// to avoid re-uploading a file that was just downloaded.
    /// </summary>
    public ConcurrentDictionary<string, byte> RecentlyHydrated { get; } = new();

    /// <summary>
    /// In-flight hydration requests keyed by TransferKey, so CANCEL_FETCH_DATA
    /// can cancel the corresponding download.
    /// </summary>
    private readonly ConcurrentDictionary<long, CancellationTokenSource> _inFlightFetches = new();

    /// <summary>
    /// Whether the server supports HTTP Range requests. Set to false after
    /// a non-206 response or error, disabling range requests for the session.
    /// </summary>
    private volatile bool _rangeRequestsSupported = true;

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

    public record DirectoryPopulatedInfo(string DirectoryPath);
    public event Action<DirectoryPopulatedInfo>? OnDirectoryPopulated;

    public SyncCallbacks(IJmapClient jmapClient)
    {
        _jmapClient = jmapClient;
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
            Console.WriteLine($"FETCH_PLACEHOLDERS: node={nodeId}, path={callbackInfo->NormalizedPath}");

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
            Console.Error.WriteLine($"FETCH_PLACEHOLDERS error: {ex.Message}");
        }
    }

    private unsafe void FetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var transferKey = callbackInfo->TransferKey;
        var cts = new CancellationTokenSource();
        _inFlightFetches[transferKey] = cts;

        try
        {
            var fetchParams = callbackParameters->Anonymous.FetchData;
            long requiredOffset = fetchParams.RequiredFileOffset;
            long requiredLength = fetchParams.RequiredLength;

            // Extract the StorageNode ID from the file identity blob
            var nodeId = ExtractNodeId(callbackInfo);

            if (string.IsNullOrEmpty(nodeId))
            {
                Console.Error.WriteLine($"FETCH_DATA: No identity for {callbackInfo->NormalizedPath}");
                TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC000000D))); // STATUS_INVALID_PARAMETER
                return;
            }

            Console.WriteLine($"FETCH_DATA: node={nodeId}, offset={requiredOffset}, len={requiredLength}");

            // Download blob data asynchronously, then transfer to cfapi
            // synchronously on the callback thread (CfExecute requires
            // the callback thread context for the transfer key to be valid).
            var (data, dataStartOffset, totalSize) = FetchBlobDataAsync(nodeId, requiredOffset, requiredLength, cts.Token)
                .GetAwaiter().GetResult();

            int sourceOffset = (int)(requiredOffset - dataStartOffset);
            TransferData(*callbackInfo, data, sourceOffset, requiredOffset, requiredLength, totalSize, cts.Token);

            // Record that we just hydrated this file so SyncEngine doesn't
            // re-upload it when FileSystemWatcher fires a Changed event.
            if (nodeId != null)
                RecentlyHydrated[nodeId] = 0;
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine($"FETCH_DATA cancelled: transferKey={transferKey}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FETCH_DATA error: {ex.Message}");
            TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC0000001))); // STATUS_UNSUCCESSFUL
        }
        finally
        {
            _inFlightFetches.TryRemove(transferKey, out _);
            cts.Dispose();
        }
    }

    private unsafe void NotifyDeleteCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        bool allowed = true;
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);

            Console.WriteLine($"NOTIFY_DELETE: node={nodeId}, path={fullPath}");

            if (OnDeleteRequested != null)
                allowed = OnDeleteRequested(nodeId, fullPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NOTIFY_DELETE error: {ex.Message}");
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

            bool targetInScope = ((uint)renameParams.Flags & 0x1) != 0; // CF_CALLBACK_RENAME_FLAG_TARGET_IN_SCOPE

            Console.WriteLine($"NOTIFY_RENAME: node={nodeId}, {sourcePath} → {targetPath} (inScope={targetInScope})");

            if (OnRenameRequested != null)
                allowed = OnRenameRequested(nodeId, sourcePath, targetPath, targetInScope).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NOTIFY_RENAME error: {ex.Message}");
            allowed = false;
        }
        AckRename(callbackInfo, allowed);
    }

    private unsafe void CancelFetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        var transferKey = callbackInfo->TransferKey;
        var nodeId = ExtractNodeId(callbackInfo);
        Console.WriteLine($"CANCEL_FETCH_DATA: node={nodeId}, transferKey={transferKey}");

        if (_inFlightFetches.TryGetValue(transferKey, out var cts))
            cts.Cancel();
    }

    private unsafe void NotifyDehydrateCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        bool allowed = true;
        try
        {
            var nodeId = ExtractNodeId(callbackInfo);
            var fullPath = ExtractFullPath(callbackInfo);

            Console.WriteLine($"NOTIFY_DEHYDRATE: node={nodeId}, path={fullPath}");

            if (OnDehydrateRequested != null)
                allowed = OnDehydrateRequested(nodeId, fullPath).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NOTIFY_DEHYDRATE error: {ex.Message}");
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
            Console.WriteLine($"NOTIFY_DEHYDRATE_COMPLETION: node={nodeId}, path={fullPath}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"NOTIFY_DEHYDRATE_COMPLETION error: {ex.Message}");
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

    /// <summary>
    /// Download blob data asynchronously (can run on any thread).
    /// Returns the data bytes, the file offset where data starts, and total file size.
    /// Attempts a range download when supported; falls back to full download.
    /// </summary>
    private async Task<(byte[] data, long dataStartOffset, long totalSize)> FetchBlobDataAsync(
        string nodeId, long requiredOffset, long requiredLength, CancellationToken ct)
    {
        var nodes = await _jmapClient.GetStorageNodesAsync([nodeId], ct);
        if (nodes.Length == 0 || nodes[0].BlobId == null)
            throw new FileNotFoundException($"Node {nodeId} not found or has no blob");

        var node = nodes[0];

        // Try range download if supported and this is a partial request
        if (_rangeRequestsSupported && (requiredOffset > 0 || requiredLength < node.Size))
        {
            try
            {
                var (rangeStream, isPartial) = await _jmapClient.DownloadBlobRangeAsync(
                    node.BlobId!, requiredOffset, requiredLength, node.Type, node.Name, ct);
                using (rangeStream)
                {
                    using var ms = new MemoryStream();
                    await rangeStream.CopyToAsync(ms, ct);
                    var rangeBytes = ms.ToArray();

                    if (isPartial)
                        return (rangeBytes, requiredOffset, node.Size);

                    // Server returned 200 (full content) — disable range requests
                    _rangeRequestsSupported = false;
                    Console.WriteLine("Range requests not supported by server, falling back to full downloads");
                    return (rangeBytes, 0, node.Size);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                _rangeRequestsSupported = false;
                Console.Error.WriteLine($"Range request failed, falling back to full download: {ex.Message}");
            }
        }

        // Full download fallback
        using var stream = await _jmapClient.DownloadBlobAsync(node.BlobId!, node.Type, node.Name, ct);
        using var fullMs = new MemoryStream();
        await stream.CopyToAsync(fullMs, ct);
        return (fullMs.ToArray(), 0, node.Size);
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
