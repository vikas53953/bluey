using Boo.Core;

namespace Boo.Core.Tests;

public sealed class UpdateVersionGateTests
{
    [Fact]
    public void Patch_bump_is_an_upgrade()
    {
        Assert.True(UpdateVersionGate.IsUpgrade("0.1.0", "0.1.1", includePrerelease: false));
    }

    [Fact]
    public void Same_version_is_not_an_upgrade()
    {
        Assert.False(UpdateVersionGate.IsUpgrade("0.1.0", "0.1.0", includePrerelease: false));
    }

    [Theory]
    [InlineData("0.2.0", "0.1.9")]
    [InlineData("1.0.0", "0.9.9")]
    [InlineData("0.1.1", "0.1.0")]
    public void Never_downgrades(string current, string candidate)
    {
        Assert.False(UpdateVersionGate.IsUpgrade(current, candidate, includePrerelease: false));
        Assert.False(UpdateVersionGate.IsUpgrade(current, candidate, includePrerelease: true));
    }

    [Fact]
    public void Prerelease_candidate_is_skipped_unless_opted_in()
    {
        Assert.False(UpdateVersionGate.IsUpgrade("0.1.0", "0.2.0-abc1234", includePrerelease: false));
    }

    [Fact]
    public void Prerelease_candidate_is_accepted_when_opted_in()
    {
        Assert.True(UpdateVersionGate.IsUpgrade("0.1.0", "0.2.0-abc1234", includePrerelease: true));
    }

    [Fact]
    public void Stable_release_upgrades_its_own_prerelease()
    {
        Assert.True(UpdateVersionGate.IsUpgrade("0.1.0-beta", "0.1.0", includePrerelease: false));
    }

    [Fact]
    public void Leading_v_is_tolerated_on_both_sides()
    {
        Assert.True(UpdateVersionGate.IsUpgrade("v0.1.0", "v0.1.1", includePrerelease: false));
        Assert.True(UpdateVersionGate.IsUpgrade("v0.1.0", "0.1.1", includePrerelease: false));
        Assert.True(UpdateVersionGate.IsUpgrade("0.1.0", "v0.1.1", includePrerelease: false));
    }

    [Theory]
    [InlineData("", "0.1.1")]
    [InlineData("0.1.0", "")]
    [InlineData("abc", "0.1.1")]
    [InlineData("0.1.0", "abc")]
    [InlineData("1.2", "1.3")]
    [InlineData("0.1.0", "1.2")]
    public void Unparseable_versions_are_never_upgrades(string current, string candidate)
    {
        Assert.False(UpdateVersionGate.IsUpgrade(current, candidate, includePrerelease: true));
    }

    [Fact]
    public void Version_components_compare_numerically_not_lexically()
    {
        Assert.True(UpdateVersionGate.IsUpgrade("1.9.0", "1.10.0", includePrerelease: false));
        Assert.False(UpdateVersionGate.IsUpgrade("1.10.0", "1.9.0", includePrerelease: false));
    }
}
