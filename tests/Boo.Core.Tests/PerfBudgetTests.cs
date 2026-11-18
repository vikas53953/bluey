namespace Boo.Core.Tests;

// Render-budget guard rails: the notch sits on screen all day, so the frame loop must be off when nothing
// moves, must not run faster than the animation is authored for, and the per-frame face path must not
// allocate. All tests are deterministic (simulated clocks, counted bytes); no wall-clock timing is asserted.
public sealed class PerfBudgetTests
{
    private static readonly NotchSpec Spec = FaceSpecFixture.Load();

    // ---------------------------------------------------------------- NotchRenderPolicy

    public static IEnumerable<object[]> PolicyCases()
    {
        foreach (var phase in Enum.GetValues<RevealPhase>())
            foreach (var state in Enum.GetValues<NotchFaceState>())
                foreach (var settled in new[] { false, true })
                    yield return new object[] { phase, state, settled };
    }

    [Theory]
    [MemberData(nameof(PolicyCases))]
    public void NeedsFrames_IsFalseOnlyWhenHiddenHiddenAndSettled(RevealPhase phase, NotchFaceState state, bool settled)
    {
        var expected = !(phase == RevealPhase.Hidden && state == NotchFaceState.Hidden && settled);
        var needs = NotchRenderPolicy.NeedsFrames(phase, state, settled);
        Assert.True(needs == expected, $"NeedsFrames({phase}, {state}, settled={settled}) was {needs}, expected {expected}");
        Assert.True(NotchRenderPolicy.MayStop(phase, state, settled) == !needs, $"MayStop({phase}, {state}, settled={settled}) must be !NeedsFrames ({!needs})");
    }

    [Fact]
    public void PolicyCases_CoverEveryCombination()
    {
        var count = PolicyCases().Count();
        var expected = Enum.GetValues<RevealPhase>().Length * Enum.GetValues<NotchFaceState>().Length * 2;
        Assert.True(count == expected, $"{count} policy cases, expected {expected}");
    }

    // ---------------------------------------------------------------- FramePacer

    [Fact]
    public void FramePacer_FirstFrameIsAlwaysAccepted()
    {
        Assert.True(new FramePacer().Accept(TimeSpan.Zero), "first frame at t=0 must be accepted");
        Assert.True(new FramePacer().Accept(TimeSpan.FromHours(3)), "first frame at t=3h must be accepted");
        Assert.True(new FramePacer().Accept(TimeSpan.FromMilliseconds(-5)), "first frame at negative t must be accepted");
    }

    [Fact]
    public void FramePacer_At144Hz_AcceptsRoughlyEveryOtherFrame()
    {
        var pacer = new FramePacer();
        const int total = 1440;
        var ticksPerFrame = TimeSpan.TicksPerSecond / 144d; // dt = 1000/144 ms
        var accepted = 0;
        for (var i = 0; i < total; i++)
        {
            if (pacer.Accept(TimeSpan.FromTicks((long)Math.Round(i * ticksPerFrame)))) accepted++;
        }
        var ratio = accepted / (double)total;
        Assert.True(ratio is >= 0.45 and <= 0.55, $"144 Hz: accepted {accepted}/{total} = {ratio:P1}, expected 45%..55%");
        Assert.True(pacer.Accepted == accepted, $"Accepted counter {pacer.Accepted} != counted {accepted}");
        Assert.True(pacer.Skipped + pacer.Accepted == total, $"Skipped {pacer.Skipped} + Accepted {pacer.Accepted} != {total}");
    }

    [Fact]
    public void FramePacer_At60Hz_AcceptsEveryFrame()
    {
        var pacer = new FramePacer();
        const int total = 600;
        var dt = TimeSpan.FromTicks(166667); // 16.6667 ms vsync
        var now = TimeSpan.Zero;
        for (var i = 0; i < total; i++, now += dt)
        {
            Assert.True(pacer.Accept(now), $"60 Hz frame {i} at {now.TotalMilliseconds:F3} ms was skipped (MinInterval {pacer.MinInterval.TotalMilliseconds:F3} ms)");
        }
        Assert.True(pacer.Skipped == 0, $"60 Hz stream skipped {pacer.Skipped} frames");
        Assert.True(pacer.Accepted == total, $"60 Hz stream accepted {pacer.Accepted}, expected {total}");
    }

