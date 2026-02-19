using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using FilesClient.Jmap;
using FilesClient.Jmap.Models;
using Windows.Win32;
using Windows.Win32.Storage.CloudFilters;

namespace FilesClient.Windows;

public enum SyncStatus { Idle, Syncing, Error, Disconnected }

public class SyncEngine : IDisposable
{
    private readonly string _syncRootPath;
    private readonly IJmapClient _jmapClient;
    private readonly JmapQueue _queue;
    private readonly SyncRoot _syncRoot;
    private readonly PlaceholderManager _placeholderManager;
    private readonly SyncCallbacks _syncCallbacks;
    private readonly FileChangeWatcher _fileChangeWatcher;
    private readonly SyncOutbox _outbox;
    private readonly OutboxProcessor _outboxProcessor;
    private readonly string _scopeKey;

    // Maps local file path → FileNode ID (populated during sync)
    private readonly ConcurrentDictionary<string, string> _pathToNodeId = new(StringComparer.OrdinalIgnoreCase);
    // Reverse mapping: FileNode ID → local file path (needed for delete handling)
    private readonly ConcurrentDictionary<string, string> _nodeIdToPath = new();
    // Home node ID discovered during PopulateAsync
    private string _homeNodeId = null!;
    // Trash node ID for server-side recycle bin (null if not available)
    private string? _trashNodeId;
    // Directories the user has pinned ("Always keep on this device")
    // Value is a CancellationTokenSource used to cancel in-progress hydration when unpinned.
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pinnedDirectories = new(StringComparer.OrdinalIgnoreCase);
    // Folders where myRights.mayWrite is false — user cannot create/delete/rename children
    // Value is the cached FilesRights for serialization to the node cache.
    private readonly ConcurrentDictionary<string, FilesRights> _readOnlyPaths = new(StringComparer.OrdinalIgnoreCase);
    // Directories currently being hydrated by HydrateDehydratedFiles — used to
    // suppress duplicate hydration from OnDirectoryPopulated firing concurrently.
    private readonly ConcurrentDictionary<string, byte> _hydratingDirectories = new(StringComparer.OrdinalIgnoreCase);
    // Files recently uploaded — stores the LastWriteTimeUtc at upload time so we
    // can suppress the FileSystemWatcher echo that fires when
    // ConvertToPlaceholder / UpdatePlaceholderIdentity changes file attributes.
    // If the timestamp has changed by the time the echo arrives, a real edit
    // happened and we let it through.
    private readonly ConcurrentDictionary<string, DateTime> _recentlyUploaded = new(StringComparer.OrdinalIgnoreCase);

    public string SyncRootPath => _syncRootPath;
    public SyncOutbox Outbox => _outbox;

    public event Action<SyncStatus>? StatusChanged;
    public event Action<string?>? StatusDetailChanged;
    public event Action<int>? PendingCountChanged;

    private readonly ConcurrentDictionary<long, string> _activeDownloads = new();
    private string? _downloadDetail;

    /// <summary>
    /// Unregister a previous sync root for the given account and delete all
    /// local files.  Call before creating a SyncEngine instance to start fresh.
    /// </summary>
    public static void Clean(string syncRootPath, string accountId)
        => SyncRoot.Clean(syncRootPath, accountId);

    /// <summary>
    /// Detach a sync root: delete dehydrated placeholder files, remove empty
    /// directories, and unregister the sync root. Hydrated files are left in place.
    /// </summary>
    public static void Detach(string syncRootPath, string accountId)
        => SyncRoot.Detach(syncRootPath, accountId);

    private void ReportStatus(CF_SYNC_PROVIDER_STATUS status)
    {
        _syncRoot.UpdateProviderStatus(status);

        var syncStatus = status switch
        {
            CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE => SyncStatus.Idle,
            CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_POPULATE_NAMESPACE
                or CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_INCREMENTAL
                or CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_FULL
                or CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_POPULATE_CONTENT => SyncStatus.Syncing,
            CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_CONNECTIVITY_LOST => SyncStatus.Disconnected,
            CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_DISCONNECTED => SyncStatus.Disconnected,
            _ => SyncStatus.Error,
        };
        StatusChanged?.Invoke(syncStatus);
    }

