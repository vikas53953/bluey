using Boo.Core;

namespace Boo.Core.Tests;

// FROZEN acceptance tests for the face lane (plan U1/U2). Builders must not edit, weaken, skip or delete.
public sealed class BlinkSchedulerTests
{
    private static BlinkScheduler Create(int seed = 42, bool reducedMotion = false)
        => new(FaceSpecFixture.Timings, new Random(seed), reducedMotion);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Theory]
    [InlineData(1)]
    [InlineData(42)]
    [InlineData(2026)]
    public void Group_intervals_are_between_2800_and_6000_ms(int seed)
    {
        var events = Create(seed).EventsUntil(TimeSpan.FromHours(2));

        Assert.True(events.Count > 1000);
        Assert.InRange(events[0].Start.TotalMilliseconds, 2800, 6000);
        for (var i = 1; i < events.Count; i++)
            Assert.InRange((events[i].Start - events[i - 1].Start).TotalMilliseconds, 2800, 6000);
    }

    [Fact]
    public void Intervals_use_the_whole_range_not_a_constant()
    {
        var events = Create().EventsUntil(TimeSpan.FromHours(2));
        var gaps = events.Zip(events.Skip(1), (a, b) => (b.Start - a.Start).TotalMilliseconds).ToList();

        Assert.True(gaps.Min() < 3200, $"min {gaps.Min()}");
        Assert.True(gaps.Max() > 5600, $"max {gaps.Max()}");
    }

    [Fact]
    public void Eye_is_open_between_blinks_and_follows_close_70_open_90_profile()
    {
        var scheduler = Create();
        var start = scheduler.EventsUntil(TimeSpan.FromSeconds(7))[0].Start;

        Assert.Equal(1, scheduler.OpennessAt(TimeSpan.Zero));
        Assert.Equal(1, scheduler.OpennessAt(start - Ms(1)));
        Assert.Equal(1, scheduler.OpennessAt(start));
        Assert.InRange(scheduler.OpennessAt(start + Ms(35)), 0.01, 0.99);
        Assert.Equal(0, scheduler.OpennessAt(start + Ms(70)), 6);
        Assert.InRange(scheduler.OpennessAt(start + Ms(115)), 0.01, 0.99);
        Assert.Equal(1, scheduler.OpennessAt(start + Ms(160)), 6);
        Assert.Equal(1, scheduler.OpennessAt(start + Ms(161 + 80 + 160)), 6); // after any double blink
    }

    [Fact]
    public void Closing_is_monotonic_down_then_opening_monotonic_up()
    {
        var scheduler = Create();
        var start = scheduler.EventsUntil(TimeSpan.FromSeconds(7))[0].Start;

        var previous = 1.0;
        for (var t = 0; t <= 70; t += 5)
        {
            var o = scheduler.OpennessAt(start + Ms(t));
            Assert.True(o <= previous + 1e-12);
            previous = o;
        }
        for (var t = 70; t <= 160; t += 5)
        {
            var o = scheduler.OpennessAt(start + Ms(t));
            Assert.True(o >= previous - 1e-12);
            previous = o;
        }
    }

    [Fact]
    public void Openness_is_always_within_0_and_1()
    {
        var scheduler = Create(7);
        for (var t = 0; t < 60_000; t += 3)
            Assert.InRange(scheduler.OpennessAt(Ms(t)), 0, 1);
    }

    [Fact]
    public void Double_blink_closes_twice()
    {
        var scheduler = Create();
        var events = scheduler.EventsUntil(TimeSpan.FromHours(1));
        var dbl = events.First(e => e.IsDouble);
        var second = dbl.Start + Ms(70 + 90 + scheduler.DoubleBlinkGapMs);

        Assert.Equal(80, scheduler.DoubleBlinkGapMs);
        Assert.Equal(0, scheduler.OpennessAt(dbl.Start + Ms(70)), 6);
        Assert.Equal(1, scheduler.OpennessAt(dbl.Start + Ms(160 + scheduler.DoubleBlinkGapMs / 2.0)), 6);
        Assert.Equal(0, scheduler.OpennessAt(second + Ms(70)), 6);
        Assert.Equal(1, scheduler.OpennessAt(second + Ms(160)), 6);

        var single = events.First(e => !e.IsDouble);
        Assert.Equal(1, scheduler.OpennessAt(single.Start + Ms(160 + 80 + 70)), 6);
    }

    [Fact]
    public void About_15_percent_of_blinks_are_double()
    {
        var events = Create(99).EventsUntil(TimeSpan.FromHours(20));
        var ratio = events.Count(e => e.IsDouble) / (double)events.Count;

        Assert.True(events.Count > 10_000);
        Assert.InRange(ratio, 0.13, 0.17);
    }

    [Fact]
    public void Same_seed_gives_same_schedule_and_repeat_calls_are_stable()
    {
        var a = Create(5);
        var b = Create(5);

        var first = a.EventsUntil(TimeSpan.FromMinutes(5));
        Assert.Equal(first, b.EventsUntil(TimeSpan.FromMinutes(5)));
        Assert.Equal(first, a.EventsUntil(TimeSpan.FromMinutes(5)));
        // Extending the horizon keeps the earlier prefix.
        Assert.Equal(first, a.EventsUntil(TimeSpan.FromMinutes(10)).Take(first.Count));
        Assert.All(first, e => Assert.True(e.Start < TimeSpan.FromMinutes(5)));
    }

    [Fact]
    public void Reduced_motion_never_blinks()
    {
        var scheduler = Create(reducedMotion: true);

        Assert.True(scheduler.ReducedMotion);
        Assert.Empty(scheduler.EventsUntil(TimeSpan.FromHours(1)));
        for (var t = 0; t < 120_000; t += 7)
            Assert.Equal(1, scheduler.OpennessAt(Ms(t)));
    }
}
