using System.Collections.Concurrent;
using FileNodeClient.Jmap;
using FileNodeClient.Logging;
using FileNodeClient.Tests.Fakes;
using FileNodeClient.Windows;

namespace FileNodeClient.Tests.Harness;

/// <summary>
/// One real cfapi sync root in a throw-away folder under the user profile, driven
/// by a real <see cref="SyncEngine"/> against a <see cref="FakeJmapClient"/>.
///
/// Every test gets a unique account id, so its sync-root registration, outbox and
/// node cache (all keyed by account/scope) never collide with another test or with
/// the real client. Teardown runs <see cref="SyncEngine.Clean"/>, which unregisters
/// the root and deletes the folder with the reparse-point bypass, then removes the
/// per-scope state under %LOCALAPPDATA%. If a test process is killed mid-run, the
/// leftovers are visible as "Test tXXXXXXXX" sync roots; the real client's startup
/// audit will offer to clean them.
///
/// Log lines from the engine are captured per fixture; <see cref="Log.Sink"/> is a
/// process-wide static, which is why xunit.runner.json disables parallelization.
/// </summary>
public sealed class SyncRootFixture : IAsyncDisposable
{
    public string AccountId { get; }
    public string SyncRootPath { get; }
    public string ScopeKey { get; }
    public FakeJmapClient Server { get; }
    public JmapQueue Queue { get; } = new();
    public SyncEngine Engine { get; private set; } = null!;
    public string State { get; private set; } = "";
    public LogCapture Log { get; } = new();

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(15);

    private SyncRootFixture()
    {
        AccountId = "t" + Guid.NewGuid().ToString("N")[..8];
        SyncRootPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "FileNodeClientTests", AccountId);
        ScopeKey = $"tests/{AccountId}";
        Server = new FakeJmapClient(AccountId);
    }

    /// <summary>Create the fixture, let <paramref name="seed"/> populate the fake server, then register + populate + connect.</summary>
    public static async Task<SyncRootFixture> StartAsync(Action<FakeJmapClient>? seed = null)
    {
        var f = new SyncRootFixture();
        f.Log.Attach();
        seed?.Invoke(f.Server);
        try
        {
            await f.StartEngineAsync();
        }
        catch
        {
            await f.DisposeAsync();
            throw;
        }
        return f;
    }

    private async Task StartEngineAsync()
    {
        Engine = new SyncEngine(SyncRootPath, Server, Queue, ScopeKey, $"Test {AccountId}");
        await Engine.RegisterAsync($"Test {AccountId}", AccountId);
        State = await Engine.PopulateAsync(CancellationToken.None);
        Engine.Connect();
    }

    /// <summary>What the supervisor's sync loop does on a push or SyncNow.</summary>
    public async Task PollAsync()
    {
        var (state, _) = await Engine.PollChangesAsync(State, CancellationToken.None);
        State = state;
    }

    /// <summary>
    /// Simulate the app exiting and relaunching: dispose the engine (the sync root
    /// stays registered, the cache and outbox persist), optionally mutate disk or
    /// server while "down", then build a new engine which warm-starts from cache.
    /// </summary>
    public async Task RestartAsync(Action? whileDown = null)
    {
        Engine.Dispose();
        whileDown?.Invoke();
        await StartEngineAsync();
    }

    public string LocalPath(params string[] parts) => Path.Combine([SyncRootPath, .. parts]);

    /// <summary>Wait until the outbox has nothing pending and nothing in flight.</summary>
    public async Task WaitForOutboxIdleAsync(TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultWait);
        while (DateTime.UtcNow < deadline)
        {
            var (entries, processing) = Engine.Outbox.GetSnapshot();
            if (entries.All(e => e.IsRejected) && processing.Count == 0)
                return;
            await Task.Delay(100);
        }
        var (left, _) = Engine.Outbox.GetSnapshot();
        throw new TimeoutException($"Outbox still busy: {string.Join(", ", left.Select(e => $"{Path.GetFileName(e.LocalPath)}[{e.LastError}]"))}\n{Log.Tail()}");
    }

    /// <summary>Wait until <paramref name="condition"/> holds (polled every 100 ms).</summary>
    public async Task WaitUntilAsync(Func<bool> condition, string what, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? DefaultWait);
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return;
            await Task.Delay(100);
        }
        throw new TimeoutException($"Timed out waiting for: {what}\n{Log.Tail()}");
    }

    public async ValueTask DisposeAsync()
    {
        try { Engine?.Dispose(); } catch { }
        try { SyncEngine.Clean(SyncRootPath, AccountId); } catch { }
        // Clean deletes the contents; the root itself can survive if a handle is still
        // closing (watcher, filter driver). Retry briefly so nothing is left behind.
        for (int attempt = 0; attempt < 40 && Directory.Exists(SyncRootPath); attempt++)
        {
            try { Directory.Delete(SyncRootPath, recursive: true); }
            catch { await Task.Delay(250); }
        }
        if (Directory.Exists(SyncRootPath))
            throw new IOException($"Test sync root not cleaned up: {SyncRootPath}\n{Log.Tail(80)}");
        try
        {
            var scopeDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Fastmail", "FileNodeClient", "tests", AccountId);
            if (Directory.Exists(scopeDir)) Directory.Delete(scopeDir, recursive: true);
        }
        catch { }
        Queue.Dispose();
        Log.Detach();
        await Task.CompletedTask;
    }
}

/// <summary>Captures the engine's log lines for assertions and failure diagnostics.</summary>
public sealed class LogCapture
{
    private readonly ConcurrentQueue<string> _lines = new();
    private Action<LogLevel, string>? _previous;
    private LogLevel _previousLevel;

    public void Attach()
    {
        _previous = FileNodeClient.Logging.Log.Sink;
        _previousLevel = FileNodeClient.Logging.Log.MinLevel;
        FileNodeClient.Logging.Log.MinLevel = LogLevel.Debug;
        FileNodeClient.Logging.Log.Sink = (level, msg) => _lines.Enqueue($"{DateTime.Now:HH:mm:ss.fff} [{level}] {msg}");
    }

    public void Detach()
    {
        FileNodeClient.Logging.Log.Sink = _previous;
        FileNodeClient.Logging.Log.MinLevel = _previousLevel;
    }

    public IReadOnlyList<string> Lines => _lines.ToArray();
    public bool Contains(string fragment) => _lines.Any(l => l.Contains(fragment, StringComparison.Ordinal));
    public int Count(string fragment) => _lines.Count(l => l.Contains(fragment, StringComparison.Ordinal));
    public IEnumerable<string> Errors => _lines.Where(l => l.Contains("[Error]") || l.Contains("[Warning]"));

    public async Task WaitForAsync(string fragment, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? SyncRootFixture.DefaultWait);
        while (DateTime.UtcNow < deadline)
        {
            if (Contains(fragment)) return;
            await Task.Delay(50);
        }
        throw new TimeoutException($"Log never contained \"{fragment}\"\n{Tail()}");
    }

    public string Tail(int n = 40) => string.Join("\n", _lines.TakeLast(n));
}
