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

    public SyncCallbacks(IJmapClient jmapClient)
    {
        _jmapClient = jmapClient;
    }

    public unsafe (CF_CALLBACK_REGISTRATION[] registrations, CF_CALLBACK[] delegates) CreateCallbackRegistrations()
    {
        // Must keep delegate references alive to prevent GC
        CF_CALLBACK fetchDataDelegate = new(FetchDataCallback);

        var delegates = new CF_CALLBACK[] { fetchDataDelegate };

        var registrations = new CF_CALLBACK_REGISTRATION[]
        {
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_FETCH_DATA,
                Callback = fetchDataDelegate,
            },
            // Sentinel entry to mark end of array
            new()
            {
                Type = CF_CALLBACK_TYPE.CF_CALLBACK_TYPE_NONE,
            },
        };

        return (registrations, delegates);
    }

    private unsafe void FetchDataCallback(CF_CALLBACK_INFO* callbackInfo, CF_CALLBACK_PARAMETERS* callbackParameters)
    {
        try
        {
            var fetchParams = callbackParameters->Anonymous.FetchData;
            long requiredOffset = fetchParams.RequiredFileOffset;
            long requiredLength = fetchParams.RequiredLength;

            // Extract the StorageNode ID from the file identity blob
            string? nodeId = null;
            if (callbackInfo->FileIdentity != null && callbackInfo->FileIdentityLength > 0)
            {
                nodeId = Encoding.UTF8.GetString(
                    new ReadOnlySpan<byte>(callbackInfo->FileIdentity, (int)callbackInfo->FileIdentityLength));
            }

            if (string.IsNullOrEmpty(nodeId))
            {
                Console.Error.WriteLine($"FETCH_DATA: No identity for {callbackInfo->NormalizedPath}");
                TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC000000D))); // STATUS_INVALID_PARAMETER
                return;
            }

            Console.WriteLine($"FETCH_DATA: {callbackInfo->NormalizedPath} (node={nodeId}, offset={requiredOffset}, len={requiredLength})");

            // Bridge async -> sync for the callback
            TransferDataAsync(*callbackInfo, nodeId, requiredOffset, requiredLength)
                .GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FETCH_DATA error: {ex.Message}");
            TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC0000001))); // STATUS_UNSUCCESSFUL
        }
    }

    private async Task TransferDataAsync(CF_CALLBACK_INFO callbackInfo, string nodeId, long offset, long length)
    {
        var nodes = await _jmapClient.GetStorageNodesAsync([nodeId]);
        if (nodes.Length == 0 || nodes[0].BlobId == null)
        {
            Console.Error.WriteLine($"Node {nodeId} not found or has no blob");
            TransferError(callbackInfo, new NTSTATUS(unchecked((int)0xC0000034))); // STATUS_OBJECT_NAME_NOT_FOUND
            return;
        }

        var node = nodes[0];
        using var stream = await _jmapClient.DownloadBlobAsync(node.BlobId!, node.Type, node.Name);

        const int chunkSize = 4 * 1024 * 1024; // 4MB chunks
        var buffer = new byte[chunkSize];
        long totalTransferred = 0;
        long totalSize = node.Size;

        // Skip to the requested offset
        if (offset > 0)
        {
            long skipped = 0;
            while (skipped < offset)
            {
                int toRead = (int)Math.Min(buffer.Length, offset - skipped);
                int read = await stream.ReadAsync(buffer, 0, toRead);
                if (read == 0) break;
                skipped += read;
            }
        }

        // Read and transfer data
        long remaining = length;
        while (remaining > 0)
        {
            int toRead = (int)Math.Min(chunkSize, remaining);
            int bytesRead = await stream.ReadAsync(buffer, 0, toRead);
            if (bytesRead == 0)
                break;

            TransferChunk(callbackInfo, buffer, offset + totalTransferred, bytesRead);
            totalTransferred += bytesRead;
            remaining -= bytesRead;

            // Report progress
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

    private static unsafe void TransferChunk(CF_CALLBACK_INFO callbackInfo, byte[] data, long offset, int length)
    {
        var opInfo = new CF_OPERATION_INFO
        {
            StructSize = (uint)sizeof(CF_OPERATION_INFO),
            Type = CF_OPERATION_TYPE.CF_OPERATION_TYPE_TRANSFER_DATA,
            ConnectionKey = callbackInfo.ConnectionKey,
            TransferKey = callbackInfo.TransferKey,
            RequestKey = callbackInfo.RequestKey,
        };

        fixed (byte* pData = data)
        {
            var opParams = new CF_OPERATION_PARAMETERS();
            opParams.ParamSize = (uint)sizeof(CF_OPERATION_PARAMETERS);
            opParams.Anonymous.TransferData.CompletionStatus = new NTSTATUS(0); // STATUS_SUCCESS
            opParams.Anonymous.TransferData.Buffer = pData;
            opParams.Anonymous.TransferData.Offset = offset;
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
