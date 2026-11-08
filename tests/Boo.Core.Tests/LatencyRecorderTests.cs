using Boo.Core;

namespace Boo.Core.Tests;

// Frozen acceptance tests for plan unit U4 / AE2 latency. Builders must not edit, weaken, skip or delete.
public sealed class LatencyRecorderTests
{
    private static readonly DateTimeOffset T0 = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);

    private static void Add(LatencyRecorder r, double ms, int index = 0)
    {
        var end = T0.AddSeconds(index * 10);
        r.Record(end, end.AddMilliseconds(ms));
    }

    private static LatencyRecorder WithWarm(double coldMs, params double[] warmMs)
    {
        var r = new LatencyRecorder();
        Add(r, coldMs);
        for (var i = 0; i < warmMs.Length; i++)
        {
            Add(r, warmMs[i], i + 1);
        }
        return r;
    }

    [Fact]
    public void Empty_recorder_has_no_statistics()
    {
        var r = new LatencyRecorder();
        Assert.Empty(r.Samples);
        Assert.Equal(0, r.WarmCount);
        Assert.Equal(0, r.ColdCount);
        Assert.Null(r.WarmMedianMs());
        Assert.Null(r.WarmP95Ms());
        Assert.False(r.MeetsTarget());
    }

    [Fact]
    public void Records_latency_between_end_of_speech_and_first_audio()
    {
        var r = new LatencyRecorder();
        var sample = r.Record(T0, T0.AddMilliseconds(1234));
        Assert.Equal(TimeSpan.FromMilliseconds(1234), sample.Latency);
        Assert.Equal(1, sample.Turn);
        Assert.Single(r.Samples);
    }

    [Fact]
    public void First_turn_is_cold_and_excluded_from_warm_statistics()
    {
        var r = new LatencyRecorder();
        Add(r, 4000);
        Assert.True(r.Samples[0].Cold);
        Assert.Equal(1, r.ColdCount);
        Assert.Equal(0, r.WarmCount);
        Assert.Null(r.WarmMedianMs());
        Assert.Null(r.WarmP95Ms());
    }

    [Fact]
    public void Turns_after_the_first_are_warm()
    {
        var r = WithWarm(4000, 900, 1100);
        Assert.Equal(new[] { true, false, false }, r.Samples.Select(s => s.Cold).ToArray());
        Assert.Equal(new[] { 1, 2, 3 }, r.Samples.Select(s => s.Turn).ToArray());
        Assert.Equal(2, r.WarmCount);
        Assert.Equal(1, r.ColdCount);
    }

    [Fact]
    public void Reconnect_makes_next_turn_cold_again()
    {
        var r = WithWarm(4000, 900);
        r.Connected();
        Add(r, 3500, 5);
        Add(r, 1000, 6);
        Assert.Equal(new[] { true, false, true, false }, r.Samples.Select(s => s.Cold).ToArray());
        Assert.Equal(2, r.ColdCount);
        Assert.Equal(2, r.WarmCount);
        Assert.Equal(950, r.WarmMedianMs());
    }

    [Fact]
    public void Connected_before_any_turn_keeps_first_turn_cold()
    {
        var r = new LatencyRecorder();
        r.Connected();
        Add(r, 2000);
        Add(r, 800, 1);
        Assert.Equal(1, r.ColdCount);
        Assert.Equal(1, r.WarmCount);
    }

    [Fact]
    public void Single_warm_sample_is_its_own_median_and_p95()
    {
        var r = WithWarm(5000, 1300);
        Assert.Equal(1300, r.WarmMedianMs());
        Assert.Equal(1300, r.WarmP95Ms());
    }

    [Fact]
    public void Median_of_odd_count_is_middle_value()
    {
        var r = WithWarm(9000, 3000, 1000, 2000);
        Assert.Equal(2000, r.WarmMedianMs());
    }

    [Fact]
    public void Median_of_even_count_is_mean_of_middle_values()
    {
        var r = WithWarm(9000, 400, 1000, 2000, 100);
        Assert.Equal(700, r.WarmMedianMs());
    }

    [Fact]
    public void P95_uses_nearest_rank_for_twenty_samples()
    {
        // warm = 100, 200, ..., 2000 (shuffled). Nearest rank: ceil(0.95 * 20) = 19 -> 1900.
        var warm = Enumerable.Range(1, 20).Select(i => i * 100.0).Reverse().ToArray();
        var r = WithWarm(9999, warm);
        Assert.Equal(1900, r.WarmP95Ms());
        Assert.Equal(1050, r.WarmMedianMs());
    }

    [Fact]
    public void P95_uses_nearest_rank_for_ten_samples()
    {
        // ceil(0.95 * 10) = 10 -> the maximum.
        var warm = Enumerable.Range(1, 10).Select(i => i * 100.0).ToArray();
        var r = WithWarm(9999, warm);
        Assert.Equal(1000, r.WarmP95Ms());
    }

    [Fact]
    public void P95_uses_nearest_rank_for_forty_samples()
    {
        // ceil(0.95 * 40) = 38 -> 3800.
        var warm = Enumerable.Range(1, 40).Select(i => i * 100.0).ToArray();
        var r = WithWarm(9999, warm);
        Assert.Equal(3800, r.WarmP95Ms());
    }

    [Fact]
    public void Cold_turns_do_not_affect_warm_statistics()
    {
        var r = WithWarm(60000, 1000, 1000, 1000);
        Assert.Equal(1000, r.WarmMedianMs());
        Assert.Equal(1000, r.WarmP95Ms());
    }

    [Fact]
    public void Sub_millisecond_precision_is_kept()
    {
        var r = new LatencyRecorder();
        Add(r, 1000);
        r.Record(T0.AddSeconds(10), T0.AddSeconds(10).AddTicks(12_345_678)); // 1234.5678 ms
        Assert.Equal(1234.5678, r.WarmMedianMs()!.Value, 4);
    }

    [Fact]
    public void First_audio_before_end_of_speech_is_rejected()
    {
        var r = new LatencyRecorder();
        Assert.Throws<ArgumentException>(() => r.Record(T0, T0.AddMilliseconds(-1)));
        Assert.Empty(r.Samples);
    }

    [Fact]
    public void Zero_latency_is_allowed()
    {
        var r = new LatencyRecorder();
        var s = r.Record(T0, T0);
        Assert.Equal(TimeSpan.Zero, s.Latency);
    }

    [Fact]
    public void Target_constants_match_plan()
    {
        Assert.Equal(20, LatencyRecorder.TargetWarmTurns);
        Assert.Equal(1500, LatencyRecorder.MedianTargetMs);
        Assert.Equal(3000, LatencyRecorder.P95TargetMs);
    }

    [Fact]
    public void Twenty_fast_warm_turns_meet_target()
    {
        var warm = Enumerable.Repeat(1200.0, 18).Concat(new[] { 2500.0, 2900.0 }).ToArray();
        var r = WithWarm(6000, warm);
        Assert.True(r.MeetsTarget());
    }

    [Fact]
    public void Target_boundaries_are_inclusive()
    {
        // 18 x 1500 then 2 x 3000: median 1500, nearest-rank p95 (19th value) 3000. Both exactly on target.
        var warm = Enumerable.Repeat(1500.0, 18).Concat(new[] { 3000.0, 3000.0 }).ToArray();
        var r = WithWarm(6000, warm);
        Assert.Equal(1500, r.WarmMedianMs());
        Assert.Equal(3000, r.WarmP95Ms());
        Assert.True(r.MeetsTarget());
    }

    [Fact]
    public void Nineteen_warm_turns_are_not_enough()
    {
        var r = WithWarm(6000, Enumerable.Repeat(800.0, 19).ToArray());
        Assert.False(r.MeetsTarget());
        Add(r, 800, 50);
        Assert.True(r.MeetsTarget());
    }

    [Fact]
    public void Slow_median_fails_target()
    {
        var r = WithWarm(6000, Enumerable.Repeat(1501.0, 20).ToArray());
        Assert.False(r.MeetsTarget());
    }

    [Fact]
    public void Slow_tail_fails_target()
    {
        // 18 fast, 2 slow: nearest-rank p95 is the 19th value = 3001.
        var warm = Enumerable.Repeat(900.0, 18).Concat(new[] { 3001.0, 3001.0 }).ToArray();
        var r = WithWarm(6000, warm);
        Assert.Equal(900, r.WarmMedianMs());
        Assert.Equal(3001, r.WarmP95Ms());
        Assert.False(r.MeetsTarget());
    }

    [Fact]
    public void Slow_cold_turns_alone_do_not_fail_target()
    {
        var r = WithWarm(20000, Enumerable.Repeat(1000.0, 20).ToArray());
        r.Connected();
        Add(r, 25000, 99);
        Assert.Equal(2, r.ColdCount);
        Assert.True(r.MeetsTarget());
    }
}
