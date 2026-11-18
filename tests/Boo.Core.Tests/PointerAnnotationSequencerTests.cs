using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

// Pointer lane acceptance (plan U3): Fly -> Land -> DrawRing -> Hold -> Clear, cancellable at any phase.
public sealed class PointerAnnotationSequencerTests
{
    private const double T0 = 1000;
    private static readonly PointerSequenceTimings Timings = new(LandMs: 100, DrawStrokeMs: 620, HoldMs: 1000);
    // Fly [1000,1400) Land [1400,1500) DrawRing [1500,2120) Hold [2120,3120) Clear at 3120.

    // PS-001
    [Fact]
    public void A_sequence_flies_lands_draws_the_ring_holds_and_clears_in_order()
    {
        var plan = Plan();
        var sequencer = new PointerAnnotationSequencer(Timings);

        Assert.True(sequencer.Begin(7, plan, T0));

        AssertFrame(sequencer.Tick(1000), PointerPhase.Fly, plan.Start, 0);
        AssertFrame(sequencer.Tick(1200), PointerPhase.Fly, plan.PositionAt(200), 0);
        AssertFrame(sequencer.Tick(1400), PointerPhase.Land, plan.Anchor, 0);
        AssertFrame(sequencer.Tick(1499.9), PointerPhase.Land, plan.Anchor, 0);
        AssertFrame(sequencer.Tick(1500), PointerPhase.DrawRing, plan.Anchor, 0);
        AssertFrame(sequencer.Tick(1810), PointerPhase.DrawRing, plan.Anchor, 0.5);
        AssertFrame(sequencer.Tick(2120), PointerPhase.Hold, plan.Anchor, 1);
        AssertFrame(sequencer.Tick(3119), PointerPhase.Hold, plan.Anchor, 1);
        var clear = sequencer.Tick(3120);
        Assert.NotNull(clear);
        Assert.Equal(PointerPhase.Clear, clear.Phase);
        Assert.Equal(7, clear.Generation);

        Assert.Null(sequencer.Tick(3121));
        Assert.Null(sequencer.Tick(10_000));
        Assert.Equal(PointerClearReason.Completed, sequencer.LastClearReason);
        Assert.Equal(PointerPhase.Idle, sequencer.Phase);
        Assert.Null(sequencer.ActiveGeneration);
    }

    // PS-002
    [Fact]
    public void Frames_carry_the_generation_and_target_and_phases_never_go_backwards()
    {
        var plan = Plan();
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.True(sequencer.Begin(3, plan, T0));

        var lastPhase = PointerPhase.Idle;
        var lastProgress = 0d;
        for (var now = T0; now < 3120; now += 16)
        {
            var frame = sequencer.Tick(now);
            Assert.NotNull(frame);
            Assert.Equal(3, frame.Generation);
            Assert.Equal(plan.Target, frame.Target);
            Assert.True(frame.Phase >= lastPhase, $"phase went back at {now}");
            Assert.True(frame.RingProgress >= lastProgress, $"ring progress went back at {now}");
            Assert.InRange(frame.RingProgress, 0, 1);
            if (frame.Phase is PointerPhase.Fly or PointerPhase.Land)
            {
                Assert.Equal(0d, frame.RingProgress);
            }

            if (frame.Phase is PointerPhase.Land or PointerPhase.DrawRing or PointerPhase.Hold)
            {
                Assert.Equal(plan.Anchor, frame.Pointer);
            }

            lastPhase = frame.Phase;
            lastProgress = frame.RingProgress;
        }

        Assert.Equal(PointerPhase.Hold, lastPhase);
    }

    // PS-003
    [Fact]
    public void A_zero_distance_plan_skips_the_flight()
    {
        var anchor = new PointD(400, 0);
        var plan = new PointerFlightPlan(anchor, anchor, new RectD(380, -20, 40, 40), "primary", 0,
            new[] { new PointerFrame(0, anchor) }, 620);
        var sequencer = new PointerAnnotationSequencer(Timings);

        Assert.True(sequencer.Begin(1, plan, T0));

        AssertFrame(sequencer.Tick(T0), PointerPhase.Land, anchor, 0);
        AssertFrame(sequencer.Tick(T0 + 100), PointerPhase.DrawRing, anchor, 0);
    }

    // PS-004
    [Fact]
    public void Stop_mid_flight_clears_immediately_and_no_further_frames_are_emitted()
    {
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.True(sequencer.Begin(1, Plan(), T0));
        Assert.Equal(PointerPhase.Fly, sequencer.Tick(1100)?.Phase);

        Assert.True(sequencer.Clear(PointerClearReason.Stop));

        Assert.Equal(PointerPhase.Idle, sequencer.Phase);
        Assert.Null(sequencer.ActiveGeneration);
        Assert.Equal(PointerClearReason.Stop, sequencer.LastClearReason);
        for (var now = 1101d; now < 5000; now += 50)
        {
            Assert.Null(sequencer.Tick(now));
        }
    }

    // PS-005 marks clear on scroll / resize / foreground change / display change in every phase.
    [Theory]
    [InlineData(1100, PointerClearReason.Scroll)]
    [InlineData(1450, PointerClearReason.Resize)]
    [InlineData(1700, PointerClearReason.ForegroundChanged)]
    [InlineData(2500, PointerClearReason.DisplayChanged)]
    [InlineData(2500, PointerClearReason.Scroll)]
    [InlineData(1100, PointerClearReason.Stop)]
    public void Any_clear_reason_in_any_phase_clears_immediately(double clearAt, PointerClearReason reason)
    {
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.True(sequencer.Begin(1, Plan(), T0));
        Assert.NotNull(sequencer.Tick(clearAt));

        Assert.True(sequencer.Clear(reason));

        Assert.Equal(reason, sequencer.LastClearReason);
        Assert.Null(sequencer.Tick(clearAt));
        Assert.Null(sequencer.Tick(clearAt + 1));
        Assert.Null(sequencer.Tick(3120));
    }

