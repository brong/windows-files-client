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
        _syncCallbacks.OnRenameCompleted += OnPlaceholderRenamed;
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
        await FetchTreeAsync("root", _syncRootPath, tree, ct);

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

                // Mark pre-existing items as in-sync (handles stale state from previous runs)
                if (Path.Exists(childPath) && !newChildren.Contains(child))
                {
                    try { SetInSync(childPath); }
                    catch { /* not a placeholder — ignore */ }
                }
            }
        }

        // Phase 3: Mark populated directories as ALWAYS_FULL so cfapi can
        // recursively hydrate pinned folders.  Each tree entry's localParentPath
        // has had all its children created, so it's safe to mark it now.
        // Skip the sync root itself (not a placeholder).
        foreach (var (_, localParentPath, _) in tree)
        {
            if (string.Equals(localParentPath, _syncRootPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try { MarkDirectoryAlwaysFull(localParentPath); }
            catch { /* directory might not be a placeholder */ }
        }

        return await _jmapClient.GetStateAsync(ct);
    }

    private async Task FetchTreeAsync(
        string parentId, string localParentPath,
        List<(string parentId, string localParentPath, StorageNode[] children)> tree,
        CancellationToken ct)
    {
        var children = await _jmapClient.GetChildrenAsync(parentId, ct);
        tree.Add((parentId, localParentPath, children));

        foreach (var child in children.Where(c => c.IsFolder))
        {
            var childPath = Path.Combine(localParentPath, PlaceholderManager.SanitizeName(child.Name));
            await FetchTreeAsync(child.Id, childPath, tree, ct);
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
            var createdSet = new HashSet<string>(changes.Created);

            // Process updated nodes first — sort shallowest-first by existing path
            // to handle parent renames before children
            var updatedNodes = nodes
                .Where(n => !createdSet.Contains(n.Id))
                .OrderBy(n => _nodeIdToPath.TryGetValue(n.Id, out var p)
                    ? p.Count(ch => ch == Path.DirectorySeparatorChar)
                    : int.MaxValue)
                .ToList();

            foreach (var node in updatedNodes)
            {
                if (node.ParentId == null)
                    continue;

                var oldPath = _nodeIdToPath.GetValueOrDefault(node.Id);
                var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);
                if (parentPath == null)
                    continue;

                var newPath = Path.Combine(parentPath, PlaceholderManager.SanitizeName(node.Name));

                if (oldPath != null && !string.Equals(oldPath, newPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Name or parent changed — rename on disk
                    // Update mappings FIRST so the file watcher echo finds the new path
                    // and skips the redundant server call
                    var isDirectory = Directory.Exists(oldPath);
                    if (isDirectory)
                        UpdateDescendantMappings(oldPath, newPath);

                    _pathToNodeId.TryRemove(oldPath, out _);
                    _pathToNodeId[newPath] = node.Id;
                    _nodeIdToPath[node.Id] = newPath;

                    try
                    {
                        if (isDirectory)
                        {
                            Directory.Move(oldPath, newPath);
                            Console.WriteLine($"  Renamed folder: {oldPath} → {newPath}");
                        }
                        else if (File.Exists(oldPath))
                        {
                            File.Move(oldPath, newPath);
                            Console.WriteLine($"  Renamed file: {oldPath} → {newPath}");
                        }
                        // Re-mark as in-sync after move (cfapi may clear in-sync on rename)
                        SetInSync(newPath);
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Failed to rename {oldPath} → {newPath}: {ex.Message}");
                    }
                }
                else
                {
                    // No rename — just ensure mappings are up-to-date
                    _pathToNodeId[newPath] = node.Id;
                    _nodeIdToPath[node.Id] = newPath;
                    if (!Path.Exists(newPath))
                        _placeholderManager.CreatePlaceholders(parentPath, [node]);
                    else
                    {
                        try { SetInSync(newPath); }
                        catch { /* not a placeholder — ignore */ }
                    }
                }
            }

            // Process created nodes — create placeholders for new items
            var createdNodes = nodes.Where(n => createdSet.Contains(n.Id));
            foreach (var node in createdNodes)
            {
                if (node.ParentId == null)
                    continue;

                var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);
                if (parentPath == null)
                    continue;

                var childPath = Path.Combine(parentPath, PlaceholderManager.SanitizeName(node.Name));
                _pathToNodeId[childPath] = node.Id;
                _nodeIdToPath[node.Id] = childPath;
                if (!Path.Exists(childPath))
                    _placeholderManager.CreatePlaceholders(parentPath, [node]);
                else
                {
                    try { SetInSync(childPath); }
                    catch { /* not a placeholder — ignore */ }
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
            // Sort renames shallowest-first (by depth of old path) to handle
            // parent renames before children
            var renames = changes
                .Where(c => c.Kind == FileChangeWatcher.ChangeKind.Renamed)
                .OrderBy(c => c.OldFullPath?.Count(ch => ch == Path.DirectorySeparatorChar) ?? 0)
                .ToList();
            // Creates and modifies in original order
            var createsAndModifies = changes
                .Where(c => c.Kind == FileChangeWatcher.ChangeKind.Created
                          || c.Kind == FileChangeWatcher.ChangeKind.Modified)
                .ToList();
            // Deletes deepest-first so children are destroyed before parents
            var deletes = changes
                .Where(c => c.Kind == FileChangeWatcher.ChangeKind.Deleted)
                .OrderByDescending(c => c.FullPath.Count(ch => ch == Path.DirectorySeparatorChar))
                .ToList();

            // Process renames first, then creates/modifies, then deletes
            foreach (var change in renames.Concat(createsAndModifies).Concat(deletes))
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

        // Handle deletes
        if (change.Kind == FileChangeWatcher.ChangeKind.Deleted)
        {
            await ProcessDeleteAsync(change);
            return;
        }

        // Handle new directories
        if (Directory.Exists(change.FullPath))
        {
            await ProcessNewFolderAsync(change);
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

            // Modified existing file — content is immutable, so destroy+create atomically
            var parentId = ResolveParentNodeId(parentDir);
            if (parentId == null)
            {
                Console.Error.WriteLine($"Cannot update {fileName}: parent folder not mapped");
                return;
            }

            Console.WriteLine($"Uploading modified file: {fileName}");
            using var stream = new FileStream(change.FullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var blobId = await _jmapClient.UploadBlobAsync(stream, contentType);
            var newNode = await _jmapClient.ReplaceStorageNodeBlobAsync(existingNodeId, parentId, fileName, blobId, contentType);

            // Update cfapi placeholder identity with new node ID
            UpdatePlaceholderIdentity(change.FullPath, newNode.Id);
            _nodeIdToPath.TryRemove(existingNodeId, out _);
            _pathToNodeId[change.FullPath] = newNode.Id;
            _nodeIdToPath[newNode.Id] = change.FullPath;
            StripZoneIdentifier(change.FullPath);
            Console.WriteLine($"Updated: {fileName} → node {newNode.Id} (blob {blobId})");
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

        // Primary lookup: old path in our dictionary
        string? nodeId = null;
        if (!_pathToNodeId.TryGetValue(change.OldFullPath, out nodeId))
        {
            // Fallback: read the node ID from cfapi placeholder identity on the new path
            nodeId = ReadPlaceholderNodeId(change.FullPath);
            if (nodeId == null)
            {
                Console.Error.WriteLine($"Rename: cannot resolve node ID for: {change.OldFullPath}");
                return;
            }
        }

        // Echo check: if this rename was initiated by PollChangesAsync,
        // the mapping already points to the new path — skip the server call
        if (_nodeIdToPath.TryGetValue(nodeId, out var mappedPath) &&
            string.Equals(mappedPath, change.FullPath, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"Rename: skipping echo for {Path.GetFileName(change.FullPath)} (node {nodeId})");
            return;
        }

        var newName = Path.GetFileName(change.FullPath);
        var parentDir = Path.GetDirectoryName(change.FullPath)!;
        var parentId = ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            Console.Error.WriteLine($"Rename: cannot resolve parent for {change.FullPath}");
            return;
        }

        Console.WriteLine($"Moving node {nodeId}: {Path.GetFileName(change.OldFullPath)} → {parentId}/{newName}");

        await _jmapClient.MoveStorageNodeAsync(nodeId, parentId, newName);

        // Update descendant mappings first if this is a directory
        if (Directory.Exists(change.FullPath))
            UpdateDescendantMappings(change.OldFullPath, change.FullPath);

        // Update the renamed item's own mapping
        _pathToNodeId.TryRemove(change.OldFullPath, out _);
        _pathToNodeId[change.FullPath] = nodeId;
        _nodeIdToPath[nodeId] = change.FullPath;

        Console.WriteLine($"Renamed: {newName} (node {nodeId})");
    }

    private async Task ProcessDeleteAsync(FileChangeWatcher.FileChange change)
    {
        // Look up the node ID for this path; if not found, it's an echo from
        // a server-side destroy (PollChangesAsync already removed the mapping)
        // or a file we never knew about
        if (!_pathToNodeId.TryGetValue(change.FullPath, out var nodeId))
        {
            Console.WriteLine($"Delete: skipping unknown or echo path: {change.FullPath}");
            return;
        }

        Console.WriteLine($"Destroying node {nodeId} for deleted path: {change.FullPath}");
        await _jmapClient.DestroyStorageNodeAsync(nodeId);

        // Clean up descendant mappings if this was a directory
        var prefix = change.FullPath + Path.DirectorySeparatorChar;
        var descendants = _pathToNodeId
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var (descPath, descNodeId) in descendants)
        {
            _pathToNodeId.TryRemove(descPath, out _);
            _nodeIdToPath.TryRemove(descNodeId, out _);
        }

        _pathToNodeId.TryRemove(change.FullPath, out _);
        _nodeIdToPath.TryRemove(nodeId, out _);
    }

    private async Task ProcessNewFolderAsync(FileChangeWatcher.FileChange change)
    {
        // If already mapped, this is an echo from a server-side create
        if (_pathToNodeId.ContainsKey(change.FullPath))
        {
            Console.WriteLine($"New folder: skipping echo for {change.FullPath}");
            return;
        }

        var folderName = Path.GetFileName(change.FullPath);
        var parentDir = Path.GetDirectoryName(change.FullPath)!;
        var parentId = ResolveParentNodeId(parentDir);
        if (parentId == null)
        {
            Console.Error.WriteLine($"Cannot create folder {folderName}: parent not mapped");
            return;
        }

        Console.WriteLine($"Creating folder on server: {folderName}");
        var node = await _jmapClient.CreateStorageNodeAsync(parentId, null, folderName);

        ConvertToPlaceholder(change.FullPath, node.Id, isDirectory: true);
        _pathToNodeId[change.FullPath] = node.Id;
        _nodeIdToPath[node.Id] = change.FullPath;
        Console.WriteLine($"Created folder: {folderName} → node {node.Id}");
    }

    private void OnPlaceholderRenamed(SyncCallbacks.RenameCompletedInfo info)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                if (info.NodeId == null)
                {
                    Console.Error.WriteLine($"RENAME_COMPLETION: no node ID for {info.OldFullPath} → {info.NewFullPath}");
                    return;
                }

                // Echo check: if mappings already point to the new path, this rename
                // was initiated by PollChangesAsync — skip the server call
                if (_nodeIdToPath.TryGetValue(info.NodeId, out var mappedPath) &&
                    string.Equals(mappedPath, info.NewFullPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.WriteLine($"RENAME_COMPLETION: skipping echo for {info.NodeId}");
                    return;
                }

                // Moves out of the sync root are not supported yet
                if (!info.NewFullPath.StartsWith(_syncRootPath, StringComparison.OrdinalIgnoreCase))
                {
                    Console.Error.WriteLine($"RENAME_COMPLETION: moved out of sync root, skipping: {info.NewFullPath}");
                    return;
                }

                var parentDir = Path.GetDirectoryName(info.NewFullPath)!;
                var parentId = ResolveParentNodeId(parentDir);
                if (parentId == null)
                {
                    Console.Error.WriteLine($"RENAME_COMPLETION: cannot resolve parent for {info.NewFullPath}");
                    return;
                }

                var newName = Path.GetFileName(info.NewFullPath);
                Console.WriteLine($"RENAME_COMPLETION: moving node {info.NodeId} → {parentId}/{newName}");
                await _jmapClient.MoveStorageNodeAsync(info.NodeId, parentId, newName);

                // Update descendant mappings if this is a directory
                if (Directory.Exists(info.NewFullPath))
                    UpdateDescendantMappings(info.OldFullPath, info.NewFullPath);

                // Update the renamed item's own mapping
                _pathToNodeId.TryRemove(info.OldFullPath, out _);
                _pathToNodeId[info.NewFullPath] = info.NodeId;
                _nodeIdToPath[info.NodeId] = info.NewFullPath;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"RENAME_COMPLETION error: {ex.Message}");
            }
        });
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

    private static unsafe void ConvertToPlaceholder(string filePath, string nodeId, bool isDirectory = false)
    {
        var identityBytes = Encoding.UTF8.GetBytes(nodeId);
        using var safeHandle = OpenWithRetry(filePath, isDirectory);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        fixed (byte* pIdentity = identityBytes)
        {
            var flags = CF_CONVERT_FLAGS.CF_CONVERT_FLAG_MARK_IN_SYNC;
            if (isDirectory)
                flags |= CF_CONVERT_FLAGS.CF_CONVERT_FLAG_ALWAYS_FULL;

            long usn = 0;
            PInvoke.CfConvertToPlaceholder(
                handle,
                pIdentity,
                (uint)identityBytes.Length,
                flags,
                &usn,
                null).ThrowOnFailure();
        }
    }

    private static unsafe void UpdatePlaceholderIdentity(string filePath, string newNodeId)
    {
        var identityBytes = Encoding.UTF8.GetBytes(newNodeId);
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        fixed (byte* pIdentity = identityBytes)
        {
            long usn = 0;
            PInvoke.CfUpdatePlaceholder(
                handle,
                null,   // no metadata update
                pIdentity,
                (uint)identityBytes.Length,
                null,   // no dehydrate range
                0,
                CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
                &usn,
                null).ThrowOnFailure();
        }
    }

    private static unsafe void MarkDirectoryAlwaysFull(string dirPath)
    {
        using var safeHandle = OpenWithRetry(dirPath, isDirectory: true);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        long usn = 0;
        PInvoke.CfUpdatePlaceholder(
            handle,
            null,   // no metadata update
            null,   // keep existing identity
            0,
            null,   // no dehydrate range
            0,
            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC
                | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ENABLE_ON_DEMAND_POPULATION
                | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_ALWAYS_FULL,
            &usn,
            null).ThrowOnFailure();
    }

    private static unsafe void SetInSync(string path)
    {
        var isDirectory = Directory.Exists(path);
        using var safeHandle = OpenWithRetry(path, isDirectory);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        PInvoke.CfSetInSyncState(
            handle,
            CF_IN_SYNC_STATE.CF_IN_SYNC_STATE_IN_SYNC,
            CF_SET_IN_SYNC_FLAGS.CF_SET_IN_SYNC_FLAG_NONE,
            null).ThrowOnFailure();
    }

    private static unsafe string? ReadPlaceholderNodeId(string path)
    {
        try
        {
            var options = Directory.Exists(path) ? (FileOptions)0x02000000 : FileOptions.None;
            using var safeHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, options);
            var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());

            var buffer = new byte[256];
            fixed (byte* pBuffer = buffer)
            {
                uint returnedLen;
                var hr = PInvoke.CfGetPlaceholderInfo(
                    handle,
                    CF_PLACEHOLDER_INFO_CLASS.CF_PLACEHOLDER_INFO_BASIC,
                    pBuffer,
                    (uint)buffer.Length,
                    &returnedLen);

                if (hr.Failed)
                    return null;

                // CF_PLACEHOLDER_BASIC_INFO layout:
                //   PinState (int, offset 0)
                //   InSyncState (int, offset 4)
                //   FileId (long, offset 8)
                //   SyncRootFileId (long, offset 16)
                //   FileIdentityLength (uint, offset 24)
                //   FileIdentity (byte[], offset 28)
                const int fileIdentityLengthOffset = 24;
                const int fileIdentityOffset = 28;

                if (returnedLen < (uint)fileIdentityOffset)
                    return null;

                var identityLength = *(uint*)(pBuffer + fileIdentityLengthOffset);
                if (identityLength == 0 || returnedLen < (uint)fileIdentityOffset + identityLength)
                    return null;

                return Encoding.UTF8.GetString(pBuffer + fileIdentityOffset, (int)identityLength);
            }
        }
        catch
        {
            return null;
        }
    }

    private void UpdateDescendantMappings(string oldDirPath, string newDirPath)
    {
        var oldPrefix = oldDirPath + Path.DirectorySeparatorChar;

        // Collect entries to update (can't modify dictionary while enumerating)
        var toUpdate = new List<(string oldPath, string nodeId)>();
        foreach (var kvp in _pathToNodeId)
        {
            if (kvp.Key.StartsWith(oldPrefix, StringComparison.OrdinalIgnoreCase))
                toUpdate.Add((kvp.Key, kvp.Value));
        }

        foreach (var (oldPath, nodeId) in toUpdate)
        {
            var newPath = newDirPath + oldPath.Substring(oldDirPath.Length);
            _pathToNodeId.TryRemove(oldPath, out _);
            _pathToNodeId[newPath] = nodeId;
            _nodeIdToPath[nodeId] = newPath;
        }

        // Update the directory's own entry
        if (_pathToNodeId.TryRemove(oldDirPath, out var dirNodeId))
        {
            _pathToNodeId[newDirPath] = dirNodeId;
            _nodeIdToPath[dirNodeId] = newDirPath;
        }
    }

    private static void StripZoneIdentifier(string filePath)
    {
        try { File.Delete(filePath + ":Zone.Identifier"); } catch { }
    }

    private static Microsoft.Win32.SafeHandles.SafeFileHandle OpenWithRetry(string filePath, bool isDirectory = false)
    {
        const int maxRetries = 5;
        // FILE_FLAG_BACKUP_SEMANTICS is required to open a directory handle
        var options = isDirectory ? (FileOptions)0x02000000 : FileOptions.None;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                return File.OpenHandle(filePath, FileMode.Open, FileAccess.ReadWrite,
                    FileShare.ReadWrite | FileShare.Delete, options);
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
