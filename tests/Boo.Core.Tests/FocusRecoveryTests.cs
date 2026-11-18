using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FocusRecoveryTests
{
    private static FocusSnapshot Snapshot(FocusStatus status, params FocusCheck[] checks) => new(
        Guid.NewGuid(), "write report", 1500, DateTimeOffset.Parse("2026-11-08T10:00:00Z"),
        Elapsed: 420, RealElapsed: 420, status, DeveloperMode: false, UsedDeveloperMode: false, checks);

    [Fact]
    public void Running_snapshot_left_by_a_crash_becomes_paused_with_progress_kept()
    {
        var onTask = new FocusCheck(390, FocusActivity.OnTask, 0.9, 0.8);
        var crashed = Snapshot(FocusStatus.Running, onTask);

        var recovered = FocusRecovery.RecoverInterrupted(crashed);

        Assert.Equal(FocusStatus.Paused, recovered.Status);
        Assert.Equal(crashed.Id, recovered.Id);
        Assert.Equal(crashed.Task, recovered.Task);
        Assert.Equal(crashed.StartedAt, recovered.StartedAt);
        Assert.Equal(420, recovered.Elapsed);
        Assert.Equal(420, recovered.RealElapsed);
        Assert.Equal(2, recovered.Checks.Count);
        Assert.Equal(onTask, recovered.Checks[0]);
        Assert.Equal(new FocusCheck(420, FocusActivity.Paused), recovered.Checks[1]);
    }

    [Theory]
    [InlineData(FocusStatus.Paused)]
    [InlineData(FocusStatus.Ended)]
    public void Settled_snapshots_are_returned_unchanged(FocusStatus status)
    {
        var snapshot = Snapshot(status);
        Assert.Same(snapshot, FocusRecovery.RecoverInterrupted(snapshot));
    }

    [Fact]
    public void Recovery_is_idempotent()
    {
        var once = FocusRecovery.RecoverInterrupted(Snapshot(FocusStatus.Running));
        var twice = FocusRecovery.RecoverInterrupted(once);
        Assert.Same(once, twice);
        Assert.Single(twice.Checks);
    }

    [Fact]
    public void Null_snapshot_is_rejected()
    {
        Assert.Throws<ArgumentNullException>(() => FocusRecovery.RecoverInterrupted(null!));
    }

    [Fact]
    public void Checkpoint_is_due_every_thirty_real_seconds_of_running()
    {
        Assert.False(FocusRecovery.CheckpointDue(lastSavedRealElapsed: 0, realElapsed: 29.9));
        Assert.True(FocusRecovery.CheckpointDue(lastSavedRealElapsed: 0, realElapsed: 30));
        Assert.False(FocusRecovery.CheckpointDue(lastSavedRealElapsed: 30, realElapsed: 45));
        Assert.True(FocusRecovery.CheckpointDue(lastSavedRealElapsed: 30, realElapsed: 60.5));
        Assert.False(FocusRecovery.CheckpointDue(lastSavedRealElapsed: 0, realElapsed: double.NaN));
    }
}
