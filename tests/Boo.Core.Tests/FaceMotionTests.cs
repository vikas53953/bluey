// Frozen tests for face polish (independent author). Do not weaken.
//
// Source of truth: src/Boo.Core/FaceMotion.cs and NotchRowLayout (bottom of NotchFaceGeometry.cs) doc comments,
// cross-checked against docs/design/boo-notch-studio.html (class Boo: mood(), tick(), apply(); CSS .bars/.dots/
// .recdot/.shake/.boo .r) and docs/design/boo-notch-spec.json (face B chosen). Where they differ the contract wins.
// Every expected number below is derived by hand from those formulas; the arithmetic is written next to it.
using System.Diagnostics;
using Boo.Core;

namespace Boo.Core.Tests;

// ---------------------------------------------------------------------------------------------------------------
// 1. FaceMoods.For: the studio mood() table.
// ---------------------------------------------------------------------------------------------------------------
public sealed class FaceMotionMoodTests
{
    [Theory]
    [InlineData(NotchFaceState.Hidden)]
    [InlineData(NotchFaceState.Hover)]
    [InlineData(NotchFaceState.Idle)]
    [InlineData(NotchFaceState.Capture)]
    public void Default_states_follow_the_pointer_with_neutral_eyes(NotchFaceState state)
    {
        var m = FaceMoods.For(state, 1234);
        Assert.Equal(1, m.Open, 9);
        Assert.Equal(1, m.PupilScale, 9);
        Assert.Null(m.LookAt);
        Assert.True(m.FollowPointer);
        Assert.False(m.Wander);
        Assert.True(m.Blinks);
        Assert.False(m.Happy);
        Assert.Equal(1, m.EyeOpacity, 9);
    }

    [Fact]
    public void Listening_opens_wide_with_bigger_pupils_and_follows()
    {
        var m = FaceMoods.For(NotchFaceState.Listening, 500);
        Assert.Equal(1.14, m.Open, 9);
        Assert.Equal(1.15, m.PupilScale, 9);
        Assert.Null(m.LookAt);
        Assert.True(m.FollowPointer);
        Assert.False(m.Wander);
        Assert.True(m.Blinks);
        Assert.False(m.Happy);
        Assert.Equal(1, m.EyeOpacity, 9);
    }

    [Fact]
    public void Thinking_looks_up_and_away_with_slow_wobble()
    {
        // nowMs = 0: sin(0) = 0 -> (0.85, -0.9).
        var m0 = FaceMoods.For(NotchFaceState.Thinking, 0);
        Assert.Equal(0.74, m0.Open, 9);
        Assert.Equal(0.9, m0.PupilScale, 9);
        Assert.NotNull(m0.LookAt);
        Assert.Equal(0.85, m0.LookAt!.Value.X, 9);
        Assert.Equal(-0.9, m0.LookAt!.Value.Y, 9);
        Assert.False(m0.FollowPointer);
        Assert.False(m0.Wander);
        Assert.True(m0.Blinks);
        Assert.False(m0.Happy);
        Assert.Equal(1, m0.EyeOpacity, 9);

        // nowMs = 700·π/2: sin = 1 -> x = 0.85 + 0.08 = 0.93.  nowMs = 700·3π/2: sin = -1 -> 0.77.
        Assert.Equal(0.93, FaceMoods.For(NotchFaceState.Thinking, 700 * Math.PI / 2).LookAt!.Value.X, 9);
        Assert.Equal(0.77, FaceMoods.For(NotchFaceState.Thinking, 700 * 3 * Math.PI / 2).LookAt!.Value.X, 9);
        Assert.Equal(-0.9, FaceMoods.For(NotchFaceState.Thinking, 700 * 3 * Math.PI / 2).LookAt!.Value.Y, 9);
    }

    [Fact]
    public void Responding_wanders_instead_of_following()
    {
        var m = FaceMoods.For(NotchFaceState.Responding, 900);
        Assert.Equal(0.96, m.Open, 9);
        Assert.Equal(1.05, m.PupilScale, 9);
        Assert.Null(m.LookAt);
        Assert.False(m.FollowPointer);
        Assert.True(m.Wander);
        Assert.True(m.Blinks);
        Assert.False(m.Happy);
        Assert.Equal(1, m.EyeOpacity, 9);
    }

    [Fact]
    public void Nudge_looks_down_left_with_big_pupils()
    {
        var m = FaceMoods.For(NotchFaceState.Nudge, 0);
        Assert.Equal(1.2, m.Open, 9);
        Assert.Equal(1.25, m.PupilScale, 9);
        Assert.Equal(new PointD(-0.7, 0.85), m.LookAt);
        Assert.False(m.FollowPointer);
        Assert.False(m.Wander);
        Assert.True(m.Blinks);
        Assert.False(m.Happy);
        Assert.Equal(1, m.EyeOpacity, 9);
    }

