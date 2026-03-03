namespace FilesClient.Jmap.Auth;

/// <summary>
/// Shared utilities for OAuth HTTP calls.
/// </summary>
internal static class OAuthHelpers
{
    /// <summary>
    /// Like EnsureSuccessStatusCode but includes the response body in the exception
    /// so server error details are visible in the UI (the tray app has no console).
    /// </summary>
    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;

        var body = "";
        try
        {
            body = await response.Content.ReadAsStringAsync();
            // Trim to a reasonable length for display
            if (body.Length > 500)
                body = body[..500] + "...";
        }
        catch { }

        var status = (int)response.StatusCode;
        var url = response.RequestMessage?.RequestUri?.ToString() ?? "unknown URL";
        throw new InvalidOperationException(
            string.IsNullOrWhiteSpace(body)
                ? $"{operation}: HTTP {status} {response.ReasonPhrase} ({url})"
                : $"{operation}: HTTP {status} {response.ReasonPhrase} ({url}) — {body}");
    }
}
