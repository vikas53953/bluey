using Boo.Core;

namespace Boo.Core.Tests;

// FROZEN acceptance tests for the face lane (plan U1/U2). Builders must not edit, weaken, skip or delete.
public sealed class NotchLayoutTests
{
    private static readonly RectD Primary1080 = new(0, 0, 1920, 1080);

    public static TheoryData<NotchFaceState, double, double, double> ScaledSizes()
    {
        var data = new TheoryData<NotchFaceState, double, double, double>();
        // Face B sizes written literally (docs/design/boo-notch-spec.json directions.B.sizes).
        var sizes = new Dictionary<NotchFaceState, (double W, double H)>
        {
            [NotchFaceState.Hidden] = (84, 36), [NotchFaceState.Hover] = (84, 36), [NotchFaceState.Idle] = (84, 36),
            [NotchFaceState.Listening] = (172, 36), [NotchFaceState.Thinking] = (128, 36), [NotchFaceState.Responding] = (348, 94),
            [NotchFaceState.Capture] = (148, 36), [NotchFaceState.Nudge] = (348, 108), [NotchFaceState.Paused] = (120, 36),
            [NotchFaceState.Happy] = (96, 36),
        };
        foreach (var scale in new[] { 1.0, 1.25, 1.5 })
            foreach (var (state, size) in sizes)
                data.Add(state, scale, size.W * scale, size.H * scale);
        return data;
    }

    [Theory]
    [MemberData(nameof(ScaledSizes))]
    public void Each_state_size_is_spec_times_scale_and_centred_at_top(NotchFaceState state, double scale, double width, double height)
    {
        var face = FaceSpecFixture.LoadFaceB().Face;
        var screen = new RectD(0, 0, 1920 * scale, 1080 * scale);

        var bounds = NotchLayout.Bounds(face, state, screen, scale);

        Assert.Equal(width, bounds.Width, 6);
        Assert.Equal(height, bounds.Height, 6);
        Assert.Equal(screen.Width / 2 - width / 2, bounds.X, 6);
        Assert.Equal(0, bounds.Y, 6);
    }

    [Fact]
    public void Face_B_literal_sizes_at_100_percent()
    {
        var face = FaceSpecFixture.LoadFaceB().Face;

        Assert.Equal(new RectD(918, 0, 84, 36), NotchLayout.Bounds(face, NotchFaceState.Idle, Primary1080, 1));
        Assert.Equal(new RectD(874, 0, 172, 36), NotchLayout.Bounds(face, NotchFaceState.Listening, Primary1080, 1));
        Assert.Equal(new RectD(786, 0, 348, 94), NotchLayout.Bounds(face, NotchFaceState.Responding, Primary1080, 1));
        Assert.Equal(new RectD(886, 0, 148, 36), NotchLayout.Bounds(face, NotchFaceState.Capture, Primary1080, 1));
        Assert.Equal(new RectD(786, 0, 348, 108), NotchLayout.Bounds(face, NotchFaceState.Nudge, Primary1080, 1));
    }

    [Fact]
    public void Sideways_growth_keeps_centre_and_height()
    {
        var face = FaceSpecFixture.LoadFaceB().Face;
        var idle = NotchLayout.Bounds(face, NotchFaceState.Idle, Primary1080, 1);

        foreach (var state in new[] { NotchFaceState.Listening, NotchFaceState.Thinking, NotchFaceState.Capture, NotchFaceState.Paused, NotchFaceState.Happy })
        {
            var b = NotchLayout.Bounds(face, state, Primary1080, 1);
            Assert.Equal(idle.X + idle.Width / 2, b.X + b.Width / 2, 6);
            Assert.Equal(idle.Height, b.Height);
            Assert.True(b.Width >= idle.Width);
            Assert.Equal(0, b.Y);
        }
    }

    [Fact]
    public void Secondary_monitor_offsets_are_respected()
    {
        var face = FaceSpecFixture.LoadFaceB().Face;
        var right = new RectD(1920, 0, 2560, 1440);
        var left = new RectD(-2560, -360, 2560, 1440);

        Assert.Equal(new RectD(1920 + 1280 - 63, 0, 126, 54), NotchLayout.Bounds(face, NotchFaceState.Idle, right, 1.5));
        Assert.Equal(new RectD(-2560 + 1280 - 42, -360, 84, 36), NotchLayout.Bounds(face, NotchFaceState.Hover, left, 1));
    }

    [Fact]
    public void Face_C_grows_down_and_stays_centred()
    {
        var c = FaceSpecFixture.Load().GetFace("C");

        var idle = NotchLayout.Bounds(c, NotchFaceState.Idle, Primary1080, 1);
        var listening = NotchLayout.Bounds(c, NotchFaceState.Listening, Primary1080, 1);

        Assert.Equal(idle.Width, listening.Width);
        Assert.True(listening.Height > idle.Height);
        Assert.Equal(960 - 52, listening.X);
        Assert.Equal(0, listening.Y);
    }

    [Theory]
    [InlineData(1.0, 850, 220, 8)]
    [InlineData(1.25, 1062.5, 275, 10)]
    [InlineData(1.5, 1275, 330, 12)]
    public void Hover_strip_is_220x8_scaled_centred_at_top(double scale, double x, double width, double height)
    {
        var screen = new RectD(0, 0, 1920 * scale, 1080 * scale);

        var strip = NotchLayout.HoverStripBounds(FaceSpecFixture.Load(), screen, scale);

        Assert.Equal(x, strip.X, 6);
        Assert.Equal(0, strip.Y);
        Assert.Equal(width, strip.Width, 6);
        Assert.Equal(height, strip.Height, 6);
    }

