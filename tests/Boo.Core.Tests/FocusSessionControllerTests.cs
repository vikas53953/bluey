using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FocusSessionControllerTests
{
    private static FocusCheck OnTask(double elapsed) => new(elapsed, FocusActivity.OnTask, 0.9, 0.9);

    private static FocusCheck OffTask(double elapsed, double probability = 0.96) =>
        new(elapsed, FocusActivity.OffTask, probability, 0.9);

    private sealed class EndRecorder
    {
        public List<(object? Sender, FocusSessionEndedEventArgs Args, FocusStatus StatusInsideHandler)> Calls { get; } = new();

        public void Attach(FocusSessionController controller) =>
            controller.SessionEnded += (sender, args) => Calls.Add((sender, args, controller.Status));
    }

    [Fact]
    public void New_controller_starts_running_with_normal_mode_rates()
    {
        var controller = new FocusSessionController("write report", 1500);

        Assert.Equal(FocusStatus.Running, controller.Status);
        Assert.False(controller.DeveloperMode);
        Assert.Equal(1, controller.ClockRate);
        Assert.Equal(30, controller.CheckInterval);
        Assert.Equal(1500, controller.Remaining);
        Assert.False(controller.IsCheckDue);

        var snapshot = controller.Snapshot;
        Assert.Equal("write report", snapshot.Task);
        Assert.Equal(1500, snapshot.Duration);
        Assert.Equal(0, snapshot.Elapsed);
        Assert.Equal(0, snapshot.RealElapsed);
        Assert.Equal(FocusStatus.Running, snapshot.Status);
        Assert.False(snapshot.DeveloperMode);
        Assert.False(snapshot.UsedDeveloperMode);
        Assert.Empty(snapshot.Checks);
    }

    [Fact]
    public void New_controller_in_developer_mode_uses_developer_rates()
    {
        var controller = new FocusSessionController("write report", 1500, developerMode: true);

        Assert.True(controller.DeveloperMode);
        Assert.Equal(4, controller.ClockRate);
        Assert.Equal(10, controller.CheckInterval);
        Assert.True(controller.Snapshot.DeveloperMode);
        Assert.True(controller.Snapshot.UsedDeveloperMode);
    }

    [Fact]
    public void Duration_is_clamped_like_FocusSession()
    {
        Assert.Equal(60, new FocusSessionController("t", 5).Snapshot.Duration);
        Assert.Equal(14_400, new FocusSessionController("t", 100_000).Snapshot.Duration);
    }

    [Fact]
    public void Full_25_minute_session_in_normal_mode_checks_every_30_seconds_and_ends_once()
    {
        var controller = new FocusSessionController("write report", 1500);
        var ended = new EndRecorder();
        ended.Attach(controller);

        var checkTimes = new List<double>();
        var endedAtTick = -1;
        for (var tick = 1; tick <= 1500; tick++)
        {
            controller.Tick(1);
            if (ended.Calls.Count > 0 && endedAtTick < 0)
                endedAtTick = tick;

            if (controller.IsCheckDue)
            {
                checkTimes.Add(controller.Snapshot.Elapsed);
                Assert.False(controller.RecordCheck(OnTask(controller.Snapshot.Elapsed), age: 1));
                Assert.False(controller.IsCheckDue);
            }
        }

        // A check becomes due once 30 running real seconds have passed since start or the last check,
        // i.e. after ticks 30, 60, ..., 1470. The tick at 1500 completes the session, so Status is Ended
        // and IsCheckDue is false: the would-be 50th check is never due. Hence exactly 49 checks.
        Assert.Equal(49, checkTimes.Count);
        Assert.Equal(Enumerable.Range(1, 49).Select(i => i * 30.0), checkTimes);
        Assert.Equal(1500, endedAtTick);

        var snapshot = controller.Snapshot;
        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.Equal(FocusStatus.Ended, snapshot.Status);
        Assert.Equal(1500, snapshot.Elapsed);
        Assert.Equal(1500, snapshot.RealElapsed);
        Assert.Equal(0, controller.Remaining);
        Assert.Equal(49, snapshot.Checks.Count);
        Assert.All(snapshot.Checks, check => Assert.Equal(FocusActivity.OnTask, check.Activity));
        Assert.False(snapshot.UsedDeveloperMode);

        var call = Assert.Single(ended.Calls);
        Assert.Equal(FocusEndReason.Completed, call.Args.Reason);
        Assert.Equal(FocusStatus.Ended, call.Args.Snapshot.Status);
        Assert.Equal(1500, call.Args.Snapshot.Elapsed);

        for (var i = 0; i < 100; i++)
        {
            controller.Tick(1);
            Assert.False(controller.IsCheckDue);
        }

        Assert.Single(ended.Calls);
        Assert.Equal(1500, controller.Snapshot.Elapsed);
        Assert.Equal(1500, controller.Snapshot.RealElapsed);
    }

    [Fact]
    public void Full_25_minute_session_in_developer_mode_finishes_after_375_real_seconds()
    {
        var controller = new FocusSessionController("write report", 1500, developerMode: true);
        var ended = new EndRecorder();
        ended.Attach(controller);

        var checkRealTimes = new List<int>();
        var ticks = 0;
        while (controller.Status != FocusStatus.Ended && ticks < 10_000)
        {
            controller.Tick(1);
            ticks++;
            if (controller.IsCheckDue)
            {
                checkRealTimes.Add(ticks);
                controller.RecordCheck(OnTask(controller.Snapshot.Elapsed), age: 1);
            }
        }

        Assert.Equal(375, ticks);

        // Due after real seconds 10, 20, ..., 370; the session ends at 375 before the next one.
        Assert.Equal(Enumerable.Range(1, 37).Select(i => i * 10), checkRealTimes);

        var snapshot = controller.Snapshot;
        Assert.Equal(FocusStatus.Ended, snapshot.Status);
        Assert.Equal(1500, snapshot.Elapsed);
        Assert.Equal(375, snapshot.RealElapsed);
        Assert.True(snapshot.UsedDeveloperMode);
        Assert.Equal(37, snapshot.Checks.Count);
        Assert.Equal(Enumerable.Range(1, 37).Select(i => i * 40.0), snapshot.Checks.Select(c => c.Elapsed));

        var call = Assert.Single(ended.Calls);
        Assert.Equal(FocusEndReason.Completed, call.Args.Reason);
        Assert.True(call.Args.Snapshot.UsedDeveloperMode);

        controller.Tick(1);
        controller.Tick(50);
        Assert.Single(ended.Calls);
    }

    [Fact]
    public void Sustained_off_task_run_in_a_normal_session_nudges_exactly_once()
    {
        // Checks land at focus seconds 30, 60, ... 1470. Off-task (p = 0.96, strong evidence) from 300 to 510.
        // NudgePolicy (normal): strength >= 0.95 so the run must span 120 s: run starts at 300 -> nudge at 420
        // (cooldown is free). The run then resets; 450, 480, 510 span only 60 s, and 540 is on-task (reset).
        var controller = new FocusSessionController("write report", 1500);
        var nudges = new List<double>();

        for (var tick = 1; tick <= 1500; tick++)
        {
            controller.Tick(1);
            if (!controller.IsCheckDue)
                continue;

            var elapsed = controller.Snapshot.Elapsed;
            var check = elapsed is >= 300 and <= 510 ? OffTask(elapsed) : OnTask(elapsed);
            if (controller.RecordCheck(check, age: 1))
                nudges.Add(elapsed);
        }

        Assert.Equal(new[] { 420.0 }, nudges);
        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.Equal(8, controller.Snapshot.Checks.Count(c => c.Activity == FocusActivity.OffTask));
    }

    [Fact]
    public void Two_short_off_task_checks_do_not_nudge_in_normal_mode()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(30);
        Assert.False(controller.RecordCheck(OffTask(controller.Snapshot.Elapsed), age: 1));
        controller.Tick(30);
        Assert.False(controller.RecordCheck(OffTask(controller.Snapshot.Elapsed), age: 1));
    }

    [Fact]
    public void Developer_mode_uses_developer_nudge_thresholds()
    {
        // Developer Mode: checks every 10 real s = 40 focus s, and the strong-evidence minimum is 40 focus s.
        var controller = new FocusSessionController("t", 1500, developerMode: true);

        controller.Tick(10);
        Assert.True(controller.IsCheckDue);
        Assert.Equal(40, controller.Snapshot.Elapsed);
        Assert.False(controller.RecordCheck(OffTask(controller.Snapshot.Elapsed), age: 1));

        controller.Tick(10);
        Assert.True(controller.IsCheckDue);
        Assert.Equal(80, controller.Snapshot.Elapsed);
        Assert.True(controller.RecordCheck(OffTask(controller.Snapshot.Elapsed), age: 1));
    }

    [Fact]
    public void Check_becomes_due_exactly_at_the_interval()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(29);
        Assert.False(controller.IsCheckDue);
        controller.Tick(1);
        Assert.True(controller.IsCheckDue);
        controller.Tick(5);
        Assert.True(controller.IsCheckDue);
    }

    [Fact]
    public void RecordCheck_restarts_the_interval_and_adds_the_check_to_the_snapshot()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(35);
        Assert.True(controller.IsCheckDue);
        Assert.False(controller.RecordCheck(OnTask(35), age: 1));
        Assert.False(controller.IsCheckDue);
        Assert.Equal(35, Assert.Single(controller.Snapshot.Checks).Elapsed);

        controller.Tick(29);
        Assert.False(controller.IsCheckDue);
        controller.Tick(1);
        Assert.True(controller.IsCheckDue);
    }

    [Fact]
    public void SkipCheck_restarts_the_interval_without_recording()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(30);
        Assert.True(controller.IsCheckDue);
        controller.SkipCheck();
        Assert.False(controller.IsCheckDue);
        Assert.Empty(controller.Snapshot.Checks);

        controller.Tick(29);
        Assert.False(controller.IsCheckDue);
        controller.Tick(1);
        Assert.True(controller.IsCheckDue);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(0)]
    [InlineData(-5)]
    public void Invalid_tick_values_are_ignored(double realSeconds)
    {
        var controller = new FocusSessionController("t", 1500);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Tick(realSeconds);

        Assert.Equal(0, controller.Snapshot.Elapsed);
        Assert.Equal(0, controller.Snapshot.RealElapsed);
        Assert.Equal(FocusStatus.Running, controller.Status);
        Assert.False(controller.IsCheckDue);
        Assert.Empty(ended.Calls);

        controller.Tick(29);
        Assert.False(controller.IsCheckDue);
        controller.Tick(1);
        Assert.True(controller.IsCheckDue);
    }

    [Fact]
    public void One_large_tick_completes_the_session_and_clamps_elapsed()
    {
        var controller = new FocusSessionController("t", 1500);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Tick(10_000);

        Assert.Equal(1500, controller.Snapshot.Elapsed);
        Assert.Equal(1500, controller.Snapshot.RealElapsed);
        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.False(controller.IsCheckDue);
        Assert.Equal(FocusEndReason.Completed, Assert.Single(ended.Calls).Args.Reason);
    }

    [Fact]
    public void Remaining_counts_down_in_focus_seconds()
    {
        var controller = new FocusSessionController("t", 1500, developerMode: true);

        controller.Tick(100);

        Assert.Equal(400, controller.Snapshot.Elapsed);
        Assert.Equal(1100, controller.Remaining);
    }

    [Fact]
    public void End_raises_EndedByUser_exactly_once()
    {
        var controller = new FocusSessionController("t", 1500);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Tick(100);
        controller.End();
        controller.End();
        controller.Tick(1);

        var call = Assert.Single(ended.Calls);
        Assert.Equal(FocusEndReason.EndedByUser, call.Args.Reason);
        Assert.Equal(FocusStatus.Ended, call.Args.Snapshot.Status);
        Assert.Equal(100, call.Args.Snapshot.Elapsed);
        Assert.Equal(FocusStatus.Ended, controller.Status);
        Assert.Equal(100, controller.Snapshot.Elapsed);
        Assert.False(controller.IsCheckDue);
    }

    [Fact]
    public void End_after_completion_does_not_raise_again()
    {
        var controller = new FocusSessionController("t", 60);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Tick(60);
        controller.End();

        var call = Assert.Single(ended.Calls);
        Assert.Equal(FocusEndReason.Completed, call.Args.Reason);
    }

    [Fact]
    public void End_while_paused_raises_EndedByUser()
    {
        var controller = new FocusSessionController("t", 1500);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Tick(10);
        controller.Pause("locked");
        controller.End();

        var call = Assert.Single(ended.Calls);
        Assert.Equal(FocusEndReason.EndedByUser, call.Args.Reason);
        Assert.Equal(FocusStatus.Ended, call.Args.Snapshot.Status);
    }

    [Fact]
    public void Handler_receives_the_controller_and_an_ended_snapshot_synchronously()
    {
        var controller = new FocusSessionController("write report", 120);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Tick(119);
        Assert.Empty(ended.Calls);
        controller.Tick(1);

        var call = Assert.Single(ended.Calls);
        Assert.Same(controller, call.Sender);
        Assert.Equal(FocusStatus.Ended, call.StatusInsideHandler);
        Assert.Equal(FocusStatus.Ended, call.Args.Snapshot.Status);
        Assert.Equal("write report", call.Args.Snapshot.Task);
        Assert.Equal(120, call.Args.Snapshot.Elapsed);
        Assert.Equal(controller.Snapshot.Id, call.Args.Snapshot.Id);
    }

    [Fact]
    public void Pause_stops_ticks_and_check_due_and_paused_time_does_not_count()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(20);
        controller.Pause("screen locked");

        Assert.Equal(FocusStatus.Paused, controller.Status);
        Assert.Equal(FocusStatus.Paused, controller.Snapshot.Status);
        var pauseMarker = Assert.Single(controller.Snapshot.Checks);
        Assert.Equal(FocusActivity.Paused, pauseMarker.Activity);
        Assert.Equal(20, pauseMarker.Elapsed);

        controller.Tick(100);
        Assert.False(controller.IsCheckDue);
        Assert.Equal(20, controller.Snapshot.Elapsed);
        Assert.Equal(20, controller.Snapshot.RealElapsed);

        controller.Resume();
        Assert.Equal(FocusStatus.Running, controller.Status);
        Assert.False(controller.IsCheckDue);

        controller.Tick(9);
        Assert.False(controller.IsCheckDue);
        controller.Tick(1);
        Assert.True(controller.IsCheckDue);
        Assert.Equal(30, controller.Snapshot.Elapsed);
        Assert.Equal(30, controller.Snapshot.RealElapsed);
    }

    [Fact]
    public void Paused_session_does_not_complete_from_ticks()
    {
        var controller = new FocusSessionController("t", 60);
        var ended = new EndRecorder();
        ended.Attach(controller);

        controller.Pause("away");
        controller.Tick(1000);
        Assert.Empty(ended.Calls);
        Assert.Equal(FocusStatus.Paused, controller.Status);

        controller.Resume();
        controller.Tick(60);
        Assert.Equal(FocusEndReason.Completed, Assert.Single(ended.Calls).Args.Reason);
    }

    [Fact]
    public void RecordCheck_while_paused_is_ignored_and_returns_false()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(30);
        Assert.True(controller.IsCheckDue);
        controller.Pause("away");

        Assert.False(controller.RecordCheck(OnTask(30), age: 1));
        Assert.False(controller.RecordCheck(OffTask(30), age: 1));

        var only = Assert.Single(controller.Snapshot.Checks);
        Assert.Equal(FocusActivity.Paused, only.Activity);

        // The ignored call did not restart the interval: the check is still due once running again.
        controller.Resume();
        Assert.True(controller.IsCheckDue);
    }

    [Fact]
    public void RecordCheck_after_end_is_ignored_and_returns_false()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(30);
        controller.End();

        Assert.False(controller.RecordCheck(OffTask(30), age: 1));
        Assert.Empty(controller.Snapshot.Checks);
        Assert.False(controller.IsCheckDue);
    }

    [Fact]
    public void SetDeveloperMode_mid_session_changes_rates_and_marks_usage()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(10);
        Assert.Equal(10, controller.Snapshot.Elapsed);
        Assert.False(controller.Snapshot.UsedDeveloperMode);

        controller.SetDeveloperMode(true);
        Assert.True(controller.DeveloperMode);
        Assert.Equal(4, controller.ClockRate);
        Assert.Equal(10, controller.CheckInterval);
        Assert.True(controller.Snapshot.DeveloperMode);
        Assert.True(controller.Snapshot.UsedDeveloperMode);

        controller.Tick(10);
        Assert.Equal(50, controller.Snapshot.Elapsed);
        Assert.Equal(20, controller.Snapshot.RealElapsed);

        controller.SkipCheck();
        controller.Tick(9);
        Assert.False(controller.IsCheckDue);
        controller.Tick(1);
        Assert.True(controller.IsCheckDue);

        controller.SetDeveloperMode(false);
        Assert.False(controller.DeveloperMode);
        Assert.Equal(1, controller.ClockRate);
        Assert.Equal(30, controller.CheckInterval);
        Assert.False(controller.Snapshot.DeveloperMode);
        Assert.True(controller.Snapshot.UsedDeveloperMode);

        var before = controller.Snapshot.Elapsed;
        controller.Tick(5);
        Assert.Equal(before + 5, controller.Snapshot.Elapsed);
    }

    [Fact]
    public void SetDeveloperMode_mid_session_switches_the_nudge_policy_to_developer_thresholds()
    {
        var controller = new FocusSessionController("t", 1500);

        controller.Tick(30);
        Assert.False(controller.RecordCheck(OnTask(controller.Snapshot.Elapsed), age: 1));

        controller.SetDeveloperMode(true);

        controller.Tick(10);
        Assert.True(controller.IsCheckDue);
        Assert.Equal(70, controller.Snapshot.Elapsed);
        Assert.False(controller.RecordCheck(OffTask(controller.Snapshot.Elapsed), age: 1));

        controller.Tick(10);
        Assert.True(controller.IsCheckDue);
        Assert.Equal(110, controller.Snapshot.Elapsed);
        Assert.True(controller.RecordCheck(OffTask(controller.Snapshot.Elapsed), age: 1));
    }
}
