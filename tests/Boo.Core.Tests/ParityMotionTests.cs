using System.Text.Json.Nodes;
using Boo.Core;

namespace Boo.Core.Tests;

// Parity audit (2026-11-08): spec "timings" values that were not loaded or not modelled in Boo.Core.
// Written before the implementation (test-first) on lane/parity.
public sealed class NotchMotionSpecTests
{
    // Shipped values, written out literally so the tests do not depend on the parser under test.
    private static readonly NotchMotionTimings Shipped = new(
        CaptionFadeMs: 160, CaptionDelayMs: 120, EntryBlinkDelayMs: 120,
        PeekRiseMs: 240, PeekRiseDelayMs: 90, IdleWanderAfterMs: 2000,
        SaccadeEveryMinMs: 600, SaccadeEveryMaxMs: 2200,
        ThinkingDotCycleMs: 1200, RecordPulseMs: 2000, DrawStrokeMs: 620, AppOpenMs: 280,
        NudgeShake: new NudgeShakeSpec(Count: 2, AmplitudeDip: 3, EachMs: 320));

    [Fact]
    public void Shipped_spec_motion_timings_match_the_json()
    {
        Assert.Equal(Shipped, FaceSpecFixture.Load().Motion);
    }

    [Fact]
    public void Default_motion_equals_shipped_motion()
    {
        Assert.Equal(FaceSpecFixture.Load().Motion, NotchSpec.Default.Motion);
    }

    [Fact]
    public void Motion_survives_a_face_swap()
    {
        Assert.Equal(Shipped, FaceSpecFixture.Load().WithChosenFace("C").Motion);
    }

    [Fact]
    public void Missing_motion_keys_fall_back_to_shipped_values()
    {
        using var stream = FaceSpecFixture.Mutated(n =>
        {
            var t = n["timings"]!.AsObject();
            foreach (var key in new[] { "captionFadeMs", "captionDelayMs", "entryBlinkDelayMs", "peekRiseMs", "peekRiseDelayMs",
                         "idleWanderAfterMs", "saccadeEveryMs", "thinkingDotCycleMs", "recordPulseMs", "drawStrokeMs", "appOpenMs", "nudgeShake" })
                t.Remove(key);
        });

        Assert.Equal(Shipped, NotchSpec.Load(stream).Motion);
    }

    [Fact]
    public void Changed_motion_values_are_data_driven()
    {
        using var stream = FaceSpecFixture.Mutated(n =>
        {
            var t = n["timings"]!;
            t["captionDelayMs"] = 50;
            t["saccadeEveryMs"] = new JsonArray(300, 900);
            t["nudgeShake"] = "3 × ±4.5 px, 200 ms each";
        });

        var motion = NotchSpec.Load(stream).Motion;
        Assert.Equal(50, motion.CaptionDelayMs);
        Assert.Equal(300, motion.SaccadeEveryMinMs);
        Assert.Equal(900, motion.SaccadeEveryMaxMs);
        Assert.Equal(new NudgeShakeSpec(3, 4.5, 200), motion.NudgeShake);
    }

    [Theory]
    [InlineData("captionFadeMs", -1)]
    [InlineData("thinkingDotCycleMs", 0)]
    [InlineData("recordPulseMs", 1.5)]
    public void Invalid_present_motion_values_throw(string key, double value)
    {
        using var stream = FaceSpecFixture.Mutated(n => n["timings"]![key] = value);

        Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
    }

