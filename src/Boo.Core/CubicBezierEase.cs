namespace Boo.Core;

/// <summary>
/// CSS-style cubic-bezier(x1, y1, x2, y2) easing. Maps a time fraction in [0,1] to a progress value
/// (which may leave [0,1] when y1/y2 do, e.g. the notch reveal overshoot).
/// </summary>
public sealed class CubicBezierEase
{
    private readonly double _x1, _y1, _x2, _y2;

    public CubicBezierEase(double x1, double y1, double x2, double y2)
    {
        if (!(x1 is >= 0 and <= 1) || !(x2 is >= 0 and <= 1))
            throw new ArgumentOutOfRangeException(nameof(x1), "x control points must be within [0,1].");
        if (!double.IsFinite(y1) || !double.IsFinite(y2))
            throw new ArgumentOutOfRangeException(nameof(y1), "y control points must be finite.");
        _x1 = x1; _y1 = y1; _x2 = x2; _y2 = y2;
    }

    /// <summary>Spec "revealEase": cubic-bezier(.25,.9,.3,1.18), about 4% overshoot.</summary>
    public static CubicBezierEase Reveal { get; } = new(0.25, 0.9, 0.3, 1.18);

    /// <summary>Spec "retractEase": cubic-bezier(.5,0,.75,0).</summary>
    public static CubicBezierEase Retract { get; } = new(0.5, 0, 0.75, 0);

    /// <summary>Ease-out used for size changes (CSS "ease-out").</summary>
    public static CubicBezierEase EaseOut { get; } = new(0, 0, 0.58, 1);

    public double Evaluate(double t)
    {
        if (double.IsNaN(t) || t <= 0) return 0;
        if (t >= 1) return 1;

        // Solve x(s) = t for s with Newton, falling back to bisection.
        var s = t;
        for (var i = 0; i < 8; i++)
        {
            var x = Coord(s, _x1, _x2) - t;
            if (Math.Abs(x) < 1e-9) return Coord(s, _y1, _y2);
            var dx = Slope(s, _x1, _x2);
            if (Math.Abs(dx) < 1e-9) break;
            s -= x / dx;
            if (s is < 0 or > 1) break;
        }

        double lo = 0, hi = 1;
        s = t;
        for (var i = 0; i < 60; i++)
        {
            var x = Coord(s, _x1, _x2);
            if (Math.Abs(x - t) < 1e-9) break;
            if (x < t) lo = s; else hi = s;
            s = (lo + hi) / 2;
        }
        return Coord(s, _y1, _y2);
    }

    private static double Coord(double s, double p1, double p2)
    {
        var u = 1 - s;
        return 3 * u * u * s * p1 + 3 * u * s * s * p2 + s * s * s;
    }

    private static double Slope(double s, double p1, double p2)
    {
        var u = 1 - s;
        return 3 * u * u * p1 + 6 * u * s * (p2 - p1) + 3 * s * s * (1 - p2);
    }
}