    [Fact]
    public void FramePacer_ClockGoingBackwards_IsAcceptedNotStalled()
    {
        var pacer = new FramePacer();
        Assert.True(pacer.Accept(TimeSpan.FromSeconds(10)), "first frame");
        Assert.False(pacer.Accept(TimeSpan.FromSeconds(10) + TimeSpan.FromMilliseconds(1)), "1 ms later must be skipped");
        Assert.True(pacer.Accept(TimeSpan.FromSeconds(1)), "a frame with an earlier clock (new Stopwatch) must be accepted");
        Assert.True(pacer.Accept(TimeSpan.FromSeconds(1) + pacer.MinInterval), "pacing continues from the backwards frame");
        Assert.True(pacer.Accepted == 3, $"Accepted {pacer.Accepted}, expected 3");
        Assert.True(pacer.Skipped == 1, $"Skipped {pacer.Skipped}, expected 1");
    }

    [Fact]
    public void FramePacer_Reset_ClearsCountersAndAcceptsNextFrame()
    {
        var pacer = new FramePacer();
        pacer.Accept(TimeSpan.Zero);
        pacer.Accept(TimeSpan.FromMilliseconds(1));
        pacer.Accept(TimeSpan.FromMilliseconds(2));
        Assert.True(pacer.Accepted == 1 && pacer.Skipped == 2, $"before Reset: Accepted {pacer.Accepted}, Skipped {pacer.Skipped}");
        pacer.Reset();
        Assert.True(pacer.Accepted == 0, $"Accepted after Reset {pacer.Accepted}");
        Assert.True(pacer.Skipped == 0, $"Skipped after Reset {pacer.Skipped}");
        Assert.True(pacer.Accept(TimeSpan.FromMilliseconds(3)), "frame 1 ms after the last accepted one must run after Reset");
        Assert.True(pacer.Accepted == 1, $"Accepted after Reset+Accept {pacer.Accepted}");
    }

    [Fact]
    public void FramePacer_NegativeMinInterval_Throws()
    {
        var ex = Assert.Throws<ArgumentOutOfRangeException>(() => new FramePacer(TimeSpan.FromTicks(-1)));
        Assert.Equal("minInterval", ex.ParamName);
    }

    [Fact]
    public void FramePacer_ZeroMinInterval_AcceptsEverything()
    {
        var pacer = new FramePacer(TimeSpan.Zero);
        Assert.True(pacer.MinInterval == TimeSpan.Zero, $"MinInterval {pacer.MinInterval}");
        for (var i = 0; i < 1000; i++)
        {
            Assert.True(pacer.Accept(TimeSpan.FromTicks(i)), $"frame {i} skipped with MinInterval zero");
        }
        Assert.True(pacer.Accept(TimeSpan.FromTicks(999)), "same timestamp twice must still be accepted with MinInterval zero");
        Assert.True(pacer.Skipped == 0, $"Skipped {pacer.Skipped}");
        Assert.True(pacer.Accepted == 1001, $"Accepted {pacer.Accepted}");
    }

    [Fact]
    public void FramePacer_DefaultMinInterval_SitsBetween60And144HzFrames()
    {
        var ms = FramePacer.DefaultMinInterval.TotalMilliseconds;
        Assert.True(ms < 1000d / 60 && ms > 1000d / 144, $"DefaultMinInterval {ms:F3} ms must be shorter than a 60 Hz frame (16.667 ms) and longer than a 144 Hz frame (6.944 ms)");
        Assert.True(new FramePacer().MinInterval == FramePacer.DefaultMinInterval, "parameterless ctor must use DefaultMinInterval");
    }

    // ---------------------------------------------------------------- IdleAllocationBudget

    [Fact]
    public void IdleAllocationBudget_BytesPerSecond_Math()
    {
        var rate = IdleAllocationBudget.BytesPerSecond(1_000, 5_000, TimeSpan.FromSeconds(2));
        Assert.True(rate == 2_000, $"4000 bytes over 2 s = {rate} B/s, expected 2000");
        rate = IdleAllocationBudget.BytesPerSecond(0, 512, TimeSpan.FromMilliseconds(250));
        Assert.True(rate == 2_048, $"512 bytes over 250 ms = {rate} B/s, expected 2048");
        rate = IdleAllocationBudget.BytesPerSecond(123, 123, TimeSpan.FromSeconds(1));
        Assert.True(rate == 0, $"no allocation = {rate} B/s, expected 0");
    }

