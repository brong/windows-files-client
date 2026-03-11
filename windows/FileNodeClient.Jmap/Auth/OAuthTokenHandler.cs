using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FileNodeClient.Logging;

namespace FileNodeClient.Jmap.Auth;

/// <summary>
/// DelegatingHandler that adds Bearer token auth and auto-refreshes on 401.
/// Replaces TokenAuth for OAuth-based logins.
/// </summary>
public class OAuthTokenHandler : DelegatingHandler
{
    private string _accessToken;
    private string _refreshToken;
    private readonly string _tokenEndpoint;
    private readonly string _clientId;
    private DateTimeOffset _expiresAt;
    private readonly SemaphoreSlim _refreshLock = new(1, 1);

    /// <summary>
    /// Fired after a successful token refresh so the caller can persist updated tokens.
    /// </summary>
    public event Action<OAuthTokenHandler>? TokenRefreshed;

    public string AccessToken => _accessToken;
    public string RefreshToken => _refreshToken;
    public string TokenEndpoint => _tokenEndpoint;
    public string ClientId => _clientId;
    public DateTimeOffset ExpiresAt => _expiresAt;

    public OAuthTokenHandler(string accessToken, string refreshToken,
        string tokenEndpoint, string clientId, DateTimeOffset expiresAt)
        : base(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
        })
    {
        _accessToken = accessToken;
        _refreshToken = refreshToken;
        _tokenEndpoint = tokenEndpoint;
        _clientId = clientId;
        _expiresAt = expiresAt;
    }

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // Proactively refresh if token is about to expire (within 60s)
        if (DateTimeOffset.UtcNow >= _expiresAt.AddSeconds(-60) && !string.IsNullOrEmpty(_refreshToken))
            await TryRefreshAsync(cancellationToken);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
        var response = await base.SendAsync(request, cancellationToken);

        // If 401 and we have a refresh token, try refreshing once
        if (response.StatusCode == HttpStatusCode.Unauthorized && !string.IsNullOrEmpty(_refreshToken))
        {
            if (await TryRefreshAsync(cancellationToken))
            {
                // Clone the request with the new token and retry
                using var retry = await CloneRequestAsync(request);
                retry.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _accessToken);
                response.Dispose();
                response = await base.SendAsync(retry, cancellationToken);
            }
        }

        return response;
    }

    private async Task<bool> TryRefreshAsync(CancellationToken ct)
    {
        await _refreshLock.WaitAsync(ct);
        try
        {
            // Double-check: another thread may have already refreshed
            if (DateTimeOffset.UtcNow < _expiresAt.AddSeconds(-60))
                return true; // Already refreshed by another caller

            // Retry transient failures with exponential backoff (1s, 2s, 4s)
            const int maxRetries = 3;
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    var tokenResponse = await OAuthClient.RefreshTokenAsync(
                        _tokenEndpoint, _clientId, _refreshToken, ct);

                    _accessToken = tokenResponse.AccessToken;
                    if (tokenResponse.RefreshToken != null)
                        _refreshToken = tokenResponse.RefreshToken;
                    _expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

                    Log.Info($"[OAuth] Token refreshed, expires at {_expiresAt:u}");
                    TokenRefreshed?.Invoke(this);
                    return true;
                }
                catch (HttpRequestException ex) when (attempt < maxRetries - 1
                    && (!ex.StatusCode.HasValue || (int)ex.StatusCode.Value >= 500))
                {
                    // Transient (network error or 5xx) — retry after backoff
                    var delayMs = 1000 * (1 << attempt);
                    Log.Warn($"[OAuth] Token refresh attempt {attempt + 1} failed ({ex.Message}), retrying in {delayMs}ms");
                    await Task.Delay(delayMs, ct);
                }
                catch (HttpRequestException ex) when (ex.StatusCode.HasValue && (int)ex.StatusCode.Value >= 400 && (int)ex.StatusCode.Value < 500)
                {
                    // 4xx = permanent auth failure (revoked token, invalid grant) — don't retry
                    Log.Error($"[OAuth] Token refresh rejected ({(int)ex.StatusCode.Value}): {ex.Message}");
                    return false;
                }
            }

            Log.Error($"[OAuth] Token refresh failed after {maxRetries} attempts");
            return false;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            Log.Error($"[OAuth] Token refresh failed: {ex.Message}");
            return false;
        }
        finally
        {
            _refreshLock.Release();
        }
    }

    private static async Task<HttpRequestMessage> CloneRequestAsync(HttpRequestMessage original)
    {
        var clone = new HttpRequestMessage(original.Method, original.RequestUri);
        foreach (var header in original.Headers)
            clone.Headers.TryAddWithoutValidation(header.Key, header.Value);

        if (original.Content != null)
        {
            var bytes = await original.Content.ReadAsByteArrayAsync();
            clone.Content = new ByteArrayContent(bytes);
            foreach (var header in original.Content.Headers)
                clone.Content.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        return clone;
    }
}
