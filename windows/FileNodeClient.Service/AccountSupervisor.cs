using System.Threading.Channels;
using FileNodeClient.Logging;
using FileNodeClient.Jmap;
using FileNodeClient.Jmap.Models;
using FileNodeClient.Windows;

namespace FileNodeClient.Service;

/// <summary>
/// Manages the sync lifecycle for a single JMAP account:
/// register sync root, populate placeholders, run push/poll loop.
/// </summary>
sealed class AccountSupervisor : IDisposable
{
    private const long DiskFullThresholdBytes = 500L * 1024 * 1024;   // Pause at <500MB
    private const long DiskResumeThresholdBytes = 1024L * 1024 * 1024; // Resume at >1GB

    private readonly IJmapClient _jmapClient;
    private readonly string _syncRootPath;
    private readonly string _displayName;
    private readonly bool _debug;
    private const string SyncNowSentinel = "\x01SYNC_NOW";
    private readonly Channel<string> _stateChannel = Channel.CreateBounded<string>(
        new BoundedChannelOptions(16) { FullMode = BoundedChannelFullMode.DropOldest });

    private JmapQueue? _queue;
    private SyncEngine? _engine;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    public string SyncRootPath => _syncRootPath;
    public string DisplayName => _displayName;
    public string AccountId => _jmapClient.AccountId;
    public string Username => _jmapClient.Username;

    public SyncStatus Status { get; private set; }
    public string? StatusDetail { get; private set; }
    public int PendingCount { get; private set; }
    public SyncOutbox? Outbox => _engine?.Outbox;
    public long? QuotaUsed { get; private set; }
    public long? QuotaLimit { get; private set; }
    public SyncPauseReason PauseReason => _engine?.PauseReason ?? SyncPauseReason.None;
    public int ActiveDownloadCount => _engine?.ActiveDownloadCount ?? 0;

    public List<(string FileName, DateTime StartedAt, int? Progress, long? TotalSize, bool IsPending)> GetActiveDownloadSnapshot()
        => _engine?.GetActiveDownloadSnapshot() ?? new();

    public event Action<AccountSupervisor>? StatusChanged;
    public event Action<AccountSupervisor>? StatusDetailChanged;
    public event Action<AccountSupervisor>? PendingCountChanged;
    public event Action<AccountSupervisor>? QuotaChanged;

    public AccountSupervisor(IJmapClient jmapClient, string syncRootPath, string displayName, bool debug)
    {
        _jmapClient = jmapClient;
        _syncRootPath = syncRootPath;
        _displayName = displayName;
        _debug = debug;
    }

    public async Task StartAsync(string? iconPath, bool clean, CancellationToken ct)
    {
        _queue = new JmapQueue();
        Log.Info($"[{_displayName}] Account: {_jmapClient.AccountId}");
        Log.Info($"[{_displayName}] Sync root: {_syncRootPath}");

        if (clean)
        {
            Log.Info($"[{_displayName}] Cleaning previous sync state...");
            SyncEngine.Clean(_syncRootPath, _jmapClient.Context.AccountId);
        }

        _engine = new SyncEngine(_syncRootPath, _jmapClient, _queue, _jmapClient.Context.ScopeKey, _displayName);
        if (clean)
            _engine.ClearOutbox();
        _engine.StatusChanged += OnEngineStatusChanged;
        _engine.StatusDetailChanged += OnEngineStatusDetailChanged;
        _engine.PendingCountChanged += OnEnginePendingCountChanged;
        _engine.ActiveDownloadCountChanged += OnEngineActiveDownloadCountChanged;

        // Register sync root
        Log.Info($"[{_displayName}] Registering sync root...");
        var trashUrl = _jmapClient.TrashUrl;
        Uri? recycleBinUri = trashUrl != null ? new Uri(trashUrl) : null;
        var webUrlTemplate = _jmapClient.WebUrlTemplate;
        await _engine.RegisterAsync(_displayName, _jmapClient.Context.AccountId, iconPath,
            recycleBinUri, webUrlTemplate);

        // Populate placeholders
        Log.Info($"[{_displayName}] Populating placeholders...");
        var state = await _engine.PopulateAsync(ct);
        Log.Info($"[{_displayName}] Initial sync complete. State: {state}");

        // Fetch initial quota info if available
        try
        {
            var quotas = await _jmapClient.GetQuotasAsync(ct);
            UpdateQuota(quotas);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Log.Error($"[{_displayName}] Initial quota fetch failed: {ex.Message}");
        }

        // Reconcile local changes made while offline
        _engine.ReconcileLocalChanges();

        // Register thumbnail service for this sync root
        ThumbnailService.Register(_syncRootPath, _jmapClient, _engine.GetBlobIdForNodeId);

        // Connect callbacks
        _engine.Connect();

        // Start push/poll loop
        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _loopTask = Task.Run(() => SyncLoopAsync(state, _loopCts.Token));
    }

    /// <summary>
    /// Push a state change from the shared push watcher.
    /// Empty string = "poll now regardless of state".
    /// </summary>
    internal void PushState(string state) => _stateChannel.Writer.TryWrite(state);

    /// <summary>
    /// User-requested one-shot sync. Bypasses metered pause (but not user-pause or disk-full).
    /// </summary>
    internal void SyncNow() => _stateChannel.Writer.TryWrite(SyncNowSentinel);

    internal void NotifyConnectivityLost() => _engine?.ReportConnectivityLost();
    internal void NotifyConnectivityRestored() => _engine?.ReportConnectivityRestored();

    public void Pause(SyncPauseReason reason) => _engine?.Pause(reason);
    public void Resume(SyncPauseReason reason) => _engine?.Resume(reason);

