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
    /// <summary>
    /// The chunk size from the blobext capability, or null if not supported.
    /// Files larger than this should be uploaded in chunks.
    /// </summary>
    long? ChunkSize { get; }
    Task<string> FindHomeNodeIdAsync(CancellationToken ct = default);
    Task<string?> FindTrashNodeIdAsync(CancellationToken ct = default);
    Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default);
    Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default);
    Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default);
    Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default);
    Task<string> GetStateAsync(string homeNodeId, CancellationToken ct = default);
    Task<string> GetCurrentStateAsync(CancellationToken ct = default);
    Task<(string[] Ids, string QueryState, int Total)> QueryAllFileNodeIdsAsync(CancellationToken ct = default);
    Task<(FileNode[] Nodes, string State)> GetFileNodesByIdsPagedAsync(string[] ids, int pageSize = 1024, CancellationToken ct = default);
    Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default);
    Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default);
    Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default);
    Task<string> UploadBlobChunkedAsync(Stream data, string contentType, long totalSize,
        Action<int>? onProgress = null, CancellationToken ct = default);
    Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, string? onExists = null, CancellationToken ct = default);
    Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default);
    Task MoveFileNodeAsync(string nodeId, string parentId, string newName, string? onExists = null, CancellationToken ct = default);
    Task DestroyFileNodeAsync(string nodeId, CancellationToken ct = default);
    IAsyncEnumerable<string> WatchForChangesAsync(CancellationToken ct = default);
    /// <summary>
    /// Fetch blob metadata/data via Blob/get (RFC 9404).
    /// </summary>
    Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties,
        long? offset = null, long? length = null, CancellationToken ct = default);
}