    [Fact]
    public void Inverted_saccade_range_throws()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["timings"]!["saccadeEveryMs"] = new JsonArray(2200, 600));

        Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
    }

    [Theory]
    [InlineData("shake a bit")]
    [InlineData("0 × ±3 px, 320 ms each")]
    [InlineData("2 × ±3 px, 0 ms each")]
    public void Unreadable_nudge_shake_throws(string text)
    {
        using var stream = FaceSpecFixture.Mutated(n => n["timings"]!["nudgeShake"] = text);

        Assert.Throws<NotchSpecException>(() => NotchSpec.Load(stream));
    }

    [Fact]
    public void Ascii_x_and_plus_minus_are_accepted_in_nudge_shake()
    {
        using var stream = FaceSpecFixture.Mutated(n => n["timings"]!["nudgeShake"] = "2 x +-3 px, 320 ms each");

        Assert.Equal(new NudgeShakeSpec(2, 3, 320), NotchSpec.Load(stream).Motion.NudgeShake);
    }

    [Fact]
    public void Spec_draw_stroke_matches_the_pointer_ring_default()
    {
        Assert.Equal(PointerFlightPlanner.DefaultRingStrokeMs, FaceSpecFixture.Load().Motion.DrawStrokeMs);
        Assert.Equal(new PointerSequenceTimings().DrawStrokeMs, FaceSpecFixture.Load().Motion.DrawStrokeMs);
    }

    [Fact]
    public void Spec_chirp_text_matches_the_synth_constants()
    {
        var root = JsonNode.Parse(FaceSpecFixture.Json)!;
        var timing = root["timings"]!["chirp"]!.GetValue<string>();
        var chosen = root["chosen"]!["chirp"]!["spec"]!.GetValue<string>();

        foreach (var text in new[] { timing, chosen })
        {
            Assert.Contains($"{ChirpSynth.FirstToneHz:0} Hz", text);
            Assert.Contains($"{ChirpSynth.SecondToneHz:0} Hz", text);
            Assert.Contains($"{ChirpSynth.ToneMilliseconds} ms each", text);
            Assert.Contains($"{ChirpSynth.GapMilliseconds} ms gap", text);
        }
        Assert.Contains("gain 0.12", chosen);
        Assert.Equal(0.12, ChirpSynth.DefaultVolume);
    }
}

public sealed class CaptionRevealTests
{
    private static CaptionReveal Create() => CaptionReveal.FromSpec(FaceSpecFixture.Load());

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void From_spec_uses_28_ms_per_char_120_ms_delay_and_160_ms_fade()
    {
        var caption = Create();

        Assert.Equal(28, caption.CharMs);
        Assert.Equal(120, caption.DelayMs);
        Assert.Equal(160, caption.FadeMs);
    }

    [Fact]
    public void Nothing_shows_before_the_delay()
    {
        var caption = Create();

        Assert.Equal(string.Empty, caption.VisibleText("Back to the essay?", Ms(119.9)));
        Assert.Equal(0, caption.OpacityAt(Ms(0)));
        Assert.Equal(0, caption.OpacityAt(Ms(120)));
    }

