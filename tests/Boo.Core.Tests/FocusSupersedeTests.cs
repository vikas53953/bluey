using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Starting a new focus session while one is live replaces it. The replaced session must be settled as Ended
/// without the end-of-session moments (SessionEnded drives the happy face and chirp), so that it is neither left
/// Running in history nor recovered as "interrupted" at the next start.
/// </summary>
public sealed class FocusSupersedeTests
{
    [Fact]
    public void EndQuietly_ends_a_running_session_without_raising_SessionEnded()
    {
        var controller = new FocusSessionController("write report", 1500);
        var raised = 0;
        controller.SessionEnded += (_, _) => raised++;
        controller.Tick(10);

        controller.EndQuietly();

        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.Equal(FocusStatus.Ended, controller.Snapshot.Status);
        Assert.False(controller.ShouldMonitor);
        Assert.Equal(10, controller.Snapshot.Elapsed);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void EndQuietly_ends_a_paused_session_and_keeps_its_pause_marker()
    {
        var controller = new FocusSessionController("write report", 1500);
        var raised = 0;
        controller.SessionEnded += (_, _) => raised++;
        controller.Tick(30);
        controller.Pause("User pause");

        controller.EndQuietly();

        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.Contains(controller.Snapshot.Checks, check => check.Activity == FocusActivity.Paused && check.Elapsed == 30);
        Assert.Equal(0, raised);
    }

    [Fact]
    public void After_EndQuietly_later_End_and_Tick_raise_nothing_and_change_nothing()
    {
        var controller = new FocusSessionController("write report", 120);
        var raised = 0;
        controller.SessionEnded += (_, _) => raised++;
        controller.Tick(5);
        controller.EndQuietly();

        controller.End();
        controller.Tick(1000);
        controller.Resume();

        Assert.Equal(0, raised);
        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.Equal(5, controller.Snapshot.Elapsed);
        Assert.False(controller.IsCheckDue);
    }

    [Fact]
    public void EndQuietly_on_a_completed_session_is_a_no_op_and_SessionEnded_stays_raised_once()
    {
        var controller = new FocusSessionController("write report", 60);
        var reasons = new List<FocusEndReason>();
        controller.SessionEnded += (_, args) => reasons.Add(args.Reason);
        controller.Tick(60);
        Assert.Equal(new[] { FocusEndReason.Completed }, reasons);

        controller.EndQuietly();

        Assert.Equal(new[] { FocusEndReason.Completed }, reasons);
        Assert.Equal(FocusStatus.Ended, controller.Status);
    }

    [Fact]
    public void EndQuietly_on_a_user_ended_session_is_a_no_op()
    {
        var controller = new FocusSessionController("write report", 600);
        var raised = 0;
        controller.SessionEnded += (_, _) => raised++;
        controller.End();

        controller.EndQuietly();

        Assert.Equal(1, raised);
        Assert.Equal(FocusStatus.Ended, controller.Status);
    }

    [Fact]
    public void A_quietly_ended_snapshot_is_not_recovered_as_interrupted()
    {
        var controller = new FocusSessionController("write report", 1500);
        controller.Tick(40);
        var runningSnapshot = controller.Snapshot;
        Assert.NotSame(runningSnapshot, FocusRecovery.RecoverInterrupted(runningSnapshot)); // the bug: left Running, it would be "interrupted"

        controller.EndQuietly();
        var settled = controller.Snapshot;

        Assert.Same(settled, FocusRecovery.RecoverInterrupted(settled));
        Assert.Equal(FocusStatus.Ended, settled.Status);
        Assert.Contains("Ended early", FocusTimeline.ToHistoryRow(settled, 260).Detail);
    }
}
