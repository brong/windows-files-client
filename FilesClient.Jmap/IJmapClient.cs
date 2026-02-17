using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public interface IJmapClient : IDisposable
{
    JmapContext Context { get; }
    string AccountId { get; }
    string Username { get; }
    /// <summary>
    /// The first digest algorithm from the server's supportedDigestAlgorithms
    /// that we support locally (sha or sha-256), or null if blob capability not present.
    /// </summary>
    string? PreferredDigestAlgorithm { get; }
    Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default);
    Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default);
    Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default);
    Task<(ChangesResponse Changes, StorageNode[] Created, StorageNode[] Updated)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default);
    Task<string> GetStateAsync(CancellationToken ct = default);
    Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default);
    Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default);
    Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default);
    Task<StorageNode> CreateStorageNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default);
    Task<StorageNode> ReplaceStorageNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default);
    Task MoveStorageNodeAsync(string nodeId, string parentId, string newName, CancellationToken ct = default);
    Task DestroyStorageNodeAsync(string nodeId, CancellationToken ct = default);
    IAsyncEnumerable<string> WatchForChangesAsync(CancellationToken ct = default);
    /// <summary>
    /// Fetch blob metadata/data via Blob/get (RFC 9404).
    /// </summary>
    Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties,
        long? offset = null, long? length = null, CancellationToken ct = default);
}
