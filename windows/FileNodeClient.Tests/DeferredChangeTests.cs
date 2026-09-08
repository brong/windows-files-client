using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>
/// RELIABILITY I3, structural side: a server change for a node the outbox still
/// owns is deferred rather than dropped, and applied once the outbox lets go.
/// </summary>
public class DeferredChangeTests
{
    [Fact]
    public async Task ServerEdit_DuringPendingLocalRename_IsDeferredThenApplied()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;

        // Hold the outbox's FileNode/set move open so the entry stays pending.
        f.Server.Hold = new TaskCompletionSource();
        File.Move(f.LocalPath("a.txt"), f.LocalPath("local.txt"));
        await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForNodeId(id), "outbox owns the node");

        // Meanwhile the other device edits the same file.
        f.Server.SetContent(id, "v2");
        await f.PollAsync();

        Assert.True(f.Log.Contains("Deferring server change"), f.Log.Tail());
        Assert.NotEqual(Sha1Hex("v2"u8.ToArray()), f.Engine.GetBlobIdForNodeId(id)); // not applied yet

        var requested = false;
        f.Engine.SyncRequested += () => requested = true;

        // Release the upload path: the local rename reaches the server, the engine asks
        // for a poll, and the poll applies the deferred server-side edit.
        f.Server.Hold.SetResult();
        await f.WaitForOutboxIdleAsync();
        Assert.Equal("local.txt", f.Server.Get(id).Name);
        await f.WaitUntilAsync(() => requested, "SyncRequested after the outbox completed");

        await f.PollAsync();
        Assert.True(f.Log.Contains("Applying 1 deferred server change"), f.Log.Tail());
        Assert.True(File.Exists(f.LocalPath("local.txt")));
        Assert.Equal(Sha1Hex("v2"u8.ToArray()), f.Engine.GetBlobIdForNodeId(id));
        Assert.Empty(f.Log.Errors);
    }

    [Fact]
    public async Task ServerDelete_DuringPendingLocalRename_IsDeferred_ThenLocalIsRemoved()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "v1"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;

        f.Server.Hold = new TaskCompletionSource();
        File.Move(f.LocalPath("a.txt"), f.LocalPath("local.txt"));
        await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForNodeId(id), "outbox owns the node");

        f.Server.Delete(id);
        await f.PollAsync();
        Assert.True(File.Exists(f.LocalPath("local.txt")), "must not delete a file the outbox still owns");

        // The held move now fails with notFound; the outbox gives up on it, and the
        // deferred destroy is applied on the next poll: server wins a structural conflict.
        f.Server.Hold.SetResult();
        await f.WaitUntilAsync(() => !f.Engine.Outbox.HasPendingForNodeId(id), "outbox released the node",
            TimeSpan.FromSeconds(30));
        await f.PollAsync();
        Assert.False(File.Exists(f.LocalPath("local.txt")));
    }
}
