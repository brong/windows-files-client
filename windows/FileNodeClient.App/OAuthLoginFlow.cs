using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;
using FileNodeClient.Jmap.Auth;

namespace FileNodeClient.App;

/// <summary>
/// Orchestrates the full OAuth flow from the App process:
/// discovery → registration → browser auth → token exchange.
/// </summary>
sealed class OAuthLoginFlow : IDisposable
{
    private HttpListener? _listener;
    private static readonly TimeSpan Timeout = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Runs the full OAuth flow for the given email address.
    /// Opens the user's browser for authentication and waits for the callback.
    /// </summary>
    public async Task<OAuthCredential> SignInAsync(IProgress<string>? progress = null,
        CancellationToken ct = default, string? sessionUrlOverride = null)
    {
        // Step 1: Discover OAuth metadata
        progress?.Report("Discovering server...");
        var (sessionUrl, metadata) = await OAuthDiscovery.DiscoverAsync(ct: ct);
        if (sessionUrlOverride != null)
            sessionUrl = sessionUrlOverride;

        if (metadata.RegistrationEndpoint == null)
            throw new InvalidOperationException("Server does not support dynamic client registration");

        // Step 2: Start local HTTP listener for callback
        var (listener, redirectUri, port) = StartListener();
        _listener = listener;

        try
        {
            // Step 3: Register client
            progress?.Report("Registering client...");
            var registration = await OAuthClient.RegisterAsync(
                metadata.RegistrationEndpoint,
                [redirectUri],
                OAuthClient.FilesScope,
                ct);

            // Step 4: Generate PKCE
            var (codeVerifier, codeChallenge) = OAuthClient.GeneratePkce();
            var state = Guid.NewGuid().ToString("N");

            // Step 5: Build authorization URL and open browser
            var authUrl = OAuthClient.BuildAuthorizationUrl(
                metadata.AuthorizationEndpoint,
                registration.ClientId,
                redirectUri,
                OAuthClient.FilesScope,
                codeChallenge,
                state);

            progress?.Report("Opening browser...");
            Process.Start(new ProcessStartInfo(authUrl) { UseShellExecute = true });

            // Step 6: Wait for callback
            progress?.Report("Waiting for authorization...");
            var (code, returnedState) = await WaitForCallbackAsync(listener, ct);

            if (returnedState != state)
                throw new InvalidOperationException("OAuth state mismatch — possible CSRF attack");

            // Step 7: Exchange code for tokens
            progress?.Report("Exchanging authorization code...");
            var tokenResponse = await OAuthClient.ExchangeCodeAsync(
                metadata.TokenEndpoint,
                registration.ClientId,
                code,
                redirectUri,
                codeVerifier,
                ct);

            if (tokenResponse.RefreshToken == null)
                throw new InvalidOperationException("Server did not return a refresh token. " +
                    "Ensure 'offline_access' scope is supported.");

            var expiresAt = DateTimeOffset.UtcNow.AddSeconds(tokenResponse.ExpiresIn);

            progress?.Report("Authenticated.");
            return new OAuthCredential(
                sessionUrl,
                tokenResponse.AccessToken,
                tokenResponse.RefreshToken,
                metadata.TokenEndpoint,
                registration.ClientId,
                expiresAt);
        }
        finally
        {
            StopListener();
        }
    }

    private static (HttpListener Listener, string RedirectUri, int Port) StartListener()
    {
        // Try a few ports in a range to find an available one
        var random = new Random();
        for (int attempt = 0; attempt < 10; attempt++)
        {
            var port = random.Next(49152, 65535);
            var prefix = $"http://127.0.0.1:{port}/";
            var listener = new HttpListener();
            listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return (listener, $"http://127.0.0.1:{port}/oauth/callback", port);
            }
            catch (HttpListenerException)
            {
                listener.Close();
            }
        }
        throw new InvalidOperationException("Could not find an available port for OAuth callback listener");
    }

    private static async Task<(string Code, string State)> WaitForCallbackAsync(
        HttpListener listener, CancellationToken ct)
    {
        using var timeoutCts = new CancellationTokenSource(Timeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

        var tcs = new TaskCompletionSource<(string, string)>();
        using var reg = linkedCts.Token.Register(() =>
            tcs.TrySetException(new OperationCanceledException("OAuth callback timed out")));

        _ = Task.Run(async () =>
        {
            try
            {
                var context = await listener.GetContextAsync();
                var query = context.Request.QueryString;
                var code = query["code"];
                var state = query["state"];
                var error = query["error"];

                // Send response page to browser
                var responseHtml = error != null
                    ? $"<html><body><h2>Authorization failed</h2><p>{WebUtility.HtmlEncode(error)}</p><p>You can close this tab.</p></body></html>"
                    : "<html><body><h2>Authorization successful</h2><p>You can close this tab and return to the application.</p></body></html>";

                var buffer = System.Text.Encoding.UTF8.GetBytes(responseHtml);
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.ContentLength64 = buffer.Length;
                await context.Response.OutputStream.WriteAsync(buffer);
                context.Response.Close();

                if (error != null)
                    tcs.TrySetException(new InvalidOperationException($"OAuth error: {error}"));
                else if (code == null || state == null)
                    tcs.TrySetException(new InvalidOperationException("Missing code or state in OAuth callback"));
                else
                    tcs.TrySetResult((code, state));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                tcs.TrySetException(ex);
            }
        }, CancellationToken.None);

        return await tcs.Task;
    }

    private void StopListener()
    {
        try
        {
            _listener?.Stop();
            _listener?.Close();
        }
        catch { }
        _listener = null;
    }

    public void Dispose()
    {
        StopListener();
    }
}
