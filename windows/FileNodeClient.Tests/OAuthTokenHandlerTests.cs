using System.Net;
using FileNodeClient.Jmap.Auth;
using FileNodeClient.Tests.Fakes;
using Xunit;

namespace FileNodeClient.Tests;

/// <summary>
/// OAuthTokenHandler: proactive refresh near expiry, refresh-and-retry on 401, one
/// refresh under concurrency, and how a rejected refresh surfaces. The token endpoint
/// and the API are both scripted handlers.
/// </summary>
public sealed class OAuthTokenHandlerTests : IDisposable
{
    private const string TokenEndpoint = "https://auth.test/oauth/refresh";
    private readonly ScriptedHttpHandler _api = new();
    private readonly ScriptedHttpHandler _tokenServer = new();
    private readonly Func<HttpClient> _previousFactory = OAuthClient.HttpClientFactory;
    private int _refreshCalls;

    public OAuthTokenHandlerTests()
    {
        // OAuthClient talks to the token endpoint through its own HttpClient; route it here.
        OAuthClient.HttpClientFactory = () => new HttpClient(_tokenServer, disposeHandler: false);
        _tokenServer.Respond = req =>
        {
            Interlocked.Increment(ref _refreshCalls);
            return Task.FromResult(ScriptedHttpHandler.JsonResponse(new
            {
                access_token = $"AT{_refreshCalls}", token_type = "bearer", expires_in = 3600, refresh_token = $"RT{_refreshCalls}",
            }));
        };
        // The API accepts exactly the current access token.
        _api.Respond = req => Task.FromResult(new HttpResponseMessage(
            req.Headers.Authorization?.Parameter == ValidAccessToken ? HttpStatusCode.OK : HttpStatusCode.Unauthorized));
    }

    private string ValidAccessToken { get; set; } = "AT0";

    private (OAuthTokenHandler Handler, HttpClient Client) Build(DateTimeOffset expiresAt, string accessToken = "AT0")
    {
        var handler = new OAuthTokenHandler(accessToken, "RT0", TokenEndpoint, "client-1", expiresAt, _api);
        return (handler, new HttpClient(handler));
    }

    private static string Bearer(HttpRequestMessage r) => r.Headers.Authorization!.Parameter!;

    [Fact]
    public async Task ValidToken_IsSentAsIs_NoRefresh()
    {
        var (_, client) = Build(DateTimeOffset.UtcNow.AddHours(1));

        var resp = await client.GetAsync("https://api.test/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(0, _refreshCalls);
        Assert.Equal("AT0", Bearer(_api.Requests.Single()));
    }

    [Fact]
    public async Task TokenNearExpiry_IsRefreshedBeforeTheRequest_AndTheEventCarriesTheRotatedRefreshToken()
    {
        var (handler, client) = Build(DateTimeOffset.UtcNow.AddSeconds(30));
        OAuthTokenHandler? refreshed = null;
        handler.TokenRefreshed += h => refreshed = h;
        ValidAccessToken = "AT1";

        var resp = await client.GetAsync("https://api.test/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, _refreshCalls);
        Assert.Equal("AT1", Bearer(_api.Requests.Single()));
        Assert.NotNull(refreshed);
        Assert.Equal("AT1", refreshed!.AccessToken);
        Assert.Equal("RT1", refreshed.RefreshToken);
        Assert.True(refreshed.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(50));

        var form = await _tokenServer.Requests.Single().Content!.ReadAsStringAsync();
        Assert.Contains("grant_type=refresh_token", form);
        Assert.Contains("refresh_token=RT0", form);
        Assert.Contains("client_id=client-1", form);
    }

    [Fact]
    public async Task Unauthorized_TriggersRefreshAndOneRetry()
    {
        // Token looks fresh to us but the server has revoked it.
        var (_, client) = Build(DateTimeOffset.UtcNow.AddHours(1), accessToken: "revoked");
        ValidAccessToken = "AT1";

        var resp = await client.GetAsync("https://api.test/x");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(1, _refreshCalls);
        Assert.Equal(["revoked", "AT1"], _api.Requests.Select(Bearer));
    }

    [Fact]
    public async Task ConcurrentRequestsNearExpiry_ShareOneRefresh()
    {
        var (_, client) = Build(DateTimeOffset.UtcNow.AddSeconds(30));
        ValidAccessToken = "AT1";

        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => client.GetAsync("https://api.test/x")));

        Assert.All(responses, r => Assert.Equal(HttpStatusCode.OK, r.StatusCode));
        Assert.Equal(1, _refreshCalls);
        Assert.All(_api.Requests, r => Assert.Equal("AT1", Bearer(r)));
    }

    [Fact]
    public async Task RefreshRejected_DoesNotThrow_RequestFailsWithTheServersAnswer()
    {
        _tokenServer.Respond = _ =>
        {
            Interlocked.Increment(ref _refreshCalls);
            return Task.FromResult(ScriptedHttpHandler.JsonResponse(
                new { error = "invalid_grant", error_description = "ratchet or client_id mismatch" }, HttpStatusCode.BadRequest));
        };
        var (_, client) = Build(DateTimeOffset.UtcNow.AddSeconds(30));
        ValidAccessToken = "something-we-never-get"; // the stale AT0 is refused too

        var resp = await client.GetAsync("https://api.test/x");

        // Proactive refresh fails (4xx: not retried), the request goes out with the stale
        // token, the 401 triggers one more refresh attempt, and the caller sees the 401.
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(2, _refreshCalls);
    }

    public void Dispose() => OAuthClient.HttpClientFactory = _previousFactory;
}
