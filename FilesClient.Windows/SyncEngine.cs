using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FilesClient.Jmap;
using FilesClient.Jmap.Models;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FilesClient.Windows;

public class SyncEngine : IDisposable
{
    private readonly string _syncRootPath;
    private readonly IJmapClient _jmapClient;
    private readonly SyncRoot _syncRoot;
    private readonly PlaceholderManager _placeholderManager;
    private readonly SyncCallbacks _syncCallbacks;
    private readonly FileChangeWatcher _fileChangeWatcher;

    // Maps local file path → StorageNode ID (populated during sync)
    private readonly ConcurrentDictionary<string, string> _pathToNodeId = new(StringComparer.OrdinalIgnoreCase);
    // Reverse mapping: StorageNode ID → local file path (needed for delete handling)
    private readonly ConcurrentDictionary<string, string> _nodeIdToPath = new();

    public string SyncRootPath => _syncRootPath;

    public SyncEngine(string syncRootPath, IJmapClient jmapClient)
    {
        _syncRootPath = syncRootPath;
        _jmapClient = jmapClient;
        _syncRoot = new SyncRoot(syncRootPath);
        _placeholderManager = new PlaceholderManager(syncRootPath);
        _syncCallbacks = new SyncCallbacks(jmapClient);
        _fileChangeWatcher = new FileChangeWatcher(syncRootPath);
        _fileChangeWatcher.OnChanges += OnLocalFileChanges;
    }

    public async Task RegisterAndConnectAsync(string displayName, string accountId, string? iconPath = null)
    {
        await _syncRoot.RegisterAsync(displayName, "1.0", accountId, iconPath);

        var (registrations, delegates) = _syncCallbacks.CreateCallbackRegistrations();
        _syncRoot.Connect(registrations, delegates);

        _fileChangeWatcher.Start();
    }

    public async Task<string> PopulateAsync(CancellationToken ct)
    {
        // Phase 1: Fetch the entire tree from the server
        Console.WriteLine("Fetching directory tree...");
        var tree = new List<(string parentId, string localParentPath, StorageNode[] children)>();
        await FetchTreeAsync("root", _syncRootPath, tree, ct, depth: 0);

        // Phase 2: Create all placeholders in one fast pass (parent before children,
        // but each directory's children are created immediately after the directory)
        Console.WriteLine($"Creating placeholders ({tree.Sum(t => t.children.Length)} items)...");
        foreach (var (parentId, localParentPath, children) in tree)
        {
            var newChildren = children
                .Where(c => !Path.Exists(Path.Combine(localParentPath, PlaceholderManager.SanitizeName(c.Name))))
                .ToArray();

            if (newChildren.Length > 0)
                _placeholderManager.CreatePlaceholders(localParentPath, newChildren);

            foreach (var child in children)
            {
                var childPath = Path.Combine(localParentPath, PlaceholderManager.SanitizeName(child.Name));
                _pathToNodeId[childPath] = child.Id;
                _nodeIdToPath[child.Id] = childPath;
            }
        }

        return await _jmapClient.GetStateAsync(ct);
    }

