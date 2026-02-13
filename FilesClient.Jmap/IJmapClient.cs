using FilesClient.Jmap.Models;

namespace FilesClient.Jmap;

public interface IJmapClient : IDisposable
{
    string AccountId { get; }
    Task<StorageNode[]> GetStorageNodesAsync(string[] ids, CancellationToken ct = default);
    Task<StorageNode[]> GetChildrenAsync(string parentId, CancellationToken ct = default);
    Task<ChangesResponse> GetChangesAsync(string sinceState, CancellationToken ct = default);
    Task<string> GetStateAsync(CancellationToken ct = default);
    Task<Stream> DownloadBlobAsync(string blobId, string? type = null, string? name = null, CancellationToken ct = default);
}
