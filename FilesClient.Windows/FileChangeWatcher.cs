using System.Collections.Concurrent;

namespace FilesClient.Windows;

internal sealed class FileChangeWatcher : IDisposable
{
    public enum ChangeKind { Created, Modified, Renamed, Deleted }

    public record FileChange(string FullPath, ChangeKind Kind, string? OldFullPath = null);

    private readonly string _syncRootPath;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, (ChangeKind Kind, string? OldFullPath)> _pending = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer _debounceTimer;
    private readonly TimeSpan _debounceInterval = TimeSpan.FromSeconds(1);

    public event Action<FileChange[]>? OnChanges;

    public FileChangeWatcher(string syncRootPath)
    {
        _syncRootPath = syncRootPath;
        _debounceTimer = new Timer(FlushChanges);
    }

    public void Start()
    {
        _watcher = new FileSystemWatcher(_syncRootPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Created += (_, e) => OnCreated(e.FullPath);
        _watcher.Changed += (_, e) => EnqueueIfNotPlaceholder(e.FullPath, ChangeKind.Modified);
        _watcher.Renamed += (_, e) => OnRenamed(e.FullPath, e.OldFullPath);
        _watcher.Deleted += (_, e) => OnDeleted(e.FullPath);

        _watcher.EnableRaisingEvents = true;
        Console.WriteLine("File change watcher started.");
    }

    public void Stop()
    {
        if (_watcher != null)
            _watcher.EnableRaisingEvents = false;
        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnCreated(string path)
    {
        // For directories, strip Zone.Identifier and enqueue as Created
        if (Directory.Exists(path))
        {
            StripZoneIdentifier(path);
            _pending[path] = (ChangeKind.Created, null);
            _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            return;
        }

        EnqueueIfNotPlaceholder(path, ChangeKind.Created);
    }

    private void OnDeleted(string path)
    {
        // File/folder is already gone — no placeholder check possible or needed
        _pending[path] = (ChangeKind.Deleted, null);
        _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void OnRenamed(string fullPath, string oldFullPath)
    {
        // No placeholder check needed — we just update the server-side name
        _pending[fullPath] = (ChangeKind.Renamed, oldFullPath);
        _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
    }

    private static void StripZoneIdentifier(string path)
    {
        try { File.Delete(path + ":Zone.Identifier"); } catch { }
    }

    private void EnqueueIfNotPlaceholder(string path, ChangeKind kind)
    {
        try
        {
            // Skip directories — we only upload files
            if (Directory.Exists(path))
                return;

            // Skip dehydrated placeholders (RECALL_ON_DATA_ACCESS means the
            // data hasn't been fetched yet).  Hydrated placeholders still have
            // ReparsePoint but no longer have RECALL_ON_DATA_ACCESS — those
            // should pass through so real edits get uploaded.
            var attrs = File.GetAttributes(path);
            const FileAttributes dehydratedFlag =
                (FileAttributes)0x00400000;     // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            if ((attrs & dehydratedFlag) != 0)
                return;
        }
        catch
        {
            // File may have been deleted between event and check
            return;
        }

        _pending[path] = (kind, null);
        // Reset debounce timer
        _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
    }

    private void FlushChanges(object? state)
    {
        if (_pending.IsEmpty)
            return;

        var changes = new List<FileChange>();
        foreach (var key in _pending.Keys.ToArray())
        {
            if (_pending.TryRemove(key, out var entry))
                changes.Add(new FileChange(key, entry.Kind, entry.OldFullPath));
        }

        if (changes.Count > 0)
            OnChanges?.Invoke(changes.ToArray());
    }

    public void Dispose()
    {
        Stop();
        _debounceTimer.Dispose();
        _watcher?.Dispose();
    }
}
