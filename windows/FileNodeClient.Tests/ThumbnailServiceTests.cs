using FileNodeClient.Tests.Fakes;
using FileNodeClient.Windows;
using Xunit;

namespace FileNodeClient.Tests;

/// <summary>ThumbnailService: metered gating, batching, cache, and the activity stats the UI shows.</summary>
public sealed class ThumbnailServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(@"C:\thumbs", Guid.NewGuid().ToString("N"));
    private readonly FakeJmapClient _server = new("thumbs") { SupportsBlobConvert = true };
    private readonly Dictionary<string, string> _blobByNode = new();

    private readonly string _diskDir = Path.Combine(Path.GetTempPath(), "FileNodeClientTests", "thumbcache-" + Guid.NewGuid().ToString("N"));
    private readonly string _previousDiskDir = ThumbnailService.DiskCacheDirectory;
    private readonly long _previousCap = ThumbnailService.MaxDiskCacheBytes;

    public ThumbnailServiceTests()
    {
        ThumbnailService.DiskCacheDirectory = _diskDir;
        var id = _server.AddFile(FakeJmapClient.HomeId, "photo.jpg", $"jpeg bytes {Guid.NewGuid():N}"); // unique blobId: the cache is static
        _blobByNode[id] = _server.Get(id).BlobId!;
        ThumbnailService.NetworkIsMetered = false;
        ThumbnailService.Register(_root, _server, node => _blobByNode.GetValueOrDefault(node));
    }

    private string NodeId => _blobByNode.Keys.Single();

    [Fact]
    public void Fetches_ConvertsOnce_CachesAndReportsStats()
    {
        var roots = new List<string>();
        ThumbnailService.ActivityChanged += roots.Add;
        try
        {
            var png = ThumbnailService.GetThumbnail(_root, NodeId, 96);
            Assert.NotNull(png);
            Assert.StartsWith("PNG:", System.Text.Encoding.ASCII.GetString(png!));
            Assert.Equal(1, _server.ConvertCount);

            var again = ThumbnailService.GetThumbnail(_root, NodeId, 96);
            Assert.Same(png, again);                  // served from cache
            Assert.Equal(1, _server.ConvertCount);

            var (inFlight, fetched, bytes, _) = ThumbnailService.GetStats(_root);
            Assert.Equal(0, inFlight);
            Assert.Equal(1, fetched);
            Assert.Equal(png.Length, bytes);
            Assert.Contains(_root, roots);            // begin + end both raised for our root
            Assert.True(roots.Count(r => r == _root) >= 2);
        }
        finally { ThumbnailService.ActivityChanged -= roots.Add; }
    }

    [Fact]
    public void Metered_ServesCacheOnly_AndDoesNotRememberAFailure()
    {
        ThumbnailService.NetworkIsMetered = true;
        Assert.Null(ThumbnailService.GetThumbnail(_root, NodeId, 96));
        Assert.Equal(0, _server.ConvertCount);

        // Back on an unmetered connection the very next request fetches (no failure cooldown).
        ThumbnailService.NetworkIsMetered = false;
        Assert.NotNull(ThumbnailService.GetThumbnail(_root, NodeId, 96));
        Assert.Equal(1, _server.ConvertCount);

        // And a cached thumbnail is still served while metered.
        ThumbnailService.NetworkIsMetered = true;
        Assert.NotNull(ThumbnailService.GetThumbnail(_root, NodeId, 96));
        Assert.Equal(1, _server.ConvertCount);
    }

    [Fact]
    public void UnknownNode_ReturnsNullWithoutServerTraffic()
    {
        Assert.Null(ThumbnailService.GetThumbnail(_root, "nope", 96));
        Assert.Equal(0, _server.ConvertCount);
    }

    [Fact]
    public void DiskCache_ServesAfterMemoryIsCleared_WithoutServerTraffic()
    {
        var png = ThumbnailService.GetThumbnail(_root, NodeId, 96)!;
        Assert.Equal(1, _server.ConvertCount);
        Assert.Single(Directory.GetFiles(_diskDir, "*.png"));

        ThumbnailService.ClearMemoryCache();   // e.g. the app restarted

        var again = ThumbnailService.GetThumbnail(_root, NodeId, 96);
        Assert.Equal(png, again);
        Assert.Equal(1, _server.ConvertCount);

        // Different size is a different key
        Assert.NotNull(ThumbnailService.GetThumbnail(_root, NodeId, 256));
        Assert.Equal(2, _server.ConvertCount);
        Assert.Equal(2, Directory.GetFiles(_diskDir, "*.png").Length);
    }

    [Fact]
    public void DiskCache_Metered_StillServesFromDisk()
    {
        ThumbnailService.GetThumbnail(_root, NodeId, 96);
        ThumbnailService.ClearMemoryCache();
        ThumbnailService.NetworkIsMetered = true;

        Assert.NotNull(ThumbnailService.GetThumbnail(_root, NodeId, 96));
        Assert.Equal(1, _server.ConvertCount);
    }

    [Fact]
    public void DiskCache_TrimsOldestFirst_ToUnderTheCap()
    {
        Directory.CreateDirectory(_diskDir);
        var now = DateTime.UtcNow;
        for (int i = 0; i < 10; i++)
        {
            var p = Path.Combine(_diskDir, $"blob{i:D2}_96.png");
            File.WriteAllBytes(p, new byte[100]);
            File.SetLastWriteTimeUtc(p, now.AddMinutes(-10 + i));   // blob00 is the oldest
        }
        ThumbnailService.MaxDiskCacheBytes = 500;   // 1000 bytes on disk → trim to ≤ 400

        ThumbnailService.TrimDiskCache();

        var left = Directory.GetFiles(_diskDir, "*.png").Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray();
        Assert.Equal(["blob06_96.png", "blob07_96.png", "blob08_96.png", "blob09_96.png"], left);
    }

    public void Dispose()
    {
        ThumbnailService.NetworkIsMetered = false;
        ThumbnailService.Unregister(_root);
        ThumbnailService.ClearMemoryCache();
        ThumbnailService.DiskCacheDirectory = _previousDiskDir;
        ThumbnailService.MaxDiskCacheBytes = _previousCap;
        try { if (Directory.Exists(_diskDir)) Directory.Delete(_diskDir, recursive: true); } catch { }
    }
}
