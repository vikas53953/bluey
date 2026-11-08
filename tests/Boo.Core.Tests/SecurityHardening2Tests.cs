using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>Second security pass (8 Nov 2026): updater token scope, provider key shape, hands-free voice commands.</summary>
public sealed class SecurityHardening2Tests
{
    private const string ClassicToken = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";
    private const string FineGrainedToken = "github_pat_ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz01234567";

    // --- Updater: token scope -------------------------------------------------------------------------------

    [Fact]
    public void SH2_001_ClassicTokenGetsScopeNote()
    {
        var note = GitHubAccessToken.ScopeNote(ClassicToken);
        Assert.NotNull(note);
        Assert.Contains("fine-grained", note, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(ClassicToken, note, StringComparison.Ordinal);
        Assert.DoesNotContain("7890", note, StringComparison.Ordinal);
    }

    [Fact]
    public void SH2_002_FineGrainedTokenHasNoScopeNote() =>
        Assert.Null(GitHubAccessToken.ScopeNote("  " + FineGrainedToken + "\n"));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("ghp_short")]
    [InlineData("sk-proj-xxxxxxxxxxxxxxxxxxxxxxxx")]
    public void SH2_003_InvalidTokenHasNoScopeNote(string? token) =>
        Assert.Null(GitHubAccessToken.ScopeNote(token));

    [Theory]
    [InlineData("1.2.3", "1.2.3", false)]
    [InlineData("1.2.3", "1.2.2", false)]
    [InlineData("1.2.3", "1.2.4-beta.1", false)]
    [InlineData("1.2.3", "1.2.4", true)]
    [InlineData("1.2.3-beta.2", "1.2.3", true)]
    [InlineData("unknown", "9.9.9", false)]
    public void SH2_004_VersionGateNeverDowngradesOrReinstalls(string current, string candidate, bool expected) =>
        Assert.Equal(expected, UpdateVersionGate.IsUpgrade(current, candidate, includePrerelease: false));

    [Fact]
    public void SH2_005_UpdateFeedUrlIsPinnedToGitHubRepo()
    {
        Assert.True(UpdateChannel.IsTrustedRepositoryUrl(UpdateChannel.RepositoryUrl));
        Assert.False(UpdateChannel.IsTrustedRepositoryUrl("https://github.com/vikas53953/bluey/releases/download/x"));
        Assert.False(UpdateChannel.IsTrustedRepositoryUrl("https://user@github.com/vikas53953/bluey"));
        Assert.False(UpdateChannel.IsTrustedRepositoryUrl("https://github.com.evil.test/vikas53953/bluey"));
    }

    // --- Providers: key shape ---------------------------------------------------------------------------------

    [Theory]
    [InlineData(AiProvider.OpenAI, "sk-proj-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData(AiProvider.Anthropic, "sk-ant-api03-ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789")]
    [InlineData(AiProvider.Gemini, "AIzaSyABCDEFGHIJKLMNOPQRSTUVWXYZ0123456")]
    public void SH2_010_PlausibleKeysAreAccepted(AiProvider provider, string key)
    {
        Assert.Null(AiProviders.CredentialRejectReason(provider, key));
        Assert.Null(AiProviders.CredentialRejectReason(provider, "  " + key + "\r\n"));
    }

    [Theory]
    [InlineData(AiProvider.Gemini, "AIzaSyABCDEFGHIJKLMNOP\r\nX-Injected: 1")]
    [InlineData(AiProvider.Anthropic, "sk-ant-api03-ABCDEFGHIJKL MNOPQRSTUVWXYZ")]
    [InlineData(AiProvider.OpenAI, "sk-proj-ABCDEFGHIJKLMNOP\u0007QRSTUVWXYZ")]
    [InlineData(AiProvider.OpenAI, "sk-proj-ABCDEFGHIJKLMNOP‮QRSTUVWXYZ")]
    [InlineData(AiProvider.OpenAI, "sk-short")]
    [InlineData(AiProvider.OpenAI, "")]
    [InlineData(AiProvider.OpenAI, null)]
    public void SH2_011_KeysWithHeaderBreakingOrShortTextAreRefused(AiProvider provider, string? key)
    {
        var reason = AiProviders.CredentialRejectReason(provider, key);
        Assert.NotNull(reason);
        Assert.Contains(AiProviders.VendorName(provider), reason, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(key))
            Assert.DoesNotContain(key, reason, StringComparison.Ordinal);
    }

    // --- Voice: hands-free (server VAD) cannot disturb a running session ------------------------------------

    private static FocusSnapshot Snapshot(FocusStatus status) => new(
        Guid.NewGuid(), "Quarterly report", 1800, DateTimeOffset.UtcNow, 300, 300, status, false, false, Array.Empty<FocusCheck>());

    [Theory]
    [InlineData(VoiceCommandKind.FocusPause, FocusStatus.Running)]
    [InlineData(VoiceCommandKind.FocusEnd, FocusStatus.Running)]
    [InlineData(VoiceCommandKind.FocusEnd, FocusStatus.Paused)]
    public void SH2_020_HandsFreePauseOrEndIsRefused(VoiceCommandKind kind, FocusStatus status)
    {
        var decision = VoiceFocusPolicy.Decide(new VoiceCommand(kind), Snapshot(status), 600, handsFree: true);
        Assert.False(decision.Apply);
        Assert.Equal(VoiceFocusPolicy.HandsFreeRefusal, decision.Reply);
    }

    [Theory]
    [InlineData(VoiceCommandKind.FocusPause, FocusStatus.Running)]
    [InlineData(VoiceCommandKind.FocusEnd, FocusStatus.Running)]
    public void SH2_021_HoldToTalkPauseOrEndStillApplies(VoiceCommandKind kind, FocusStatus status)
    {
        var decision = VoiceFocusPolicy.Decide(new VoiceCommand(kind), Snapshot(status), 600, handsFree: false);
        Assert.True(decision.Apply);
    }

    [Fact]
    public void SH2_022_HandsFreeStartStatusAndSleepAreUnchanged()
    {
        Assert.True(VoiceFocusPolicy.Decide(new VoiceCommand(VoiceCommandKind.FocusStart, "Write tests", 25), null, 0, handsFree: true).Apply);
        Assert.True(VoiceFocusPolicy.Decide(new VoiceCommand(VoiceCommandKind.GoToSleep), Snapshot(FocusStatus.Running), 600, handsFree: true).Apply);
        var status = VoiceFocusPolicy.Decide(new VoiceCommand(VoiceCommandKind.FocusStatus), Snapshot(FocusStatus.Running), 600, handsFree: true);
        Assert.False(status.Apply);
        Assert.Contains("Quarterly report", status.Reply, StringComparison.Ordinal);
    }

    [Fact]
    public void SH2_023_HandsFreeResumeOfPausedSessionStillApplies() =>
        Assert.True(VoiceFocusPolicy.Decide(new VoiceCommand(VoiceCommandKind.FocusResume), Snapshot(FocusStatus.Paused), 600, handsFree: true).Apply);

    [Fact]
    public void SH2_024_DefaultIsHoldToTalk() =>
        Assert.True(VoiceFocusPolicy.Decide(new VoiceCommand(VoiceCommandKind.FocusEnd), Snapshot(FocusStatus.Running)).Apply);
}
