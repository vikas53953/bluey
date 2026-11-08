using Boo.Core;

namespace Boo.Core.Tests;

public sealed class UpdateStatusTextTests
{
    private const string Token = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";

    [Fact]
    public void Not_checked_text()
    {
        Assert.Equal("Updates have not been checked yet.", UpdateStatusText.Describe(UpdateCheckOutcome.NotChecked));
    }

    [Fact]
    public void Disabled_text_says_github_is_never_contacted()
    {
        Assert.Equal(
            "Automatic update checks are off. Boo never contacts GitHub unless you turn them on.",
            UpdateStatusText.Describe(UpdateCheckOutcome.Disabled));
    }

    [Fact]
    public void Portable_build_text_points_at_the_installer()
    {
        Assert.Equal(
            "This is a portable build. Updates apply to the installed version only (run Boo-win-Setup.exe).",
            UpdateStatusText.Describe(UpdateCheckOutcome.PortableBuild));
    }

    [Fact]
    public void Missing_token_text()
    {
        Assert.Equal(
            "Add a GitHub token with read access to the Boo repository to check for updates.",
            UpdateStatusText.Describe(UpdateCheckOutcome.MissingToken));
    }

    [Fact]
    public void Checking_text()
    {
        Assert.Equal("Checking GitHub Releases…", UpdateStatusText.Describe(UpdateCheckOutcome.Checking));
    }

    [Fact]
    public void Up_to_date_text_uses_current_version()
    {
        Assert.Equal("Boo 0.1.0 is up to date.", UpdateStatusText.Describe(UpdateCheckOutcome.UpToDate, current: "0.1.0"));
    }

    [Fact]
    public void Update_available_text_names_both_versions()
    {
        Assert.Equal(
            "Boo 0.2.0 is available (you have 0.1.0). Press Install and restart.",
            UpdateStatusText.Describe(UpdateCheckOutcome.UpdateAvailable, current: "0.1.0", available: "0.2.0"));
    }

    [Fact]
    public void Downloading_text_includes_percent()
    {
        Assert.Equal("Downloading Boo 0.2.0… 42%", UpdateStatusText.Describe(UpdateCheckOutcome.Downloading, available: "0.2.0", percent: 42));
    }

    [Fact]
    public void Ready_to_restart_text()
    {
        Assert.Equal(
            "Boo 0.2.0 is downloaded. Press Install and restart to finish.",
            UpdateStatusText.Describe(UpdateCheckOutcome.ReadyToRestart, available: "0.2.0"));
    }

    [Fact]
    public void Failed_text_prefixes_sanitised_error()
    {
        Assert.Equal("Update check failed: Bad credentials", UpdateStatusText.Describe(UpdateCheckOutcome.Failed, error: "Bad credentials"));
    }

    [Fact]
    public void Failed_without_error_reports_unknown_error()
    {
        Assert.Equal("Update check failed: unknown error", UpdateStatusText.Describe(UpdateCheckOutcome.Failed));
    }

    [Theory]
    [InlineData(UpdateCheckOutcome.NotChecked)]
    [InlineData(UpdateCheckOutcome.Disabled)]
    [InlineData(UpdateCheckOutcome.PortableBuild)]
    [InlineData(UpdateCheckOutcome.MissingToken)]
    [InlineData(UpdateCheckOutcome.Checking)]
    [InlineData(UpdateCheckOutcome.UpToDate)]
    [InlineData(UpdateCheckOutcome.UpdateAvailable)]
    [InlineData(UpdateCheckOutcome.Downloading)]
    [InlineData(UpdateCheckOutcome.ReadyToRestart)]
    [InlineData(UpdateCheckOutcome.Failed)]
    public void No_outcome_ever_echoes_a_token_passed_as_error(UpdateCheckOutcome outcome)
    {
        var text = UpdateStatusText.Describe(outcome, current: "0.1.0", available: "0.2.0", percent: 10, error: "Bad credentials " + Token + " rejected");

        Assert.False(string.IsNullOrWhiteSpace(text));
        Assert.DoesNotContain(Token, text, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_error_replaces_classic_token_with_placeholder()
    {
        var sanitised = UpdateStatusText.SanitizeError("Bad credentials " + Token + " rejected");

        Assert.Contains("[token]", sanitised, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG", sanitised, StringComparison.Ordinal);
        Assert.Contains("Bad credentials", sanitised, StringComparison.Ordinal);
        Assert.Contains("rejected", sanitised, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("github_pat_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz01234567")]
    [InlineData("gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123")]
    public void Sanitize_error_redacts_fine_grained_and_oauth_tokens(string token)
    {
        var sanitised = UpdateStatusText.SanitizeError("401 for " + token);

        Assert.Contains("[token]", sanitised, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG", sanitised, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Sanitize_error_turns_blank_into_unknown_error(string? error)
    {
        Assert.Equal("unknown error", UpdateStatusText.SanitizeError(error));
    }

    [Fact]
    public void Sanitize_error_flattens_newlines_to_a_single_line()
    {
        var sanitised = UpdateStatusText.SanitizeError("first line\r\nsecond line\nthird");

        Assert.DoesNotContain('\n', sanitised);
        Assert.DoesNotContain('\r', sanitised);
        Assert.Contains("first line", sanitised, StringComparison.Ordinal);
        Assert.Contains("second line", sanitised, StringComparison.Ordinal);
        Assert.Contains("third", sanitised, StringComparison.Ordinal);
    }

    [Fact]
    public void Sanitize_error_leaves_ordinary_messages_alone()
    {
        Assert.Equal("No network connection", UpdateStatusText.SanitizeError("No network connection"));
    }
}
