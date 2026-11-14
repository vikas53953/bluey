using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>Crash-log redaction for the key formats Boo handles beyond OpenAI: Google/Gemini, Anthropic and GitHub tokens.</summary>
public sealed class CrashRedactionTests
{
    [Theory]
    [InlineData("GET https://generativelanguage.googleapis.com/v1beta/models?key=AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r failed",
        "GET https://generativelanguage.googleapis.com/v1beta/models?key=[redacted-key] failed")]
    [InlineData("x-goog-api-key: AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r", "x-goog-api-key: [redacted]")] // key=value pass wins after the bare-key pass
    [InlineData("sk-ant-api03-AbCdEfGhIjKlMnOpQrStUvWxYz0123456789-AA", "[redacted-key]")]
    [InlineData("token ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890 rejected", "token [redacted-key] rejected")]
    [InlineData("github_pat_11ABCDEFG0abcdefghijklmnopqrstuvwxyz_ABCDEFGHIJKLMNOPQRSTUVWXYZ", "[redacted-key]")]
    [InlineData("gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890", "[redacted-key]")]
    public void RedactRemovesGoogleAnthropicAndGitHubKeys(string input, string expected)
    {
        Assert.Equal(expected, CrashReport.Redact(input));
    }

    [Fact]
    public void RedactLeavesLookalikeWordsAlone()
    {
        const string text = "at Boo.Core.AIzaHelper.Run() ghp_short gho";
        Assert.Equal(text, CrashReport.Redact(text));
    }

    [Fact]
    public void FormatRedactsGoogleKeyInsideExceptionMessage()
    {
        var error = new InvalidOperationException("Gemini rejected key AIzaSyA1b2C3d4E5f6G7h8I9j0K1l2M3n4O5p6Q7r");
        var report = CrashReport.Format(error, "Dispatcher", DateTimeOffset.UtcNow, recovered: true);
        Assert.DoesNotContain("AIzaSy", report);
        Assert.Contains("[redacted-key]", report);
    }
}