    [Fact]
    public void Invalid_scale_throws()
    {
        var face = FaceSpecFixture.LoadFaceB().Face;

        Assert.Throws<ArgumentOutOfRangeException>(() => NotchLayout.Bounds(face, NotchFaceState.Idle, Primary1080, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => NotchLayout.Bounds(face, NotchFaceState.Idle, Primary1080, double.NaN));
    }
}

public sealed class NotchRevealTimelineTests
{
    private static NotchRevealTimeline Create() => new(FaceSpecFixture.Timings);

    private static TimeSpan Ms(double ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Starts_hidden()
    {
        var t = Create();

        Assert.Equal(RevealPhase.Hidden, t.PhaseAt(Ms(0)));
        Assert.Equal(0, t.ProgressAt(Ms(0)));
    }

    [Fact]
    public void Reveal_takes_240_ms()
    {
        var t = Create();
        t.PointerEntered(Ms(1000));

        Assert.Equal(RevealPhase.Revealing, t.PhaseAt(Ms(1000)));
        Assert.Equal(0, t.ProgressAt(Ms(1000)), 6);
        Assert.Equal(0.5, t.ProgressAt(Ms(1120)), 6);
        Assert.Equal(RevealPhase.Revealing, t.PhaseAt(Ms(1239)));
        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(1240)));
        Assert.Equal(1, t.ProgressAt(Ms(1240)), 6);
    }

    [Fact]
    public void Retract_waits_900_ms_then_takes_160_ms()
    {
        var t = Create();
        t.PointerEntered(Ms(0));
        t.PointerLeft(Ms(2000));

        Assert.Equal(RevealPhase.RetractPending, t.PhaseAt(Ms(2000)));
        Assert.Equal(RevealPhase.RetractPending, t.PhaseAt(Ms(2899)));
        Assert.Equal(1, t.ProgressAt(Ms(2899)), 6);
        Assert.Equal(RevealPhase.Retracting, t.PhaseAt(Ms(2900)));
        Assert.Equal(1, t.ProgressAt(Ms(2900)), 6);
        Assert.Equal(0.5, t.ProgressAt(Ms(2980)), 6);
        Assert.Equal(RevealPhase.Retracting, t.PhaseAt(Ms(3059)));
        Assert.Equal(RevealPhase.Hidden, t.PhaseAt(Ms(3060)));
        Assert.Equal(0, t.ProgressAt(Ms(3060)), 6);
    }

    [Fact]
    public void Reentering_during_delay_cancels_retract()
    {
        var t = Create();
        t.PointerEntered(Ms(0));
        t.PointerLeft(Ms(1000));
        t.PointerEntered(Ms(1500));

        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(1500)));
        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(5000)));
        Assert.Equal(1, t.ProgressAt(Ms(5000)));

        // A fresh leave restarts the full 900 ms delay.
        t.PointerLeft(Ms(6000));
        Assert.Equal(RevealPhase.RetractPending, t.PhaseAt(Ms(6899)));
        Assert.Equal(RevealPhase.Retracting, t.PhaseAt(Ms(6900)));
    }

    [Fact]
    public void Reentering_while_retracting_reveals_from_current_progress()
    {
        var t = Create();
        t.PointerEntered(Ms(0));
        t.PointerLeft(Ms(1000));
        t.PointerEntered(Ms(1980)); // 80 ms into the 160 ms retract: progress 0.5

        Assert.Equal(RevealPhase.Revealing, t.PhaseAt(Ms(1980)));
        Assert.Equal(0.5, t.ProgressAt(Ms(1980)), 6);
        Assert.True(t.ProgressAt(Ms(2000)) > 0.5);
        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(1980 + 240)));
    }

    [Fact]
    public void Leaving_mid_reveal_finishes_reveal_then_waits_the_delay()
    {
        var t = Create();
        t.PointerEntered(Ms(0));
        t.PointerLeft(Ms(100));

        Assert.True(t.ProgressAt(Ms(200)) > t.ProgressAt(Ms(100)));
        Assert.Equal(RevealPhase.Hidden, t.PhaseAt(Ms(100 + 900 + 160 + 240)));
        Assert.NotEqual(RevealPhase.Hidden, t.PhaseAt(Ms(999)));
        Assert.NotEqual(RevealPhase.Retracting, t.PhaseAt(Ms(999)));
    }

    [Fact]
    public void Pinned_never_retracts_and_unpin_starts_delay()
    {
        var t = Create();
        t.SetPinned(true, Ms(0));
        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(240)));

        t.PointerEntered(Ms(500));
        t.PointerLeft(Ms(600));
        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(10_000)));

        t.SetPinned(false, Ms(10_000));
        Assert.Equal(RevealPhase.RetractPending, t.PhaseAt(Ms(10_899)));
        Assert.Equal(RevealPhase.Retracting, t.PhaseAt(Ms(10_900)));
        Assert.Equal(RevealPhase.Hidden, t.PhaseAt(Ms(11_060)));
    }

    [Fact]
    public void Unpin_with_pointer_inside_stays_revealed()
    {
        var t = Create();
        t.SetPinned(true, Ms(0));
        t.PointerEntered(Ms(300));
        t.SetPinned(false, Ms(400));

        Assert.Equal(RevealPhase.Revealed, t.PhaseAt(Ms(5000)));
    }

    [Fact]
    public void Leave_without_enter_is_harmless()
    {
        var t = Create();
        t.PointerLeft(Ms(10));

        Assert.Equal(RevealPhase.Hidden, t.PhaseAt(Ms(2000)));
    }
}