    private async Task FetchTreeAsync(
        string parentId, string localParentPath,
        List<(string parentId, string localParentPath, StorageNode[] children)> tree,
        CancellationToken ct, int depth)
    {
        var children = await _jmapClient.GetChildrenAsync(parentId, ct);
        tree.Add((parentId, localParentPath, children));

        // Recurse into subdirectories (limit depth for PoC)
        if (depth < 3)
        {
            foreach (var child in children.Where(c => c.IsFolder))
            {
                var childPath = Path.Combine(localParentPath, PlaceholderManager.SanitizeName(child.Name));
                await FetchTreeAsync(child.Id, childPath, tree, ct, depth + 1);
            }
        }
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
                        var childPath = Path.Combine(parentPath, PlaceholderManager.SanitizeName(node.Name));
                        _pathToNodeId[childPath] = node.Id;
                        _nodeIdToPath[node.Id] = childPath;
                        if (!Path.Exists(childPath))
                            _placeholderManager.CreatePlaceholders(parentPath, [node]);
                    }
                }
            }
        }

        foreach (var destroyedId in changes.Destroyed)
        {
            if (_nodeIdToPath.TryRemove(destroyedId, out var localPath))
            {
                _pathToNodeId.TryRemove(localPath, out _);
                DeleteLocalItem(localPath);
            }
            else
            {
                Console.WriteLine($"  Destroyed: {destroyedId} (no local path mapped)");
            }
        }

        if (changes.HasMoreChanges)
            return await PollChangesAsync(changes.NewState, ct);

        return changes.NewState;
    }

    private void OnLocalFileChanges(FileChangeWatcher.FileChange[] changes)
    {
        // Process uploads on a background thread (fire-and-forget from the timer callback)
        _ = Task.Run(async () =>
        {
            foreach (var change in changes)
            {
                try
                {
                    await ProcessLocalChangeAsync(change);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Upload error for {change.FullPath}: {ex.Message}");
                }
            }
        });
    }

    private async Task ProcessLocalChangeAsync(FileChangeWatcher.FileChange change)
    {
        // Handle renames separately — the file may still be a placeholder
        if (change.Kind == FileChangeWatcher.ChangeKind.Renamed)
        {
            await ProcessRenameAsync(change);
            return;
        }

        if (!File.Exists(change.FullPath))
            return;

        var fileName = Path.GetFileName(change.FullPath);
        var parentDir = Path.GetDirectoryName(change.FullPath)!;

        // Determine MIME type from extension
        var ext = Path.GetExtension(change.FullPath).ToLowerInvariant();
        var contentType = ext switch
        {
            ".txt" => "text/plain",
            ".html" or ".htm" => "text/html",
            ".css" => "text/css",
            ".js" => "application/javascript",
            ".json" => "application/json",
            ".xml" => "application/xml",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".pdf" => "application/pdf",
            ".zip" => "application/zip",
            _ => "application/octet-stream",
        };

        if (_pathToNodeId.TryGetValue(change.FullPath, out var existingNodeId))
        {
            // Skip re-upload if this file was just hydrated by cfapi
            if (_syncCallbacks.RecentlyHydrated.TryRemove(existingNodeId, out _))
            {
                Console.WriteLine($"Skipping re-upload of recently hydrated file: {fileName}");
                return;
            }

            // Modified existing file — upload new blob and update the StorageNode
            Console.WriteLine($"Uploading modified file: {fileName}");
            using var stream = new FileStream(change.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var blobId = await _jmapClient.UploadBlobAsync(stream, contentType);
            await _jmapClient.UpdateStorageNodeBlobAsync(existingNodeId, blobId);
            StripZoneIdentifier(change.FullPath);
            SetInSync(change.FullPath);
            Console.WriteLine($"Updated: {fileName} → blob {blobId}");
        }
        else
        {
            // New file — upload blob, create StorageNode, convert to placeholder
            var parentId = ResolveParentNodeId(parentDir);
            if (parentId == null)
            {
                Console.Error.WriteLine($"Cannot upload {fileName}: parent folder not mapped");
                return;
            }

            Console.WriteLine($"Uploading new file: {fileName}");
            using var stream = new FileStream(change.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var blobId = await _jmapClient.UploadBlobAsync(stream, contentType);
            var node = await _jmapClient.CreateStorageNodeAsync(parentId, blobId, fileName, contentType);

            // Convert the regular file to a cloud placeholder
            ConvertToPlaceholder(change.FullPath, node.Id);
            _pathToNodeId[change.FullPath] = node.Id;
            _nodeIdToPath[node.Id] = change.FullPath;
            StripZoneIdentifier(change.FullPath);
            SetInSync(change.FullPath);
            Console.WriteLine($"Created: {fileName} → node {node.Id}");
        }
    }

    private async Task ProcessRenameAsync(FileChangeWatcher.FileChange change)
    {
        if (change.OldFullPath == null)
            return;

        if (!_pathToNodeId.TryGetValue(change.OldFullPath, out var nodeId))
        {
            Console.Error.WriteLine($"Rename: old path not mapped: {change.OldFullPath}");
            return;
        }

        var newName = Path.GetFileName(change.FullPath);
        Console.WriteLine($"Renaming node {nodeId}: {Path.GetFileName(change.OldFullPath)} → {newName}");

        await _jmapClient.RenameStorageNodeAsync(nodeId, newName);

        // Update local path mappings
        _pathToNodeId.TryRemove(change.OldFullPath, out _);
        _pathToNodeId[change.FullPath] = nodeId;
        _nodeIdToPath[nodeId] = change.FullPath;

        Console.WriteLine($"Renamed: {newName} (node {nodeId})");
    }

    private string? ResolveParentNodeId(string localPath)
    {
        if (string.Equals(localPath, _syncRootPath, StringComparison.OrdinalIgnoreCase))
            return "root";
        return _pathToNodeId.TryGetValue(localPath, out var id) ? id : null;
    }

    private static void DeleteLocalItem(string localPath)
    {
        try
        {
            if (Directory.Exists(localPath))
            {
                Directory.Delete(localPath, recursive: true);
                Console.WriteLine($"  Deleted folder: {localPath}");
            }
            else if (File.Exists(localPath))
            {
                File.Delete(localPath);
                Console.WriteLine($"  Deleted file: {localPath}");
            }
            else
            {
                Console.WriteLine($"  Already gone: {localPath}");
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"  Failed to delete {localPath}: {ex.Message}");
        }
    }

    private static unsafe void ConvertToPlaceholder(string filePath, string nodeId)
    {
        var identityBytes = Encoding.UTF8.GetBytes(nodeId);
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        fixed (byte* pIdentity = identityBytes)
        {
            long usn = 0;
            PInvoke.CfConvertToPlaceholder(
                handle,
                pIdentity,
                (uint)identityBytes.Length,
                CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC,
                &usn,
                null).ThrowOnFailure();
        }
    }

    private static unsafe void SetInSync(string filePath)
    {
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        PInvoke.CfSetInSyncState(
            handle,
            CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
            null).ThrowOnFailure();
    }

    private static void StripZoneIdentifier(string filePath)
    {
        try { File.Delete(filePath + ":Zone.Identifier"); } catch { }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenWithRetry(string filePath)
    {
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return File.OpenHandle(filePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete);
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
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
        return parentPath != null ? Path.Combine(parentPath, PlaceholderManager.SanitizeName(node.Name)) : null;
    }

    public void Dispose()
    {
        _fileChangeWatcher.Dispose();
        _syncRoot.Dispose();
    }
}
