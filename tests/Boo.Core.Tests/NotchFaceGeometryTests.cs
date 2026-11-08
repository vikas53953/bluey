using Boo.Core;

namespace Boo.Core.Tests;

// Builder-added tests (face lane); not part of the frozen set.
public sealed class NotchFaceGeometryTests
{
    private static NotchSpec Spec() => FaceSpecFixture.Load();

    [Fact]
    public void Idle_eyes_are_centred_7px_apart()
    {
        var face = Spec().Face;
        var parts = NotchFaceGeometry.Place(face, face.SizeFor(NotchFaceState.Idle));

        Assert.Equal(new PointD(42 - 11.5, 18), parts.LeftEye);
        Assert.Equal(new PointD(42 + 11.5, 18), parts.RightEye);
        // Inner edges are exactly the gap apart.
        Assert.Equal(7, (parts.RightEye.X - 8) - (parts.LeftEye.X + 8), 9);
        Assert.Equal(0, parts.IndicatorSlot.Width);
    }

    [Fact]
    public void Sideways_growth_keeps_eyes_and_gives_extra_width_to_indicator()
    {
        var face = Spec().Face;
        var parts = NotchFaceGeometry.Place(face, face.SizeFor(NotchFaceState.Capture));

        Assert.Equal(42 - 11.5, parts.LeftEye.X, 9);
        Assert.Equal(new RectD(84, 0, 64, 36), parts.IndicatorSlot);
    }

    [Fact]
    public void Face_C_grows_down_with_indicator_below()
    {
        var c = Spec().GetFace("C");
        var parts = NotchFaceGeometry.Place(c, c.SizeFor(NotchFaceState.Listening));

        Assert.Equal(13, parts.LeftEye.Y, 9);
        Assert.Equal(new RectD(0, 26, 104, 22), parts.IndicatorSlot);
    }
}

public sealed class PressHoldTrackerTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void Short_press_is_a_click()
    {
        var t = new PressHoldTracker();
        t.Down(Ms(0));
        Assert.False(t.Tick(Ms(150)));
        Assert.Equal(PressOutcome.Click, t.Up(Ms(199)));
    }

    [Fact]
    public void Long_press_becomes_hold_once_and_never_clicks()
    {
        var t = new PressHoldTracker();
        t.Down(Ms(0));
        Assert.True(t.Tick(Ms(200)));
        Assert.False(t.Tick(Ms(400)));
        Assert.True(t.IsHeld);
        Assert.Equal(PressOutcome.HoldEnded, t.Up(Ms(900)));
        Assert.False(t.IsHeld);
    }

    [Fact]
    public void Late_release_without_tick_is_not_a_click()
    {
        var t = new PressHoldTracker();
        t.Down(Ms(0));
        Assert.Equal(PressOutcome.None, t.Up(Ms(250)));
    }

    [Fact]
    public void Cancel_reports_active_hold()
    {
        var t = new PressHoldTracker();
        t.Down(Ms(0));
        t.Tick(Ms(300));
        Assert.True(t.Cancel());
        Assert.False(t.Cancel());
        Assert.Equal(PressOutcome.None, t.Up(Ms(400)));
    }
}
