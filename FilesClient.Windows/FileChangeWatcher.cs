using System.Collections.Concurrent;

namespace FilesClient.Windows;

internal sealed class FileChangeWatcher : IDisposable
{
    public enum ChangeKind { Created, Modified }

    public record FileChange(string FullPath, ChangeKind Kind);

    private readonly string _syncRootPath;
    private FileSystemWatcher? _watcher;
    private readonly ConcurrentDictionary<string, ChangeKind> _pending = new(StringComparer.OrdinalIgnoreCase);
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
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };

        _watcher.Created += (_, e) => EnqueueIfNotPlaceholder(e.FullPath, ChangeKind.Created);
        _watcher.Changed += (_, e) => EnqueueIfNotPlaceholder(e.FullPath, ChangeKind.Modified);
        _watcher.Renamed += (_, e) => EnqueueIfNotPlaceholder(e.FullPath, ChangeKind.Created);

        _watcher.EnableRaisingEvents = true;
        Console.WriteLine("File change watcher started.");
    }

    public void Stop()
    {
        if (_watcher != null)
            _watcher.EnableRaisingEvents = false;
        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void EnqueueIfNotPlaceholder(string path, ChangeKind kind)
    {
        try
        {
            // Skip directories — we only upload files
            if (Directory.Exists(path))
                return;

            // Skip cloud file placeholders (files owned by cfapi).
            // Recall-on-data-access and recall-on-open are set on placeholders.
            var attrs = File.GetAttributes(path);
            const FileAttributes placeholderFlags =
                FileAttributes.ReparsePoint |   // placeholder reparse point
                (FileAttributes)0x00400000;     // FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS
            if ((attrs & placeholderFlags) != 0)
                return;
        }
        catch
        {
            // File may have been deleted between event and check
            return;
        }

        _pending[path] = kind;
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
            if (_pending.TryRemove(key, out var kind))
                changes.Add(new FileChange(key, kind));
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
