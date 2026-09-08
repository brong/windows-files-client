using System.Text.Json;

namespace FileNodeClient.Jmap;

/// <summary>
/// A JMAP-level failure: a method-level <c>error</c> response, or a per-item
/// SetError from <c>notCreated</c>/<c>notUpdated</c>/<c>notDestroyed</c>. Carries
/// the error <see cref="Type"/> so callers can decide whether retrying can ever
/// help (RELIABILITY D4) instead of pattern-matching the message.
/// </summary>
public class JmapErrorException : InvalidOperationException
{
    /// <summary>What failed, e.g. "FileNode/set create" or "FileNode/changes".</summary>
    public string Method { get; }
    /// <summary>The JMAP error type, e.g. "notFound", "invalidProperties", "serverFail".</summary>
    public string Type { get; }
    public string? Description { get; }

    public JmapErrorException(string method, string type, string? description)
        : base($"{method} failed: {type}{(string.IsNullOrEmpty(description) ? "" : " — " + description)}")
    {
        Method = method;
        Type = type;
        Description = description;
    }

    /// <summary>Build from a method-level error response's arguments object.</summary>
    public static JmapErrorException FromMethodError(string method, JsonElement args)
    {
        var type = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("type", out var t) ? t.GetString() ?? "unknown" : "unknown";
        var description = args.ValueKind == JsonValueKind.Object && args.TryGetProperty("description", out var d) ? d.GetString() : null;
        return new JmapErrorException(method, type, description ?? (args.ValueKind == JsonValueKind.Object && type == "unknown" ? args.ToString() : null));
    }

    /// <summary>
    /// Retrying the same request can never succeed: the target is gone, the
    /// arguments are wrong, or the payload is too big. Everything else (server
    /// failure, rate limits, auth, unknown types) is treated as transient. Mirrors
    /// the Apple client's JmapError.isRetriable. A Blob/set notFound is the one
    /// exception: it means an uploaded chunk expired, and re-uploading fixes it.
    /// </summary>
    public bool IsPermanent =>
        Type is "notFound" or "invalidProperties" or "invalidArguments" or "tooLarge"
                or "unknownMethod" or "invalidResultReference"
        && !(Method.StartsWith("Blob/", StringComparison.Ordinal) && Type == "notFound");
}
