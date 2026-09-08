using System.Text.Json;
using FileNodeClient.Jmap;
using FileNodeClient.Tests.Fakes;
using Xunit;

namespace FileNodeClient.Tests;

/// <summary>JmapClient's own protocol logic, against a scripted HTTP surface.</summary>
public class JmapClientTests
{
    private static async Task<(JmapClient Client, FakeJmapHttpHandler Http)> ConnectAsync()
    {
        var http = new FakeJmapHttpHandler();
        var client = new JmapClient(http);
        await client.ConnectAsync(FakeJmapHttpHandler.SessionUrl);
        return (client, http);
    }

    /// <summary>
    /// A FileNode/query responder over <paramref name="ids"/>: honours position/limit,
    /// reports total, and lets the test decide the queryState for each call.
    /// </summary>
    private static Func<string, JsonElement, string, (string, object)> QueryResponder(
        string[] ids, Func<int, string> queryStateForCall)
    {
        int call = 0;
        return (method, args, _) =>
        {
            Assert.Equal("FileNode/query", method);
            var position = args.GetProperty("position").GetInt32();
            var limit = args.GetProperty("limit").GetInt32();
            var page = ids.Skip(position).Take(limit).ToArray();
            call++;
            return ("FileNode/query", new
            {
                accountId = FakeJmapHttpHandler.AccountId, queryState = queryStateForCall(call),
                canCalculateChanges = true, position, ids = page, total = ids.Length,
            });
        };
    }

    private static string[] Ids(int n) => Enumerable.Range(1, n).Select(i => $"n{i}").ToArray();

    [Fact]
    public async Task QueryAll_StablePaging_ReturnsEveryIdAndIsConsistent()
    {
        var (client, http) = await ConnectAsync();
        var ids = Ids(10_000);
        http.OnMethod = QueryResponder(ids, _ => "qs1");

        var (got, queryState, total, consistent) = await client.QueryAllFileNodeIdsAsync();

        Assert.Equal(ids, got);
        Assert.Equal("qs1", queryState);
        Assert.Equal(10_000, total);
        Assert.True(consistent);
        Assert.Equal(3, http.PostCount); // 4096 + 4096 + 1808
    }

    [Fact]
    public async Task QueryAll_QueryStateChangesBetweenPages_RestartsFromTheTop()
    {
        var (client, http) = await ConnectAsync();
        var ids = Ids(10_000);
        // Call 2 (second page of the first attempt) sees a different queryState: a
        // concurrent change shifted the pages. Everything after is stable at "qs2".
        http.OnMethod = QueryResponder(ids, call => call == 1 ? "qs1" : "qs2");

        var (got, queryState, _, consistent) = await client.QueryAllFileNodeIdsAsync();

        Assert.Equal(ids, got);
        Assert.Equal("qs2", queryState);
        Assert.True(consistent);
        Assert.Equal(2 + 3, http.PostCount); // aborted attempt + full second attempt
    }

    [Fact]
    public async Task QueryAll_NeverStable_GivesUpAndReportsInconsistent()
    {
        var (client, http) = await ConnectAsync();
        http.OnMethod = QueryResponder(Ids(10_000), call => $"qs{call}");

        var (_, _, _, consistent) = await client.QueryAllFileNodeIdsAsync();

        Assert.False(consistent);
        Assert.Equal(3 * 2, http.PostCount); // three attempts, each aborted on its second page
    }

    [Fact]
    public async Task QueryAll_ShortPageBeforeTotal_IsInconsistent()
    {
        var (client, http) = await ConnectAsync();
        var ids = Ids(10_000);
        // The server claims 10,000 but the second page comes back short, so the
        // enumeration ends with fewer ids than total: not a snapshot we can prune on.
        http.OnMethod = (method, args, _) =>
        {
            var position = args.GetProperty("position").GetInt32();
            var page = position == 0 ? ids.Take(4096).ToArray() : ids.Skip(position).Take(100).ToArray();
            return ("FileNode/query", new { accountId = FakeJmapHttpHandler.AccountId, queryState = "qs", canCalculateChanges = true, position, ids = page, total = ids.Length });
        };

        var (got, _, total, consistent) = await client.QueryAllFileNodeIdsAsync();

        Assert.False(consistent);
        Assert.Equal(10_000, total);
        Assert.Equal(4196, got.Length);
    }

    [Fact]
    public async Task ErrorMethodResponse_SurfacesAsException()
    {
        var (client, http) = await ConnectAsync();
        http.OnMethod = (_, _, _) => ("error", new { type = "serverFail", description = "boom" });

        var ex = await Assert.ThrowsAsync<JmapErrorException>(() => client.GetFileNodesAsync(["x"]));

        Assert.Equal("serverFail", ex.Type);
        Assert.Equal("FileNode/get", ex.Method);
        Assert.False(ex.IsPermanent);                 // a server failure is worth retrying
        Assert.Contains("serverFail", ex.Message);
    }