    [Fact]
    public void One_character_appears_every_28_ms_after_the_delay()
    {
        var caption = Create();
        const string text = "Back to the essay?";

        Assert.Equal(string.Empty, caption.VisibleText(text, Ms(120)));
        Assert.Equal("B", caption.VisibleText(text, Ms(148)));
        Assert.Equal("Ba", caption.VisibleText(text, Ms(176)));
        Assert.Equal("Back", caption.VisibleText(text, Ms(120 + 4 * 28 + 27)));
        Assert.Equal(text, caption.VisibleText(text, Ms(120 + text.Length * 28)));
        Assert.Equal(text, caption.VisibleText(text, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void Duration_is_delay_plus_28_ms_per_character()
    {
        Assert.Equal(Ms(120 + 18 * 28), Create().Duration("Back to the essay?"));
        Assert.Equal(Ms(120), Create().Duration(string.Empty));
    }

    [Fact]
    public void Opacity_fades_in_linearly_over_160_ms_after_the_delay()
    {
        var caption = Create();

        Assert.Equal(0.5, caption.OpacityAt(Ms(200)), 6);
        Assert.Equal(1, caption.OpacityAt(Ms(280)));
        Assert.Equal(1, caption.OpacityAt(Ms(5000)));
    }

    [Fact]
    public void Surrogate_pairs_are_never_split()
    {
        var caption = Create();
        const string text = "\U0001F440 hi";

        Assert.Equal("\U0001F440", caption.VisibleText(text, Ms(148)));
        Assert.Equal(Ms(120 + 4 * 28), caption.Duration(text));
    }

    [Fact]
    public void Reduced_motion_shows_everything_at_once()
    {
        var caption = Create();

        Assert.Equal("Back to the essay?", caption.VisibleText("Back to the essay?", TimeSpan.Zero, reducedMotion: true));
        Assert.Equal(1, caption.OpacityAt(TimeSpan.Zero, reducedMotion: true));
        Assert.Equal(TimeSpan.Zero, caption.Duration("Back to the essay?", reducedMotion: true));
    }

    [Fact]
    public void Negative_elapsed_shows_nothing_and_null_text_throws()
    {
        var caption = Create();

        Assert.Equal(string.Empty, caption.VisibleText("hi", Ms(-5)));
        Assert.Equal(0, caption.OpacityAt(Ms(-5)));
        Assert.Throws<ArgumentNullException>(() => caption.VisibleText(null!, Ms(0)));
    }

    [Fact]
    public void Invalid_construction_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptionReveal(charMs: 0, delayMs: 0, fadeMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptionReveal(charMs: 28, delayMs: -1, fadeMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new CaptionReveal(charMs: 28, delayMs: 0, fadeMs: -1));
    }

    [Fact]
    public void Zero_fade_is_fully_opaque_at_the_delay()
    {
        var caption = new CaptionReveal(charMs: 28, delayMs: 100, fadeMs: 0);

        Assert.Equal(0, caption.OpacityAt(Ms(99)));
        Assert.Equal(1, caption.OpacityAt(Ms(100)));
    }
}

public sealed class NudgeShakeTests
{
    private static readonly NudgeShakeSpec Spec = new(Count: 2, AmplitudeDip: 3, EachMs: 320);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Shake_is_two_320_ms_cycles_of_plus_minus_3()
    {
        Assert.Equal(0, NudgeShake.OffsetAt(Spec, Ms(0)), 9);
        Assert.Equal(3, NudgeShake.OffsetAt(Spec, Ms(80)), 9);
        Assert.Equal(0, NudgeShake.OffsetAt(Spec, Ms(160)), 9);
        Assert.Equal(-3, NudgeShake.OffsetAt(Spec, Ms(240)), 9);
        Assert.Equal(3, NudgeShake.OffsetAt(Spec, Ms(400)), 9);
        Assert.Equal(-3, NudgeShake.OffsetAt(Spec, Ms(560)), 9);
        Assert.Equal(Ms(640), NudgeShake.Duration(Spec));
    }

    [Fact]
    public void Shake_rests_after_the_last_cycle_and_before_start()
    {
        Assert.Equal(0, NudgeShake.OffsetAt(Spec, Ms(640)));
        Assert.Equal(0, NudgeShake.OffsetAt(Spec, Ms(10_000)));
        Assert.Equal(0, NudgeShake.OffsetAt(Spec, Ms(-1)));
    }

    [Fact]
    public void Shake_never_exceeds_the_amplitude_and_peaks_four_times()
    {
        var peaks = 0;
        var previous = 0d;
        var rising = true;
        for (var ms = 0; ms < 640; ms++)
        {
            var x = NudgeShake.OffsetAt(Spec, Ms(ms));
            Assert.InRange(x, -3, 3);
            var nowRising = x > previous;
            if (ms > 0 && rising != nowRising) peaks++;
            rising = nowRising;
            previous = x;
        }
        Assert.Equal(4, peaks);
    }

    [Fact]
    public void Reduced_motion_never_shakes()
    {
        for (var ms = 0; ms <= 700; ms += 10)
            Assert.Equal(0, NudgeShake.OffsetAt(Spec, Ms(ms), reducedMotion: true));
    }

    [Fact]
    public void Shipped_spec_shake_is_used_by_from_spec()
    {
        Assert.Equal(Spec, FaceSpecFixture.Load().Motion.NudgeShake);
    }
}

public sealed class GazeWanderTests
{
    private static NotchMotionTimings Motion => FaceSpecFixture.Load().Motion;

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    private static GazeWander Create(int seed = 7, bool reducedMotion = false) => new(Motion, new Random(seed), reducedMotion);

    [Fact]
    public void Follows_the_pointer_until_it_has_rested_for_2000_ms()
    {
        var wander = Create();
        wander.ObservePointer(new PointD(500, 500), Ms(0));

        Assert.Null(wander.TargetAt(Ms(1999), 3.5, 3.5));
        Assert.NotNull(wander.TargetAt(Ms(2000), 3.5, 3.5));
    }

    [Fact]
    public void Sub_pixel_jitter_does_not_count_as_movement_but_real_movement_does()
    {
        var wander = Create();
        wander.ObservePointer(new PointD(500, 500), Ms(0));
        wander.ObservePointer(new PointD(500.3, 500.2), Ms(1500));
        Assert.NotNull(wander.TargetAt(Ms(2000), 3.5, 3.5));

        wander.ObservePointer(new PointD(520, 500), Ms(2100));
        Assert.Null(wander.TargetAt(Ms(2100), 3.5, 3.5));
        Assert.Null(wander.TargetAt(Ms(4099), 3.5, 3.5));
        Assert.NotNull(wander.TargetAt(Ms(4100), 3.5, 3.5));
    }

    [Fact]
    public void Saccades_jump_every_600_to_2200_ms_and_hold_in_between()
    {
        var wander = Create(seed: 11);
        wander.ObservePointer(new PointD(0, 0), Ms(0));

        var changes = new List<double>();
        PointD? last = null;
        for (var ms = 2000; ms <= 2000 + 120_000; ms += 5)
        {
            var target = wander.TargetAt(Ms(ms), 3.5, 3.5);
            Assert.NotNull(target);
            if (last is not null && target != last) changes.Add(ms);
            last = target;
        }

        Assert.True(changes.Count > 50, $"only {changes.Count} saccades");
        var first = changes[0] - 2000;
        Assert.InRange(first, 600, 2205);
        for (var i = 1; i < changes.Count; i++)
            Assert.InRange(changes[i] - changes[i - 1], 595, 2205);
    }

    [Fact]
    public void Saccade_targets_stay_inside_the_gaze_ellipse()
    {
        var wander = Create(seed: 3);
        wander.ObservePointer(new PointD(0, 0), Ms(0));

        for (var ms = 2000; ms <= 200_000; ms += 50)
        {
            var t = wander.TargetAt(Ms(ms), 4, 1.5)!.Value;
            var r = (t.X / 4) * (t.X / 4) + (t.Y / 1.5) * (t.Y / 1.5);
            Assert.True(r <= 1 + 1e-9, $"{t} outside ellipse");
        }
    }

    [Fact]
    public void Same_seed_gives_the_same_wander()
    {
        var a = Create(seed: 5);
        var b = Create(seed: 5);
        a.ObservePointer(new PointD(1, 1), Ms(0));
        b.ObservePointer(new PointD(1, 1), Ms(0));

        for (var ms = 2000; ms <= 30_000; ms += 100)
            Assert.Equal(a.TargetAt(Ms(ms), 3.5, 3.5), b.TargetAt(Ms(ms), 3.5, 3.5));
    }

    [Fact]
    public void Reduced_motion_never_wanders()
    {
        var wander = Create(reducedMotion: true);
        wander.ObservePointer(new PointD(0, 0), Ms(0));

        Assert.Null(wander.TargetAt(Ms(60_000), 3.5, 3.5));
    }

    [Fact]
    public void Without_any_pointer_sample_the_idle_clock_starts_at_the_first_query()
    {
        var wander = Create();

        Assert.Null(wander.TargetAt(Ms(10_000), 3.5, 3.5));
        Assert.NotNull(wander.TargetAt(Ms(12_000), 3.5, 3.5));
    }

    [Fact]
    public void Non_finite_pointer_samples_are_ignored()
    {
        var wander = Create();
        wander.ObservePointer(new PointD(0, 0), Ms(0));
        wander.ObservePointer(new PointD(double.NaN, double.NaN), Ms(1000));

        Assert.NotNull(wander.TargetAt(Ms(2000), 3.5, 3.5));
    }
}

public sealed class EntryBlinkTests
{
    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Entry_blink_happens_120_ms_after_appearing_then_normal_schedule()
    {
        var scheduler = new BlinkScheduler(FaceSpecFixture.Timings, new Random(42), entryBlinkDelayMs: 120);
        var events = scheduler.EventsUntil(TimeSpan.FromMinutes(10));

        Assert.Equal(new BlinkEvent(Ms(120), IsDouble: false), events[0]);
        for (var i = 1; i < events.Count; i++)
            Assert.InRange((events[i].Start - events[i - 1].Start).TotalMilliseconds, 2800, 6000);

        Assert.Equal(1, scheduler.OpennessAt(Ms(119)));
        Assert.Equal(0, scheduler.OpennessAt(Ms(190)), 6);
        Assert.Equal(1, scheduler.OpennessAt(Ms(280)));
    }

    [Fact]
    public void Entry_blink_is_off_by_default_and_with_reduced_motion()
    {
        var plain = new BlinkScheduler(FaceSpecFixture.Timings, new Random(42));
        Assert.InRange(plain.EventsUntil(TimeSpan.FromMinutes(1))[0].Start.TotalMilliseconds, 2800, 6000);

        var reduced = new BlinkScheduler(FaceSpecFixture.Timings, new Random(42), reducedMotion: true, entryBlinkDelayMs: 120);
        Assert.Empty(reduced.EventsUntil(TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Negative_entry_delay_is_rejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new BlinkScheduler(FaceSpecFixture.Timings, new Random(1), entryBlinkDelayMs: -1));
    }
}