    // PS-006
    [Fact]
    public void Clear_with_nothing_showing_returns_false_and_tick_returns_null()
    {
        var sequencer = new PointerAnnotationSequencer(Timings);

        Assert.Equal(PointerPhase.Idle, sequencer.Phase);
        Assert.Null(sequencer.Tick(T0));
        Assert.False(sequencer.Clear(PointerClearReason.Scroll));
    }

    // PS-007
    [Fact]
    public void A_new_target_clears_the_old_sequence_and_restarts_the_flight()
    {
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.True(sequencer.Begin(1, Plan(), T0));
        Assert.Equal(PointerPhase.Land, sequencer.Tick(1450)?.Phase);

        var second = Plan(new PointD(400, 0), new PointD(900, 300), new RectD(880, 280, 40, 40));
        Assert.True(sequencer.Begin(2, second, 1500));

        Assert.Equal(PointerClearReason.TargetChanged, sequencer.LastClearReason);
        Assert.Equal(2, sequencer.ActiveGeneration);
        var frame = sequencer.Tick(1500);
        AssertFrame(frame, PointerPhase.Fly, second.Start, 0);
        Assert.Equal(2, frame!.Generation);
        Assert.Equal(second.Target, frame.Target);
        for (var now = 1500d; now < 3620; now += 16)
        {
            Assert.Equal(2, sequencer.Tick(now)?.Generation);
        }
    }

    // PS-008
    [Fact]
    public void An_older_or_repeated_generation_is_rejected_without_disturbing_the_current_sequence()
    {
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.True(sequencer.Begin(5, Plan(), T0));

        Assert.False(sequencer.Begin(4, Plan(new PointD(0, 0), new PointD(10, 10), new RectD(0, 0, 20, 20)), 1100));
        Assert.False(sequencer.Begin(5, Plan(new PointD(0, 0), new PointD(10, 10), new RectD(0, 0, 20, 20)), 1100));

        Assert.Equal(5, sequencer.ActiveGeneration);
        var frame = sequencer.Tick(1200);
        Assert.Equal(5, frame?.Generation);
        Assert.Equal(Plan().Target, frame?.Target);
    }

    // PS-009
    [Fact]
    public void A_cleared_generation_can_never_begin_again()
    {
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.True(sequencer.Begin(3, Plan(), T0));
        Assert.True(sequencer.Clear(PointerClearReason.Stop));

        Assert.False(sequencer.Begin(3, Plan(), 1200));
        Assert.False(sequencer.Begin(2, Plan(), 1200));
        Assert.Null(sequencer.Tick(1200));

        Assert.True(sequencer.Begin(4, Plan(), 1200));
        Assert.Equal(PointerPhase.Fly, sequencer.Tick(1200)?.Phase);
    }

    // PS-010 reuse the RequestCoordinator generation: a late model result after a new request is dropped.
    [Fact]
    public void A_late_result_from_a_superseded_request_generation_is_rejected()
    {
        using var coordinator = new RequestCoordinator();
        var sequencer = new PointerAnnotationSequencer(Timings);
        var first = coordinator.Begin();
        var second = coordinator.Begin();

        Assert.True(sequencer.Begin(second.Generation, Plan(), T0));
        Assert.False(sequencer.Begin(first.Generation, Plan(), 1100));
        Assert.Equal(second.Generation, sequencer.ActiveGeneration);
    }

    // PS-011
    [Fact]
    public void An_infinite_hold_keeps_the_ring_until_cleared()
    {
        var sequencer = new PointerAnnotationSequencer(Timings with { HoldMs = double.PositiveInfinity });
        Assert.True(sequencer.Begin(1, Plan(), T0));

        AssertFrame(sequencer.Tick(1_000_000_000), PointerPhase.Hold, Plan().Anchor, 1);
        Assert.True(sequencer.Clear(PointerClearReason.ForegroundChanged));
        Assert.Null(sequencer.Tick(1_000_000_001));
    }

    // PS-012
    [Fact]
    public void Invalid_inputs_are_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerAnnotationSequencer(Timings with { LandMs = -1 }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerAnnotationSequencer(Timings with { DrawStrokeMs = double.NaN }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointerAnnotationSequencer(Timings with { HoldMs = -5 }));
        var sequencer = new PointerAnnotationSequencer(Timings);
        Assert.Throws<ArgumentNullException>(() => sequencer.Begin(1, null!, T0));
    }

    // PS-013
    [Fact]
    public void Default_timings_draw_the_ring_for_620_ms_after_landing()
    {
        var timings = new PointerSequenceTimings();

        Assert.Equal(620d, timings.DrawStrokeMs);
        Assert.True(timings.LandMs >= 0);
        Assert.True(timings.HoldMs > 0);
    }

    private static PointerFlightPlan Plan() => Plan(new PointD(0, 0), new PointD(400, 0), new RectD(380, -20, 40, 40));

    private static PointerFlightPlan Plan(PointD start, PointD anchor, RectD target) =>
        new(start, anchor, target, "primary", 400,
            new[] { new PointerFrame(0, start), new PointerFrame(400, anchor) }, 620);

    private static void AssertFrame(PointerRenderFrame? frame, PointerPhase phase, PointD pointer, double ringProgress)
    {
        Assert.NotNull(frame);
        Assert.Equal(phase, frame.Phase);
        Assert.Equal(pointer.X, frame.Pointer.X, 9);
        Assert.Equal(pointer.Y, frame.Pointer.Y, 9);
        Assert.Equal(ringProgress, frame.RingProgress, 9);
    }
}
