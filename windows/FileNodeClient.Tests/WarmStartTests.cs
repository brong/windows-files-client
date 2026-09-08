using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using FileNodeClient.Windows;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>
/// RELIABILITY D1: on warm start the cache is checked against the disk, and the
/// discrepancies are acted on rather than trusted away.
/// </summary>
public class WarmStartTests
{
    [Fact]
    public async Task WarmStart_NothingChanged_RestoresMappingsWithoutWork()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        await f.PollAsync();

        await f.RestartAsync();

        Assert.True(f.Log.Contains("1 matched, 0 changed (0 queued for upload), 0 missing"), f.Log.Tail());
        Assert.Equal(0, f.Server.UploadCount);
        Assert.Empty(f.Log.Errors);
    }

    [Fact]
    public async Task WarmStart_FileMissingOnDisk_IsRecreatedFromServer_NotDeletedOnServer()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
        await f.PollAsync();

        await f.RestartAsync(whileDown: () =>
            SyncRoot.DeleteWithReparseBypass(f.LocalPath("a.txt"), isDirectory: false));

        Assert.True(f.Log.Contains("1 missing (will re-create from server)"), f.Log.Tail());
        Assert.True(File.Exists(f.LocalPath("a.txt")));
        Assert.NotNull(f.Server.TryGet(id));
        Assert.Equal(0, f.Server.DestroyCount);
    }

    [Fact]
    public async Task WarmStart_FolderMissingOnDisk_IsRecreatedWithItsChildren()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            var folder = s.AddFolder(HomeId, "F");
            s.AddFile(folder, "inner.txt", "v1");
        });
        await f.PollAsync();

        await f.RestartAsync(whileDown: () =>
        {
            SyncRoot.DeleteWithReparseBypass(f.LocalPath("F", "inner.txt"), isDirectory: false);
            SyncRoot.DeleteWithReparseBypass(f.LocalPath("F"), isDirectory: true);
        });

        Assert.True(f.Log.Contains("2 missing (will re-create from server)"), f.Log.Tail());
        Assert.True(File.Exists(f.LocalPath("F", "inner.txt")));
    }

    [Fact]
    public async Task WarmStart_HydratedFileEditedWhileDown_IsUploaded()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
        var path = f.LocalPath("a.txt");

        // Hydrate through cfapi (FETCH_DATA served by the engine from the fake server).
        SyncEngine.HydratePlaceholder(path);
        Assert.Equal("v1", File.ReadAllText(path));
        await f.PollAsync(); // cache now holds the hydrated file's size/mtime

        await f.RestartAsync(whileDown: () =>
        {
            File.WriteAllText(path, "v2 edited offline");
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(1));
        });

        Assert.True(f.Log.Contains("1 changed (1 queued for upload)"), f.Log.Tail());
        await f.WaitForOutboxIdleAsync();
        Assert.Equal("v2 edited offline", f.Server.ContentText(id));
        Assert.Equal(1, f.Server.ReplaceCount);

        // The offline write turned the placeholder into a plain file; the upload path
        // must convert it back so it stays a tracked cloud file.
        Assert.True((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0,
            "file should be a placeholder again after upload\n" + f.Log.Tail());
    }

    [Fact]
    public async Task WarmStart_TransientCatchUpFailure_RetriesInsteadOfRefetchingEverything()
    {
        var previous = SyncEngine.CatchUpRetryDelay;
        SyncEngine.CatchUpRetryDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
            var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
            await f.PollAsync();

            await f.RestartAsync(whileDown: () => f.Server.FailNextChangesCalls = 2);

            Assert.True(f.Log.Contains("Catch-up poll attempt 1 failed"), f.Log.Tail());
            Assert.True(f.Log.Contains("Catch-up poll attempt 2 failed"), f.Log.Tail());
            Assert.False(f.Log.Contains("falling back to full fetch"), f.Log.Tail());
            Assert.Equal(1, f.Log.Count("Creating placeholders"));   // only the fixture's initial cold start
            Assert.Equal(f.Server.State, f.State);
            Assert.NotNull(f.Engine.GetBlobIdForNodeId(id));
        }
        finally { SyncEngine.CatchUpRetryDelay = previous; }
    }

    [Fact]
    public async Task WarmStart_ServerUnreachable_KeepsCacheAndCatchesUpLater()
    {
        var previous = SyncEngine.CatchUpRetryDelay;
        SyncEngine.CatchUpRetryDelay = TimeSpan.FromMilliseconds(50);
        try
        {
            await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
            await f.PollAsync();
            var cachedState = f.State;

            await f.RestartAsync(whileDown: () =>
            {
                f.Server.FailNextChangesCalls = 100;          // still down when we come back
                f.Server.AddFile(HomeId, "meanwhile.txt", "x"); // a change we will only see later
            });

            Assert.True(f.Log.Contains("keeping cached state"), f.Log.Tail());
            Assert.False(f.Log.Contains("falling back to full fetch"), f.Log.Tail());
            Assert.Equal(cachedState, f.State);
            Assert.True(File.Exists(f.LocalPath("a.txt")));
            Assert.False(File.Exists(f.LocalPath("meanwhile.txt")));

            // Network returns: the normal poll catches up from the cached state.
            f.Server.FailNextChangesCalls = 0;
            await f.PollAsync();
            Assert.True(File.Exists(f.LocalPath("meanwhile.txt")));
            Assert.Equal(f.Server.State, f.State);
        }
        finally { SyncEngine.CatchUpRetryDelay = previous; }
    }

    [Fact]
    public async Task WarmStart_DehydratedPlaceholderWithOddMtime_IsNotUploaded()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        await f.PollAsync();

        // A dehydrated placeholder cannot have been edited locally; touching its mtime
        // must not queue an upload (which would hydrate it just to hash it).
        await f.RestartAsync(whileDown: () =>
            File.SetLastWriteTimeUtc(f.LocalPath("a.txt"), DateTime.UtcNow.AddMinutes(1)));

        Assert.True(f.Log.Contains("1 changed (0 queued for upload)"), f.Log.Tail());
        Assert.Equal(0, f.Server.UploadCount);
    }
}
