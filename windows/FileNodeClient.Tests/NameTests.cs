using System.Text;
using FileNodeClient.Windows;
using Xunit;

namespace FileNodeClient.Tests;

/// <summary>
/// Local-name round-trips (DESIGN §14): invalid characters, reserved names, trailing
/// dots/spaces, NFC normalization, and the reversible case-collision suffix.
/// </summary>
public class NameTests
{
    private const char Marker = '﻿';

    [Theory]
    [InlineData("report.txt")]
    [InlineData("a/b:c*d?e\"f<g>h|i.txt")]
    [InlineData("trailing dot.")]
    [InlineData("trailing space ")]
    [InlineData("CON")]
    [InlineData("nul.txt")]
    [InlineData("..")]
    [InlineData("")]
    [InlineData("literal (2).txt")]
    public void SanitizeThenDesanitize_RoundTrips(string serverName)
    {
        var local = PlaceholderManager.SanitizeName(serverName);

        Assert.DoesNotContain(local, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.NotEqual("..", local);
        Assert.NotEqual("", local);
        Assert.False(local.EndsWith(' ') || local.EndsWith('.'), "Windows would strip a trailing dot/space");
        Assert.Equal(serverName, PlaceholderManager.DesanitizeName(local));
    }

    [Fact]
    public void ServerNfdName_BecomesNfcLocally_AndGoesBackAsNfc()
    {
        var nfd = "café.txt".Normalize(NormalizationForm.FormD);
        var nfc = "café.txt".Normalize(NormalizationForm.FormC);

        var local = PlaceholderManager.SanitizeName(nfd);

        Assert.True(local.IsNormalized(NormalizationForm.FormC));
        Assert.Equal(nfc, local);
        Assert.Equal(nfc, PlaceholderManager.DesanitizeName(nfd));   // an NFD local name never reaches the server as NFD
    }

    [Fact]
    public void CollisionSuffix_IsInvisibleMarkerTagged_AndStrippedOnTheWayBack()
    {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Report.txt" };

        var unique = PlaceholderManager.MakeUniqueName("report.txt", taken.Contains);

        Assert.Equal($"report{Marker}(2).txt", unique);
        Assert.Equal("report.txt", PlaceholderManager.DesanitizeName(unique));

        taken.Add(unique);
        Assert.Equal($"REPORT{Marker}(3).TXT", PlaceholderManager.MakeUniqueName("REPORT.TXT", taken.Contains));
    }

    [Fact]
    public void MakeUniqueName_NoCollision_IsUnchanged()
    {
        Assert.Equal("free.txt", PlaceholderManager.MakeUniqueName("free.txt", _ => false));
    }

    [Fact]
    public void UsersLiteralParenthesisedNumber_IsNotMistakenForACollisionSuffix()
    {
        // No marker → not ours → untouched on the way back.
        Assert.Equal("photo (2).jpg", PlaceholderManager.DesanitizeName("photo (2).jpg"));
    }

    [Fact]
    public void CaseVariantSiblings_GetDistinctLocalNames_ThatReverseToTheirOwnServerNames()
    {
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var a = PlaceholderManager.MakeUniqueName(PlaceholderManager.SanitizeName("Readme.md"), used.Contains); used.Add(a);
        var b = PlaceholderManager.MakeUniqueName(PlaceholderManager.SanitizeName("README.md"), used.Contains); used.Add(b);

        Assert.NotEqual(a, b, StringComparer.OrdinalIgnoreCase);
        Assert.Equal("Readme.md", PlaceholderManager.DesanitizeName(a));
        Assert.Equal("README.md", PlaceholderManager.DesanitizeName(b));
    }
}
