using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public interface IJmapClient : IDisposable
{
    string AccountId { get; }
    string Username { get; }
    Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default);
    Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default);
    Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default);
    Task<string> GetStateAsync(CancellationToken ct = default);
    Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default);
    Task<string> UploadBlobAsync(Stream data, string contentType, CancellationToken ct = default);
    Task<StorageNode> CreateStorageNodeAsync(string parentId, string? blobId, string name, string? type = null, CancellationToken ct = default);
    Task<StorageNode> ReplaceStorageNodeBlobAsync(string nodeId, string parentId, string name, string blobId, string? type = null, CancellationToken ct = default);
    Task MoveStorageNodeAsync(string nodeId, string parentId, string newName, CancellationToken ct = default);
    Task DestroyStorageNodeAsync(string nodeId, CancellationToken ct = default);
    IAsyncEnumerable<string> WatchForChangesAsync(CancellationToken ct = default);
}
