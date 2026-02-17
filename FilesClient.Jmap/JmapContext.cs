namespace FilesClient.Jmap;

/// <summary>
/// Identifies a specific account within a JMAP login session.
/// Provides a globally unique scope key for IDs that are only
/// unique within a single account.
/// </summary>
public record JmapContext(string Username, string AccountId)
{
    /// <summary>
    /// Globally unique key for this login+account combination.
    /// Format: "username/accountId" (e.g. "brong@fastmaildev.com/v358041ae")
    /// Username is the login identity (future: OAuth lookup key).
    /// Use to scope nodeIds and blobIds in shared data structures.
    /// </summary>
    public string ScopeKey { get; } = $"{Username}/{AccountId}";

    /// <summary>
    /// Returns a globally unique identifier by prefixing a
    /// per-account ID (nodeId, blobId) with the scope key.
    /// </summary>
    public string ScopedId(string id) => $"{ScopeKey}/{id}";
}
