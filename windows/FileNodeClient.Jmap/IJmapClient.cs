using FileNodeClient.Jmap.Models;

namespace FileNodeClient.Jmap;

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
    /// <summary>
    /// Max number of data sources in a single Blob/upload combine request,
    /// from urn:ietf:params:jmap:blob capability. Null if not reported.
    /// </summary>
    int? MaxDataSources { get; }
    /// <summary>
    /// Max total size of a Blob/upload combine request, from urn:ietf:params:jmap:blob.
    /// Files larger than this cannot be uploaded via chunked combine.
    /// </summary>
    long? MaxSizeBlobSet { get; }
    /// <summary>
    /// Whether the server supports Blob/convert (blobext capability).
    /// </summary>
    bool HasBlobConvert { get; }
    /// <summary>
    /// URL to the provider's trash view on the web, or null if not available.
    /// </summary>
    string? TrashUrl { get; }
    /// <summary>
    /// URL template for viewing a file online, with {nodeId} placeholder.
    /// </summary>
    string? WebUrlTemplate { get; }
    Task<string> FindHomeNodeIdAsync(CancellationToken ct = default);
    Task<string?> FindTrashNodeIdAsync(CancellationToken ct = default);
    Task<FileNode[]> GetFileNodesAsync(string[] ids, CancellationToken ct = default);
    Task<FileNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default);
    Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default);
    Task<(ChangesResponse Changes, FileNode[] Created, FileNode[] Updated, Quota[]? Quotas)>
        GetChangesAndNodesAsync(string sinceState, CancellationToken ct = default);
    Task<string> GetStateAsync(string homeNodeId, CancellationToken ct = default);
    Task<string> GetCurrentStateAsync(CancellationToken ct = default);
    Task<(string[] Ids, string QueryState, int Total)> QueryAllFileNodeIdsAsync(CancellationToken ct = default);
    Task<(FileNode[] Nodes, string State)> GetFileNodesByIdsPagedAsync(string[] ids, int pageSize = 0, CancellationToken ct = default);
    Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default);
    Task<(Stream data, bool isPartial)> DownloadBlobRangeAsync(string blobId, long offset, long length, string? type = null, string? name = null, CancellationToken ct = default);
    Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default);
    Task<string> UploadBlobChunkedAsync(Stream data, string contentType, long totalSize,
        Action<int>? onProgress = null, Action<JmapClient.UploadedChunkInfo>? onChunkUploaded = null,
        List<JmapClient.UploadedChunkInfo>? previousChunks = null,
        CancellationToken ct = default);
    Task<FileNode> CreateFileNodeAsync(string parentId, string? blobId, string name, string? type = null, string? onExists = null, DateTime? createdAt = null, DateTime? modifiedAt = null, CancellationToken ct = default);
    Task<FileNode> ReplaceFileNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, DateTime? createdAt = null, DateTime? modifiedAt = null, CancellationToken ct = default);
    Task MoveFileNodeAsync(string nodeId, string parentId, string newName, string? onExists = null, CancellationToken ct = default);
    Task DestroyFileNodeAsync(string nodeId, CancellationToken ct = default);
    IAsyncEnumerable<string> WatchForChangesAsync(CancellationToken ct = default);
    /// <summary>
    /// Fetch blob metadata/data via Blob/get (RFC 9404).
    /// </summary>
    Task<BlobDataItem> GetBlobAsync(string blobId, string[] properties,
        long? offset = null, long? length = null, CancellationToken ct = default);
    Task<Quota[]> GetQuotasAsync(CancellationToken ct = default);
    Task<string> ConvertImageAsync(string blobId, uint width, uint height,
        string mimeType = "image/png", CancellationToken ct = default);
    /// <summary>
    /// Batch-convert multiple images in a single Blob/convert request.
    /// Returns a dictionary mapping each input blobId to the converted thumbnail blobId.
    /// Failed conversions are omitted from the result.
    /// </summary>
    Task<Dictionary<string, string>> ConvertImagesAsync(
        IReadOnlyList<(string BlobId, uint Width, uint Height)> items,
        string mimeType = "image/png", CancellationToken ct = default);
}