    [Theory]
    [InlineData("FileNode/set create", "notFound", true)]
    [InlineData("FileNode/set update", "invalidProperties", true)]
    [InlineData("FileNode/set create", "tooLarge", true)]
    [InlineData("FileNode/get", "invalidArguments", true)]
    [InlineData("Blob/set", "notFound", false)]        // an expired chunk: re-upload fixes it
    [InlineData("FileNode/set update", "alreadyExists", false)]
    [InlineData("FileNode/changes", "cannotCalculateChanges", false)]
    [InlineData("FileNode/set create", "forbidden", false)]
    [InlineData("FileNode/get", "serverFail", false)]
    [InlineData("FileNode/get", "rateLimit", false)]
    public void JmapErrorException_ClassifiesPermanence(string method, string type, bool permanent)
    {
        var ex = new JmapErrorException(method, type, "why");
        Assert.Equal(permanent, ex.IsPermanent);
        Assert.Equal($"{method} failed: {type} — why", ex.Message);
    }

    [Fact]
    public async Task Sse_SilentStream_IsAbandonedAfterTheIdleTimeout()
    {
        var (client, http) = await ConnectAsync();
        var previous = JmapClient.SseIdleTimeout;
        JmapClient.SseIdleTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            http.OnEventStream = async (w, ct) =>
            {
                await w.WriteAsync("event: state\ndata: {\"changed\":{\"acc1\":{\"FileNode\":\"s1\"}}}\n\n");
                await w.WriteAsync(": ping\n");
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);   // then the server goes silent forever
            };

            var received = new List<(string, string)>();
            var ex = await Assert.ThrowsAsync<IOException>(async () =>
            {
                await foreach (var change in client.WatchAllAccountChangesAsync())
                    received.Add(change);
            });

            Assert.Equal([("acc1", "s1")], received);
            Assert.Contains("idle", ex.Message);
        }
        finally { JmapClient.SseIdleTimeout = previous; }
    }

    [Fact]
    public async Task Sse_PingsKeepTheStreamAlive()
    {
        var (client, http) = await ConnectAsync();
        var previous = JmapClient.SseIdleTimeout;
        JmapClient.SseIdleTimeout = TimeSpan.FromMilliseconds(400);
        try
        {
            http.OnEventStream = async (w, ct) =>
            {
                for (int i = 0; i < 5; i++) { await Task.Delay(150, ct); await w.WriteAsync(": ping\n"); }
                await w.WriteAsync("event: state\ndata: {\"changed\":{\"acc1\":{\"FileNode\":\"s2\"}}}\n\n");
            };   // then the server closes the stream normally

            var received = new List<(string, string)>();
            await foreach (var change in client.WatchAllAccountChangesAsync())
                received.Add(change);

            Assert.Equal([("acc1", "s2")], received);
        }
        finally { JmapClient.SseIdleTimeout = previous; }
    }

    [Fact]
    public async Task GetChangesAndNodes_BatchesChangesAndGetsInOneRequest()
    {
        var (client, http) = await ConnectAsync();
        http.OnMethod = (method, args, _) => method switch
        {
            "FileNode/changes" => ("FileNode/changes", new
            {
                accountId = FakeJmapHttpHandler.AccountId, oldState = "1", newState = "2", hasMoreChanges = false,
                created = new[] { "c1" }, updated = new[] { "u1" }, destroyed = new[] { "d1" },
            }),
            "FileNode/get" => ("FileNode/get", new
            {
                accountId = FakeJmapHttpHandler.AccountId, state = "2",
                list = args.TryGetProperty("ids", out var idsEl) && idsEl.ValueKind == JsonValueKind.Array
                    ? idsEl.EnumerateArray().Select(e => new { id = e.GetString()!, parentId = "home", name = e.GetString() + ".txt", blobId = "b" }).ToArray()
                    : [new { id = "c1", parentId = "home", name = "c1.txt", blobId = "b" }, new { id = "u1", parentId = "home", name = "u1.txt", blobId = "b" }],
                notFound = Array.Empty<string>(),
            }),
            _ => ("error", new { type = "unknownMethod" }),
        };

        var (changes, created, updated, _) = await client.GetChangesAndNodesAsync("1");

        Assert.Equal("2", changes.NewState);
        Assert.Equal(["d1"], changes.Destroyed);
        Assert.Contains(created, n => n.Id == "c1");
        Assert.Contains(updated, n => n.Id == "u1");
        Assert.Equal(1, http.PostCount); // changes + get(s) chained in one POST
    }
}