    public void ReportConnectivityLost()
    {
        _outboxProcessor.SetOnline(false);
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_CONNECTIVITY_LOST);
    }

    public void ReportConnectivityRestored()
    {
        _outboxProcessor.SetOnline(true);
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
    }

    private void OnDownloadStarted(long transferKey, string fileName)
    {
        _activeDownloads[transferKey] = fileName;
        var count = _activeDownloads.Count;
        _downloadDetail = count > 1
            ? $"Downloading {fileName} (and {count - 1} more)"
            : $"Downloading {fileName}";
        ReportTransferDetail();
    }

    private void OnDownloadCompleted(long transferKey)
    {
        _activeDownloads.TryRemove(transferKey, out _);
        if (_activeDownloads.IsEmpty)
        {
            _downloadDetail = null;
        }
        else
        {
            var first = _activeDownloads.First();
            var count = _activeDownloads.Count;
            _downloadDetail = count > 1
                ? $"Downloading {first.Value} (and {count - 1} more)"
                : $"Downloading {first.Value}";
        }
        ReportTransferDetail();
    }

    private void ReportTransferDetail()
    {
        StatusDetailChanged?.Invoke(_downloadDetail);
    }

    public SyncEngine(string syncRootPath, IJmapClient jmapClient, JmapQueue queue, string scopeKey)
    {
        _syncRootPath = syncRootPath;
        _jmapClient = jmapClient;
        _queue = queue;
        _syncRoot = new SyncRoot(syncRootPath);
        _placeholderManager = new PlaceholderManager(syncRootPath);
        _syncCallbacks = new SyncCallbacks(jmapClient, queue);
        _syncCallbacks.OnDeleteRequested = HandleDeleteRequestAsync;
        _syncCallbacks.OnRenameRequested = HandleRenameRequestAsync;
        _syncCallbacks.OnDehydrateRequested = HandleDehydrateRequestAsync;
        _syncCallbacks.OnDownloadStarted += OnDownloadStarted;
        _syncCallbacks.OnDownloadCompleted += OnDownloadCompleted;
        _syncCallbacks.OnDirectoryPopulated += OnDirectoryPopulated;
        _fileChangeWatcher = new FileChangeWatcher(syncRootPath);
        _fileChangeWatcher.OnChanges += OnLocalFileChanges;
        _fileChangeWatcher.OnDirectoryPinned += OnDirectoryPinned;
        _fileChangeWatcher.OnFilePinned += OnFilePinned;
        _fileChangeWatcher.OnFileUnpinned += OnFileUnpinned;
        _scopeKey = scopeKey;
        _outbox = new SyncOutbox(scopeKey);
        _outbox.Load();
        _outbox.PendingCountChanged += count => PendingCountChanged?.Invoke(count);
        _outboxProcessor = new OutboxProcessor(_outbox, this, jmapClient, queue);
    }

    public async Task RegisterAsync(string displayName, string accountId, string? iconPath = null)
    {
        await _syncRoot.RegisterAsync(displayName, "1.0", accountId, iconPath);
    }

    public void Connect()
    {
        var (registrations, delegates) = _syncCallbacks.CreateCallbackRegistrations();
        _syncRoot.Connect(registrations, delegates);

        _fileChangeWatcher.Start();
        _outboxProcessor.Start();
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
    }

    public async Task<string> PopulateAsync(CancellationToken ct)
    {
        Console.WriteLine($"PopulateAsync: scopeKey={_scopeKey}, syncRoot={_syncRootPath}");

        // Try warm start from cache
        var cache = NodeCache.Load(_scopeKey);
        if (cache != null)
        {
            Console.WriteLine($"Cache found: {cache.Entries.Count} entries, state={cache.State}");
            try
            {
                return await PopulateFromCacheAsync(cache, ct);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Warm start failed, falling back to full fetch: {ex.Message}");
                _pathToNodeId.Clear();
                _nodeIdToPath.Clear();
                _pinnedDirectories.Clear();
                _readOnlyPaths.Clear();
            }
        }
        else
        {
            Console.WriteLine("No cache found, starting full fetch");
        }

        // Full fetch (cold start)
        return await PopulateFullAsync(ct);
    }

    private async Task<string> PopulateFullAsync(CancellationToken ct)
    {
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_POPULATE_NAMESPACE);

        // Discover home node
        _homeNodeId = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.FindHomeNodeIdAsync(ct), ct);
        Console.WriteLine($"Home node: {_homeNodeId}");

        // Discover trash node
        _trashNodeId = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.FindTrashNodeIdAsync(ct), ct);
        Console.WriteLine($"Trash node: {_trashNodeId ?? "(none)"}");
        _outboxProcessor.SetTrashNodeId(_trashNodeId);

        // Phase 1: Bulk fetch all FileNode IDs, then all nodes in pages
        Console.WriteLine("Fetching all FileNode IDs...");
        var (allIds, _, total) = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.QueryAllFileNodeIdsAsync(ct), ct);
        Console.WriteLine($"Found {allIds.Length} FileNodes (total: {total})");

        Console.WriteLine("Fetching FileNode details...");
        var (allNodes, state) = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.GetFileNodesByIdsPagedAsync(allIds, 1024, ct), ct);
        Console.WriteLine($"Fetched {allNodes.Length} FileNodes, state: {state}");

        BuildTreeAndCreatePlaceholders(allNodes);

        // Track permissions on the home node (sync root folder itself)
        var homeNode = allNodes.FirstOrDefault(n => n.Id == _homeNodeId);
        if (homeNode != null)
        {
            TrackFolderPermissions(homeNode, _syncRootPath);
            ApplyWriteProtection(_syncRootPath);
        }

        SaveNodeCache(state);
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
        return state;
    }

    private void BuildTreeAndCreatePlaceholders(FileNode[] allNodes)
    {
        // Build tree client-side: group by parentId, BFS from home node
        var childrenByParent = allNodes
            .Where(n => n.ParentId != null)
            .GroupBy(n => n.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var tree = new List<(string parentId, string localParentPath, FileNode[] children)>();
        var bfsQueue = new Queue<(string nodeId, string localPath)>();
        bfsQueue.Enqueue((_homeNodeId, _syncRootPath));

        while (bfsQueue.Count > 0)
        {
            var (parentId, localParentPath) = bfsQueue.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var children))
                children = [];

            tree.Add((parentId, localParentPath, children));

            foreach (var child in children.Where(c => c.IsFolder))
            {
                var childPath = Path.Combine(localParentPath, PlaceholderManager.SanitizeName(child.Name));
                bfsQueue.Enqueue((child.Id, childPath));
            }
        }

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
                TrackFolderPermissions(child, childPath);

                // Ensure pre-existing items are proper placeholders and in-sync
                if (Path.Exists(childPath) && !newChildren.Contains(child))
                {
                    try { SetInSync(childPath); }
                    catch
                    {
                        // Check if it's already a placeholder — if so, just set in-sync failed
                        // for another reason (e.g. sync root not connected yet); skip it.
                        if (ReadPlaceholderNodeId(childPath) != null)
                        {
                            // Already a placeholder — nothing more to do
                        }
                        else
                        {
                            // Not a placeholder yet — convert it
                            try { ConvertToPlaceholder(childPath, child.Id, child.IsFolder); }
                            catch (Exception ex)
                            {
                                Console.Error.WriteLine($"  Convert failed for {child.Name}: {ex.Message}");
                            }
                        }
                    }
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
            catch
            {
                if (ReadPlaceholderNodeId(localParentPath) != null)
                {
                    try { SetInSync(localParentPath); }
                    catch { }
                }
                else
                {
                    var nodeId = _pathToNodeId.GetValueOrDefault(localParentPath);
                    if (nodeId != null)
                    {
                        try
                        {
                            ConvertToPlaceholder(localParentPath, nodeId, isDirectory: true);
                            MarkDirectoryAlwaysFull(localParentPath);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  Convert+mark failed for {localParentPath}: {ex.Message}");
                        }
                    }
                }
            }
        }

        ApplyWriteProtections();

        DetectAndHydratePinnedDirectories(tree.Select(t => t.localParentPath));
    }

    private void DetectAndHydratePinnedDirectories(IEnumerable<string> directoryPaths)
    {
        const FileAttributes pinnedFlag = (FileAttributes)0x00080000;
        foreach (var dirPath in directoryPaths)
        {
            try
            {
                var attrs = File.GetAttributes(dirPath);
                if ((attrs & pinnedFlag) != 0)
                    _pinnedDirectories.TryAdd(dirPath, new CancellationTokenSource());
            }
            catch { }
        }

        if (!_pinnedDirectories.IsEmpty)
        {
            Console.WriteLine($"Found {_pinnedDirectories.Count} pinned directories, hydrating...");
            _ = Task.Run(() =>
            {
                foreach (var kvp in _pinnedDirectories)
                {
                    try
                    {
                        int count = HydrateDehydratedFiles(kvp.Key, kvp.Value.Token);
                        if (count > 0)
                            Console.WriteLine($"Hydrated {count} files in pinned directory: {kvp.Key}");
                        try { SetInSync(kvp.Key); }
                        catch { /* directory might not be a placeholder */ }
                    }
                    catch (OperationCanceledException)
                    {
                        Console.WriteLine($"Hydration cancelled for pinned directory: {kvp.Key}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"Pin hydration error for {kvp.Key}: {ex.Message}");
                    }
                }
            });
        }
    }

    private async Task<string> PopulateFromCacheAsync(CacheSnapshot cache, CancellationToken ct)
    {
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_INCREMENTAL);
        _homeNodeId = cache.HomeNodeId;
        _trashNodeId = cache.TrashNodeId;
        _outboxProcessor.SetTrashNodeId(_trashNodeId);

        // Rebuild mappings from cache, verifying items exist on disk with matching metadata
        Console.WriteLine($"Restoring {cache.Entries.Count} mappings from cache...");
        int matchCount = 0;
        int mismatchCount = 0;
        int missingCount = 0;
        var directories = new List<string>();
        foreach (var (nodeId, entry) in cache.Entries)
        {
            var fullPath = Path.Combine(_syncRootPath, entry.Path);

            if (entry.IsFolder)
            {
                if (!Directory.Exists(fullPath))
                {
                    missingCount++;
                    continue;
                }
                directories.Add(fullPath);
                if (entry.MyRights is { MayWrite: false })
                    _readOnlyPaths[fullPath] = entry.MyRights;
                matchCount++;
            }
            else
            {
                if (!File.Exists(fullPath))
                {
                    missingCount++;
                    continue;
                }
                var info = new FileInfo(fullPath);
                if (info.Length != entry.Size || info.LastWriteTimeUtc != entry.Modified)
                {
                    // File changed while app was stopped — still restore mapping
                    // but the FileChangeWatcher will pick up the difference after Connect()
                    mismatchCount++;
                }
                else
                    matchCount++;
            }

            _nodeIdToPath[nodeId] = fullPath;
            _pathToNodeId[fullPath] = nodeId;
        }
        int fileCount = cache.Entries.Values.Count(e => !e.IsFolder) - missingCount;
        Console.WriteLine($"  {matchCount} matched, {mismatchCount} changed, {missingCount} missing");

        // If no files on disk, cache is stale — fall back to full fetch
        if (fileCount <= 0 && cache.Entries.Values.Any(e => !e.IsFolder))
            throw new InvalidOperationException("Cache stale: no file placeholders on disk");

        // Ensure directories are placeholders and marked ALWAYS_FULL
        foreach (var (dir, nodeId) in directories.Select(d => (d, _pathToNodeId[d])))
        {
            try { MarkDirectoryAlwaysFull(dir); }
            catch
            {
                // Check if it's already a placeholder — if so, just set in-sync
                if (ReadPlaceholderNodeId(dir) != null)
                {
                    try { SetInSync(dir); }
                    catch { }
                }
                else
                {
                    try
                    {
                        ConvertToPlaceholder(dir, nodeId, isDirectory: true);
                        Console.WriteLine($"  Converted directory to placeholder: {dir}");
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Convert failed for {dir}: {ex.Message}");
                    }
                }
            }
        }

        // Catch up with server
        string newState;
        Console.WriteLine($"Catching up from state {cache.State}...");
        try
        {
            newState = await PollChangesAsync(cache.State, ct);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("cannotCalculateChanges"))
        {
            Console.WriteLine("State too old, reconciling from server...");
            newState = await ReconcileFromServerAsync(ct);
        }

        ApplyWriteProtections();

        // Detect and hydrate pinned directories
        DetectAndHydratePinnedDirectories(directories);

        SaveNodeCache(newState);
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
        return newState;
    }

    private async Task<string> ReconcileFromServerAsync(CancellationToken ct)
    {
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_FULL);
        Console.WriteLine("Reconciling: fetching all server node IDs...");

        // Step 1: Fetch all alive node IDs from server
        var (serverIds, _, _) = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.QueryAllFileNodeIdsAsync(ct), ct);
        var serverIdSet = new HashSet<string>(serverIds);
        var cachedIdSet = new HashSet<string>(_nodeIdToPath.Keys);

        // Step 2: Classify
        var goneIds = new HashSet<string>(cachedIdSet);
        goneIds.ExceptWith(serverIdSet);

        Console.WriteLine($"Reconcile: {serverIds.Length} server nodes, {cachedIdSet.Count} cached, {goneIds.Count} gone");

        // Step 3: Remove gone nodes locally
        foreach (var id in goneIds)
        {
            // Skip nodes with pending local changes — outbox will handle them
            if (_outbox.HasPendingForNodeId(id))
            {
                Console.WriteLine($"  Skipping gone node {id} (pending in outbox)");
                continue;
            }

            if (_nodeIdToPath.TryRemove(id, out var localPath))
            {
                _pathToNodeId.TryRemove(localPath, out _);
                DeleteLocalItem(localPath);
            }
        }

        // Step 4: Fetch all server nodes in batches to get current data
        Console.WriteLine($"Fetching {serverIds.Length} FileNode details...");
        var (allNodes, state) = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.GetFileNodesByIdsPagedAsync(serverIds, 1024, ct), ct);
        Console.WriteLine($"Fetched {allNodes.Length} FileNodes, state: {state}");

        // Step 5: Build tree and reconcile — reuses the same BFS + placeholder logic
        // The existing mappings are already populated, so:
        //   - existing placeholder at correct path → skip (already in mappings)
        //   - existing placeholder at wrong path → rename on disk, update mappings
        //   - missing placeholder → create it
        // Nodes with pending outbox changes are skipped to avoid overwriting local edits.
        var childrenByParent = allNodes
            .Where(n => n.ParentId != null)
            .GroupBy(n => n.ParentId!)
            .ToDictionary(g => g.Key, g => g.ToArray());

        var tree = new List<(string parentId, string localParentPath, FileNode[] children)>();
        var bfsQueue = new Queue<(string nodeId, string localPath)>();
        bfsQueue.Enqueue((_homeNodeId, _syncRootPath));

        while (bfsQueue.Count > 0)
        {
            var (parentId, localParentPath) = bfsQueue.Dequeue();
            if (!childrenByParent.TryGetValue(parentId, out var children))
                children = [];

            tree.Add((parentId, localParentPath, children));

            foreach (var child in children.Where(c => c.IsFolder))
            {
                var childPath = Path.Combine(localParentPath, PlaceholderManager.SanitizeName(child.Name));
                bfsQueue.Enqueue((child.Id, childPath));
            }
        }

        foreach (var (parentId, localParentPath, children) in tree)
        {
            var newChildren = new List<FileNode>();
            foreach (var child in children)
            {
                // Skip nodes with pending local changes
                if (_outbox.HasPendingForNodeId(child.Id))
                {
                    Console.WriteLine($"  Skipping reconcile for {child.Id} (pending in outbox)");
                    continue;
                }

                var expectedPath = Path.Combine(localParentPath, PlaceholderManager.SanitizeName(child.Name));

                // Check if node was at a different path (rename/move)
                if (_nodeIdToPath.TryGetValue(child.Id, out var oldPath)
                    && !string.Equals(oldPath, expectedPath, StringComparison.OrdinalIgnoreCase))
                {
                    // Rename on disk
                    try
                    {
                        _pathToNodeId.TryRemove(oldPath, out _);
                        if (child.IsFolder && Directory.Exists(oldPath))
                        {
                            UpdateDescendantMappings(oldPath, expectedPath);
                            Directory.Move(oldPath, expectedPath);
                            Console.WriteLine($"  Reconcile renamed folder: {oldPath} → {expectedPath}");
                        }
                        else if (File.Exists(oldPath))
                        {
                            File.Move(oldPath, expectedPath);
                            Console.WriteLine($"  Reconcile renamed file: {oldPath} → {expectedPath}");
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Reconcile rename failed {oldPath} → {expectedPath}: {ex.Message}");
                    }
                }

                _pathToNodeId[expectedPath] = child.Id;
                _nodeIdToPath[child.Id] = expectedPath;
                TrackFolderPermissions(child, expectedPath);

                if (!Path.Exists(expectedPath))
                    newChildren.Add(child);
                else
                {
                    try { SetInSync(expectedPath); }
                    catch
                    {
                        if (ReadPlaceholderNodeId(expectedPath) == null)
                        {
                            try { ConvertToPlaceholder(expectedPath, child.Id, child.IsFolder); }
                            catch { }
                        }
                    }
                }
            }

            if (newChildren.Count > 0)
                _placeholderManager.CreatePlaceholders(localParentPath, newChildren.ToArray());
        }

        // Mark directories as ALWAYS_FULL
        foreach (var (_, localParentPath, _) in tree)
        {
            if (string.Equals(localParentPath, _syncRootPath, StringComparison.OrdinalIgnoreCase))
                continue;
            try { MarkDirectoryAlwaysFull(localParentPath); }
            catch
            {
                if (ReadPlaceholderNodeId(localParentPath) != null)
                {
                    try { SetInSync(localParentPath); }
                    catch { }
                }
                else
                {
                    var nodeId = _pathToNodeId.GetValueOrDefault(localParentPath);
                    if (nodeId != null)
                    {
                        try
                        {
                            ConvertToPlaceholder(localParentPath, nodeId, isDirectory: true);
                            MarkDirectoryAlwaysFull(localParentPath);
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine($"  Convert+mark failed for {localParentPath}: {ex.Message}");
                        }
                    }
                }
            }
        }

        ApplyWriteProtections();

        return state;
    }

    public async Task<string> PollChangesAsync(string sinceState, CancellationToken ct)
    {
        var (changes, createdNodes, updatedNodes) = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.GetChangesAndNodesAsync(sinceState, ct), ct);

        if (changes.Created.Length == 0 && changes.Updated.Length == 0 && changes.Destroyed.Length == 0)
        {
            SaveNodeCache(changes.NewState);
            ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
            return changes.NewState;
        }

        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_SYNC_INCREMENTAL);

        Console.WriteLine($"Changes: +{changes.Created.Length} ~{changes.Updated.Length} -{changes.Destroyed.Length}");

        // Process updated nodes first — sort shallowest-first by existing path
        // to handle parent renames before children
        var sortedUpdatedNodes = updatedNodes
            .OrderBy(n => _nodeIdToPath.TryGetValue(n.Id, out var p)
                ? p.Count(ch => ch == Path.DirectorySeparatorChar)
                : int.MaxValue)
            .ToList();

        foreach (var node in sortedUpdatedNodes)
        {
            if (node.ParentId == null)
                continue;

            // Skip server changes for items with pending local changes
            if (_outbox.HasPendingForNodeId(node.Id))
            {
                Console.WriteLine($"  Skipping update for {node.Id} (pending in outbox)");
                continue;
            }

            var oldPath = _nodeIdToPath.GetValueOrDefault(node.Id);
            var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);

            // Node moved out of home tree (e.g. to trash, or ancestor moved to trash)
            if (parentPath == null)
            {
                if (oldPath != null)
                {
                    Console.WriteLine($"  Removed from sync tree: {oldPath}");
                    // Clean up descendant mappings for folders
                    if (Directory.Exists(oldPath))
                    {
                        var prefix = oldPath + Path.DirectorySeparatorChar;
                        foreach (var (descPath, descNodeId) in _pathToNodeId
                            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                            .ToList())
                        {
                            _pathToNodeId.TryRemove(descPath, out _);
                            _nodeIdToPath.TryRemove(descNodeId, out _);
                            _readOnlyPaths.TryRemove(descPath, out _);
                        }
                    }
                    _pathToNodeId.TryRemove(oldPath, out _);
                    _nodeIdToPath.TryRemove(node.Id, out _);
                    _readOnlyPaths.TryRemove(oldPath, out _);
                    var parentDir = Path.GetDirectoryName(oldPath);
                    using (parentDir != null ? SuspendFolderProtection(parentDir) : default)
                        DeleteLocalItem(oldPath);
                }
                continue;
            }

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

                // Clean up old read-only tracking on rename
                _readOnlyPaths.TryRemove(oldPath, out _);

                try
                {
                    using (SuspendFolderProtection(Path.GetDirectoryName(oldPath)))
                    using (SuspendFolderProtection(Path.GetDirectoryName(newPath)))
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
                    }
                    // Re-mark as in-sync after move (cfapi may clear in-sync on rename)
                    SetInSync(newPath);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"  Failed to rename {oldPath} → {newPath}: {ex.Message}");
                }

                TrackFolderPermissions(node, newPath);
                ApplyWriteProtection(newPath);
            }
            else
            {
                // No rename — just ensure mappings are up-to-date
                _pathToNodeId[newPath] = node.Id;
                _nodeIdToPath[node.Id] = newPath;
                TrackFolderPermissions(node, newPath);
                if (!Path.Exists(newPath))
                {
                    using (SuspendFolderProtection(parentPath))
                        _placeholderManager.CreatePlaceholders(parentPath, [node]);
                }
                else
                {
                    try { SetInSync(newPath); }
                    catch { /* not a placeholder — ignore */ }
                }
                ApplyWriteProtection(newPath);
            }
        }

        // Process created nodes — create placeholders for new items
        foreach (var node in createdNodes)
        {
            if (node.ParentId == null)
                continue;

            if (_outbox.HasPendingForNodeId(node.Id))
            {
                Console.WriteLine($"  Skipping create for {node.Id} (pending in outbox)");
                continue;
            }

            var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);
            if (parentPath == null)
                continue;

            var childPath = Path.Combine(parentPath, PlaceholderManager.SanitizeName(node.Name));
            _pathToNodeId[childPath] = node.Id;
            _nodeIdToPath[node.Id] = childPath;
            TrackFolderPermissions(node, childPath);
            if (!Path.Exists(childPath))
            {
                using (SuspendFolderProtection(parentPath))
                    _placeholderManager.CreatePlaceholders(parentPath, [node]);

                // If this file was created under a pinned directory, hydrate it
                if (!node.IsFolder && IsUnderPinnedDirectory(childPath))
                {
                    try { HydratePlaceholder(childPath); }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"  Auto-hydration failed for {node.Name}: {ex.Message}");
                    }
                }
            }
            else
            {
                try { SetInSync(childPath); }
                catch { /* not a placeholder — ignore */ }
            }
            ApplyWriteProtection(childPath);
        }

        foreach (var destroyedId in changes.Destroyed)
        {
            if (_outbox.HasPendingForNodeId(destroyedId))
            {
                Console.WriteLine($"  Skipping destroy for {destroyedId} (pending in outbox)");
                continue;
            }

            if (_nodeIdToPath.TryRemove(destroyedId, out var localPath))
            {
                // Don't delete the local file if the outbox has a pending
                // upload for this path — the nodeId may have changed (e.g.
                // onExists:"replace" destroyed the old node) but the outbox
                // still intends to upload the user's local content.
                if (_outbox.HasPendingForPath(localPath))
                {
                    Console.WriteLine($"  Skipping delete for {destroyedId} (outbox pending for path): {localPath}");
                    continue;
                }
                _pathToNodeId.TryRemove(localPath, out _);
                _readOnlyPaths.TryRemove(localPath, out _);
                var destroyParentDir = Path.GetDirectoryName(localPath);
                using (destroyParentDir != null ? SuspendFolderProtection(destroyParentDir) : default)
                    DeleteLocalItem(localPath);
            }
            else
            {
                Console.WriteLine($"  Destroyed: {destroyedId} (no local path mapped)");
            }
        }

        if (changes.HasMoreChanges)
            return await PollChangesAsync(changes.NewState, ct);

        SaveNodeCache(changes.NewState);
        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_IDLE);
        return changes.NewState;
    }

    private void OnLocalFileChanges(FileChangeWatcher.FileChange[] changes)
    {
        foreach (var change in changes)
        {
            var isDirectory = Directory.Exists(change.FullPath);

            // Reject local changes in read-only folders
            var parentDir = Path.GetDirectoryName(change.FullPath);
            if (parentDir != null && _readOnlyPaths.ContainsKey(parentDir))
            {
                Console.WriteLine($"Ignoring local change in read-only folder: {change.FullPath}");
                try
                {
                    using (SuspendFolderProtection(parentDir))
                    {
                        if (isDirectory && Directory.Exists(change.FullPath))
                            Directory.Delete(change.FullPath, recursive: true);
                        else if (!isDirectory && File.Exists(change.FullPath))
                            File.Delete(change.FullPath);
                    }
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Failed to remove rejected local file: {ex.Message}");
                }
                continue;
            }

            // Skip echo from our own placeholder conversion/update
            if (!isDirectory && _recentlyUploaded.TryRemove(change.FullPath, out var uploadedWriteTime))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(change.FullPath) == uploadedWriteTime)
                        continue;
                }
                catch { continue; }
            }

            // Skip echo for directories already mapped (server-side create)
            if (isDirectory && _pathToNodeId.ContainsKey(change.FullPath))
                continue;

            // Skip re-enqueue if this file was just hydrated by cfapi
            if (!isDirectory && _pathToNodeId.TryGetValue(change.FullPath, out var existingNodeId)
                && _syncCallbacks.RecentlyHydrated.TryRemove(existingNodeId, out _))
                continue;

            var nodeId = _pathToNodeId.TryGetValue(change.FullPath, out var nid) ? nid : null;
            var contentType = ResolveContentType(change.FullPath);

            _outbox.EnqueueContentChange(change.FullPath, nodeId, contentType, isDirectory);
        }
    }

    private Task<bool> HandleDeleteRequestAsync(string? nodeId, string path)
    {
        // No node ID or path not in our mappings → not a tracked placeholder,
        // or an echo from PollChangesAsync which already removed the mapping.
        if (nodeId == null || !_pathToNodeId.ContainsKey(path))
        {
            Console.WriteLine($"NOTIFY_DELETE: allowing untracked/echo delete: {path}");
            return Task.FromResult(true);
        }

        // Reject delete if parent folder is read-only
        var deleteParentDir = Path.GetDirectoryName(path);
        if (deleteParentDir != null && _readOnlyPaths.ContainsKey(deleteParentDir))
        {
            Console.WriteLine($"NOTIFY_DELETE: rejected (folder is read-only): {path}");
            return Task.FromResult(false);
        }

        Console.WriteLine($"NOTIFY_DELETE: queuing delete for node {nodeId}: {path}");

        // Enqueue child deletes for directories
        var prefix = path + Path.DirectorySeparatorChar;
        var descendants = _pathToNodeId
            .Where(kvp => kvp.Key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var (descPath, descNodeId) in descendants)
        {
            _outbox.EnqueueDelete(descPath, descNodeId);
            _pathToNodeId.TryRemove(descPath, out _);
            _nodeIdToPath.TryRemove(descNodeId, out _);
        }

        _outbox.EnqueueDelete(path, nodeId);
        _pathToNodeId.TryRemove(path, out _);
        _nodeIdToPath.TryRemove(nodeId, out _);
        return Task.FromResult(true);
    }

    private Task<bool> HandleRenameRequestAsync(string? nodeId, string source, string target, bool targetInScope)
    {
        // No node ID → not a tracked placeholder, allow
        if (nodeId == null)
        {
            Console.WriteLine($"NOTIFY_RENAME: allowing untracked rename: {source} → {target}");
            return Task.FromResult(true);
        }

        // Reject rename if source parent is read-only (can't remove from it)
        var sourceParent = Path.GetDirectoryName(source);
        if (sourceParent != null && _readOnlyPaths.ContainsKey(sourceParent))
        {
            Console.WriteLine($"NOTIFY_RENAME: rejected (source folder is read-only): {source}");
            return Task.FromResult(false);
        }

        // Reject rename if target parent is read-only (can't add to it)
        if (targetInScope)
        {
            var targetParent = Path.GetDirectoryName(target);
            if (targetParent != null && _readOnlyPaths.ContainsKey(targetParent))
            {
                Console.WriteLine($"NOTIFY_RENAME: rejected (target folder is read-only): {target}");
                return Task.FromResult(false);
            }
        }

        // Move out of sync root → queue as delete
        if (!targetInScope)
        {
            Console.WriteLine($"NOTIFY_RENAME: move out of sync root, queuing delete: {source} → {target}");
            _outbox.EnqueueDelete(source, nodeId);
            _pathToNodeId.TryRemove(source, out _);
            _nodeIdToPath.TryRemove(nodeId, out _);
            return Task.FromResult(true);
        }

        // Echo check: if mappings already point to the target, this rename
        // was initiated by PollChangesAsync — allow without server call
        if (_nodeIdToPath.TryGetValue(nodeId, out var mappedPath) &&
            string.Equals(mappedPath, target, StringComparison.OrdinalIgnoreCase))
        {
            Console.WriteLine($"NOTIFY_RENAME: allowing echo for {nodeId}");
            return Task.FromResult(true);
        }

        Console.WriteLine($"NOTIFY_RENAME: queuing move for node {nodeId}: {source} → {target}");
        _outbox.EnqueueMove(nodeId, source, target);

        // Update descendant mappings if this is a directory
        if (_pathToNodeId.TryGetValue(source, out _) && Directory.Exists(source))
            UpdateDescendantMappings(source, target);

        // Update the renamed item's own mapping immediately
        _pathToNodeId.TryRemove(source, out _);
        _pathToNodeId[target] = nodeId;
        _nodeIdToPath[nodeId] = target;

        return Task.FromResult(true);
    }

    private void OnDirectoryPopulated(SyncCallbacks.DirectoryPopulatedInfo info)
    {
        // Skip if HydrateDehydratedFiles is already processing this directory
        // (it recurses into subdirs, so the populated callback would duplicate work)
        if (_hydratingDirectories.ContainsKey(info.DirectoryPath))
            return;

        // If this directory or any ancestor is pinned, hydrate its children
        var cts = FindPinnedAncestorCts(info.DirectoryPath);
        if (cts != null)
        {
            _ = Task.Run(() =>
            {
                try
                {
                    int count = HydrateDehydratedFiles(info.DirectoryPath, cts.Token);
                    if (count > 0)
                        Console.WriteLine($"Hydrated {count} files after directory populated: {info.DirectoryPath}");
                    try { SetInSync(info.DirectoryPath); }
                    catch { /* directory might not be a placeholder */ }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"Hydration cancelled for populated directory: {info.DirectoryPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Hydration error for {info.DirectoryPath}: {ex.Message}");
                }
            });
        }
    }

    private void OnDirectoryPinned(string directoryPath)
    {
        var cts = new CancellationTokenSource();
        if (_pinnedDirectories.TryAdd(directoryPath, cts))
        {
            Console.WriteLine($"Directory pinned: {directoryPath}");
            _ = Task.Run(() =>
            {
                try
                {
                    int count = HydrateDehydratedFiles(directoryPath, cts.Token);
                    if (count > 0)
                        Console.WriteLine($"Hydrated {count} files in {directoryPath}");
                    try { SetInSync(directoryPath); }
                    catch { /* directory might not be a placeholder */ }
                }
                catch (OperationCanceledException)
                {
                    Console.WriteLine($"Hydration cancelled for {directoryPath}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Pin hydration error for {directoryPath}: {ex.Message}");
                }
            });
        }
        else
        {
            cts.Dispose();
        }
    }

    private void OnFilePinned(string filePath)
    {
        // If the file is inside a pinned directory, the directory's
        // HydrateDehydratedFiles loop will handle it sequentially.
        // Processing it here too causes concurrent CfHydratePlaceholder calls
        // which starve the thread pool and slow everything down.
        if (IsUnderPinnedDirectory(filePath))
            return;

        _ = Task.Run(() =>
        {
            try
            {
                const FileAttributes dehydratedFlag = (FileAttributes)0x00400000;
                var attrs = File.GetAttributes(filePath);
                if ((attrs & dehydratedFlag) == 0)
                    return; // Already hydrated

                Console.WriteLine($"Hydrating pinned file: {Path.GetFileName(filePath)}");
                HydratePlaceholder(filePath);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"File pin hydration error for {Path.GetFileName(filePath)}: {ex.Message}");
            }
        });
    }

    private Task<bool> HandleDehydrateRequestAsync(string? nodeId, string path)
    {
        Console.WriteLine($"Allowing OS-initiated dehydration: node={nodeId}, path={path}");
        return Task.FromResult(true);
    }


    /// <summary>
    /// Directories currently being dehydrated. Prevents the FileSystemWatcher
    /// feedback loop: dehydrate changes attributes → UNPINNED still set →
    /// OnFileUnpinned fires again → infinite loop.
    /// </summary>
    private readonly ConcurrentDictionary<string, byte> _dehydratingPaths = new(StringComparer.OrdinalIgnoreCase);

    private void OnFileUnpinned(string path)
    {
        if (Directory.Exists(path))
        {
            // Guard against FileSystemWatcher feedback loop — every attribute
            // change on a file with UNPINNED set re-fires this handler.
            if (!_dehydratingPaths.TryAdd(path, 0))
                return;

            // 1. Cancel any in-progress hydration loop for this directory
            CancelPinnedDirectory(path);

            // 2. Collect all files under this directory (with their nodeIds)
            var filesToDehydrate = new List<(string FilePath, string NodeId)>();
            CollectFilesForDehydration(path, filesToDehydrate);

            // 3. Cancel in-flight downloads for exactly these files
            var nodeIdSet = new HashSet<string>(filesToDehydrate.Select(f => f.NodeId));
            _syncCallbacks.CancelFetchesWhere(nodeId => nodeIdSet.Contains(nodeId));

            Console.WriteLine($"Unpinned directory: {path} ({filesToDehydrate.Count} files to dehydrate)");

            // 4. Dehydrate each file on a background thread.
            _ = Task.Run(() =>
            {
                try
                {
                    DehydrateFiles(filesToDehydrate);
                }
                finally
                {
                    _dehydratingPaths.TryRemove(path, out _);
                }
            });
        }
        else if (File.Exists(path))
        {
            // Skip if this file is under a directory already being dehydrated,
            // or if we're already dehydrating this specific file.
            var dir = Path.GetDirectoryName(path);
            if (dir != null && _dehydratingPaths.ContainsKey(dir))
                return;
            if (!_dehydratingPaths.TryAdd(path, 0))
                return;

            _ = Task.Run(() =>
            {
                try
                {
                    const FileAttributes dehydratedFlag = (FileAttributes)0x00400000;
                    var attrs = File.GetAttributes(path);
                    if ((attrs & dehydratedFlag) != 0)
                        return; // Already dehydrated (e.g. by OS via NOTIFY_DEHYDRATE)

                    DehydratePlaceholderWithRetry(path);
                    Console.WriteLine($"Dehydrated: {Path.GetFileName(path)}");
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"Dehydration error for {path}: {ex.Message}");
                }
                finally
                {
                    _dehydratingPaths.TryRemove(path, out _);
                }
            });
        }
    }

    private static void DehydrateFiles(List<(string FilePath, string NodeId)> files)
    {
        const FileAttributes dehydratedFlag = (FileAttributes)0x00400000;
        int count = 0;
        var failed = new List<(string FilePath, string NodeId)>();

        foreach (var (filePath, nodeId) in files)
        {
            try
            {
                var attrs = File.GetAttributes(filePath);
                if ((attrs & dehydratedFlag) != 0)
                    continue; // Already dehydrated

                // Clear the PINNED attribute before dehydrating. When the user
                // unpins a directory, Windows propagates UNPINNED to children
                // asynchronously — we may get here before that finishes.
                ClearPinState(filePath);
                DehydratePlaceholder(filePath);
                Console.WriteLine($"Dehydrated: {Path.GetFileName(filePath)}");
                count++;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"  Dehydrate failed: {Path.GetFileName(filePath)}: {ex.Message}");
                failed.Add((filePath, nodeId));
            }
        }

        // Retry failed files (e.g. 0x80070187 "cloud files in use" during concurrent hydration)
        for (int retry = 1; retry <= 5 && failed.Count > 0; retry++)
        {
            Thread.Sleep(1000);
            var stillFailed = new List<(string FilePath, string NodeId)>();
            foreach (var (filePath, nodeId) in failed)
            {
                try
                {
                    var attrs = File.GetAttributes(filePath);
                    if ((attrs & dehydratedFlag) != 0)
                        continue; // Dehydrated by another path

                    DehydratePlaceholder(filePath);
                    Console.WriteLine($"Dehydrated (retry {retry}): {Path.GetFileName(filePath)}");
                    count++;
                }
                catch (Exception ex)
                {
                    if (retry == 5)
                        Console.Error.WriteLine($"  Dehydrate failed after retries: {Path.GetFileName(filePath)}: {ex.Message}");
                    stillFailed.Add((filePath, nodeId));
                }
            }
            failed = stillFailed;
        }

        Console.WriteLine($"Dehydrated {count}/{files.Count} files");
    }

    /// <summary>
    /// Recursively collect all files under a directory that have known nodeIds.
    /// Also cancels pinned state for subdirectories.
    /// </summary>
    private void CollectFilesForDehydration(string directoryPath, List<(string FilePath, string NodeId)> result)
    {
        foreach (var filePath in Directory.EnumerateFiles(directoryPath))
        {
            if (_pathToNodeId.TryGetValue(filePath, out var nodeId))
                result.Add((filePath, nodeId));
        }
        foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
        {
            CancelPinnedDirectory(subDir);
            CollectFilesForDehydration(subDir, result);
        }
    }

    private static unsafe void ClearPinState(string filePath)
    {
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        PInvoke.CfSetPinState(
            handle,
            CF_PIN_STATE.CF_PIN_STATE_UNSPECIFIED,
            CF_SET_PIN_FLAGS.CF_SET_PIN_FLAG_NONE,
            null    // synchronous
        ).ThrowOnFailure();
    }

    private static unsafe void DehydratePlaceholder(string filePath)
    {
        using var safeHandle = OpenWithRetry(filePath);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        // Use CfUpdatePlaceholder with DEHYDRATE + MARK_IN_SYNC so the file
        // is atomically dehydrated and marked in-sync in one call. This avoids
        // the window where a dehydrated-but-not-in-sync file triggers Explorer
        // to send FETCH_DATA, and avoids TransferError marking it not-in-sync.
        long usn = 0;
        PInvoke.CfUpdatePlaceholder(
            handle,
            null,   // no metadata update
            null,   // keep existing identity
            0,
            null,   // no dehydrate range (dehydrate whole file)
            0,
            CF_UPDATE_FLAGS.CF_UPDATE_FLAG_DEHYDRATE
                | CF_UPDATE_FLAGS.CF_UPDATE_FLAG_MARK_IN_SYNC,
            &usn,
            null    // synchronous
        ).ThrowOnFailure();
    }

    /// <summary>
    /// Dehydrate a single file, retrying up to 5 times with 1-second delays
    /// for transient failures (e.g. 0x80070187 "cloud files in use").
    /// </summary>
    private static void DehydratePlaceholderWithRetry(string filePath)
    {
        const int maxRetries = 5;
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                DehydratePlaceholder(filePath);
                if (attempt > 0)
                    Console.WriteLine($"  Dehydration retry {attempt} succeeded for {Path.GetFileName(filePath)}");
                return;
            }
            catch when (attempt < maxRetries - 1)
            {
                Thread.Sleep(1000);
            }
        }
    }

    /// <summary>
    /// Check whether any ancestor directory of the given path is pinned,
    /// meaning new files created here should be hydrated immediately.
    /// </summary>
    private bool IsUnderPinnedDirectory(string path)
    {
        var dir = Path.GetDirectoryName(path);
        while (dir != null && dir.Length >= _syncRootPath.Length)
        {
            if (_pinnedDirectories.ContainsKey(dir))
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    /// <summary>
    /// Find the CancellationTokenSource for the given path or its nearest
    /// pinned ancestor.  Returns null if no ancestor is pinned.
    /// </summary>
    private CancellationTokenSource? FindPinnedAncestorCts(string path)
    {
        // Check the path itself first, then walk up
        var dir = path;
        while (dir != null && dir.Length >= _syncRootPath.Length)
        {
            if (_pinnedDirectories.TryGetValue(dir, out var cts))
                return cts;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }

    /// <summary>
    /// Remove a directory from <see cref="_pinnedDirectories"/> and cancel its
    /// CTS.  Safe to call on subdirectories that share a parent's CTS —
    /// CancellationTokenSource.Cancel and Dispose are idempotent.
    /// </summary>
    private void CancelPinnedDirectory(string path)
    {
        if (_pinnedDirectories.TryRemove(path, out var cts))
        {
            try { cts.Cancel(); } catch (ObjectDisposedException) { }
            // Don't dispose here — parent's OnFileUnpinned owns the CTS lifetime
            // for shared instances.  Orphan CTSes get cleaned up in Dispose().
        }
    }

    /// <summary>
    /// Hydrate all dehydrated files in a directory (recursively).
    /// Returns the number of files hydrated.  Throws OperationCanceledException
    /// if the cancellation token fires (e.g. directory was unpinned).
    /// </summary>
    private int HydrateDehydratedFiles(string directoryPath, CancellationToken ct)
    {
        if (!Directory.Exists(directoryPath))
            return 0;

        _hydratingDirectories.TryAdd(directoryPath, 0);
        try
        {
            const FileAttributes dehydratedFlag = (FileAttributes)0x00400000; // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            int count = 0;
            var failed = new List<string>();

            foreach (var filePath in Directory.EnumerateFiles(directoryPath))
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var attrs = File.GetAttributes(filePath);
                    if ((attrs & dehydratedFlag) == 0)
                        continue; // Already hydrated

                    Console.WriteLine($"Hydrating pinned file: {Path.GetFileName(filePath)}");
                    HydratePlaceholder(filePath);
                    count++;
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Console.Error.WriteLine($"  Hydration failed for {Path.GetFileName(filePath)}: {ex.Message}");
                    failed.Add(filePath);
                }
            }

            // Retry failed hydrations (e.g. transient network errors)
            for (int retry = 1; retry <= 3 && failed.Count > 0; retry++)
            {
                ct.ThrowIfCancellationRequested();
                Console.WriteLine($"Retrying {failed.Count} failed hydrations (attempt {retry})...");
                Thread.Sleep(2000 * retry);

                var stillFailed = new List<string>();
                foreach (var filePath in failed)
                {
                    ct.ThrowIfCancellationRequested();
                    try
                    {
                        var attrs = File.GetAttributes(filePath);
                        if ((attrs & dehydratedFlag) == 0)
                            continue; // Hydrated by another path

                        Console.WriteLine($"Hydrating pinned file (retry {retry}): {Path.GetFileName(filePath)}");
                        HydratePlaceholder(filePath);
                        count++;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        if (retry == 3)
                            Console.Error.WriteLine($"  Hydration failed after retries for {Path.GetFileName(filePath)}: {ex.Message}");
                        stillFailed.Add(filePath);
                    }
                }
                failed = stillFailed;
            }

            // Mark successfully-hydrated files as in-sync so they don't show "syncing"
            // (TransferError during a failed FETCH_DATA marks the placeholder not-in-sync)
            foreach (var filePath in Directory.EnumerateFiles(directoryPath))
            {
                try { SetInSync(filePath); }
                catch { /* not a placeholder — ignore */ }
            }

            // Recurse into subdirectories — they inherit the pin from the parent.
            // Store the parent's CTS so IsUnderPinnedDirectory / OnDirectoryPopulated
            // can find it for these subdirectories.
            foreach (var subDir in Directory.EnumerateDirectories(directoryPath))
            {
                ct.ThrowIfCancellationRequested();
                if (_pinnedDirectories.TryGetValue(directoryPath, out var parentCts))
                    _pinnedDirectories.TryAdd(subDir, parentCts);
                count += HydrateDehydratedFiles(subDir, ct);
            }

            return count;
        }
        finally
        {
            _hydratingDirectories.TryRemove(directoryPath, out _);
        }
    }

    private static unsafe void HydratePlaceholder(string filePath)
    {
        using var safeHandle = File.OpenHandle(filePath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        var handle = new global::Windows.Win32.Foundation.HANDLE(safeHandle.DangerousGetHandle());
        PInvoke.CfHydratePlaceholder(
            handle,
            0,      // start offset
            -1,     // entire file
            CF_HYDRATE_FLAGS.CF_HYDRATE_FLAG_NONE,
            null    // synchronous
        ).ThrowOnFailure();
    }

    internal string? ResolveParentNodeId(string localPath)
    {
        if (string.Equals(localPath, _syncRootPath, StringComparison.OrdinalIgnoreCase))
            return _homeNodeId;
        return _pathToNodeId.TryGetValue(localPath, out var id) ? id : null;
    }

    /// <summary>
    /// Update path↔nodeId mappings. Called by OutboxProcessor after server operations.
    /// </summary>
    internal void UpdateMappings(string localPath, string? oldNodeId, string newNodeId)
    {
        if (oldNodeId != null)
            _nodeIdToPath.TryRemove(oldNodeId, out _);
        // Clean up any intermediate mapping — e.g. a remote replace arrived via
        // PollChanges while the outbox entry was pending, remapping the path to
        // a different nodeId that we're now replacing with onExists:"replace".
        if (_pathToNodeId.TryGetValue(localPath, out var currentNodeId)
            && currentNodeId != oldNodeId && currentNodeId != newNodeId)
            _nodeIdToPath.TryRemove(currentNodeId, out _);
        _pathToNodeId[localPath] = newNodeId;
        _nodeIdToPath[newNodeId] = localPath;
    }

    /// <summary>
    /// Record that a file was recently uploaded, to suppress FileSystemWatcher echo.
    /// </summary>
    internal void RecordRecentUpload(string localPath)
    {
        _recentlyUploaded[localPath] = File.GetLastWriteTimeUtc(localPath);
    }

    internal static string ResolveContentType(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext switch
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
    }

    private static void SetDirectoryReadOnly(string path, bool readOnly)
    {
        try
        {
            if (!Directory.Exists(path)) return;
            var attrs = File.GetAttributes(path);
            var newAttrs = readOnly
                ? attrs | FileAttributes.ReadOnly
                : attrs & ~FileAttributes.ReadOnly;
            if (attrs != newAttrs)
                File.SetAttributes(path, newAttrs);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to set read-only={readOnly} on {path}: {ex.Message}");
        }
    }

    private void TrackFolderPermissions(FileNode node, string localPath)
    {
        if (!node.IsFolder) return;
        if (node.MyRights != null && !node.MyRights.MayWrite)
        {
            _readOnlyPaths[localPath] = node.MyRights;
            SetDirectoryReadOnly(localPath, true);
        }
        else if (_readOnlyPaths.TryRemove(localPath, out _))
        {
            SetDirectoryReadOnly(localPath, false);
            SyncRoot.SetDirectoryWriteProtection(localPath, false);
        }
    }

    /// <summary>
    /// Apply NTFS DENY ACLs to all tracked read-only folders.
    /// Called after bulk operations (BuildTree, Reconcile) that defer ACL application.
    /// </summary>
    private void ApplyWriteProtections()
    {
        foreach (var path in _readOnlyPaths.Keys)
            SyncRoot.SetDirectoryWriteProtection(path, true);
    }

    /// <summary>
    /// Apply NTFS DENY ACL to a single folder if it is tracked as read-only.
    /// Called after incremental operations (PollChanges create/update).
    /// </summary>
    private void ApplyWriteProtection(string path)
    {
        if (_readOnlyPaths.ContainsKey(path))
            SyncRoot.SetDirectoryWriteProtection(path, true);
    }

    /// <summary>
    /// Temporarily lift the DENY ACL on a folder so the sync engine can
    /// create/delete placeholders.  Returns a disposable scope that restores
    /// the ACL when disposed.
    /// </summary>
    private SuspendProtectionScope SuspendFolderProtection(string? folderPath)
    {
        return new SuspendProtectionScope(folderPath, _readOnlyPaths);
    }

    private struct SuspendProtectionScope : IDisposable
    {
        private readonly string? _path;
        private readonly bool _wasProtected;

        public SuspendProtectionScope(string? path, ConcurrentDictionary<string, FilesRights> readOnlyPaths)
        {
            _path = path;
            _wasProtected = path != null && readOnlyPaths.ContainsKey(path);
            if (_wasProtected)
                SyncRoot.SetDirectoryWriteProtection(path!, false);
        }

        public void Dispose()
        {
            if (_wasProtected && _path != null)
                SyncRoot.SetDirectoryWriteProtection(_path, true);
        }
    }

    /// <summary>
    /// Ensures the given local directory is mapped to a server node, creating
    /// it (and any missing ancestors) on the server if necessary.
    /// This handles the case where a directory event was missed or arrives
    /// after its children during a bulk copy.
    /// </summary>
    private async Task<string> EnsureParentMappedAsync(string localDir)
    {
        // Sync root maps to the home node
        if (string.Equals(localDir, _syncRootPath, StringComparison.OrdinalIgnoreCase))
            return _homeNodeId;

        // Already mapped — fast path
        if (_pathToNodeId.TryGetValue(localDir, out var existingId))
            return existingId;

        // Recursively ensure the grandparent is mapped first
        var grandparentDir = Path.GetDirectoryName(localDir)!;
        var parentNodeId = await EnsureParentMappedAsync(grandparentDir);

        // Double-check after awaiting (another task may have created it)
        if (_pathToNodeId.TryGetValue(localDir, out var raceId))
            return raceId;

        // Create the directory on the server
        var folderName = Path.GetFileName(localDir);
        Console.WriteLine($"Auto-creating missing parent folder on server: {folderName}");
        var node = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.CreateFileNodeAsync(parentNodeId, null, folderName));

        // Convert to placeholder and update mappings
        ConvertToPlaceholder(localDir, node.Id, isDirectory: true);
        _pathToNodeId[localDir] = node.Id;
        _nodeIdToPath[node.Id] = localDir;
        Console.WriteLine($"Auto-created folder: {folderName} → node {node.Id}");

        return node.Id;
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

    internal static unsafe void ConvertToPlaceholder(string filePath, string nodeId, bool isDirectory = false)
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

    internal static unsafe void UpdatePlaceholderIdentity(string filePath, string newNodeId)
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

    private const uint FILE_WRITE_ATTRIBUTES = 0x100;
    private const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    internal static unsafe void SetInSync(string path)
    {
        // Use FILE_WRITE_ATTRIBUTES to avoid triggering hydration on dehydrated
        // files. GENERIC_READ/GENERIC_WRITE would cause cfapi to send FETCH_DATA.
        var isDirectory = Directory.Exists(path);
        var flags = isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0u;

        using var handle = PInvoke.CreateFile(
            path,
            FILE_WRITE_ATTRIBUTES,
            global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
            null,
            global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
            (global::Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES)flags,
            null);

        var cfHandle = new global::Windows.Win32.Foundation.HANDLE(handle.DangerousGetHandle());
        PInvoke.CfSetInSyncState(
            cfHandle,
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

    internal static void StripZoneIdentifier(string filePath)
    {
        try { File.Delete(filePath + ":Zone.Identifier"); } catch { }
    }

    private const uint GENERIC_WRITE = 0x40000000;

    /// <summary>
    /// Open a file handle suitable for cfapi operations (CfUpdatePlaceholder,
    /// CfConvertToPlaceholder, etc.) WITHOUT triggering hydration on dehydrated
    /// placeholders.  Uses GENERIC_WRITE (not GENERIC_READ | GENERIC_WRITE)
    /// because GENERIC_READ on a dehydrated placeholder triggers FETCH_DATA.
    /// </summary>
    private static unsafe Microsoft.Win32.SafeHandles.SafeFileHandle OpenWithRetry(string filePath, bool isDirectory = false)
    {
        const int maxRetries = 5;
        var flags = isDirectory ? FILE_FLAG_BACKUP_SEMANTICS : 0u;

        for (int attempt = 0; ; attempt++)
        {
            try
            {
                var handle = PInvoke.CreateFile(
                    filePath,
                    GENERIC_WRITE,
                    global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_READ
                        | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_WRITE
                        | global::Windows.Win32.Storage.FileSystem.FILE_SHARE_MODE.FILE_SHARE_DELETE,
                    null,
                    global::Windows.Win32.Storage.FileSystem.FILE_CREATION_DISPOSITION.OPEN_EXISTING,
                    (global::Windows.Win32.Storage.FileSystem.FILE_FLAGS_AND_ATTRIBUTES)flags,
                    null);

                if (handle.IsInvalid)
                    throw new IOException($"CreateFile failed for {filePath}");

                // Wrap in SafeFileHandle for automatic disposal
                return new Microsoft.Win32.SafeHandles.SafeFileHandle(handle.DangerousGetHandle(), ownsHandle: true);
            }
            catch (IOException) when (attempt < maxRetries - 1)
            {
                Thread.Sleep(200 * (attempt + 1));
            }
        }
    }

    private async Task<string?> ResolveLocalPathAsync(string nodeId, CancellationToken ct)
    {
        if (nodeId == _homeNodeId)
            return _syncRootPath;

        // Trash boundary — don't resolve paths under the trash folder
        if (_trashNodeId != null && nodeId == _trashNodeId)
            return null;

        var nodes = await _queue.EnqueueAsync(QueuePriority.Background,
            () => _jmapClient.GetFileNodesAsync([nodeId], ct), ct);
        if (nodes.Length == 0)
            return null;

        var node = nodes[0];
        if (node.ParentId == null)
            return _syncRootPath;

        var parentPath = await ResolveLocalPathAsync(node.ParentId, ct);
        return parentPath != null ? Path.Combine(parentPath, PlaceholderManager.SanitizeName(node.Name)) : null;
    }

    private void SaveNodeCache(string state)
    {
        NodeCache.Save(_scopeKey, _homeNodeId, state, _nodeIdToPath, _syncRootPath,
            _readOnlyPaths, _trashNodeId);
    }

    public void Dispose()
    {
        // Remove NTFS DENY ACLs so folders aren't left locked after shutdown
        foreach (var path in _readOnlyPaths.Keys)
        {
            SyncRoot.SetDirectoryWriteProtection(path, false);
            SetDirectoryReadOnly(path, false);
        }
        _readOnlyPaths.Clear();

        ReportStatus(CF_SYNC_PROVIDER_STATUS.CF_PROVIDER_STATUS_DISCONNECTED);

        // Cancel all in-progress hydrations
        foreach (var kvp in _pinnedDirectories)
        {
            kvp.Value.Cancel();
            kvp.Value.Dispose();
        }
        _pinnedDirectories.Clear();

        _outboxProcessor.Dispose();
        _outbox.Dispose();
        _fileChangeWatcher.Dispose();
        _syncRoot.Dispose();
    }
}
