using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>
/// Server-side changes arriving through FileNode/changes → PollChangesAsync →
/// ApplyChangedNodeAsync / RemoveNode. The "other device" is the fake server.
/// </summary>
public class ServerChangeTests
{
    [Fact]
    public async Task Populate_CreatesPlaceholdersForTree()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            var folder = s.AddFolder(HomeId, "Docs");
            s.AddFile(folder, "report.txt", "twelve chars");
            s.AddFile(HomeId, "top.txt", "x");
        });

        Assert.True(Directory.Exists(f.LocalPath("Docs")));
        Assert.Equal(12, new FileInfo(f.LocalPath("Docs", "report.txt")).Length);
        Assert.Equal(1, new FileInfo(f.LocalPath("top.txt")).Length);
        Assert.Empty(f.Log.Errors);
    }

    [Fact]
    public async Task ServerRename_MovesLocalPlaceholder_WithoutEchoingToServer()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "hello"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;

        f.Server.Rename(id, "b.txt");
        await f.PollAsync();

        Assert.False(File.Exists(f.LocalPath("a.txt")));
        Assert.True(File.Exists(f.LocalPath("b.txt")));

        // The watcher sees our own Directory/File.Move; the mapping was updated first,
        // so it must not round-trip a FileNode/set move back to the server.
        await Task.Delay(1500);
        Assert.Equal(0, f.Server.MoveCount);
        Assert.Equal("b.txt", f.Server.Get(id).Name);
    }

    [Fact]
    public async Task ServerMove_IntoFolder_MovesLocalPlaceholder()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.AddFolder(HomeId, "Dest");
            s.AddFile(HomeId, "a.txt", "hello");
        });
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
        var dest = f.Server.FindByName(HomeId, "Dest")!.Id;

        f.Server.Move(id, dest);
        await f.PollAsync();

        Assert.False(File.Exists(f.LocalPath("a.txt")));
        Assert.True(File.Exists(f.LocalPath("Dest", "a.txt")));
        await Task.Delay(1500);
        Assert.Equal(0, f.Server.MoveCount);
    }

    [Fact]
    public async Task ServerMove_IntoReadOnlyFolder_LiftsTheAclAroundTheMove()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.AddFolder(HomeId, "Shared", readOnly: true);
            s.AddFile(HomeId, "a.txt", "hello");
        });
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;
        var shared = f.Server.FindByName(HomeId, "Shared")!.Id;

        f.Server.Move(id, shared);
        await f.PollAsync();

        Assert.True(File.Exists(f.LocalPath("Shared", "a.txt")));
        Assert.Empty(f.Log.Errors);
    }

    [Fact]
    public async Task ServerDelete_RemovesLocalPlaceholder()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "hello"));
        var id = f.Server.FindByName(HomeId, "a.txt")!.Id;

        f.Server.Delete(id);
        await f.PollAsync();

        Assert.False(File.Exists(f.LocalPath("a.txt")));
        Assert.Null(f.Engine.GetBlobIdForNodeId(id));
    }

    [Fact]
    public async Task ServerDeleteFolder_RemovesTreeAndDescendantMappings()
    {
        string fileId = "";
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            var folder = s.AddFolder(HomeId, "Gone");
            fileId = s.AddFile(folder, "inner.txt", "hello");
        });
        var folderId = f.Server.FindByName(HomeId, "Gone")!.Id;
        Assert.NotNull(f.Engine.GetBlobIdForNodeId(fileId));

        f.Server.Delete(folderId);
        await f.PollAsync();

        Assert.False(Directory.Exists(f.LocalPath("Gone")));
        Assert.Null(f.Engine.GetBlobIdForNodeId(fileId));
    }

    [Fact]
    public async Task ServerCreate_AfterStart_CreatesPlaceholderWithServerSize()
    {
        await using var f = await SyncRootFixture.StartAsync();

        var folder = f.Server.AddFolder(HomeId, "New");
        f.Server.AddFile(folder, "n.txt", "0123456789");
        await f.PollAsync();

        Assert.Equal(10, new FileInfo(f.LocalPath("New", "n.txt")).Length);
    }

    [Fact]
    public async Task Poll_PagesThroughHasMoreChanges_AndLandsOnTheServerState()
    {
        await using var f = await SyncRootFixture.StartAsync();
        f.Server.ChangesPageSize = 1;   // every change record is its own page (R3)

        f.Server.AddFile(HomeId, "p1.txt", "a");
        f.Server.AddFile(HomeId, "p2.txt", "b");
        f.Server.AddFile(HomeId, "p3.txt", "c");
        await f.PollAsync();

        Assert.True(File.Exists(f.LocalPath("p1.txt")));
        Assert.True(File.Exists(f.LocalPath("p2.txt")));
        Assert.True(File.Exists(f.LocalPath("p3.txt")));
        Assert.Equal(f.Server.State, f.State);
    }
}
