using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FocusPolicyTests
{
    private static FocusCheck OffTask(double elapsed, double probability = 0.96, double confidence = 0.5) =>
        new(elapsed, FocusActivity.OffTask, probability, confidence);

    [Fact]
    public void Strong_off_task_run_nudges_at_the_120_second_lower_bound()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0), age: 5));
        Assert.False(policy.Observe(OffTask(30), age: 5));
        Assert.False(policy.Observe(OffTask(60), age: 5));
        Assert.False(policy.Observe(OffTask(90), age: 5));
        Assert.True(policy.Observe(OffTask(120), age: 5));
    }

    [Fact]
    public void Majority_strength_run_waits_until_the_240_second_upper_bound()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(30, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(60, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(90, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(120, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(150, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(180, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(210, probability: 0.50), age: 5));
        Assert.False(policy.Observe(OffTask(239, probability: 0.50), age: 5));
        Assert.True(policy.Observe(OffTask(240, probability: 0.50), age: 5));
    }

    [Fact]
    public void Confidence_is_diagnostic_and_does_not_add_a_second_threshold()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0, confidence: 0), age: 5));
        Assert.False(policy.Observe(OffTask(30, confidence: 0), age: 5));
        Assert.False(policy.Observe(OffTask(60, confidence: 0), age: 5));
        Assert.False(policy.Observe(OffTask(90, confidence: 0), age: 5));
        Assert.True(policy.Observe(OffTask(120, confidence: 0), age: 5));
    }

    [Fact]
    public void Age_twenty_is_accepted_but_older_observations_reset_the_run()
    {
        var accepted = new NudgePolicy();
        Assert.False(accepted.Observe(OffTask(0), age: 20));
        Assert.False(accepted.Observe(OffTask(30), age: 20));
        Assert.False(accepted.Observe(OffTask(60), age: 20));
        Assert.False(accepted.Observe(OffTask(90), age: 20));
        Assert.True(accepted.Observe(OffTask(120), age: 20));

        var rejected = new NudgePolicy();
        Assert.False(rejected.Observe(OffTask(0), age: 1));
        Assert.False(rejected.Observe(OffTask(30), age: 20.01));
        Assert.False(rejected.Observe(OffTask(120), age: 1));
    }

    [Fact]
    public void Uncertain_observations_reset_the_evidence_run()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0), age: 5));
        Assert.False(policy.Observe(new FocusCheck(30, FocusActivity.Uncertain, 0.96, 0.5), age: 5));
        Assert.False(policy.Observe(OffTask(60), age: 5));
        Assert.False(policy.Observe(OffTask(90), age: 5));
        Assert.False(policy.Observe(OffTask(120), age: 5));
        Assert.False(policy.Observe(OffTask(150), age: 5));
        Assert.True(policy.Observe(OffTask(180), age: 5));
    }

    [Fact]
    public void Invalid_probability_resets_the_evidence_run()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0), age: 5));
        Assert.False(policy.Observe(new FocusCheck(30, FocusActivity.OffTask, 0.49, 0.5), age: 5));
        Assert.False(policy.Observe(OffTask(60), age: 5));
        Assert.False(policy.Observe(OffTask(90), age: 5));
        Assert.False(policy.Observe(OffTask(120), age: 5));
        Assert.False(policy.Observe(OffTask(150), age: 5));
        Assert.True(policy.Observe(OffTask(180), age: 5));
    }

    [Fact]
    public void Out_of_order_observations_reset_the_evidence_run()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0), age: 5));
        Assert.False(policy.Observe(OffTask(30), age: 5));
        Assert.False(policy.Observe(OffTask(60), age: 5));
        Assert.False(policy.Observe(OffTask(50), age: 5));
        Assert.False(policy.Observe(OffTask(80), age: 5));
        Assert.False(policy.Observe(OffTask(110), age: 5));
        Assert.False(policy.Observe(OffTask(120), age: 5));
        Assert.False(policy.Observe(OffTask(140), age: 5));
        Assert.False(policy.Observe(OffTask(170), age: 5));
        Assert.True(policy.Observe(OffTask(200), age: 5));
    }

    [Fact]
    public void Gaps_over_45_seconds_reset_the_evidence_run()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0), age: 5));
        Assert.False(policy.Observe(OffTask(30), age: 5));
        Assert.False(policy.Observe(OffTask(76), age: 5));
        Assert.False(policy.Observe(OffTask(106), age: 5));
        Assert.False(policy.Observe(OffTask(136), age: 5));
        Assert.False(policy.Observe(OffTask(166), age: 5));
        Assert.True(policy.Observe(OffTask(196), age: 5));
    }

    [Fact]
    public void Nudge_cooldown_is_exactly_120_seconds()
    {
        var policy = new NudgePolicy();

        Assert.False(policy.Observe(OffTask(0), age: 5));
        Assert.False(policy.Observe(OffTask(30), age: 5));
        Assert.False(policy.Observe(OffTask(60), age: 5));
        Assert.False(policy.Observe(OffTask(90), age: 5));
        Assert.True(policy.Observe(OffTask(120), age: 5));

        Assert.False(policy.Observe(OffTask(121), age: 5));
        Assert.False(policy.Observe(OffTask(151), age: 5));
        Assert.False(policy.Observe(OffTask(181), age: 5));
        Assert.False(policy.Observe(OffTask(211), age: 5));
        Assert.False(policy.Observe(OffTask(240), age: 5));
        Assert.True(policy.Observe(OffTask(241), age: 5));
    }

    [Fact]
    public void Developer_acceleration_keeps_real_elapsed_distinct_from_focus_elapsed()
    {
        var normal = new FocusSession("normal task", duration: 300, developerMode: false);
        Assert.Equal(30, normal.CheckInterval);
        normal.Advance(30);
        Assert.Equal(30, normal.Snapshot.Elapsed);
        Assert.Equal(30, normal.Snapshot.RealElapsed);
        Assert.False(normal.Snapshot.DeveloperMode);

        var accelerated = new FocusSession("developer task", duration: 300, developerMode: true);
        Assert.Equal(10, accelerated.CheckInterval);
        accelerated.Advance(10);
        Assert.Equal(40, accelerated.Snapshot.Elapsed);
        Assert.Equal(10, accelerated.Snapshot.RealElapsed);
        Assert.True(accelerated.Snapshot.DeveloperMode);
        Assert.True(accelerated.Snapshot.UsedDeveloperMode);
    }

    [Fact]
    public void Duration_is_clamped_to_the_supported_focus_range()
    {
        Assert.Equal(60, new FocusSession("short", duration: 1).Snapshot.Duration);
        Assert.Equal(14_400, new FocusSession("long", duration: 20_000).Snapshot.Duration);
    }

    [Fact]
    public void Pause_stops_time_until_explicit_resume_and_end_cannot_resume()
    {
        var session = new FocusSession("write report", duration: 300);
        session.Advance(30);
        var beforePause = session.Snapshot.Elapsed;

        session.Pause("locked");
        Assert.Equal(FocusStatus.Paused, session.Snapshot.Status);
        Assert.False(session.ShouldMonitor);
        session.Advance(30);
        Assert.Equal(beforePause, session.Snapshot.Elapsed);

        session.Resume();
        Assert.Equal(FocusStatus.Running, session.Snapshot.Status);
        Assert.True(session.ShouldMonitor);
        session.Advance(30);
        Assert.Equal(beforePause + 30, session.Snapshot.Elapsed);

        session.End();
        Assert.Equal(FocusStatus.Ended, session.Snapshot.Status);
        Assert.False(session.ShouldMonitor);
        session.Resume();
        session.Advance(30);
        Assert.Equal(beforePause + 30, session.Snapshot.Elapsed);
    }

    [Fact]
    public void Snapshot_preserves_observations_and_represents_pause_as_a_gap_marker()
    {
        var session = new FocusSession("research", duration: 300);
        var onTask = new FocusCheck(30, FocusActivity.OnTask, 0.90, 0.80);
        var uncertain = new FocusCheck(60, FocusActivity.Uncertain, 0.10, 0.40);

        session.AddObservation(onTask);
        session.AddObservation(uncertain);
        session.Pause("sleep");

        Assert.Equal(3, session.Snapshot.Checks.Count);
        Assert.Equal(onTask, session.Snapshot.Checks[0]);
        Assert.Equal(uncertain, session.Snapshot.Checks[1]);
        Assert.Equal(FocusActivity.Paused, session.Snapshot.Checks[2].Activity);
    }
}
