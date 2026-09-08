namespace FileNodeClient.Windows;

/// <summary>
/// Words for "when did we last hear from the server" (RELIABILITY V1). A stalled
/// account and an up-to-date one both show Idle; this is what tells them apart.
/// </summary>
public static class SyncAge
{
    /// <summary>
    /// Idle for longer than this while believing we're connected is suspicious: push
    /// should have delivered something or the 15-minute safety poll should have run.
    /// </summary>
    public static readonly TimeSpan StaleAfter = TimeSpan.FromHours(1);

    public static bool IsStale(DateTime? lastSyncedUtc, DateTime nowUtc) =>
        lastSyncedUtc is { } t && nowUtc - t > StaleAfter;

    /// <summary>"synced just now", "synced 5 min ago", "synced 3 h ago", "synced 2 days ago", or "never synced".</summary>
    public static string Describe(DateTime? lastSyncedUtc, DateTime nowUtc)
    {
        if (lastSyncedUtc is not { } t) return "never synced";
        var age = nowUtc - t;
        if (age < TimeSpan.FromMinutes(1)) return "synced just now";
        if (age < TimeSpan.FromHours(1)) return $"synced {(int)age.TotalMinutes} min ago";
        if (age < TimeSpan.FromHours(24)) return $"synced {(int)age.TotalHours} h ago";
        var days = (int)age.TotalDays;
        return days == 1 ? "synced 1 day ago" : $"synced {days} days ago";
    }

    /// <summary>Trim a server reason to fit a list column; the tooltip keeps the full text.</summary>
    public static string Shorten(string? text, int max = 60)
    {
        if (string.IsNullOrEmpty(text)) return "";
        return text.Length <= max ? text : text[..(max - 1)] + "…";
    }
}
