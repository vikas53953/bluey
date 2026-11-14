using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FocusClockTests
{
    private sealed class FakeTime : TimeProvider
    {
        private long _ticks = 1_000_000;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public void Advance(double seconds) => _ticks += (long)(seconds * TimeSpan.TicksPerSecond);
        public void Rewind(double seconds) => _ticks -= (long)(seconds * TimeSpan.TicksPerSecond);
    }

    [Fact]
    public void Reports_real_elapsed_seconds_not_one_second_per_tick()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        clock.Start();

        time.Advance(1.6);
        var first = clock.Tick();
        time.Advance(2.4);
        var second = clock.Tick();

        Assert.Equal(1.6, first.Seconds, 6);
        Assert.Equal(2.4, second.Seconds, 6);
        Assert.False(first.GapDetected);
        Assert.False(second.GapDetected);
    }

    [Fact]
    public void Starved_timer_still_adds_up_to_wall_clock_time()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        var session = new FocusSession("write", duration: 1500);
        clock.Start();

        // 1000 ticks that each arrive 1.5 s apart (DispatcherTimer drift under load).
        for (var i = 0; i < 1000; i++)
        {
            time.Advance(1.5);
            session.Advance(clock.Tick().Seconds);
        }

        Assert.Equal(FocusStatus.Ended, session.Snapshot.Status);
        Assert.Equal(1500, session.Snapshot.RealElapsed, 6);
    }

    [Fact]
    public void Controller_driven_by_a_drifting_timer_completes_on_wall_clock_time()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        var controller = new FocusSessionController("write", 1500);
        FocusSessionEndedEventArgs? ended = null;
        controller.SessionEnded += (_, e) => ended = e;
        clock.Start();

        var ticks = 0;
        while (controller.Status == FocusStatus.Running && ticks < 5000)
        {
            time.Advance(1.25); // each 1 s tick arrives 250 ms late
            controller.Tick(clock.Tick().Seconds);
            ticks++;
        }

        Assert.Equal(1200, ticks);
        Assert.NotNull(ended);
        Assert.Equal(FocusEndReason.Completed, ended!.Reason);
        Assert.Equal(1500, controller.Snapshot.RealElapsed, 6);
    }

    [Fact]
    public void Controller_does_not_count_a_sleep_gap_or_paused_time()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        var controller = new FocusSessionController("deep work", 1500);
        clock.Start();
        for (var i = 0; i < 100; i++)
        {
            time.Advance(1);
            controller.Tick(clock.Tick().Seconds);
        }

        time.Advance(3600); // modern standby, no suspend event
        var gap = clock.Tick();
        Assert.True(gap.GapDetected);
        controller.Tick(gap.Seconds);
        controller.Pause(FocusClock.GapPauseReason);
        time.Advance(600); // stays paused until explicit Resume
        controller.Tick(clock.Tick().Seconds);

        controller.Resume();
        clock.Start();
        time.Advance(1);
        controller.Tick(clock.Tick().Seconds);

        Assert.Equal(FocusStatus.Running, controller.Status);
        Assert.Equal(101, controller.Snapshot.Elapsed, 6);
        Assert.Equal(1399, controller.Remaining, 6);
    }

    [Fact]
    public void Long_gap_is_reported_and_not_counted()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        clock.Start();
        time.Advance(1);
        clock.Tick();

        time.Advance(3600);
        var tick = clock.Tick();

        Assert.True(tick.GapDetected);
        Assert.Equal(0, tick.Seconds);
        Assert.Equal(3600, tick.GapSeconds, 6);
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void Gap_threshold_is_inclusive_of_the_maximum()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time, maximumTickSeconds: 10);
        clock.Start();

        time.Advance(10);
        var atLimit = clock.Tick();
        time.Advance(10.5);
        var overLimit = clock.Tick();

        Assert.False(atLimit.GapDetected);
        Assert.Equal(10, atLimit.Seconds, 6);
        Assert.True(overLimit.GapDetected);
        Assert.Equal(0, overLimit.Seconds);
    }

    [Fact]
    public void Default_threshold_tolerates_short_ui_hangs()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        clock.Start();
        time.Advance(4);

        var tick = clock.Tick();

        Assert.False(tick.GapDetected);
        Assert.Equal(4, tick.Seconds, 6);
        Assert.True(FocusClock.DefaultMaximumTickSeconds >= 5);
    }

    [Fact]
    public void Stopped_clock_counts_nothing_and_restart_resets_the_baseline()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        clock.Start();
        time.Advance(2);
        clock.Tick();
        clock.Stop();

        time.Advance(7200); // paused overnight
        var whilePaused = clock.Tick();
        clock.Start();
        time.Advance(1);
        var afterResume = clock.Tick();

        Assert.Equal(0, whilePaused.Seconds);
        Assert.False(whilePaused.GapDetected);
        Assert.Equal(1, afterResume.Seconds, 6);
        Assert.False(afterResume.GapDetected);
    }

    [Fact]
    public void Tick_before_start_counts_nothing()
    {
        var clock = new FocusClock(new FakeTime());
        var tick = clock.Tick();
        Assert.Equal(0, tick.Seconds);
        Assert.False(tick.GapDetected);
        Assert.False(clock.IsRunning);
    }

    [Fact]
    public void Backwards_timestamp_counts_nothing_and_rebases()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        clock.Start();
        time.Rewind(5);
        var backwards = clock.Tick();
        time.Advance(1);
        var next = clock.Tick();

        Assert.Equal(0, backwards.Seconds);
        Assert.False(backwards.GapDetected);
        Assert.Equal(1, next.Seconds, 6);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Rejects_invalid_threshold(double maximum)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new FocusClock(new FakeTime(), maximum));
    }

    [Fact]
    public void Sleep_without_suspend_event_pauses_session_without_counting_the_sleep()
    {
        var time = new FakeTime();
        var clock = new FocusClock(time);
        var session = new FocusSession("deep work", duration: 1500);
        clock.Start();
        for (var i = 0; i < 60; i++)
        {
            time.Advance(1);
            session.Advance(clock.Tick().Seconds);
        }

        time.Advance(5400); // modern standby: no PowerModeChanged
        var tick = clock.Tick();
        session.Advance(tick.Seconds);
        if (tick.GapDetected)
            session.Pause(FocusClock.GapPauseReason);

        Assert.Equal(FocusStatus.Paused, session.Snapshot.Status);
        Assert.Equal(60, session.Snapshot.Elapsed, 6);
        Assert.Equal(1440, session.Remaining, 6);
    }
}
