using FileNodeClient.Jmap.Models;
using FileNodeClient.Windows;
using Xunit;

namespace FileNodeClient.Tests;

/// <summary>The one BFS over a server snapshot: order, orphans, cycles, name collisions.</summary>
public class WalkFromHomeTests
{
    private const string Root = @"C:\root";

    private static FileNode Folder(string id, string? parent, string name) => new() { Id = id, ParentId = parent, Name = name };
    private static FileNode File(string id, string? parent, string name) => new() { Id = id, ParentId = parent, Name = name, BlobId = "b" };

    private static List<SyncEngine.TreeLevel> Walk(params FileNode[] nodes) =>
        SyncEngine.WalkFromHome(nodes, "home", Root, "[walk-test]");

    [Fact]
    public void ParentsComeBeforeChildren_WithLocalPathsChained()
    {
        var levels = Walk(
            Folder("home", null, ""),
            Folder("d1", "home", "Docs"),
            Folder("d2", "d1", "2026"),
            File("f1", "d2", "tax.pdf"),
            File("f0", "home", "top.txt"));

        Assert.Equal(["home", "d1", "d2"], levels.Select(l => l.ParentId));
        Assert.Equal(Root, levels[0].LocalParentPath);
        Assert.Equal(Path.Combine(Root, "Docs"), levels[1].LocalParentPath);
        Assert.Equal(Path.Combine(Root, "Docs", "2026"), levels[2].LocalParentPath);
        Assert.Equal("tax.pdf", levels[2].LocalNames["f1"]);
        Assert.Equal(2, levels[0].Children.Length);
    }

    [Fact]
    public void Orphans_AreNotVisited()
    {
        var levels = Walk(
            Folder("home", null, ""),
            File("f1", "home", "kept.txt"),
            Folder("lost", "nowhere", "Orphan"),
            File("f2", "lost", "unreachable.txt"));

        var visited = levels.SelectMany(l => l.Children.Select(c => c.Id)).ToList();
        Assert.Equal(["f1"], visited);
    }

    [Fact]
    public void MutualParents_AreUnreachable_AndTheWalkTerminates()
    {
        // A and B claim each other as parent: neither hangs off home. Must not hang.
        var levels = Walk(
            Folder("home", null, ""),
            Folder("A", "B", "A"),
            Folder("B", "A", "B"),
            Folder("entry", "home", "Entry"));

        Assert.Equal(["home", "entry"], levels.Select(l => l.ParentId));
    }

    [Fact]
    public void DuplicateIdCycle_IsNotDescended()
    {
        // A corrupt snapshot lists folder C twice, the second time as its own child.
        var levels = Walk(
            Folder("home", null, ""),
            Folder("C", "home", "C"),
            Folder("C", "C", "C"));

        Assert.Equal(["home", "C"], levels.Select(l => l.ParentId));   // C's level exists, but no level *under* the duplicate
    }

    [Fact]
    public void SiblingsDifferingOnlyByCase_GetDistinctLocalNames_Deterministically()
    {
        FileNode[] nodes =
        [
            Folder("home", null, ""),
            File("n2", "home", "README.md"),
            File("n1", "home", "Readme.md"),
        ];

        var first = Walk(nodes)[0].LocalNames;
        var again = Walk(nodes.Reverse().ToArray())[0].LocalNames;

        Assert.NotEqual(first["n1"], first["n2"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Readme.md", first["n1"]);          // lowest id keeps the plain name
        Assert.Equal(first["n1"], again["n1"]);          // input order doesn't matter
        Assert.Equal(first["n2"], again["n2"]);
    }

    [Fact]
    public void FolderNameCollision_PropagatesIntoChildPaths()
    {
        var levels = Walk(
            Folder("home", null, ""),
            Folder("d1", "home", "Photos"),
            Folder("d2", "home", "PHOTOS"),
            File("f", "d2", "x.jpg"));

        var photosLevel = levels.Single(l => l.ParentId == "d2");
        Assert.NotEqual(Path.Combine(Root, "PHOTOS"), photosLevel.LocalParentPath, StringComparer.OrdinalIgnoreCase);
        Assert.StartsWith(Path.Combine(Root, "PHOTOS"), photosLevel.LocalParentPath);
    }
}
