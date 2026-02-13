using FilesClient.Jmap;
using FilesClient.Jmap.Models;

namespace FilesClient.Windows;

public class SyncEngine : IDisposable
{
    private readonly string _syncRootPath;
    private readonly JmapClient _jmapClient;
    private readonly SyncRoot _syncRoot;
    private readonly PlaceholderManager _placeholderManager;
    private readonly SyncCallbacks _syncCallbacks;

    public string SyncRootPath => _syncRootPath;

    public SyncEngine(string syncRootPath, JmapClient jmapClient)
    {
        _syncRootPath = syncRootPath;
        _jmapClient = jmapClient;
        _syncRoot = new SyncRoot(syncRootPath);
        _placeholderManager = new PlaceholderManager(syncRootPath);
        _syncCallbacks = new SyncCallbacks(jmapClient);
    }

    public void RegisterAndConnect()
    {
        _syncRoot.Register("Fastmail Files", "1.0");

        var (registrations, delegates) = _syncCallbacks.CreateCallbackRegistrations();
        _syncRoot.Connect(registrations, delegates);
    }

    public async Task<string> PopulateAsync(CancellationToken ct)
    {
        return await PopulateRecursiveAsync("root", _syncRootPath, ct, depth: 0);
    }

    private async Task<string> PopulateRecursiveAsync(
        string parentId, string localParentPath, CancellationToken ct, int depth)
    {
        var children = await _jmapClient.GetChildrenAsync(parentId, ct);

        if (children.Length > 0)
        {
            var newChildren = children
                .Where(c => !Path.Exists(Path.Combine(localParentPath, c.Name)))
                .ToArray();

            if (newChildren.Length > 0)
                _placeholderManager.CreatePlaceholders(localParentPath, newChildren);
        }

        // Recurse into subdirectories (limit depth for PoC)
        if (depth < 3)
        {
            foreach (var child in children.Where(c => c.IsFolder))
            {
                var childPath = Path.Combine(localParentPath, child.Name);
                Directory.CreateDirectory(childPath);
                await PopulateRecursiveAsync(child.Id, childPath, ct, depth + 1);
            }
        }

        // Get state from a StorageNode/get call
        var request = JmapRequest.Create(
            [JmapClient.StorageNodeCapability],
            ("StorageNode/get", new { accountId = _jmapClient.AccountId, ids = new[] { parentId } }, "s0"));
        var response = await _jmapClient.CallAsync(request, ct);
        return response.GetArgs<GetResponse<StorageNode>>(0).State;
    }

    public async Task<string> PollChangesAsync(string sinceState, CancellationToken ct)
    {
        var changes = await _jmapClient.GetChangesAsync(sinceState, ct);

        if (changes.Created.Length == 0 && changes.Updated.Length == 0 && changes.Destroyed.Length == 0)
            return changes.NewState;

        Console.WriteLine($"Changes: +{changes.Created.Length} ~{changes.Updated.Length} -{changes.Destroyed.Length}");

        var idsToFetch = changes.Created.Concat(changes.Updated).ToArray();
        if (idsToFetch.Length > 0)
        {
            var nodes = await _jmapClient.GetStorageNodesAsync(idsToFetch, ct);
            foreach (var node in nodes)
            {
                if (node.ParentId != null)
                {
                    var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);
                    if (parentPath != null)
                    {
                        var childPath = Path.Combine(parentPath, node.Name);
                        if (!Path.Exists(childPath))
                            _placeholderManager.CreatePlaceholders(parentPath, [node]);
                    }
                }
            }
        }

        foreach (var destroyedId in changes.Destroyed)
            Console.WriteLine($"  Destroyed: {destroyedId} (removal not yet implemented)");

        if (changes.HasMoreChanges)
            return await PollChangesAsync(changes.NewState, ct);

        return changes.NewState;
    }

    private async Task<string?> ResolveLocalPathAsync(string nodeId, CancellationToken ct)
    {
        if (nodeId == "root")
            return _syncRootPath;

        var nodes = await _jmapClient.GetStorageNodesAsync([nodeId], ct);
        if (nodes.Length == 0)
            return null;

        var node = nodes[0];
        if (node.ParentId == null)
            return _syncRootPath;

        var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);
        return parentPath != null ? Path.Combine(parentPath, node.Name) : null;
    }

    public void Dispose()
    {
        _syncRoot.Dispose();
    }
}
