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

            Console.WriteLine($"FETCH_DATA: node={nodeId}, offset={requiredOffset}, len={requiredLength}");

            // Download blob data asynchronously, then transfer to cfapi
            // synchronously on the callback thread (CfExecute requires
            // the callback thread context for the transfer key to be valid).
            var (data, totalSize) = FetchBlobDataAsync(nodeId)
                .GetAwaiter().GetResult();

            TransferData(*callbackInfo, data, requiredOffset, requiredLength, totalSize);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FETCH_DATA error: {ex.Message}");
            TransferError(*callbackInfo, new NTSTATUS(unchecked((int)0xC0000001))); // STATUS_UNSUCCESSFUL
        }
    }

    /// <summary>
    /// Download blob data asynchronously (can run on any thread).
    /// Returns the full blob contents and the file size.
    /// </summary>
    private async Task<(byte[] data, long totalSize)> FetchBlobDataAsync(string nodeId)
    {
        var nodes = await _jmapClient.GetStorageNodesAsync([nodeId]);
        if (nodes.Length == 0 || nodes[0].BlobId == null)
            throw new FileNotFoundException($"Node {nodeId} not found or has no blob");

        var node = nodes[0];
        using var stream = await _jmapClient.DownloadBlobAsync(node.BlobId!, node.Type, node.Name);
        using var ms = new MemoryStream();
        await stream.CopyToAsync(ms);
        return (ms.ToArray(), node.Size);
    }

    /// <summary>
    /// Transfer downloaded data to cfapi. Must run on the callback thread.
    /// </summary>
    private static void TransferData(CF_CALLBACK_INFO callbackInfo, byte[] data, long offset, long length, long totalSize)
    {
        const int chunkSize = 4 * 1024 * 1024; // 4MB
        long totalTransferred = 0;

        // Transfer the requested range
        long dataOffset = offset; // offset within the file where data starts
        long remaining = Math.Min(length, data.Length - offset);
        while (remaining > 0)
        {
            int chunkLen = (int)Math.Min(chunkSize, remaining);
            TransferChunk(callbackInfo, data, (int)(offset + totalTransferred), dataOffset + totalTransferred, chunkLen);
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
