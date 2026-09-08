using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using FileNodeClient.Windows;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

public class RepairAndIntegrityTests
{
    [Fact]
    public async Task Upload_CorruptedInTransit_IsDetectedAndReuploaded()
    {
        await using var f = await SyncRootFixture.StartAsync();
        f.Server.CorruptNextUpload = true;

        File.WriteAllText(f.LocalPath("new.txt"), "precious bytes");
        await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForPath(f.LocalPath("new.txt")), "queued");
        await f.WaitUntilAsync(() => f.Server.FindByName(HomeId, "new.txt") != null, "created on server", TimeSpan.FromSeconds(30));
        await f.WaitForOutboxIdleAsync(TimeSpan.FromSeconds(30));

        Assert.True(f.Log.Contains("failed verification"), f.Log.Tail());
        Assert.Equal(2, f.Server.UploadCount);   // the corrupt attempt, then the good one
        var node = f.Server.FindByName(HomeId, "new.txt")!;
        Assert.Equal("precious bytes", f.Server.ContentText(node.Id));
    }

    [Fact]
    public async Task VerifyAndRepair_RecreatesMissing_PrunesGone_RetriesRejected()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.AddFile(HomeId, "keep.txt", "k");
            s.AddFile(HomeId, "vanished.txt", "v");
            s.AddFolder(HomeId, "Gone");
        });
        var vanishedId = f.Server.FindByName(HomeId, "vanished.txt")!.Id;
        var goneId = f.Server.FindByName(HomeId, "Gone")!.Id;
        await f.PollAsync();

        // A rejected upload (folder deleted on the server before the create went through)...
        f.Server.Hold = new TaskCompletionSource();
        File.WriteAllText(f.LocalPath("Gone", "orphan.txt"), "o");
        await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForPath(f.LocalPath("Gone", "orphan.txt")), "queued");
        f.Server.Delete(goneId);
        f.Server.Hold.SetResult();
        await f.WaitUntilAsync(() => f.Engine.Outbox.RejectedCount == 1, "rejected");

        // ...the server deleting a file we haven't polled about, and a placeholder lost locally.
        f.Server.Delete(vanishedId);
        SyncRoot.DeleteWithReparseBypass(f.LocalPath("keep.txt"), isDirectory: false);
        Assert.False(File.Exists(f.LocalPath("keep.txt")));

        var (state, checkedCount, repaired, retried) = await f.Engine.VerifyAndRepairAsync(CancellationToken.None);

        Assert.Equal(f.Server.State, state);
        Assert.True(File.Exists(f.LocalPath("keep.txt")), "missing placeholder re-created\n" + f.Log.Tail(60));
        Assert.False(File.Exists(f.LocalPath("vanished.txt")), "server-deleted file pruned\n" + f.Log.Tail(60));
        Assert.True(repaired >= 2, $"repaired={repaired}\n" + f.Log.Tail(60));
        Assert.Equal(1, retried);
        Assert.True(checkedCount >= 1, $"checked={checkedCount}");   // keep.txt is the only tracked node left
        Assert.Contains("Verified", f.Engine.LastRepairSummary);
        Assert.Contains("retried 1", f.Engine.LastRepairSummary);
    }

    [Fact]
    public void NodeCache_DamagedFile_IsIgnored()
    {
        var scope = $"tests/cache-{Guid.NewGuid():N}";
        var root = Path.Combine(Path.GetTempPath(), "FileNodeClientTests", $"cache-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.txt"), "a");   // Save only records entries it can stat
        File.WriteAllText(Path.Combine(root, "b.txt"), "b");
        var paths = new Dictionary<string, string> { ["n1"] = Path.Combine(root, "a.txt"), ["n2"] = Path.Combine(root, "b.txt") };
        NodeCache.Save(scope, "home", "42", paths, root);
        var loaded = NodeCache.Load(scope);
        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.EntryCount);

        // Simulate a damaged write: header intact, one entry lost.
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fastmail", "FileNodeClient", Path.Combine(scope.Split('/')));
        var file = Path.Combine(dir, "nodecache.json");
        var json = File.ReadAllText(file);
        var damaged = System.Text.Json.Nodes.JsonNode.Parse(json)!;
        damaged["entries"]!.AsObject().Remove(damaged["entries"]!.AsObject().First().Key);
        File.WriteAllText(file, damaged.ToJsonString());

        try { Assert.Null(NodeCache.Load(scope)); }
        finally { Directory.Delete(dir, recursive: true); Directory.Delete(root, recursive: true); }
    }
}
