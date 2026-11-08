using Boo.Core;

namespace Boo.Core.Tests;

public sealed class UpdatePolicyTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static UpdateSettings Enabled(DateTimeOffset? lastChecked = null) =>
        new(Enabled: true, IncludePrerelease: false, LastCheckedUtc: lastChecked);

    // --- Defaults: updates are opt-in ---

    [Fact]
    public void App_settings_default_has_no_update_settings()
    {
        Assert.Null(new AppSettings().Updates);
    }

    [Fact]
    public void Effective_defaults_are_opt_in_and_never_checked()
    {
        var effective = UpdatePolicy.Effective(new AppSettings());

        Assert.False(effective.Enabled);
        Assert.False(effective.IncludePrerelease);
        Assert.Null(effective.LastCheckedUtc);
    }

    [Fact]
    public void Effective_returns_stored_settings_when_present()
    {
        var stored = new UpdateSettings(Enabled: true, IncludePrerelease: true, LastCheckedUtc: Now);
        var settings = new AppSettings(Updates: stored);

        Assert.Same(stored, UpdatePolicy.Effective(settings));
    }

    [Fact]
    public void Check_interval_is_twenty_four_hours()
    {
        Assert.Equal(TimeSpan.FromHours(24), UpdatePolicy.CheckInterval);
    }

    // --- Channel constants and trusted URL gate ---

    [Fact]
    public void Channel_is_pinned_to_the_boo_repository()
    {
        Assert.Equal("https://github.com/vikas53953/bluey", UpdateChannel.RepositoryUrl);
        Assert.Equal("Boo", UpdateChannel.PackId);
        Assert.Equal("win", UpdateChannel.ChannelName);
    }

    [Fact]
    public void Pinned_repository_url_is_trusted()
    {
        Assert.True(UpdateChannel.IsTrustedRepositoryUrl(UpdateChannel.RepositoryUrl));
    }

    [Theory]
    [InlineData("https://github.com/vikas53953/bluey/")]
    [InlineData("https://github.com/vikas53953/bluey.git")]
    [InlineData("https://github.com/someone/other-repo")]
    public void Https_github_owner_repo_urls_are_trusted(string url)
    {
        Assert.True(UpdateChannel.IsTrustedRepositoryUrl(url));
    }

    [Theory]
    [InlineData("http://github.com/vikas53953/bluey")]
    [InlineData("https://gitlab.com/vikas53953/bluey")]
    [InlineData("https://github.com.evil.com/vikas53953/bluey")]
    [InlineData("https://api.github.com/vikas53953/bluey")]
    [InlineData("https://user@github.com/a/b")]
    [InlineData("https://github.com/vikas53953/bluey?x=1")]
    [InlineData("https://github.com/vikas53953/bluey#readme")]
    [InlineData("https://github.com/a/b/c")]
    [InlineData("https://github.com/vikas53953")]
    [InlineData("https://github.com/")]
    [InlineData(" https://github.com/vikas53953/bluey")]
    [InlineData("https://github.com/vikas53953/bluey ")]
    [InlineData("   ")]
    [InlineData("")]
    [InlineData(null)]
    public void Untrusted_repository_urls_are_rejected(string? url)
    {
        Assert.False(UpdateChannel.IsTrustedRepositoryUrl(url));
    }

    // --- Startup check precedence ---

    [Theory]
    [InlineData(UpdateInstallKind.Installed, true)]
    [InlineData(UpdateInstallKind.Installed, false)]
    [InlineData(UpdateInstallKind.Portable, true)]
    [InlineData(UpdateInstallKind.Portable, false)]
    public void Disabled_wins_over_everything(UpdateInstallKind install, bool hasToken)
    {
        var settings = new UpdateSettings(Enabled: false, LastCheckedUtc: Now.AddDays(-30));

        Assert.Equal(UpdateCheckDecision.DisabledByUser, UpdatePolicy.DecideStartupCheck(settings, install, hasToken, Now));
    }

    [Fact]
    public void Default_settings_never_check_on_startup()
    {
        var settings = UpdatePolicy.Effective(new AppSettings());

        Assert.Equal(UpdateCheckDecision.DisabledByUser, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: true, Now));
    }

    [Fact]
    public void Portable_wins_over_missing_token()
    {
        Assert.Equal(UpdateCheckDecision.PortableBuild, UpdatePolicy.DecideStartupCheck(Enabled(), UpdateInstallKind.Portable, hasToken: false, Now));
    }

    [Fact]
    public void Portable_with_token_is_still_portable()
    {
        Assert.Equal(UpdateCheckDecision.PortableBuild, UpdatePolicy.DecideStartupCheck(Enabled(), UpdateInstallKind.Portable, hasToken: true, Now));
    }

    [Fact]
    public void Missing_token_wins_over_recently_checked()
    {
        var settings = Enabled(Now.AddHours(-1));

        Assert.Equal(UpdateCheckDecision.MissingToken, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: false, Now));
    }

    [Fact]
    public void Checked_twenty_three_hours_ago_is_recent()
    {
        var settings = Enabled(Now.AddHours(-23));

        Assert.Equal(UpdateCheckDecision.CheckedRecently, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: true, Now));
    }

    [Fact]
    public void Checked_twenty_five_hours_ago_checks_again()
    {
        var settings = Enabled(Now.AddHours(-25));

        Assert.Equal(UpdateCheckDecision.Check, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: true, Now));
    }

    [Fact]
    public void Never_checked_checks()
    {
        Assert.Equal(UpdateCheckDecision.Check, UpdatePolicy.DecideStartupCheck(Enabled(null), UpdateInstallKind.Installed, hasToken: true, Now));
    }

    [Fact]
    public void Last_check_in_the_future_counts_as_recent()
    {
        var settings = Enabled(Now.AddHours(5));

        Assert.Equal(UpdateCheckDecision.CheckedRecently, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: true, Now));
    }

    [Fact]
    public void Exactly_check_interval_ago_checks_again()
    {
        var settings = Enabled(Now - UpdatePolicy.CheckInterval);

        Assert.Equal(UpdateCheckDecision.Check, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: true, Now));
    }

    [Fact]
    public void One_second_inside_check_interval_is_recent()
    {
        var settings = Enabled(Now - UpdatePolicy.CheckInterval + TimeSpan.FromSeconds(1));

        Assert.Equal(UpdateCheckDecision.CheckedRecently, UpdatePolicy.DecideStartupCheck(settings, UpdateInstallKind.Installed, hasToken: true, Now));
    }

    // --- Manual check ---

    [Fact]
    public void Manual_check_on_installed_build_with_token_checks()
    {
        Assert.Equal(UpdateCheckDecision.Check, UpdatePolicy.DecideManualCheck(UpdateInstallKind.Installed, hasToken: true));
    }

    [Fact]
    public void Manual_check_on_portable_build_reports_portable()
    {
        Assert.Equal(UpdateCheckDecision.PortableBuild, UpdatePolicy.DecideManualCheck(UpdateInstallKind.Portable, hasToken: true));
    }

    [Fact]
    public void Manual_check_without_token_reports_missing_token()
    {
        Assert.Equal(UpdateCheckDecision.MissingToken, UpdatePolicy.DecideManualCheck(UpdateInstallKind.Installed, hasToken: false));
    }

    [Fact]
    public void Manual_check_on_portable_build_without_token_reports_portable()
    {
        Assert.Equal(UpdateCheckDecision.PortableBuild, UpdatePolicy.DecideManualCheck(UpdateInstallKind.Portable, hasToken: false));
    }
}
