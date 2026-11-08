namespace Boo.Core.Tests;

// Regression: the notch decided "caption finished" from the opacity it had last written through a 0.01 change
// threshold, so a frame landing at 0.99x left the fade (and the nudge pills gated on it) stuck. Completion must
// come from the caption timeline itself.
public sealed class CaptionRevealCompletionTests
{
    private static readonly CaptionReveal Caption = CaptionReveal.FromSpec(FaceSpecFixture.Load());

    [Fact]
    public void IsComplete_FollowsTheTimeline_NotTheLastWrittenOpacity()
    {
        const string text = "Back to it?";
        var typed = Caption.Duration(text);
        var faded = TimeSpan.FromMilliseconds(Caption.DelayMs + Caption.FadeMs);
        var done = typed > faded ? typed : faded;

        // A frame that lands with the fade at 0.99x is not complete yet, and the next frame past the end is.
        var almost = TimeSpan.FromMilliseconds(Caption.DelayMs + Caption.FadeMs - 1);
        Assert.True(Caption.OpacityAt(almost) is > 0.98 and < 1, $"fixture: opacity at {almost} should be just short of 1");
        Assert.False(Caption.IsComplete(string.Empty, almost), "fade still running: not complete even with empty text");
        Assert.False(Caption.IsComplete(text, done - TimeSpan.FromMilliseconds(1)), "one ms before the end: not complete");
        Assert.True(Caption.IsComplete(text, done), "at the end: complete");
        Assert.True(Caption.IsComplete(text, done + TimeSpan.FromMinutes(5)), "long after: still complete");
        Assert.Equal(1, Caption.OpacityAt(done));
        Assert.Equal(text, Caption.VisibleText(text, done));
    }

    [Fact]
    public void IsComplete_RequiresBothTypingAndFade()
    {
        var slowType = new CaptionReveal(charMs: 100, delayMs: 0, fadeMs: 50);
        Assert.False(slowType.IsComplete("abcd", TimeSpan.FromMilliseconds(60)), "faded but still typing");
        Assert.True(slowType.IsComplete("abcd", TimeSpan.FromMilliseconds(400)), "typed and faded");

        var slowFade = new CaptionReveal(charMs: 10, delayMs: 0, fadeMs: 500);
        Assert.False(slowFade.IsComplete("ab", TimeSpan.FromMilliseconds(100)), "typed but still fading");
        Assert.True(slowFade.IsComplete("ab", TimeSpan.FromMilliseconds(500)), "typed and faded");
    }

    [Fact]
    public void IsComplete_ReducedMotionIsImmediate_NegativeTimeIsNot()
    {
        Assert.True(Caption.IsComplete("anything at all", TimeSpan.Zero, reducedMotion: true));
        Assert.False(Caption.IsComplete("x", TimeSpan.FromMilliseconds(-1)));
        Assert.Throws<ArgumentNullException>(() => Caption.IsComplete(null!, TimeSpan.Zero));
    }
}
