using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ChirpPolicyTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 20, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Minimum_interval_is_two_seconds()
    {
        Assert.Equal(TimeSpan.FromSeconds(2), ChirpPolicy.MinimumInterval);
    }

    [Fact]
    public void Plays_when_enabled_and_microphone_idle()
    {
        Assert.True(new ChirpPolicy().TryPlay(chirpsEnabled: true, microphoneListening: false, T0));
    }

    [Fact]
    public void Never_plays_when_chirps_are_disabled()
    {
        var policy = new ChirpPolicy();

        Assert.False(policy.TryPlay(chirpsEnabled: false, microphoneListening: false, T0));
        Assert.False(policy.TryPlay(chirpsEnabled: false, microphoneListening: false, T0.AddSeconds(10)));
    }

    [Fact]
    public void Never_plays_while_the_microphone_is_listening()
    {
        var policy = new ChirpPolicy();

        Assert.False(policy.TryPlay(chirpsEnabled: true, microphoneListening: true, T0));
        Assert.False(policy.TryPlay(chirpsEnabled: true, microphoneListening: true, T0.AddSeconds(10)));
    }

    [Fact]
    public void Second_chirp_within_two_seconds_is_suppressed()
    {
        var policy = new ChirpPolicy();

        Assert.True(policy.TryPlay(true, false, T0));
        Assert.False(policy.TryPlay(true, false, T0.AddMilliseconds(1)));
        Assert.False(policy.TryPlay(true, false, T0.AddMilliseconds(1999)));
    }

    [Fact]
    public void Chirp_exactly_two_seconds_later_is_allowed()
    {
        var policy = new ChirpPolicy();

        Assert.True(policy.TryPlay(true, false, T0));
        Assert.True(policy.TryPlay(true, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Suppressed_attempt_does_not_extend_the_cooldown()
    {
        var policy = new ChirpPolicy();

        Assert.True(policy.TryPlay(true, false, T0));
        Assert.False(policy.TryPlay(true, false, T0.AddSeconds(1.5)));
        Assert.True(policy.TryPlay(true, false, T0.AddSeconds(2)));
    }

    [Fact]
    public void Refused_muted_or_listening_attempts_do_not_start_a_cooldown()
    {
        var policy = new ChirpPolicy();

        Assert.False(policy.TryPlay(false, false, T0));
        Assert.False(policy.TryPlay(true, true, T0.AddMilliseconds(100)));
        Assert.True(policy.TryPlay(true, false, T0.AddMilliseconds(500)));
    }

    [Fact]
    public void Cooldown_restarts_from_each_played_chirp()
    {
        var policy = new ChirpPolicy();

        Assert.True(policy.TryPlay(true, false, T0));
        Assert.True(policy.TryPlay(true, false, T0.AddSeconds(3)));
        Assert.False(policy.TryPlay(true, false, T0.AddSeconds(4)));
        Assert.True(policy.TryPlay(true, false, T0.AddSeconds(5)));
    }
}
