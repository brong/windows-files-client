using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using FileNodeClient.Windows;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>
/// DESIGN §6 content conflicts, resolved at upload time by the outbox: the base
/// blobId frozen when the local edit was detected differs from the server's blobId
/// at upload, so either both versions are kept (conflict copy, the default) or the
/// newer one wins atomically via onExists:"newest" with conflict copy as fallback.
/// </summary>
public class ConflictTests
{
    /// <summary>
    /// Hydrate a.txt, pause sync, edit it locally (queued, base = v1), then let the
    /// other device change it on the server. Resuming runs the upload into a conflict.
    /// </summary>
    private static async Task<(SyncRootFixture F, string Id, string Path)> StageConflictAsync(
        ConflictResolution strategy, bool localIsNewer)
    {
        var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
        var path = f.LocalPath("a.txt");
        f.Engine.ConflictStrategy = strategy;

        CfApi.HydratePlaceholder(path);
        f.Engine.Pause(SyncPauseReason.UserRequested);

        File.WriteAllText(path, "local edit");
        await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForNodeId(id), "local edit queued");

        f.Server.SetContent(id, "server edit");          // server modified = now
        if (localIsNewer)
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(5));

        return (f, id, path);
    }

    private static async Task ResumeAndSettleAsync(SyncRootFixture f)
    {
        f.Engine.Resume(SyncPauseReason.UserRequested);
        await f.WaitForOutboxIdleAsync();
        await Task.Delay(1500); // let the watcher digest our own rename/create echoes
    }

    [Fact]
    public async Task ConflictCopy_KeepsBothVersions()
    {
        var (f, id, path) = await StageConflictAsync(ConflictResolution.ConflictCopy, localIsNewer: true);
        await using var _ = f;

        await ResumeAndSettleAsync(f);

        Assert.True(f.Log.Contains("CONFLICT on a.txt"), f.Log.Tail());

        // Server: the original node keeps the other device's edit; ours became a renamed copy.
        Assert.Equal("server edit", f.Server.ContentText(id));
        var copy = f.Server.FindByName(HomeId, "a (2).txt");
        Assert.NotNull(copy);
        Assert.Equal("local edit", f.Server.ContentText(copy!.Id));
        Assert.Equal(1, f.Server.CreateCount);

        // Local: a.txt is a fresh placeholder for the server's version, the copy holds our edit.
        Assert.True(File.Exists(path));
        Assert.Equal("server edit".Length, new FileInfo(path).Length);
        Assert.Equal("local edit", File.ReadAllText(f.LocalPath("a (2).txt")));
        Assert.NotNull(f.Engine.GetBlobIdForNodeId(copy.Id));

        // Our own File.Move / placeholder create must not echo to the server.
        Assert.Equal(0, f.Server.MoveCount);
        Assert.Empty(f.Log.Errors);
    }

    [Fact]
    public async Task NewestWins_LocalNewer_OverwritesServerInPlace()
    {
        var (f, id, path) = await StageConflictAsync(ConflictResolution.NewestWins, localIsNewer: true);
        await using var _ = f;

        await ResumeAndSettleAsync(f);

        Assert.True(f.Log.Contains("newest-wins, local newer"), f.Log.Tail());
        Assert.Equal("local edit", f.Server.ContentText(id));
        Assert.Null(f.Server.FindByName(HomeId, "a (2).txt"));
        Assert.Equal(0, f.Server.CreateCount);
        Assert.False(File.Exists(f.LocalPath("a (2).txt")));
        Assert.Equal(Sha1Hex("local edit"u8.ToArray()), f.Engine.GetBlobIdForNodeId(id));
    }

    [Fact]
    public async Task NewestWins_ServerNewer_FallsBackToConflictCopy()
    {
        var (f, id, path) = await StageConflictAsync(ConflictResolution.NewestWins, localIsNewer: false);
        await using var _ = f;

        await ResumeAndSettleAsync(f);

        Assert.True(f.Log.Contains("newest-wins declined"), f.Log.Tail());
        Assert.Equal("server edit", f.Server.ContentText(id));
        var copy = f.Server.FindByName(HomeId, "a (2).txt");
        Assert.NotNull(copy);
        Assert.Equal("local edit", f.Server.ContentText(copy!.Id));
        Assert.Equal("local edit", File.ReadAllText(f.LocalPath("a (2).txt")));
        Assert.Equal("server edit".Length, new FileInfo(path).Length);
    }

    [Fact]
    public async Task NoConflict_WhenServerUnchanged_UpdatesInPlace()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
        var path = f.LocalPath("a.txt");

        CfApi.HydratePlaceholder(path);
        File.WriteAllText(path, "local edit");
        try
        {
            await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForNodeId(id) || f.Server.ReplaceCount > 0, "edit queued");
        }
        catch (TimeoutException ex)
        {
            var (entries, processing) = f.Engine.Outbox.GetSnapshot();
            var dump = string.Join(" | ", entries.Select(e => $"path={e.LocalPath} node={e.NodeId} content={e.IsDirtyContent} attempts={e.AttemptCount} err={e.LastError} rejected={e.IsRejected}"));
            throw new TimeoutException($"{ex.Message}\nOUTBOX[{entries.Length}, processing {processing.Count}]: {dump}\nserver uploads={f.Server.UploadCount} creates={f.Server.CreateCount} replaces={f.Server.ReplaceCount}\nfile mtime={File.GetLastWriteTimeUtc(path):O} attrs={File.GetAttributes(path)}");
        }
        await f.WaitForOutboxIdleAsync();

        Assert.Equal("local edit", f.Server.ContentText(id));
        Assert.Equal(1, f.Server.ReplaceCount);
        Assert.Equal(0, f.Server.CreateCount);
        Assert.False(f.Log.Contains("CONFLICT"));
    }
}