    /// <summary>
    /// Check free disk space on the sync root drive. Returns bytes free, or null on error.
    /// </summary>
    public long? GetFreeDiskSpace()
    {
        try
        {
            var driveInfo = new DriveInfo(Path.GetPathRoot(_syncRootPath)!);
            return driveInfo.AvailableFreeSpace;
        }
        catch (Exception ex)
        {
            Log.Warn($"[{_displayName}] Cannot read disk space: {ex.Message}");
            return null;
        }
    }

    private void CheckDiskSpaceAfterPoll()
    {
        var freeBytes = GetFreeDiskSpace();
        if (freeBytes == null) return;

        var isPausedForDisk = PauseReason.HasFlag(SyncPauseReason.DiskFull);
        if (!isPausedForDisk && freeBytes < DiskFullThresholdBytes)
        {
            Log.Warn($"[{_displayName}] Disk space low ({freeBytes / (1024 * 1024)}MB free) — pausing sync");
            _engine?.Pause(SyncPauseReason.DiskFull);
            StatusChanged?.Invoke(this);
        }
        else if (isPausedForDisk && freeBytes > DiskResumeThresholdBytes)
        {
            Log.Info($"[{_displayName}] Disk space restored ({freeBytes / (1024 * 1024)}MB free) — resuming sync");
            _engine?.Resume(SyncPauseReason.DiskFull);
            StatusChanged?.Invoke(this);
        }
    }

    public async Task StopAsync()
    {
        if (_loopCts != null)
        {
            _loopCts.Cancel();
            if (_loopTask != null)
            {
                try { await _loopTask; }
                catch (OperationCanceledException) { }
                catch (Exception ex) { Log.Error($"[{_displayName}] Loop stop error: {ex.Message}"); }
            }
            _loopCts.Dispose();
            _loopCts = null;
        }
    }

    private async Task SyncLoopAsync(string initialState, CancellationToken ct)
    {
        string currentState = initialState;
        int pollBackoffMs = 0;
        const int maxPollBackoffMs = 60_000;

        Log.Info($"[{_displayName}] Waiting for state changes...");

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var newState = await _stateChannel.Reader.ReadAsync(ct);
                var isSyncNow = newState == SyncNowSentinel;

                if (_engine!.IsPaused)
                {
                    // SyncNow bypasses user-pause and metered, but not disk-full
                    if (!isSyncNow || _engine.PauseReason.HasFlag(SyncPauseReason.DiskFull))
                    {
                        Log.Debug($"[{_displayName}] Sync paused ({_engine.PauseReason}), skipping poll");
                        continue;
                    }
                    Log.Info($"[{_displayName}] User-requested sync (paused: {_engine.PauseReason})");
                }

                // Empty string or SyncNow = forced poll
                if (!isSyncNow && newState.Length > 0
                    && string.Equals(newState, currentState, StringComparison.Ordinal))
                {
                    Log.Info($"[{_displayName}] State unchanged ({currentState}), skipping poll");
                    continue;
                }

                if (pollBackoffMs > 0)
                {
                    try { await Task.Delay(pollBackoffMs, ct); }
                    catch (OperationCanceledException) { break; }
                }

                try
                {
                    var pollResult = await _engine!.PollChangesAsync(currentState, ct);
                    currentState = pollResult.State;
                    UpdateQuota(pollResult.Quotas);
                    pollBackoffMs = 0; // Reset on success
                    CheckDiskSpaceAfterPoll();
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    Log.Error($"[{_displayName}] Change poll error: {ex.Message}");
                    pollBackoffMs = pollBackoffMs == 0 ? 5_000 : Math.Min(pollBackoffMs * 2, maxPollBackoffMs);
                    Log.Info($"[{_displayName}] Backing off poll for {pollBackoffMs / 1000}s");
                }
            }
            catch (OperationCanceledException) { break; }
            catch (ChannelClosedException) { break; }
        }
    }

    private void OnEngineStatusChanged(SyncStatus status)
    {
        Status = status;
        StatusChanged?.Invoke(this);
    }

    private void OnEngineStatusDetailChanged(string? detail)
    {
        StatusDetail = detail;
        StatusDetailChanged?.Invoke(this);
    }

    private void OnEnginePendingCountChanged(int count)
    {
        PendingCount = count;
        PendingCountChanged?.Invoke(this);
    }

    private void OnEngineActiveDownloadCountChanged(int count)
    {
        // When downloads start/stop, fire StatusChanged so the tray icon
        // reflects Syncing (blue) while downloads are active.
        StatusChanged?.Invoke(this);
    }

    private void UpdateQuota(Quota[]? quotas)
    {
        if (quotas == null || quotas.Length == 0)
            return;

        // Pick the "octets" quota with best scope: account > domain > global
        var octetsQuota = quotas
            .Where(q => q.ResourceType == "octets")
            .OrderBy(q => q.Scope switch { "account" => 0, "domain" => 1, _ => 2 })
            .FirstOrDefault();

        if (octetsQuota == null)
            return;

        var oldUsed = QuotaUsed;
        var oldLimit = QuotaLimit;
        QuotaUsed = octetsQuota.Used;
        QuotaLimit = octetsQuota.HardLimit;

        if (oldUsed != QuotaUsed || oldLimit != QuotaLimit)
            QuotaChanged?.Invoke(this);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        ThumbnailService.Unregister(_syncRootPath);

        _loopCts?.Cancel();
        try { _loopTask?.Wait(3000); } catch { }
        _loopCts?.Dispose();

        _engine?.Dispose();
        _queue?.Dispose();
    }
}
