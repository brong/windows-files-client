using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace FilesClient.Windows;

public class PendingChange
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; } = Guid.NewGuid();

    [JsonPropertyName("localPath")]
    public string? LocalPath { get; set; }

    [JsonPropertyName("nodeId")]
    public string? NodeId { get; set; }

    [JsonPropertyName("contentType")]
    public string? ContentType { get; set; }

    [JsonPropertyName("isFolder")]
    public bool IsFolder { get; set; }

    [JsonPropertyName("isDirtyContent")]
    public bool IsDirtyContent { get; set; }

    [JsonPropertyName("isDirtyLocation")]
    public bool IsDirtyLocation { get; set; }

    [JsonPropertyName("isDeleted")]
    public bool IsDeleted { get; set; }

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [JsonPropertyName("updatedAt")]
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("attemptCount")]
    public int AttemptCount { get; set; }

    [JsonPropertyName("lastError")]
    public string? LastError { get; set; }

    [JsonPropertyName("nextRetryAfter")]
    public DateTime? NextRetryAfter { get; set; }
}

public class SyncOutbox : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<Guid, PendingChange> _entries = new();
    // Secondary indexes for O(1) coalescing lookups
    private readonly Dictionary<string, Guid> _byPath = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Guid> _byNodeId = new();

    private readonly string _persistPath;
    private bool _dirty;
    private readonly Timer _saveTimer;
    private readonly ManualResetEventSlim _workSignal = new(false);
    private bool _disposed;
    private readonly HashSet<Guid> _processingIds = new();
    private readonly ConcurrentDictionary<Guid, int> _uploadProgress = new();

    public event Action<int>? PendingCountChanged;

    public SyncOutbox(string scopeKey)
    {
        // ScopeKey is "username/accountId" — split into path segments
        var parts = scopeKey.Split('/', '\\');
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FastmailFiles", Path.Combine(parts));
        Directory.CreateDirectory(dir);
        _persistPath = Path.Combine(dir, "outbox.json");
        _saveTimer = new Timer(SaveCallback);
    }

    public int PendingCount
    {
        get { lock (_lock) return _entries.Count; }
    }

    /// <summary>Load persisted state from disk. Call once at startup.</summary>
    public void Load()
    {
        if (!File.Exists(_persistPath))
            return;

        try
        {
            var json = File.ReadAllText(_persistPath);
            var entries = JsonSerializer.Deserialize<PendingChange[]>(json);
            if (entries == null) return;

            lock (_lock)
            {
                foreach (var entry in entries)
                {
                    // Reset retry timers — fresh start after app restart
                    entry.NextRetryAfter = null;
                    entry.AttemptCount = 0;
                    entry.LastError = null;

                    _entries[entry.Id] = entry;
                    if (entry.LocalPath != null)
                        _byPath[entry.LocalPath] = entry.Id;
                    if (entry.NodeId != null)
                        _byNodeId[entry.NodeId] = entry.Id;
                }
            }

            if (entries.Length > 0)
            {
                Console.WriteLine($"Loaded {entries.Length} pending changes from outbox");
                _workSignal.Set();
                RaisePendingCountChanged();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to load outbox: {ex.Message}");
        }
    }

    /// <summary>
    /// Enqueue a content change (file created or modified, or new folder).
    /// Coalesces with existing entries for the same path.
    /// </summary>
    public void EnqueueContentChange(string localPath, string? nodeId, string? contentType, bool isFolder)
    {
        lock (_lock)
        {
            if (_byPath.TryGetValue(localPath, out var existingId) && _entries.TryGetValue(existingId, out var existing))
            {
                // Coalesce: update timestamp, content is always re-read at process time
                if (existing.IsDeleted)
                    return; // Delete is terminal

                existing.IsDirtyContent = !isFolder; // folders don't have content
                existing.ContentType = contentType;
                existing.UpdatedAt = DateTime.UtcNow;
                // Reset retry state so it gets processed promptly
                existing.NextRetryAfter = null;
                existing.AttemptCount = 0;
                existing.LastError = null;
            }
            else
            {
                var entry = new PendingChange
                {
                    LocalPath = localPath,
                    NodeId = nodeId,
                    ContentType = contentType,
                    IsFolder = isFolder,
                    IsDirtyContent = !isFolder, // folders need create, not upload
                    IsDirtyLocation = nodeId == null, // new item needs location set
                };
                _entries[entry.Id] = entry;
                _byPath[localPath] = entry.Id;
                if (nodeId != null)
                    _byNodeId[nodeId] = entry.Id;
            }

            MarkDirtyAndSignal();
        }

        RaisePendingCountChanged();
    }

    /// <summary>
    /// Enqueue a delete operation.
    /// Coalesces: if the item was never synced (no nodeId), removes entirely.
    /// </summary>
    public void EnqueueDelete(string localPath, string? nodeId)
    {
        lock (_lock)
        {
            if (_byPath.TryGetValue(localPath, out var existingId) && _entries.TryGetValue(existingId, out var existing))
            {
                if (existing.NodeId == null && nodeId == null)
                {
                    // Never existed on server — just remove entirely
                    RemoveEntryLocked(existing);
                }
                else
                {
                    // Has a server presence — convert to delete
                    existing.IsDeleted = true;
                    existing.IsDirtyContent = false;
                    existing.IsDirtyLocation = false;
                    existing.LocalPath = null;
                    existing.NodeId = nodeId ?? existing.NodeId;
                    existing.UpdatedAt = DateTime.UtcNow;
                    existing.NextRetryAfter = null;
                    existing.AttemptCount = 0;
                    existing.LastError = null;

                    // Update indexes
                    _byPath.Remove(localPath);
                    if (existing.NodeId != null)
                        _byNodeId[existing.NodeId] = existing.Id;
                }
            }
            else if (nodeId != null)
            {
                // Check if there's an existing entry by nodeId
                if (_byNodeId.TryGetValue(nodeId, out var nodeEntryId) && _entries.TryGetValue(nodeEntryId, out var nodeEntry))
                {
                    nodeEntry.IsDeleted = true;
                    nodeEntry.IsDirtyContent = false;
                    nodeEntry.IsDirtyLocation = false;
                    if (nodeEntry.LocalPath != null)
                        _byPath.Remove(nodeEntry.LocalPath);
                    nodeEntry.LocalPath = null;
                    nodeEntry.UpdatedAt = DateTime.UtcNow;
                    nodeEntry.NextRetryAfter = null;
                    nodeEntry.AttemptCount = 0;
                    nodeEntry.LastError = null;
                }
                else
                {
                    // New delete entry
                    var entry = new PendingChange
                    {
                        NodeId = nodeId,
                        IsDeleted = true,
                    };
                    _entries[entry.Id] = entry;
                    _byNodeId[nodeId] = entry.Id;
                }
            }
            // else: no nodeId and no existing entry — nothing to do (item never existed on server)

            MarkDirtyAndSignal();
        }

        RaisePendingCountChanged();
    }

    /// <summary>
    /// Enqueue a move/rename operation.
    /// </summary>
    public void EnqueueMove(string nodeId, string oldPath, string newPath)
    {
        lock (_lock)
        {
            if (_byNodeId.TryGetValue(nodeId, out var existingId) && _entries.TryGetValue(existingId, out var existing))
            {
                if (existing.IsDeleted)
                    return; // Delete is terminal

                // Update path
                if (existing.LocalPath != null)
                    _byPath.Remove(existing.LocalPath);
                existing.LocalPath = newPath;
                existing.IsDirtyLocation = true;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.NextRetryAfter = null;
                existing.AttemptCount = 0;
                existing.LastError = null;
                _byPath[newPath] = existing.Id;
            }
            else if (_byPath.TryGetValue(oldPath, out existingId) && _entries.TryGetValue(existingId, out existing))
            {
                if (existing.IsDeleted)
                    return;

                _byPath.Remove(oldPath);
                existing.LocalPath = newPath;
                existing.IsDirtyLocation = true;
                existing.NodeId = nodeId;
                existing.UpdatedAt = DateTime.UtcNow;
                existing.NextRetryAfter = null;
                existing.AttemptCount = 0;
                existing.LastError = null;
                _byPath[newPath] = existing.Id;
                _byNodeId[nodeId] = existing.Id;
            }
            else
            {
                // New move entry
                var entry = new PendingChange
                {
                    LocalPath = newPath,
                    NodeId = nodeId,
                    IsDirtyLocation = true,
                };
                _entries[entry.Id] = entry;
                _byPath[newPath] = entry.Id;
                _byNodeId[nodeId] = entry.Id;
            }

            MarkDirtyAndSignal();
        }

        RaisePendingCountChanged();
    }

    /// <summary>
    /// Dequeue the next processable item respecting ordering and backoff.
    /// Returns null if nothing is ready.
    ///
    /// Processing order:
    /// 1. Folder creates — shallowest first, then alphabetical by name
    /// 2. Content uploads — alphabetical by name within same parent
    /// 3. Pure moves — alphabetical by name within same parent
    /// 4. Deletes — deepest first (children before parents)
    /// </summary>
    public PendingChange? DequeueNext()
    {
        var now = DateTime.UtcNow;

        lock (_lock)
        {
            var ready = _entries.Values
                .Where(e => !_processingIds.Contains(e.Id)
                    && (e.NextRetryAfter == null || e.NextRetryAfter <= now))
                .ToList();

            // 1. Folder creates — shallowest first, then alphabetical, skip if parent pending
            var folderCreate = ready
                .Where(e => e.IsFolder && !e.IsDeleted && e.LocalPath != null
                    && e.NodeId == null // not yet on server
                    && !HasPendingParentCreateLocked(e.LocalPath))
                .OrderBy(e => e.LocalPath!.Count(c => c == Path.DirectorySeparatorChar))
                .ThenBy(e => Path.GetFileName(e.LocalPath), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (folderCreate != null) return folderCreate;

            // 2. Content uploads — alphabetical within same parent, skip if parent pending
            var upload = ready
                .Where(e => e.IsDirtyContent && !e.IsDeleted
                    && !HasPendingParentCreateLocked(e.LocalPath))
                .OrderBy(e => Path.GetDirectoryName(e.LocalPath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => Path.GetFileName(e.LocalPath), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (upload != null) return upload;

            // 3. Pure moves — alphabetical within same parent, skip if parent pending
            var move = ready
                .Where(e => e.IsDirtyLocation && !e.IsDirtyContent && !e.IsDeleted
                    && !HasPendingParentCreateLocked(e.LocalPath))
                .OrderBy(e => Path.GetDirectoryName(e.LocalPath), StringComparer.OrdinalIgnoreCase)
                .ThenBy(e => Path.GetFileName(e.LocalPath), StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (move != null) return move;

            // 4. Deletes — deepest first
            var delete = ready
                .Where(e => e.IsDeleted)
                .OrderByDescending(e => (e.LocalPath ?? "").Count(c => c == Path.DirectorySeparatorChar))
                .FirstOrDefault();
            if (delete != null) return delete;

            return null;
        }
    }

    /// <summary>Mark a change as currently being processed.</summary>
    public void MarkProcessing(Guid id)
    {
        lock (_lock)
            _processingIds.Add(id);
        RaisePendingCountChanged();
    }

    /// <summary>Mark a change as successfully processed.</summary>
    public void MarkCompleted(Guid id)
    {
        _uploadProgress.TryRemove(id, out _);

        lock (_lock)
        {
            _processingIds.Remove(id);
            if (_entries.TryGetValue(id, out var entry))
            {
                RemoveEntryLocked(entry);
                MarkDirtyAndSignal();
            }
        }

        RaisePendingCountChanged();
    }

    /// <summary>Re-queue a change for retry without incrementing error count.</summary>
    public void MarkRetry(Guid id, int delayMs = 1000)
    {
        lock (_lock)
        {
            _processingIds.Remove(id);
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.NextRetryAfter = DateTime.UtcNow.AddMilliseconds(delayMs);
                MarkDirtyAndSignal();
            }
        }

        RaisePendingCountChanged();
    }

    /// <summary>Mark a change as failed with exponential backoff.</summary>
    public void MarkFailed(Guid id, string error)
    {
        _uploadProgress.TryRemove(id, out _);

        lock (_lock)
        {
            _processingIds.Remove(id);
            if (_entries.TryGetValue(id, out var entry))
            {
                entry.AttemptCount++;
                entry.LastError = error;
                var delayMs = Math.Min(1000 * (1 << entry.AttemptCount), 60000);
                entry.NextRetryAfter = DateTime.UtcNow.AddMilliseconds(delayMs);
                MarkDirtyAndSignal();
            }
        }

        RaisePendingCountChanged();
    }

    /// <summary>Mark a change as permanently rejected (e.g. permission denied). Removes from outbox.</summary>
    public void MarkRejected(Guid id, string reason)
    {
        _uploadProgress.TryRemove(id, out _);

        lock (_lock)
        {
            _processingIds.Remove(id);
            if (_entries.TryGetValue(id, out var entry))
            {
                Console.WriteLine($"Outbox: rejected {entry.LocalPath ?? entry.NodeId}: {reason}");
                RemoveEntryLocked(entry);
                MarkDirtyAndSignal();
            }
        }

        RaisePendingCountChanged();
    }

    /// <summary>Update transient upload progress for a pending change.</summary>
    public void UpdateProgress(Guid id, int percent) => _uploadProgress[id] = percent;

    /// <summary>Get current upload progress for a pending change, if any.</summary>
    public int? GetProgress(Guid id) => _uploadProgress.TryGetValue(id, out var p) ? p : null;

    /// <summary>Return a snapshot of all pending changes and the set of currently processing IDs.</summary>
    public (PendingChange[] Entries, HashSet<Guid> ProcessingIds) GetSnapshot()
    {
        lock (_lock)
            return (_entries.Values.ToArray(), new HashSet<Guid>(_processingIds));
    }

    /// <summary>Check if there's a pending change for the given nodeId.</summary>
    public bool HasPendingForNodeId(string nodeId)
    {
        lock (_lock)
            return _byNodeId.ContainsKey(nodeId);
    }

    /// <summary>Check if there's a pending change for the given local path.</summary>
    public bool HasPendingForPath(string localPath)
    {
        lock (_lock)
            return _byPath.ContainsKey(localPath);
    }

    /// <summary>Wait for work to be available, or until timeout/cancellation.</summary>
    public void WaitForWork(TimeSpan timeout, CancellationToken ct)
    {
        _workSignal.Wait(timeout, ct);
        _workSignal.Reset();
    }

    /// <summary>Force a save of the outbox to disk.</summary>
    public void Save()
    {
        PendingChange[] snapshot;
        lock (_lock)
        {
            _dirty = false;
            snapshot = _entries.Values.ToArray();
        }

        try
        {
            var json = JsonSerializer.Serialize(snapshot, new JsonSerializerOptions
            {
                WriteIndented = true,
            });

            // Atomic write: temp file + rename
            var tempPath = _persistPath + ".tmp";
            File.WriteAllText(tempPath, json);
            File.Move(tempPath, _persistPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Failed to save outbox: {ex.Message}");
        }
    }

    /// <summary>
    /// Check if the parent directory of a path has a pending or in-progress folder create.
    /// Must be called under _lock.
    /// </summary>
    private bool HasPendingParentCreateLocked(string? localPath)
    {
        if (localPath == null) return false;
        var dir = Path.GetDirectoryName(localPath);
        while (dir != null)
        {
            if (_byPath.TryGetValue(dir, out var parentId)
                && _entries.TryGetValue(parentId, out var parentEntry)
                && parentEntry.IsFolder && !parentEntry.IsDeleted && parentEntry.NodeId == null)
                return true;
            dir = Path.GetDirectoryName(dir);
        }
        return false;
    }

    private void RemoveEntryLocked(PendingChange entry)
    {
        _entries.Remove(entry.Id);
        if (entry.LocalPath != null)
            _byPath.Remove(entry.LocalPath);
        if (entry.NodeId != null)
            _byNodeId.Remove(entry.NodeId);
    }

    private void MarkDirtyAndSignal()
    {
        _dirty = true;
        _workSignal.Set();
        // Debounced save — 1 second
        _saveTimer.Change(1000, Timeout.Infinite);
    }

    private void SaveCallback(object? state)
    {
        if (_dirty)
            Save();
    }

    private void RaisePendingCountChanged()
    {
        PendingCountChanged?.Invoke(PendingCount);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _saveTimer.Dispose();
        if (_dirty)
            Save();
        _workSignal.Dispose();
    }
}