    [Fact]
    public void Paused_is_nearly_closed_dimmed_and_does_not_blink()
    {
        var m = FaceMoods.For(NotchFaceState.Paused, 0);
        Assert.Equal(0.12, m.Open, 9);
        Assert.Equal(1, m.PupilScale, 9); // studio: m.pupil undefined -> 1
        Assert.Equal(new PointD(0, 0.6), m.LookAt);
        Assert.False(m.FollowPointer);
        Assert.False(m.Wander);
        Assert.False(m.Blinks);
        Assert.False(m.Happy);
        Assert.Equal(0.72, m.EyeOpacity, 9);
    }

    [Fact]
    public void Happy_draws_arcs_looks_straight_and_does_not_blink()
    {
        var m = FaceMoods.For(NotchFaceState.Happy, 0);
        Assert.Equal(1, m.Open, 9);
        Assert.Equal(1, m.PupilScale, 9);
        Assert.Equal(new PointD(0, 0), m.LookAt);
        Assert.False(m.FollowPointer);
        Assert.False(m.Wander);
        Assert.False(m.Blinks);
        Assert.True(m.Happy);
        Assert.Equal(1, m.EyeOpacity, 9);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// 2. NotchRowLayout: studio ".boo .r" row = [lead][eyes][trail], padding 12, gap 10, space-between.
// Face B: eye w 16, gap 7 -> eyes block = 2·16 + 7 = 39; row = hover height 36; eye centres at y = 18.
// Left eye centre = eyesX + 8; right eye centre = eyesX + 16 + 7 + 8 = eyesX + 31.
// ---------------------------------------------------------------------------------------------------------------
public sealed class FaceMotionRowLayoutTests
{
    private static readonly NotchFaceSpec B = FaceSpecFixture.LoadFaceB().Face;
    private static readonly NotchFaceSpec C = FaceSpecFixture.Load().GetFace("C");
    private const double Tol = 1e-9;

    [Theory]
    [InlineData(NotchFaceState.Capture, 7, 18)]
    [InlineData(NotchFaceState.Listening, 0, 25)]   // 5·3 + 4·2.5
    [InlineData(NotchFaceState.Responding, 0, 25)]
    [InlineData(NotchFaceState.Thinking, 0, 20)]    // 3·4 + 2·4
    [InlineData(NotchFaceState.Paused, 0, 9)]       // 2·3 + 3
    [InlineData(NotchFaceState.Hidden, 0, 0)]
    [InlineData(NotchFaceState.Hover, 0, 0)]
    [InlineData(NotchFaceState.Idle, 0, 0)]
    [InlineData(NotchFaceState.Nudge, 0, 0)]
    [InlineData(NotchFaceState.Happy, 0, 0)]
    public void Slot_widths_per_state(NotchFaceState state, double lead, double trail)
    {
        var (l, t) = NotchRowLayout.SlotWidths(state);
        Assert.Equal(lead, l, 9);
        Assert.Equal(trail, t, 9);
    }

    [Fact]
    public void Indicator_constants_match_slot_widths()
    {
        Assert.Equal(25, FaceIndicators.BarCount * FaceIndicators.BarWidth + (FaceIndicators.BarCount - 1) * FaceIndicators.BarGap, 9);
        Assert.Equal(20, FaceIndicators.DotCount * FaceIndicators.DotSize + (FaceIndicators.DotCount - 1) * FaceIndicators.DotGap, 9);
    }

    private static NotchRowParts PlaceB(NotchFaceState state)
    {
        var (lead, trail) = NotchRowLayout.SlotWidths(state);
        return NotchRowLayout.Place(B, B.SizeFor(state), lead, trail);
    }

    private static void AssertRect(RectD expected, RectD actual)
    {
        Assert.Equal(expected.X, actual.X, 9);
        Assert.Equal(expected.Y, actual.Y, 9);
        Assert.Equal(expected.Width, actual.Width, 9);
        Assert.Equal(expected.Height, actual.Height, 9);
    }

    private static void AssertEyes(NotchRowParts p, double leftX, double rightX, double y)
    {
        Assert.Equal(leftX, p.LeftEye.X, 9);
        Assert.Equal(rightX, p.RightEye.X, 9);
        Assert.Equal(y, p.LeftEye.Y, 9);
        Assert.Equal(y, p.RightEye.Y, 9);
    }

    [Fact]
    public void Idle_84_centres_the_eyes()
    {
        // free = 84 - 24 - 0 - 39 - 0 - 20 = 1; eyesX = 12 + 0 + 10 + 0.5 = 22.5 -> centres 30.5 / 53.5.
        var p = PlaceB(NotchFaceState.Idle);
        AssertEyes(p, 30.5, 53.5, 18);
        Assert.Equal(36, p.RowHeight, 9);
        AssertRect(new RectD(12, 0, 0, 36), p.Lead);
        AssertRect(new RectD(72, 0, 0, 36), p.Trail);       // 84 - 12 - 0
        AssertRect(new RectD(16, 38, 52, 0), p.Caption);    // height 36 - 36 - 2 = -2 -> clamped 0
    }

    [Fact]
    public void Listening_172_places_bars_right()
    {
        // free = 172 - 24 - 0 - 39 - 25 - 20 = 64; eyesX = 22 + 32 = 54 -> centres 62 / 85. trail x = 172 - 12 - 25 = 135.
        var p = PlaceB(NotchFaceState.Listening);
        AssertEyes(p, 62, 85, 18);
        AssertRect(new RectD(135, 0, 25, 36), p.Trail);
        AssertRect(new RectD(12, 0, 0, 36), p.Lead);
    }

    [Fact]
    public void Thinking_128_places_dots_right()
    {
        // free = 128 - 24 - 39 - 20 - 20 = 25; eyesX = 22 + 12.5 = 34.5 -> centres 42.5 / 65.5. trail x = 128 - 12 - 20 = 96.
        var p = PlaceB(NotchFaceState.Thinking);
        AssertEyes(p, 42.5, 65.5, 18);
        AssertRect(new RectD(96, 0, 20, 36), p.Trail);
    }

    [Fact]
    public void Capture_148_has_record_dot_lead_and_stop_trail()
    {
        // free = 148 - 24 - 7 - 39 - 18 - 20 = 40; eyesX = 12 + 7 + 10 + 20 = 49 -> centres 57 / 80. trail x = 148 - 12 - 18 = 118.
        var p = PlaceB(NotchFaceState.Capture);
        AssertRect(new RectD(12, 0, 7, 36), p.Lead);
        AssertEyes(p, 57, 80, 18);
        AssertRect(new RectD(118, 0, 18, 36), p.Trail);
    }

    [Fact]
    public void Paused_120_places_pause_bars_right()
    {
        // free = 120 - 24 - 39 - 9 - 20 = 28; eyesX = 22 + 14 = 36 -> centres 44 / 67. trail x = 120 - 12 - 9 = 99.
        var p = PlaceB(NotchFaceState.Paused);
        AssertEyes(p, 44, 67, 18);
        AssertRect(new RectD(99, 0, 9, 36), p.Trail);
    }

    [Fact]
    public void Happy_96_centres_eyes_in_wider_body()
    {
        // free = 96 - 24 - 39 - 20 = 13; eyesX = 22 + 6.5 = 28.5 -> centres 36.5 / 59.5.
        var p = PlaceB(NotchFaceState.Happy);
        AssertEyes(p, 36.5, 59.5, 18);
    }

    [Fact]
    public void Responding_348x94_has_caption_below_row()
    {
        // free = 348 - 24 - 39 - 25 - 20 = 240; eyesX = 22 + 120 = 142 -> centres 150 / 173. trail x = 348 - 12 - 25 = 311.
        // caption = (16, 36 + 2, 348 - 32, 94 - 36 - 2) = (16, 38, 316, 56).
        var p = PlaceB(NotchFaceState.Responding);
        Assert.Equal(36, p.RowHeight, 9);
        AssertEyes(p, 150, 173, 18);
        AssertRect(new RectD(311, 0, 25, 36), p.Trail);
        AssertRect(new RectD(16, 38, 316, 56), p.Caption);
    }

    [Fact]
    public void Nudge_348x108_has_taller_caption()
    {
        // free = 348 - 24 - 39 - 20 = 265; eyesX = 22 + 132.5 = 154.5 -> centres 162.5 / 185.5.
        // caption = (16, 38, 316, 108 - 36 - 2 = 70).
        var p = PlaceB(NotchFaceState.Nudge);
        AssertEyes(p, 162.5, 185.5, 18);
        AssertRect(new RectD(16, 38, 316, 70), p.Caption);
    }

    [Fact]
    public void Too_narrow_body_packs_items_from_the_left()
    {
        // body 60x36, trail 25: free = 60 - 24 - 0 - 39 - 25 - 20 = -48 < 0 -> eyesX = 12 + 0 + 10 = 22;
        // centres 30 / 53; trail right after eyes + 10 = 22 + 39 + 10 = 71.
        var p = NotchRowLayout.Place(B, new SizeD(60, 36), 0, 25);
        AssertEyes(p, 30, 53, 18);
        AssertRect(new RectD(71, 0, 25, 36), p.Trail);

        // with a lead: lead 7, trail 18: free = 60 - 24 - 7 - 39 - 18 - 20 = -48 -> eyesX = 12 + 7 + 10 = 29;
        // centres 37 / 60; trail x = 29 + 39 + 10 = 78.
        var q = NotchRowLayout.Place(B, new SizeD(60, 36), 7, 18);
        AssertRect(new RectD(12, 0, 7, 36), q.Lead);
        AssertEyes(q, 37, 60, 18);
        AssertRect(new RectD(78, 0, 18, 36), q.Trail);
    }

    [Fact]
    public void Face_C_centres_eyes_and_puts_trail_below_the_row()
    {
        // C: eye w 14, gap 14 -> block 42; row = hover height 26 -> y = 13.
        // thinking 104x44: eyes centred: eyesX = 52 - 21 = 31 -> centres 38 / 66.
        // trail = (52 - 10, 26, 20, max(0, 44 - 26 - 6) = 12) = (42, 26, 20, 12). lead = (12, 0, 0, 26).
        var p = NotchRowLayout.Place(C, C.SizeFor(NotchFaceState.Thinking), 0, 20);
        Assert.Equal(26, p.RowHeight, 9);
        AssertEyes(p, 38, 66, 13);
        AssertRect(new RectD(12, 0, 0, 26), p.Lead);
        AssertRect(new RectD(42, 26, 20, 12), p.Trail);

        // listening 104x48, trail 25: (52 - 12.5, 26, 25, 48 - 26 - 6) = (39.5, 26, 25, 16).
        var l = NotchRowLayout.Place(C, C.SizeFor(NotchFaceState.Listening), 0, 25);
        AssertEyes(l, 38, 66, 13);
        AssertRect(new RectD(39.5, 26, 25, 16), l.Trail);

        // idle 104x26: trail height max(0, 26 - 26 - 6) = 0.
        var i = NotchRowLayout.Place(C, C.SizeFor(NotchFaceState.Idle), 0, 0);
        AssertEyes(i, 38, 66, 13);
        Assert.Equal(0, i.Trail.Height, 9);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// 3. FaceIndicators: bars, thinking dots, record ring, nudge shake.
// ---------------------------------------------------------------------------------------------------------------
public sealed class FaceMotionIndicatorTests
{
    [Theory]
    // 3 + 12·(0.35 + 0.65·|sin(0/(140+37i) + i)|) at t = 0:
    [InlineData(0, 0, 7.2)]                  // sin 0 = 0 -> 3 + 4.2
    [InlineData(1, 0, 13.763473681501592)]   // |sin 1| = 0.841471
    [InlineData(2, 0, 14.292519929240317)]   // |sin 2| = 0.909297
    [InlineData(3, 0, 8.300736062866964)]    // |sin 3| = 0.141120
    [InlineData(2, 1000, 10.163390999004648)] // |sin(1000/214 + 2)|
    public void Listening_bar_formula(int i, double t, double expected)
    {
        Assert.Equal(expected, FaceIndicators.ListeningBarHeight(i, t), 6);
    }

    [Fact]
    public void Listening_bars_stay_within_range()
    {
        for (var i = 0; i < FaceIndicators.BarCount; i++)
            for (var t = 0.0; t < 5000; t += 3.7)
            {
                var h = FaceIndicators.ListeningBarHeight(i, t);
                Assert.InRange(h, 7.2 - 1e-9, 15 + 1e-9);
            }
    }

    [Theory]
    // 3 + 12·level·(0.4 + 0.6·|sin(t/90 + 1.3i)|)
    [InlineData(0, 0, 1, 7.8)]                      // 3 + 12·0.4
    [InlineData(1, 0, 1, 14.73761893500379)]        // |sin 1.3| = 0.963558
    [InlineData(3, 500, 0.5, 5.510781849421763)]    // |sin(500/90 + 3.9)|
    public void Talking_bar_formula(int i, double t, double level, double expected)
    {
        Assert.Equal(expected, FaceIndicators.TalkingBarHeight(i, t, level), 6);
    }

    [Fact]
    public void Talking_bars_are_flat_at_level_zero_and_bounded()
    {
        for (var i = 0; i < FaceIndicators.BarCount; i++)
            for (var t = 0.0; t < 3000; t += 7.1)
            {
                Assert.Equal(3, FaceIndicators.TalkingBarHeight(i, t, 0), 9);
                Assert.InRange(FaceIndicators.TalkingBarHeight(i, t, 1), 7.8 - 1e-9, 15 + 1e-9);
            }
    }

    [Theory]
    [InlineData(0, 0, 0.25, 0)]       // 0%
    [InlineData(0, 360, 1, -2)]       // 30% of 1200
    [InlineData(0, 720, 0.25, 0)]     // 60%
    [InlineData(0, 1000, 0.25, 0)]    // between 60% and 100%, both rest
    [InlineData(0, 1200, 0.25, 0)]    // next cycle start
    [InlineData(1, 0, 0.25, 0)]       // before delay 200
    [InlineData(1, 199, 0.25, 0)]
    [InlineData(1, 560, 1, -2)]       // 200 + 360
    [InlineData(2, 399, 0.25, 0)]     // before delay 400
    [InlineData(2, 760, 1, -2)]       // 400 + 360
    public void Thinking_dot_keyframes(int i, double since, double opacity, double offsetY)
    {
        var (o, y) = FaceIndicators.ThinkingDot(i, since);
        Assert.Equal(opacity, o, 6);
        Assert.Equal(offsetY, y, 6);
    }

    [Fact]
    public void Thinking_dot_mid_rise_uses_css_ease()
    {
        // 180 ms = half of the 0..360 segment; CSS ease(.25,.1,.25,1) at x = 0.5 -> 0.802403.
        // opacity = 0.25 + 0.75·0.802403 = 0.851803; offsetY = -2·0.802403 = -1.604807.
        var (o, y) = FaceIndicators.ThinkingDot(0, 180);
        Assert.Equal(0.851803, o, 3);
        Assert.Equal(-1.604807, y, 3);
    }

    [Fact]
    public void Thinking_dot_is_periodic_and_rises_monotonically()
    {
        for (var i = 0; i < FaceIndicators.DotCount; i++)
            for (var t = 200.0 * i; t < 200.0 * i + 1200; t += 13)
            {
                var a = FaceIndicators.ThinkingDot(i, t);
                var b = FaceIndicators.ThinkingDot(i, t + 1200);
                var c = FaceIndicators.ThinkingDot(i, t + 3600);
                Assert.Equal(a.Opacity, b.Opacity, 9);
                Assert.Equal(a.OffsetY, b.OffsetY, 9);
                Assert.Equal(a.Opacity, c.Opacity, 9);
                Assert.InRange(a.Opacity, 0.25 - 1e-9, 1 + 1e-9);
                Assert.InRange(a.OffsetY, -2 - 1e-9, 1e-9);
            }

        var prev = FaceIndicators.ThinkingDot(0, 0);
        for (var t = 5.0; t <= 360; t += 5)
        {
            var cur = FaceIndicators.ThinkingDot(0, t);
            Assert.True(cur.Opacity > prev.Opacity, $"opacity not rising at {t}");
            Assert.True(cur.OffsetY < prev.OffsetY, $"offset not rising at {t}");
            prev = cur;
        }
    }

    [Theory]
    [InlineData(0, 3, 0.28)]
    [InlineData(1000, 1, 0.10)]
    [InlineData(2000, 3, 0.28)]
    [InlineData(500, 2, 0.19)]                       // ease-in-out is symmetric: 0.5 -> 0.5
    [InlineData(250, 2.7416761, 0.2567509)]          // ease(.42,0,.58,1)(0.25) = 0.129162 -> 3 - 2·0.129162, 0.28 - 0.18·0.129162
    [InlineData(3000, 1, 0.10)]                      // loops
    public void Record_ring_pulse(double since, double spread, double alpha)
    {
        var (s, a) = FaceIndicators.RecordRing(since);
        Assert.Equal(spread, s, 3);
        Assert.Equal(alpha, a, 3);
    }

    [Fact]
    public void Record_ring_stays_in_range()
    {
        for (var t = 0.0; t < 6000; t += 11.3)
        {
            var (s, a) = FaceIndicators.RecordRing(t);
            Assert.InRange(s, 1 - 1e-9, 3 + 1e-9);
            Assert.InRange(a, 0.10 - 1e-9, 0.28 + 1e-9);
        }
    }

    [Theory]
    [InlineData(-50, 0)]
    [InlineData(0, 0)]
    [InlineData(40, -2.3690951)]  // half of 0..80: -3·bezier(.36,.07,.19,.97)(0.5) = -3·0.789698
    [InlineData(80, -3)]          // 25%
    [InlineData(160, 1.7381901)]  // half of 80..240: -3 + 6·0.789698
    [InlineData(240, 3)]          // 75%
    [InlineData(320, 0)]          // end of iteration 1
    [InlineData(400, -3)]         // 320 + 80
    [InlineData(560, 3)]          // 320 + 240
    [InlineData(640, 0)]
    [InlineData(700, 0)]
    [InlineData(5000, 0)]
    public void Nudge_shake_keyframes(double since, double expected)
    {
        Assert.Equal(expected, FaceIndicators.NudgeShakeX(since), 3);
    }

    [Fact]
    public void Nudge_shake_is_bounded()
    {
        for (var t = -100.0; t < 1000; t += 1.7)
            Assert.InRange(FaceIndicators.NudgeShakeX(t), -3 - 1e-9, 3 + 1e-9);
    }
}

// ---------------------------------------------------------------------------------------------------------------
// 4. FaceMotion behaviour (deterministic Random + explicit clock).
// ---------------------------------------------------------------------------------------------------------------
public sealed class FaceMotionBehaviourTests
{
    private static readonly NotchSpec Spec = FaceSpecFixture.LoadFaceB();
    private const double GazeX = 3.5; // face B gazeX / gazeY

    private static FaceMotion New(int seed = 42, bool reduced = false) => new(Spec, new Random(seed)) { ReducedMotion = reduced };

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    // Pointer that "moves" every frame by 1 DIP (> 0.5) but always maps to the same clamped target.
    private static PointD Jitter(int frame, double x, double y) => new(x + (frame % 2), y);

    /// <summary>Tick from..to (inclusive) every step ms. Returns the frames with their times.</summary>
    private static List<(double T, FaceFrame F)> Run(FaceMotion m, double from, double to, double step, Func<int, double, PointD?> pointer)
    {
        var list = new List<(double, FaceFrame)>();
        var n = 0;
        for (var t = from; t <= to + 1e-9; t += step, n++)
            list.Add((t, m.Tick(Ms(t), pointer(n, t))));
        return list;
    }

    [Fact]
    public void Follow_settles_on_pointer_direction_and_drives_eye_parts()
    {
        var m = New();
        m.SetState(NotchFaceState.Idle, Ms(0));
        var frames = Run(m, 0, 1500, 16, (n, _) => Jitter(n, 400, 0)); // 400/320 clamps to 1
        var last = frames[^1].F;
        Assert.Equal(1, last.Gaze.X, 2);
        Assert.Equal(0, last.Gaze.Y, 2);
        Assert.Equal(GazeX, last.PupilOffset.X, 1);
        Assert.Equal(0, last.PupilOffset.Y, 2);
        Assert.Equal(1, last.EyeShift.X, 2);
        Assert.Equal(0, last.EyeShift.Y, 2);
        Assert.Equal(1, last.PupilScale, 9);
        Assert.False(last.Happy);
        Assert.Equal(0, last.Bounce, 9);

        // Button eyes: EyeShift = (gx, gy - bounce), PupilOffset = (gx·3.5, gy·3.5), on every frame.
        foreach (var (_, f) in frames)
        {
            Assert.Equal(f.Gaze.X, f.EyeShift.X, 9);
            Assert.Equal(f.Gaze.Y - f.Bounce, f.EyeShift.Y, 9);
            Assert.Equal(f.Gaze.X * GazeX, f.PupilOffset.X, 9);
            Assert.Equal(f.Gaze.Y * GazeX, f.PupilOffset.Y, 9);
        }
    }

    [Fact]
    public void Follow_clamps_far_pointer_to_unit_box()
    {
        var m = New();
        m.SetState(NotchFaceState.Idle, Ms(0));
        var last = Run(m, 0, 1500, 16, (n, _) => Jitter(n, -1000, -1000))[^1].F;
        Assert.Equal(-1, last.Gaze.X, 2);
        Assert.Equal(-1, last.Gaze.Y, 2);
    }

    [Fact]
    public void Follow_maps_pointer_by_reach_320_by_220()
    {
        // (160, -110) -> (160/320, -110/220) = (0.5, -0.5).
        var m = New();
        m.SetState(NotchFaceState.Idle, Ms(0));
        var last = Run(m, 0, 1500, 16, (n, _) => new PointD(160 + (n % 2) * 0.6, -110))[^1].F;
        Assert.Equal(0.5, last.Gaze.X, 2);
        Assert.Equal(-0.5, last.Gaze.Y, 2);
    }

    [Fact]
    public void Spring_rises_fast_without_big_overshoot_or_jumps()
    {
        // Spring k 260, ζ 0.82: analytic 90% rise ≈ 190 ms, overshoot ≈ 1%, max step per 16 ms frame ≈ 0.11.
        var m = New();
        m.SetState(NotchFaceState.Idle, Ms(0));
        var frames = Run(m, 0, 1200, 16, (n, _) => Jitter(n, 400, 0));
        var t90 = frames.First(p => p.F.Gaze.X >= 0.9).T;
        Assert.True(t90 <= 350, $"90% reached only at {t90} ms");
        Assert.True(t90 >= 80, $"90% reached at {t90} ms: too fast for a spring (snapping?)");
        Assert.True(frames.Max(p => p.F.Gaze.X) < 1.05, "overshoot too large");
        for (var i = 1; i < frames.Count; i++)
            Assert.True(Math.Abs(frames[i].F.Gaze.X - frames[i - 1].F.Gaze.X) <= 0.2, $"jump at {frames[i].T}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void Still_pointer_starts_wandering_after_2000_ms(int seed)
    {
        var m = New(seed);
        m.SetState(NotchFaceState.Idle, Ms(0));
        // Move until t = 992 (last frame index 62 -> x 1000, previous 1001), then hold (1000, 1000).
        var frames = Run(m, 0, 10000, 16, (n, t) => t <= 992 ? Jitter(n, 1000, 1000) : new PointD(1000, 1000));

        // Still following (1, 1) before 992 + 2000.
        var before = frames.Last(p => p.T <= 2900).F;
        Assert.Equal(1, before.Gaze.X, 2);
        Assert.Equal(1, before.Gaze.Y, 2);

        // After wander starts (and the spring settles): glances x ∈ [-0.9, 0.9], y ∈ [-0.9, 0.4] (+0.05 spring margin).
        var wander = frames.Where(p => p.T >= 992 + 2000 + 600).Select(p => p.F.Gaze).ToList();
        Assert.All(wander, g =>
        {
            Assert.InRange(g.X, -0.95, 0.95);
            Assert.InRange(g.Y, -0.95, 0.45);
        });
        Assert.True(wander.Max(g => g.X) - wander.Min(g => g.X) > 0.1 || wander.Max(g => g.Y) - wander.Min(g => g.Y) > 0.1,
            "gaze never moved between glances");
    }

    [Fact]
    public void Responding_wanders_even_while_pointer_moves()
    {
        var m = New(7);
        m.SetState(NotchFaceState.Responding, Ms(0));
        var frames = Run(m, 0, 8000, 16, (n, _) => Jitter(n, 1000, 1000));
        var g = frames.Where(p => p.T >= 600).Select(p => p.F.Gaze).ToList();
        Assert.All(g, p =>
        {
            Assert.InRange(p.X, -0.95, 0.95);
            Assert.InRange(p.Y, -0.95, 0.45); // following would push y to 1
        });
        Assert.True(g.Max(p => p.X) - g.Min(p => p.X) > 0.1, "responding gaze never wandered");
    }

    [Fact]
    public void Reduced_motion_wander_holds_centre()
    {
        var m = New(7, reduced: true);
        m.SetState(NotchFaceState.Responding, Ms(0));
        var frames = Run(m, 0, 4000, 16, (n, _) => Jitter(n, 1000, 1000));
        Assert.All(frames.Skip(1), p =>
        {
            Assert.Equal(0, p.F.Gaze.X, 9);
            Assert.Equal(0, p.F.Gaze.Y, 9);
        });

        // Idle with a still pointer: follows (snapped) until 2000 ms after the last move, then (0, 0).
        var idle = New(7, reduced: true);
        idle.SetState(NotchFaceState.Idle, Ms(0));
        var f2 = Run(idle, 0, 5000, 16, (n, t) => t <= 992 ? Jitter(n, 1000, 1000) : new PointD(1000, 1000));
        var following = f2.Last(p => p.T <= 2900).F.Gaze;
        Assert.Equal(new PointD(1, 1), following);
        Assert.All(f2.Where(p => p.T >= 992 + 2000 + 20), p => Assert.Equal(new PointD(0, 0), p.F.Gaze));
    }

    [Fact]
    public void Reduced_motion_snaps_gaze_to_target()
    {
        var m = New(reduced: true);
        m.SetState(NotchFaceState.Idle, Ms(0));
        m.Tick(Ms(0), new PointD(0, 0));
        var f = m.Tick(Ms(16), new PointD(160, -110)); // moved -> target (0.5, -0.5)
        Assert.Equal(0.5, f.Gaze.X, 9);
        Assert.Equal(-0.5, f.Gaze.Y, 9);

        m.SetState(NotchFaceState.Nudge, Ms(32));
        var n = m.Tick(Ms(32), new PointD(161, -110));
        Assert.Equal(-0.7, n.Gaze.X, 9);
        Assert.Equal(0.85, n.Gaze.Y, 9);
        var n2 = m.Tick(Ms(48), new PointD(162, -110));
        Assert.Equal(-0.7, n2.Gaze.X, 9); // no overshoot afterwards
    }

    [Fact]
    public void Reduced_motion_has_no_bounce()
    {
        var happy = New(reduced: true);
        happy.SetState(NotchFaceState.Happy, Ms(0));
        Assert.All(Run(happy, 0, 3000, 16, (_, _) => null), p => Assert.Equal(0, p.F.Bounce, 9));

        var talk = New(reduced: true);
        talk.SetState(NotchFaceState.Responding, Ms(0));
        var frames = Run(talk, 0, 3000, 16, (_, _) => null);
        Assert.All(frames, p => Assert.Equal(0, p.F.Bounce, 9));
        Assert.True(frames.Max(p => p.F.TalkLevel) > 0.2, "talk level still animates (it drives the bars)");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Blinks_keep_happening_including_reduced_motion(bool reduced)
    {
        var m = New(11, reduced);
        m.SetState(NotchFaceState.Idle, Ms(0));
        var frames = Run(m, 0, 12000, 16, (_, _) => null);
        // A 160 ms blink sampled every 16 ms has a frame within 16 ms of the bottom: b <= 16/70 -> openness <= 0.29.
        Assert.True(frames.Where(p => p.T >= 400).Min(p => p.F.Openness) < 0.5, "no scheduled blink in 12 s");
        Assert.All(frames, p => Assert.InRange(p.F.Openness, 0.06 - 1e-12, 1 + 1e-9));
    }

    [Fact]
    public void Entry_blink_follows_leaving_hidden()
    {
        var m = New(5);
        Run(m, 0, 990, 10, (_, _) => null); // hidden
        m.SetState(NotchFaceState.Idle, Ms(1000));
        var frames = Run(m, 1000, 1500, 5, (_, _) => null).ToDictionary(p => (int)Math.Round(p.T), p => p.F);
        // Entry blink at 1000 + 120: closes 1120..1190 (b 1 -> 0, openness 0.08 at bottom), opens 1190..1280.
        Assert.Equal(1, frames[1100].Openness, 2);
        Assert.True(frames[1190].Openness < 0.15, $"not closed at bottom: {frames[1190].Openness}");
        Assert.True(frames[1155].Openness is > 0.4 and < 0.65, $"half-closed expected ≈0.54: {frames[1155].Openness}"); // 0.08 + 0.92·0.5
        Assert.Equal(1, frames[1300].Openness, 2);
    }

    [Fact]
    public void Openness_never_drops_below_floor()
    {
        // Thinking: 0.74 · 0.08 = 0.0592 < 0.06 -> clamped to 0.06 at the bottom of the entry blink.
        var m = New(3);
        m.SetState(NotchFaceState.Thinking, Ms(0));
        var frames = Run(m, 0, 400, 1, (_, _) => null);
        var min = frames.Min(p => p.F.Openness);
        Assert.Equal(0.06, min, 6);
        Assert.All(frames, p => Assert.True(p.F.Openness >= 0.06 - 1e-12));
    }

    [Fact]
    public void Paused_is_fixed_nearly_closed_and_never_blinks()
    {
        var m = New(9);
        m.SetState(NotchFaceState.Paused, Ms(0));
        var frames = Run(m, 0, 20000, 16, (n, _) => Jitter(n, 300, 0));
        Assert.All(frames, p =>
        {
            Assert.Equal(0.12, p.F.Openness, 9);
            Assert.Equal(0.72, p.F.EyeOpacity, 9);
        });
        Assert.Equal(0, frames[^1].F.Gaze.X, 2);
        Assert.Equal(0.6, frames[^1].F.Gaze.Y, 2);
    }

    [Fact]
    public void Happy_bounces_with_arcs_and_no_pupil_motion()
    {
        var m = New(4);
        m.SetState(NotchFaceState.Happy, Ms(0));
        var frames = Run(m, 0, 4000, 16, (n, _) => Jitter(n, 400, 200));
        Assert.All(frames, p =>
        {
            var expected = Math.Max(0, Math.Sin(p.T / 180)) * 1.5;
            Assert.True(p.F.Happy);
            Assert.Equal(expected, p.F.Bounce, 9);
            Assert.Equal(0, p.F.EyeShift.X, 9);
            Assert.Equal(-expected, p.F.EyeShift.Y, 9);
            Assert.Equal(new PointD(0, 0), p.F.PupilOffset);
            Assert.Equal(1, p.F.Openness, 9); // happy never blinks
        });
        Assert.True(frames.Max(p => p.F.Bounce) > 1.4);
    }

    [Fact]
    public void Responding_talk_level_animates_and_decays()
    {
        var m = New(6);
        m.SetState(NotchFaceState.Responding, Ms(0));
        var talking = Run(m, 0, 2000, 16, (_, _) => null);
        Assert.All(talking, p =>
        {
            Assert.InRange(p.F.TalkLevel, 0, 1);
            Assert.Equal(p.F.TalkLevel * 1.6, p.F.Bounce, 9);
            Assert.Equal(p.F.Gaze.Y - p.F.Bounce, p.F.EyeShift.Y, 9);
            Assert.Equal(1.05, p.F.PupilScale, 9);
        });
        Assert.True(talking.Max(p => p.F.TalkLevel) > 0.3, "talk level never rose");

        // Not blinking (entry blink done by 280 ms, first scheduled blink >= 2800 ms): openness = 0.96 + 0.12·level.
        Assert.All(talking.Where(p => p.T >= 400), p => Assert.Equal(0.96 + 0.12 * p.F.TalkLevel, p.F.Openness, 6));

        m.SetState(NotchFaceState.Idle, Ms(2016));
        var after = Run(m, 2016, 2400, 16, (_, _) => null);
        // level *= (1 - 16·25/1000 = 0.6) per frame -> after ~19 frames ≈ 1e-4.
        Assert.True(after.First(p => p.T >= 2016 + 300).F.TalkLevel < 0.01);
        Assert.All(after, p => Assert.Equal(0, p.F.Bounce, 9));
    }

    [Fact]
    public void Listening_opens_wide_when_not_blinking()
    {
        var m = New(8);
        m.SetState(NotchFaceState.Listening, Ms(0));
        var frames = Run(m, 0, 2500, 16, (n, _) => Jitter(n, 100, 0));
        Assert.All(frames.Where(p => p.T >= 400), p =>
        {
            Assert.Equal(1.14, p.F.Openness, 9);
            Assert.Equal(1.15, p.F.PupilScale, 9);
        });
    }

    [Fact]
    public void Nudge_drives_gaze_down_left()
    {
        var m = New();
        m.SetState(NotchFaceState.Nudge, Ms(0));
        var last = Run(m, 0, 1200, 16, (n, _) => Jitter(n, 1000, -1000))[^1].F;
        Assert.Equal(-0.7, last.Gaze.X, 2);
        Assert.Equal(0.85, last.Gaze.Y, 2);
        Assert.Equal(1.25, last.PupilScale, 9);
    }

    [Fact]
    public void Thinking_gaze_tracks_the_wobble()
    {
        var m = New();
        m.SetState(NotchFaceState.Thinking, Ms(0));
        var frames = Run(m, 0, 4000, 16, (n, _) => Jitter(n, -500, 500));
        Assert.All(frames.Where(p => p.T >= 800), p =>
        {
            Assert.InRange(Math.Abs(p.F.Gaze.X - (0.85 + 0.08 * Math.Sin(p.T / 700))), 0, 0.03);
            Assert.InRange(Math.Abs(p.F.Gaze.Y + 0.9), 0, 0.03);
        });
    }

    [Fact]
    public void Ten_thousand_ticks_are_cheap()
    {
        var m = New();
        m.SetState(NotchFaceState.Responding, Ms(0));
        for (var i = 0; i < 100; i++) m.Tick(Ms(i * 16), new PointD(i, 0)); // warm-up / JIT
        var sw = Stopwatch.StartNew();
        for (var i = 100; i < 10100; i++)
            m.Tick(Ms(i * 16), new PointD(i % 500, i % 300));
        sw.Stop();
        Assert.True(sw.ElapsedMilliseconds < 500, $"10,000 ticks took {sw.ElapsedMilliseconds} ms");
    }
}
