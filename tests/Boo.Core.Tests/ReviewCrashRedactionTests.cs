using System.Net.Http;
using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Review follow-up: the update lane added a GitHub token and the provider lane added Gemini keys, neither of
/// which the crash-log redactor knew about. Both can appear bare in HTTP exception text.
/// </summary>
public sealed class ReviewCrashRedactionTests
{
    [Theory]
    [InlineData("GET https://api.github.com/repos/x/y failed for ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", "GET https://api.github.com/repos/x/y failed for [redacted-token]")]
    [InlineData("token github_pat_11ABCDEFG0123456789_abcdefghijklmnopqrstuvwxyz rejected", "token [redacted-token] rejected")]
    [InlineData("oauth gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123 expired", "oauth [redacted-token] expired")]
    [InlineData("x-goog-api-key AIzaSyA1234567890abcdefghijklmnopqrstuv was rejected", "x-goog-api-key [redacted-key] was rejected")]
    [InlineData("https://generativelanguage.googleapis.com/v1beta/models?key=AIzaSyA1234567890abcdefghijklmnopqrstuv", "https://generativelanguage.googleapis.com/v1beta/models?key=[redacted-key]")]
    public void RedactRemovesGitHubTokensAndGoogleKeys(string input, string expected)
    {
        Assert.Equal(expected, CrashReport.Redact(input));
    }

    [Fact]
    public void RedactLeavesShortLookalikesAlone()
    {
        const string text = "ghp_short and AIzaShort are not secrets; neither is the word ghost_rider.";
        Assert.Equal(text, CrashReport.Redact(text));
    }

    [Fact]
    public void FormatRedactsGitHubTokenInsideExceptionText()
    {
        var error = new HttpRequestException("401 for ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789");
        var report = CrashReport.Format(error, "Updates", DateTimeOffset.UnixEpoch, recovered: true);
        Assert.DoesNotContain("ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789", report, StringComparison.Ordinal);
        Assert.Contains("[redacted-token]", report, StringComparison.Ordinal);
    }
}
