using FileNodeClient.Tests.Harness;
using FileNodeClient.Windows;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>Clean = unregister the sync root and delete everything local.</summary>
public class CleanTests
{
    [Fact]
    public async Task Clean_RemovesReadOnlyFoldersAndPlaceholders()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            var shared = s.AddFolder(HomeId, "Shared", readOnly: true);
            s.AddFile(shared, "r.txt", "read-only content");
            var nested = s.AddFolder(shared, "Nested", readOnly: true);
            s.AddFile(nested, "n.txt", "nested");
            s.AddFile(HomeId, "a.txt", "plain");
        });
        Assert.True(Directory.Exists(f.LocalPath("Shared", "Nested")));
        Assert.True((File.GetAttributes(f.LocalPath("Shared")) & FileAttributes.ReadOnly) != 0,
            "read-only shared folder should carry the ReadOnly attribute");

        f.Engine.Dispose();
        SyncEngine.Clean(f.SyncRootPath, f.AccountId);

        Assert.False(Directory.Exists(f.SyncRootPath), f.Log.Tail());
        Assert.False(f.Log.Contains("Failed to open for delete"), f.Log.Tail());
    }

    [Fact]
    public async Task Clean_RemovesHydratedFiles()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "a.txt", "content"));
        SyncEngine.HydratePlaceholder(f.LocalPath("a.txt"));
        Assert.Equal("content", File.ReadAllText(f.LocalPath("a.txt")));

        f.Engine.Dispose();
        SyncEngine.Clean(f.SyncRootPath, f.AccountId);

        Assert.False(Directory.Exists(f.SyncRootPath), f.Log.Tail());
    }
}