    [Fact]
    public void IdleAllocationBudget_WithinBudget_Boundary()
    {
        var max = (long)IdleAllocationBudget.MaxIdleBytesPerSecond;
        Assert.True(IdleAllocationBudget.WithinBudget(0, max, TimeSpan.FromSeconds(1)), $"exactly {max} B/s must be within budget");
        Assert.False(IdleAllocationBudget.WithinBudget(0, max + 1, TimeSpan.FromSeconds(1)), $"{max + 1} B/s must exceed the budget");
        Assert.True(IdleAllocationBudget.WithinBudget(0, max * 10, TimeSpan.FromSeconds(10)), $"{max * 10} bytes over 10 s is exactly the budget");
        Assert.False(IdleAllocationBudget.WithinBudget(0, max * 10 + 1, TimeSpan.FromSeconds(10)), $"{max * 10 + 1} bytes over 10 s exceeds the budget");
    }

    [Fact]
    public void IdleAllocationBudget_NonPositiveElapsed_Throws()
    {
        var zero = Assert.Throws<ArgumentOutOfRangeException>(() => IdleAllocationBudget.BytesPerSecond(0, 1, TimeSpan.Zero));
        Assert.Equal("elapsed", zero.ParamName);
        Assert.Throws<ArgumentOutOfRangeException>(() => IdleAllocationBudget.BytesPerSecond(0, 1, TimeSpan.FromTicks(-1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => IdleAllocationBudget.WithinBudget(0, 1, TimeSpan.Zero));
    }

    [Fact]
    public void IdleAllocationBudget_AfterLessThanBefore_ClampsToZero()
    {
        var rate = IdleAllocationBudget.BytesPerSecond(10_000, 2_000, TimeSpan.FromSeconds(1));
        Assert.True(rate == 0, $"after < before gave {rate} B/s, expected 0");
        Assert.True(IdleAllocationBudget.WithinBudget(10_000, 2_000, TimeSpan.FromSeconds(1)), "a negative delta is within budget");
    }

    // ---------------------------------------------------------------- Steady-state allocation

    private static readonly TimeSpan Frame = TimeSpan.FromMilliseconds(16.67);

    private static PointD SlowPointer(int i) => new(Math.Sin(i / 90d) * 150, Math.Cos(i / 70d) * 80);

    [Fact]
    public void FaceMotion_IdleTick_SteadyStateAllocatesUnder16BytesPerTick()
    {
        var face = new FaceMotion(Spec, new Random(1));
        face.SetState(NotchFaceState.Idle, TimeSpan.Zero);
        var now = TimeSpan.Zero;
        var i = 0;
        var sink = 0d;
        for (; i < 120; i++, now += Frame)
        {
            sink += face.Tick(now, SlowPointer(i)).Openness;
        }

        const int ticks = 3600; // one minute of frames
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < ticks; n++, i++, now += Frame)
        {
            sink += face.Tick(now, SlowPointer(i)).Openness;
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        var perTick = (after - before) / (double)ticks;
        Assert.True(double.IsFinite(sink), "sink");
        // Only the BlinkScheduler's BlinkEvent records (one every few seconds) are expected to allocate.
        Assert.True(perTick < 16, $"FaceMotion.Tick allocated {after - before} bytes over {ticks} ticks = {perTick:F2} bytes/tick, budget < 16");
    }

    [Fact]
    public void FaceMotion_IdleTick_OneMinuteStaysWithinIdleAllocationBudget()
    {
        var face = new FaceMotion(Spec, new Random(1));
        face.SetState(NotchFaceState.Idle, TimeSpan.Zero);
        var now = TimeSpan.Zero;
        var i = 0;
        for (; i < 120; i++, now += Frame) face.Tick(now, SlowPointer(i));

        const int ticks = 3600;
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var n = 0; n < ticks; n++, i++, now += Frame) face.Tick(now, SlowPointer(i));
        var after = GC.GetAllocatedBytesForCurrentThread();

        var simulated = TimeSpan.FromTicks(Frame.Ticks * ticks);
        var rate = IdleAllocationBudget.BytesPerSecond(before, after, simulated);
        Assert.True(IdleAllocationBudget.WithinBudget(before, after, simulated), $"{after - before} bytes over {simulated.TotalSeconds:F1} s simulated = {rate:F0} B/s, budget {IdleAllocationBudget.MaxIdleBytesPerSecond} B/s");
    }

    [Fact]
    public void FaceStateResolver_AndRevealTimeline_HotPathAllocatesNothing()
    {
        var inputs = new FaceInputs(Revealed: true, Hovered: true);
        var timeline = new NotchRevealTimeline(Spec.Timings);
        timeline.PointerEntered(TimeSpan.Zero);
        var step = TimeSpan.FromMilliseconds(16.67);

        // Warm up (JIT, any lazily created statics) before measuring.
        var now = TimeSpan.Zero;
        var warm = 0;
        for (var i = 0; i < 100; i++, now += step)
        {
            warm += (int)FaceStateResolver.Resolve(inputs) + (int)timeline.PhaseAt(now) + (int)timeline.ProgressAt(now);
        }

        const int iterations = 10_000;
        var sink = 0d;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < iterations; i++, now += step)
        {
            sink += (int)FaceStateResolver.Resolve(inputs);
            sink += (int)timeline.PhaseAt(now);
            sink += timeline.ProgressAt(now);
        }
        var after = GC.GetAllocatedBytesForCurrentThread();

        Assert.True(warm >= 0 && double.IsFinite(sink), "sink");
        Assert.True(after - before == 0, $"Resolve + PhaseAt + ProgressAt allocated {after - before} bytes over {iterations} iterations ({(after - before) / (double)iterations:F3} bytes/iteration), expected 0");
    }

