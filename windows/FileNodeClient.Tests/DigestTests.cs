using FileNodeClient.Tests.Fakes;
using FileNodeClient.Tests.Harness;
using FileNodeClient.Windows;
using Xunit;
using static FileNodeClient.Tests.Fakes.FakeJmapClient;

namespace FileNodeClient.Tests;

/// <summary>
/// RELIABILITY I1: a download whose bytes cannot be proven to match the server's
/// digest is never served as hydrated content, on every download path.
/// </summary>
public class DigestTests
{
    private const FileAttributes RecallOnDataAccess = (FileAttributes)0x00400000;
    private static bool IsDehydrated(string path) => (File.GetAttributes(path) & RecallOnDataAccess) != 0;

    private static byte[] Bytes(int n) => Enumerable.Range(0, n).Select(i => (byte)(i * 31 % 251)).ToArray();

    [Fact]
    public async Task SmallFile_GoodDigest_Hydrates()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "s.bin", Bytes(1000)));
        var path = f.LocalPath("s.bin");

        SyncEngine.HydratePlaceholder(path);

        Assert.Equal(Bytes(1000), File.ReadAllBytes(path));
        Assert.True(f.Log.Contains("Digest OK"), f.Log.Tail());
    }

    [Fact]
    public async Task SmallFile_CorruptDigest_IsRejected_ThenRecovers()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "s.bin", Bytes(1000)));
        var id = f.Server.FindByName(HomeId, "s.bin")!.Id;
        var path = f.LocalPath("s.bin");
        f.Server.CorruptDigests.Add(f.Server.Get(id).BlobId!);

        Assert.ThrowsAny<Exception>(() => SyncEngine.HydratePlaceholder(path));

        Assert.True(IsDehydrated(path), "nothing may be written on a digest mismatch");
        Assert.True(f.Log.Contains("Digest mismatch"), f.Log.Tail());
        Assert.False(f.Log.Contains("Digest OK"));

        f.Server.CorruptDigests.Clear();
        SyncEngine.HydratePlaceholder(path);
        Assert.Equal(Bytes(1000), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task LargeFile_GoodDigest_StreamsAndHydrates()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "big.bin", Bytes(300_000)));
        var path = f.LocalPath("big.bin");

        SyncEngine.HydratePlaceholder(path);

        Assert.Equal(Bytes(300_000), File.ReadAllBytes(path));
        Assert.True(f.Log.Contains("Digest OK (stream"), f.Log.Tail());
    }

    [Fact]
    public async Task LargeFile_CorruptDigest_FailsTheRead_AndDiscardsContentOnClose()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "big.bin", Bytes(300_000)));
        var id = f.Server.FindByName(HomeId, "big.bin")!.Id;
        var path = f.LocalPath("big.bin");
        f.Server.CorruptDigests.Add(f.Server.Get(id).BlobId!);

        Assert.ThrowsAny<Exception>(() => SyncEngine.HydratePlaceholder(path));

        Assert.True(f.Log.Contains("Digest mismatch (stream"), f.Log.Tail());
        // The streamed chunks were already handed to cfapi; once the reader's handle is
        // gone the engine dehydrates the placeholder so nothing unverified survives.
        await f.Log.WaitForAsync("Discarded unverified content");
        Assert.True(IsDehydrated(path));

        f.Server.CorruptDigests.Clear();
        SyncEngine.HydratePlaceholder(path);
        Assert.Equal(Bytes(300_000), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task LargeFile_TruncatedDownload_FailsIntegrity()
    {
        await using var f = await SyncRootFixture.StartAsync(s => s.AddFile(HomeId, "big.bin", Bytes(300_000)));
        var path = f.LocalPath("big.bin");
        f.Server.TruncateDownloadsTo = 200_000;   // the server connection drops mid-stream

        Assert.ThrowsAny<Exception>(() => SyncEngine.HydratePlaceholder(path));

        Assert.True(f.Log.Contains("Short download: 200000/300000"), f.Log.Tail());
        await f.Log.WaitForAsync("Discarded unverified content");
        Assert.True(IsDehydrated(path));

        f.Server.TruncateDownloadsTo = null;
        SyncEngine.HydratePlaceholder(path);
        Assert.Equal(Bytes(300_000), File.ReadAllBytes(path));
    }

    [Fact]
    public async Task NoDigestSupport_DegradesToUnverified()
    {
        await using var f = await SyncRootFixture.StartAsync(s =>
        {
            s.SupportsDigests = false;
            s.AddFile(HomeId, "big.bin", Bytes(300_000));
        });
        var path = f.LocalPath("big.bin");

        SyncEngine.HydratePlaceholder(path);

        Assert.Equal(Bytes(300_000), File.ReadAllBytes(path));
        Assert.False(f.Log.Contains("Digest"), f.Log.Tail());
    }
}
