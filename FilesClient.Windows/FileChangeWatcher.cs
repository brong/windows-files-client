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

    /// <summary>
    /// Fired when a directory's FILE_ATTRIBUTE_PINNED is detected (user selected
    /// "Always keep on this device").
    /// </summary>
    public event Action<string>? OnDirectoryPinned;

    /// <summary>
    /// Fired when an individual file's FILE_ATTRIBUTE_PINNED is detected.
    /// </summary>
    public event Action<string>? OnFilePinned;

    /// <summary>
    /// Fired when FILE_ATTRIBUTE_UNPINNED is detected on a file or directory
    /// (user selected "Free up space").
    /// </summary>
    public event Action<string>? OnFileUnpinned;

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
                | NotifyFilters.LastWrite | NotifyFilters.Size
                | NotifyFilters.Attributes,
        };

        _watcher.Created += (_, e) => OnCreated(e.FullPath);
        _watcher.Changed += (_, e) => OnChanged(e.FullPath);

        _watcher.EnableRaisingEvents = true;
        Console.WriteLine("File change watcher started.");
    }

    public void Stop()
    {
        if (_watcher != null)
            _watcher.EnableRaisingEvents = false;
        _debounceTimer.Change(Timeout.Infinite, Timeout.Infinite);
    }

    private void OnChanged(string path)
    {
        const FileAttributes pinnedFlag = (FileAttributes)0x00080000; // FILE_ATTRIBUTE_PINNED
        const FileAttributes unpinnedFlag = (FileAttributes)0x00100000; // FILE_ATTRIBUTE_UNPINNED
        try
        {
            var attrs = File.GetAttributes(path);
            if ((attrs & pinnedFlag) != 0)
            {
                if (Directory.Exists(path))
                    OnDirectoryPinned?.Invoke(path);
                else
                    OnFilePinned?.Invoke(path);
                return;
            }

            if ((attrs & unpinnedFlag) != 0)
            {
                OnFileUnpinned?.Invoke(path);
                return;
            }

            if (Directory.Exists(path))
                return; // directories don't need upload processing
        }
        catch { return; }

        EnqueueIfNotPlaceholder(path, ChangeKind.Modified);
    }

    private void OnCreated(string path)
    {
        // For directories, strip Zone.Identifier and enqueue as Created
        if (Directory.Exists(path))
        {
            StripZoneIdentifier(path);
            _pending[path] = ChangeKind.Created;
            _debounceTimer.Change(_debounceInterval, Timeout.InfiniteTimeSpan);
            return;
        }

        EnqueueIfNotPlaceholder(path, ChangeKind.Created);
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