    // ---------------------------------------------------------------- BlinkScheduler memory growth

    [Fact]
    public void BlinkScheduler_TwoHoursOfOpenness_KeepsWorkingWithBoundedEventList()
    {
        var blink = new BlinkScheduler(Spec.Timings, new Random(7), doubleBlinkGapMs: FaceMotionTimings.DoubleBlinkGapMs);
        var twoHours = TimeSpan.FromHours(2);
        var step = TimeSpan.FromMilliseconds(16.67);
        var min = 1d;
        var max = 0d;
        var samples = 0;
        for (var t = TimeSpan.Zero; t <= twoHours; t += step, samples++)
        {
            var o = blink.OpennessAt(t);
            Assert.True(o is >= 0 and <= 1, $"openness {o} at {t} is out of [0,1]");
            if (o < min) min = o;
            if (o > max) max = o;
        }
        Assert.True(min < 0.5 && max == 1, $"over {samples} samples openness ranged {min:F3}..{max:F3}; expected blinks (min < 0.5) and open eyes (max = 1)");

        // Informational guard rail: BlinkScheduler retains every BlinkEvent it has generated (List<BlinkEvent>,
        // ~40 bytes each). With blink intervals of BlinkEveryMinMs..BlinkEveryMaxMs (spec: ~2.5-5 s) two hours
        // holds roughly 1500-2500 records, i.e. under 100 KB; the bound below only catches runaway growth
        // (e.g. a regression generating one event per frame, which would be ~430 000 records).
        var retained = blink.EventsUntil(twoHours).Count;
        var expectedMax = twoHours.TotalMilliseconds / Spec.Timings.BlinkEveryMinMs + 1;
        var expectedMin = twoHours.TotalMilliseconds / Spec.Timings.BlinkEveryMaxMs - 1;
        Assert.True(retained < 5000, $"BlinkScheduler retained {retained} BlinkEvent records after 2 h, sanity bound 5000");
        Assert.True(retained >= expectedMin && retained <= expectedMax, $"BlinkScheduler retained {retained} records; spec intervals {Spec.Timings.BlinkEveryMinMs}-{Spec.Timings.BlinkEveryMaxMs} ms imply {expectedMin:F0}..{expectedMax:F0}");
    }

    // ---------------------------------------------------------------- Spec reuse

    [Fact]
    public void NotchSpec_LoadedOnce_IsSharedByFaceMotionInstances()
    {
        NotchSpec spec;
        using (var stream = FaceSpecFixture.Open()) spec = NotchSpec.Load(stream);

        var a = new FaceMotion(spec, new Random(1));
        var b = new FaceMotion(spec, new Random(2));

        // The spec is immutable and intended to be loaded once at startup; FaceMotion must not copy it.
        Assert.True(ReferenceEquals(spec.Face, spec.Face), "NotchSpec.Face must return the same NotchFaceSpec instance each time");
        Assert.True(ReferenceEquals(spec.Timings, spec.Timings), "NotchSpec.Timings must return the same instance each time");
        Assert.True(ReferenceEquals(spec.Motion, spec.Motion), "NotchSpec.Motion must return the same instance each time");

        a.SetState(NotchFaceState.Idle, TimeSpan.Zero);
        b.SetState(NotchFaceState.Idle, TimeSpan.Zero);
        var fa = a.Tick(TimeSpan.FromMilliseconds(16), new PointD(10, 5));
        var fb = b.Tick(TimeSpan.FromMilliseconds(16), new PointD(10, 5));
        Assert.True(fa.Openness > 0 && fb.Openness > 0, $"both faces render from the shared spec: {fa.Openness}, {fb.Openness}");

        // Default is also a cached singleton, not rebuilt per access.
        Assert.True(ReferenceEquals(NotchSpec.Default, NotchSpec.Default), "NotchSpec.Default must be cached");
    }
}
