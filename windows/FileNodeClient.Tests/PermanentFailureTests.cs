using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>RELIABILITY D4: a failure that can never succeed is surfaced, not retried forever.</summary>
public class PermanentFailureTests
{
    [Fact]
    public async Task NewFile_WhoseFolderVanishedOnServer_IsRejectedWithAReason()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFolder(HomeId, "Gone"));
        var folderId = f.Server.FindByName(HomeId, "Gone")!.Id;

        // Hold the create so the folder can disappear on the server first (no poll yet).
        f.Server.Hold = new TaskCompletionSource();
        File.WriteAllText(f.LocalPath("Gone", "new.txt"), "hello");
        await f.WaitUntilAsync(() => f.Engine.Outbox.HasPendingForPath(f.LocalPath("Gone", "new.txt")), "create queued");
        f.Server.Delete(folderId);
        f.Server.Hold.SetResult();

        await f.WaitUntilAsync(() => f.Engine.Outbox.RejectedCount == 1, "entry rejected");

        var rejected = Assert.Single(f.Engine.Outbox.GetRejectedSnapshot());
        Assert.Equal(f.LocalPath("Gone", "new.txt"), rejected.LocalPath);
        Assert.Contains("folder no longer exists", rejected.RejectionReason);
        Assert.Equal(0, f.Engine.Outbox.PendingCount);          // not looping at the backoff cap
        Assert.True(f.Log.Contains("permanent failure"), f.Log.Tail());
    }
}
