using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>
/// RELIABILITY D2: reconcile (the cannotCalculateChanges fallback) only prunes on
/// a consistent enumeration, and re-confirms each candidate before deleting.
/// </summary>
public class ReconcileTests
{
    /// <summary>Make the cached state uncalculable so the next warm start reconciles.</summary>
    private static void ForceReconcile(FakeJmapClient s) => s.OldestCalculableState = int.Parse(s.State) + 1;

    [Fact]
    public async Task Reconcile_InconsistentEnumeration_SkipsPrune()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.AddFile(HomeId, "a.txt", "a");
            s.AddFile(HomeId, "b.txt", "b");
        });
        var bId = f.Server.FindByName(HomeId, "b.txt")!.Id;
        await f.PollAsync();

        await f.RestartAsync(whileDown: () =>
        {
            f.Server.Rename(f.Server.FindByName(HomeId, "a.txt")!.Id, "a2.txt"); // something to reconcile
            ForceReconcile(f.Server);
            f.Server.EnumerationUnstable = true;
            f.Server.OmitFromEnumeration.Add(bId);   // paging artefact: b looks gone
        });

        Assert.True(f.Log.Contains("enumeration unstable"), f.Log.Tail());
        Assert.True(File.Exists(f.LocalPath("b.txt")), "must not prune on an inconsistent enumeration");
        Assert.True(File.Exists(f.LocalPath("a2.txt")), "the rest of reconcile still applies");
    }

    [Fact]
    public async Task Reconcile_NodeMissingFromEnumerationButAlive_IsNotPruned()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.AddFile(HomeId, "a.txt", "a");
            s.AddFile(HomeId, "b.txt", "b");
        });
        var bId = f.Server.FindByName(HomeId, "b.txt")!.Id;
        await f.PollAsync();

        await f.RestartAsync(whileDown: () =>
        {
            ForceReconcile(f.Server);
            f.Server.OmitFromEnumeration.Add(bId);   // consistent, but b slipped through the paging
        });

        Assert.True(f.Log.Contains("FileNode/get finds it"), f.Log.Tail());
        Assert.True(File.Exists(f.LocalPath("b.txt")));
    }

    [Fact]
    public async Task Reconcile_NodeReallyGone_IsPruned()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.AddFile(HomeId, "a.txt", "a");
            s.AddFile(HomeId, "b.txt", "b");
        });
        var bId = f.Server.FindByName(HomeId, "b.txt")!.Id;
        await f.PollAsync();

        await f.RestartAsync(whileDown: () =>
        {
            f.Server.Delete(bId);
            ForceReconcile(f.Server);
        });

        Assert.True(f.Log.Contains("Reconcile: gone from server"), f.Log.Tail());
        Assert.False(File.Exists(f.LocalPath("b.txt")));
        Assert.True(File.Exists(f.LocalPath("a.txt")));
    }
}
