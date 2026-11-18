using Boo.Core;

namespace Boo.Core.Tests;

public sealed class GitHubAccessTokenTests
{
    // 36 alphanumeric characters after the prefix, like a classic personal access token.
    private const string ClassicToken = "ghp_ABCDEFGHIJKLMNOPQRSTUVWXYZ1234567890";

    // 60 characters after the prefix, like a fine-grained personal access token.
    private const string FineGrainedToken = "github_pat_" + "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz01234567";

    // 30 characters after the prefix, like an OAuth token.
    private const string OAuthToken = "gho_ABCDEFGHIJKLMNOPQRSTUVWXYZ0123";

    [Fact]
    public void Token_fixtures_have_the_documented_lengths()
    {
        Assert.Equal(36, ClassicToken.Length - "ghp_".Length);
        Assert.Equal(60, FineGrainedToken.Length - "github_pat_".Length);
        Assert.Equal(30, OAuthToken.Length - "gho_".Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void Blank_token_is_rejected(string? token)
    {
        Assert.NotNull(GitHubAccessToken.RejectReason(token));
    }

    [Theory]
    [InlineData(ClassicToken)]
    [InlineData(FineGrainedToken)]
    [InlineData(OAuthToken)]
    public void Well_formed_github_tokens_are_accepted(string token)
    {
        Assert.Null(GitHubAccessToken.RejectReason(token));
    }

    [Fact]
    public void Non_github_secret_is_rejected()
    {
        Assert.NotNull(GitHubAccessToken.RejectReason("sk-proj-xxxxxxxxxxxxxxxxxxxxxxxx"));
    }

    [Fact]
    public void Token_with_embedded_space_is_rejected()
    {
        Assert.NotNull(GitHubAccessToken.RejectReason("ghp_ABCDEFGHIJKLMNOP QRSTUVWXYZ1234567890"));
    }

    [Fact]
    public void Token_with_embedded_newline_is_rejected()
    {
        Assert.NotNull(GitHubAccessToken.RejectReason("ghp_ABCDEFGHIJKLMNOP\nQRSTUVWXYZ1234567890"));
    }

    [Fact]
    public void Too_short_token_is_rejected()
    {
        Assert.NotNull(GitHubAccessToken.RejectReason("ghp_short"));
    }

    [Fact]
    public void Surrounding_whitespace_is_trimmed_before_checking()
    {
        Assert.Null(GitHubAccessToken.RejectReason("  " + ClassicToken + "\r\n"));
    }

    [Fact]
    public void Normalize_trims_surrounding_whitespace()
    {
        Assert.Equal(ClassicToken, GitHubAccessToken.Normalize("  " + ClassicToken + "\n"));
    }

    [Fact]
    public void Normalize_leaves_clean_token_unchanged()
    {
        Assert.Equal(ClassicToken, GitHubAccessToken.Normalize(ClassicToken));
    }

    [Fact]
    public void Mask_shows_prefix_and_last_four_characters_only()
    {
        Assert.Equal("ghp_…7890", GitHubAccessToken.Mask(ClassicToken));
    }

    [Fact]
    public void Mask_never_leaks_the_middle_of_the_token()
    {
        var masked = GitHubAccessToken.Mask(ClassicToken);

        Assert.StartsWith("ghp_", masked, StringComparison.Ordinal);
        Assert.EndsWith("7890", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("1234567890", masked, StringComparison.Ordinal);
        Assert.True(masked.Length <= "ghp_".Length + 1 + 4, $"mask is too long: {masked}");
    }

    [Fact]
    public void Mask_handles_fine_grained_prefix()
    {
        var masked = GitHubAccessToken.Mask(FineGrainedToken);

        Assert.StartsWith("github_pat_", masked, StringComparison.Ordinal);
        Assert.EndsWith("4567", masked, StringComparison.Ordinal);
        Assert.DoesNotContain("ABCDEFG", masked, StringComparison.Ordinal);
        Assert.True(masked.Length <= "github_pat_".Length + 1 + 4, $"mask is too long: {masked}");
    }
}
