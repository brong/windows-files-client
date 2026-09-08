using FileNodeClient.Windows;
using Xunit;

namespace FileNodeClient.Tests;

/// <summary>SyncOutbox coalescing, ordering, pending indexes and persistence (no cfapi).</summary>
public sealed class SyncOutboxTests : IDisposable
{
    private readonly string _scopeKey = $"tests/outbox-{Guid.NewGuid():N}";
    private readonly List<SyncOutbox> _created = new();

    private SyncOutbox NewOutbox()
    {
        var o = new SyncOutbox(_scopeKey, "[outbox-test]");
        o.Load();
        _created.Add(o);
        return o;
    }

    private static string P(string name) => Path.Combine(@"C:\root", name);

    [Fact]
    public void ContentChanges_Coalesce_AndKeepTheFirstBaseBlobId()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false, baseBlobId: "base-v1");
        o.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false, baseBlobId: "base-v2-should-be-ignored");

        var (entries, _) = o.GetSnapshot();
        var e = Assert.Single(entries);
        Assert.Equal("base-v1", e.BaseBlobId);
        Assert.True(e.IsDirtyContent);
        Assert.True(o.HasPendingForNodeId("n1"));
        Assert.True(o.HasPendingForPath(P("a.txt")));
    }

    [Fact]
    public void NewFile_ThenDelete_LeavesNothing()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("new.txt"), nodeId: null, "text/plain", isFolder: false);
        o.EnqueueDelete(P("new.txt"), nodeId: null);

        Assert.Empty(o.GetSnapshot().Entries);
        Assert.Equal(0, o.PendingCount);
    }

    [Fact]
    public void EditedExistingFile_ThenDelete_BecomesADelete()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false, baseBlobId: "b");
        o.EnqueueDelete(P("a.txt"), "n1");

        var e = Assert.Single(o.GetSnapshot().Entries);
        Assert.True(e.IsDeleted);
        Assert.False(e.IsDirtyContent);
        Assert.Null(e.LocalPath);
        Assert.True(o.HasPendingForNodeId("n1"));
        Assert.False(o.HasPendingForPath(P("a.txt")));
    }

    [Fact]
    public void PendingCreate_Renamed_FollowsTheNewPath()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("draft.txt"), nodeId: null, "text/plain", isFolder: false);

        Assert.True(o.TryRenamePendingCreate(P("draft.txt"), P("final.txt")));

        var e = Assert.Single(o.GetSnapshot().Entries);
        Assert.Equal(P("final.txt"), e.LocalPath);
        Assert.True(o.HasPendingForPath(P("final.txt")));
        Assert.False(o.HasPendingForPath(P("draft.txt")));
        Assert.False(o.TryRenamePendingCreate(P("nothing.txt"), P("x.txt")));
    }

    [Fact]
    public void Move_OnPendingEdit_MarksLocationDirtyAndReindexes()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false, baseBlobId: "b");
        o.EnqueueMove("n1", P("a.txt"), P("sub\\b.txt"));

        var e = Assert.Single(o.GetSnapshot().Entries);
        Assert.True(e.IsDirtyContent);
        Assert.True(e.IsDirtyLocation);
        Assert.Equal(P("sub\\b.txt"), e.LocalPath);
        Assert.True(o.HasPendingForPath(P("sub\\b.txt")));
        Assert.False(o.HasPendingForPath(P("a.txt")));
    }

    [Fact]
    public void DequeueNext_OrdersFolderCreatesUploadsMovesThenDeletesDeepestFirst()
    {
        var o = NewOutbox();
        o.EnqueueDelete(P("gone\\deep\\x.txt"), "d2");
        o.EnqueueDelete(P("gone.txt"), "d1");
        o.EnqueueMove("m1", P("old.txt"), P("moved.txt"));
        o.EnqueueContentChange(P("upload.txt"), "u1", "text/plain", isFolder: false);
        o.EnqueueContentChange(P("NewFolder"), nodeId: null, null, isFolder: true);

        var order = new List<string>();
        for (var next = o.DequeueNext(); next != null; next = o.DequeueNext())
        {
            o.MarkProcessing(next.Id);
            order.Add(next.LocalPath ?? next.NodeId!);
        }

        Assert.Equal([P("NewFolder"), P("upload.txt"), P("moved.txt"), "d2", "d1"], order);
    }

    [Fact]
    public void Rejected_StaysPendingUntilDismissed()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false);
        var id = Assert.Single(o.GetSnapshot().Entries).Id;

        o.MarkRejected(id, "parent gone");
        Assert.True(o.HasPendingForNodeId("n1"));   // the engine must keep deferring server changes
        Assert.Equal(0, o.PendingCount);              // but the UI doesn't count it as pending work
        Assert.Equal(1, o.RejectedCount);
        Assert.Null(o.DequeueNext());

        o.DismissRejected(id);
        Assert.False(o.HasPendingForNodeId("n1"));
        Assert.Empty(o.GetSnapshot().Entries);
    }

    [Fact]
    public void Completed_ClearsAllIndexes()
    {
        var o = NewOutbox();
        o.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false);
        var id = Assert.Single(o.GetSnapshot().Entries).Id;

        o.MarkProcessing(id);
        o.MarkCompleted(id);

        Assert.False(o.HasPendingForNodeId("n1"));
        Assert.False(o.HasPendingForPath(P("a.txt")));
        Assert.Empty(o.GetSnapshot().Entries);
    }

    [Fact]
    public void Persistence_RoundTripsEntriesIncludingBaseBlobId()
    {
        var first = NewOutbox();
        first.EnqueueContentChange(P("a.txt"), "n1", "text/plain", isFolder: false, baseBlobId: "base-v1");
        first.EnqueueDelete(P("gone.txt"), "d1");
        first.Save();

        var second = NewOutbox();   // same scope → same outbox.json
        var entries = second.GetSnapshot().Entries;

        Assert.Equal(2, entries.Length);
        var edit = Assert.Single(entries, e => e.NodeId == "n1");
        Assert.Equal("base-v1", edit.BaseBlobId);
        Assert.Equal(P("a.txt"), edit.LocalPath);
        Assert.True(Assert.Single(entries, e => e.NodeId == "d1").IsDeleted);
        Assert.True(second.HasPendingForNodeId("n1"));
    }

    public void Dispose()
    {
        foreach (var o in _created) o.Dispose();
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Fastmail", "FileNodeClient", Path.Combine(_scopeKey.Split('/')));
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
    }
}
