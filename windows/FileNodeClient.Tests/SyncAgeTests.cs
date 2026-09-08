using FileNodeClient.Windows;
using Xunit;

namespace FileNodeClient.Tests;

public class SyncAgeTests
{
    private static readonly DateTime Now = new(2026, 9, 8, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(null, "never synced")]
    [InlineData(20, "synced just now")]
    [InlineData(5 * 60, "synced 5 min ago")]
    [InlineData(59 * 60, "synced 59 min ago")]
    [InlineData(3 * 3600, "synced 3 h ago")]
    [InlineData(26 * 3600, "synced 1 day ago")]
    [InlineData(3 * 86400, "synced 3 days ago")]
    public void Describe(int? secondsAgo, string expected)
    {
        DateTime? t = secondsAgo is { } s ? Now.AddSeconds(-s) : null;
        Assert.Equal(expected, SyncAge.Describe(t, Now));
    }

    [Fact]
    public void Stale_AfterAnHour_NotBefore_NeverForUnknown()
    {
        Assert.False(SyncAge.IsStale(Now.AddMinutes(-59), Now));
        Assert.True(SyncAge.IsStale(Now.AddMinutes(-61), Now));
        Assert.False(SyncAge.IsStale(null, Now));   // "never synced" is its own message, not a stall
    }

    [Fact]
    public void Shorten_KeepsShortText_TruncatesLongWithEllipsis()
    {
        Assert.Equal("ok", SyncAge.Shorten("ok", 10));
        Assert.Equal("", SyncAge.Shorten(null));
        var s = SyncAge.Shorten(new string('x', 100), 20);
        Assert.Equal(20, s.Length);
        Assert.EndsWith("…", s);
    }
}
