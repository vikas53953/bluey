using Boo.Core;

namespace Boo.Core.Tests;

// Builder-added tests (face lane) for the view easing helper; not part of the frozen set.
public sealed class CubicBezierEaseTests
{
    [Fact]
    public void Endpoints_are_exact()
    {
        foreach (var ease in new[] { CubicBezierEase.Reveal, CubicBezierEase.Retract, CubicBezierEase.EaseOut })
        {
            Assert.Equal(0, ease.Evaluate(0));
            Assert.Equal(1, ease.Evaluate(1));
            Assert.Equal(0, ease.Evaluate(-1));
            Assert.Equal(1, ease.Evaluate(2));
        }
    }

    [Fact]
    public void Reveal_overshoots_about_four_percent()
    {
        var max = Enumerable.Range(0, 1001).Select(i => CubicBezierEase.Reveal.Evaluate(i / 1000.0)).Max();

        Assert.InRange(max, 1.02, 1.06);
    }

    [Fact]
    public void Retract_starts_slow_and_is_monotonic()
    {
        Assert.True(CubicBezierEase.Retract.Evaluate(0.25) < 0.1);
        var previous = 0.0;
        for (var i = 0; i <= 100; i++)
        {
            var v = CubicBezierEase.Retract.Evaluate(i / 100.0);
            Assert.True(v >= previous - 1e-9);
            previous = v;
        }
    }

    [Fact]
    public void Linear_bezier_is_identity()
    {
        var linear = new CubicBezierEase(1 / 3.0, 1 / 3.0, 2 / 3.0, 2 / 3.0);
        for (var i = 0; i <= 10; i++)
            Assert.Equal(i / 10.0, linear.Evaluate(i / 10.0), 6);
    }
}
