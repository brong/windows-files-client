namespace FileNodeClient.Jmap.Auth;

public class TokenAuth : DelegatingHandler
{
    private readonly string _token;

    public TokenAuth(string token)
        : base(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            PooledConnectionIdleTimeout = TimeSpan.FromMinutes(2),
            EnableMultipleHttp2Connections = true,
        })
    {
        _token = token;
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new("Bearer", _token);
        return base.SendAsync(request, cancellationToken);
    }
}
