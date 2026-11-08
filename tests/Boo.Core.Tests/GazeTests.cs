using Boo.Core;

namespace Boo.Core.Tests;

// FROZEN acceptance tests for the face lane (plan U1/U2). Builders must not edit, weaken, skip or delete.
public sealed class GazeSolverTests
{
    private const double Gaze = 3.5;
    private const double Reach = 200;
    private static readonly PointD Eye = new(960, 18);

    private static double Ellipse(PointD p, double gx, double gy) => (p.X / gx) * (p.X / gx) + (p.Y / gy) * (p.Y / gy);

    [Fact]
    public void Pointer_at_eye_centre_gives_zero_offset()
    {
        Assert.Equal(new PointD(0, 0), GazeSolver.Target(Eye, Eye, Gaze, Gaze, Reach));
    }

    [Theory]
    [InlineData(10000, 18, 3.5, 0)]      // far right
    [InlineData(-10000, 18, -3.5, 0)]    // far left
    [InlineData(960, 5000, 0, 3.5)]      // far below
    [InlineData(960, -5000, 0, -3.5)]    // far above (second monitor stacked on top)
    public void Far_pointer_is_clamped_to_gaze_radius(double px, double py, double ex, double ey)
    {
        var offset = GazeSolver.Target(new PointD(px, py), Eye, Gaze, Gaze, Reach);

        Assert.Equal(ex, offset.X, 9);
        Assert.Equal(ey, offset.Y, 9);
    }

    [Theory]
    [InlineData(0, 0)]          // top-left corner of primary
    [InlineData(1919, 0)]       // top-right
    [InlineData(0, 1079)]       // bottom-left
    [InlineData(1919, 1079)]    // bottom-right
    [InlineData(-2560, 400)]    // monitor to the left (negative virtual coordinates)
    [InlineData(4480, 1400)]    // monitor to the right
    public void Offset_points_toward_pointer_and_stays_inside_ellipse(double px, double py)
    {
        var pointer = new PointD(px, py);
        var offset = GazeSolver.Target(pointer, Eye, Gaze, Gaze, Reach);

        var dx = px - Eye.X;
        var dy = py - Eye.Y;
        Assert.True(Ellipse(offset, Gaze, Gaze) <= 1 + 1e-9, $"outside ellipse: {offset}");
        // Same direction: positive dot product and zero cross product.
        Assert.True(offset.X * dx + offset.Y * dy > 0);
        Assert.Equal(0, offset.X * dy - offset.Y * dx, 6);
        // All these pointers are beyond reach, so the pupil sits on the boundary.
        Assert.Equal(1, Ellipse(offset, Gaze, Gaze), 6);
    }

    [Fact]
    public void Offset_grows_linearly_inside_reach()
    {
        var half = GazeSolver.Target(new PointD(Eye.X + Reach / 2, Eye.Y), Eye, Gaze, Gaze, Reach);
        var quarter = GazeSolver.Target(new PointD(Eye.X, Eye.Y - Reach / 4), Eye, Gaze, Gaze, Reach);
        var atReach = GazeSolver.Target(new PointD(Eye.X + Reach, Eye.Y), Eye, Gaze, Gaze, Reach);

        Assert.Equal(1.75, half.X, 9);
        Assert.Equal(0, half.Y, 9);
        Assert.Equal(0, quarter.X, 9);
        Assert.Equal(-0.875, quarter.Y, 9);
        Assert.Equal(3.5, atReach.X, 9);
    }

    [Fact]
    public void Magnitude_is_monotonic_in_distance()
    {
        var previous = 0.0;
        for (var d = 0.0; d <= 1000; d += 7.3)
        {
            var o = GazeSolver.Target(new PointD(Eye.X + d * 0.6, Eye.Y + d * 0.8), Eye, Gaze, Gaze, Reach);
            var magnitude = Math.Sqrt(o.X * o.X + o.Y * o.Y);
            Assert.True(magnitude >= previous - 1e-12);
            Assert.True(magnitude <= Gaze + 1e-9);
            previous = magnitude;
        }
    }

    [Fact]
    public void Elliptical_gaze_clamps_to_ellipse_boundary_for_other_faces()
    {
        // Face A: gazeX 3, gazeY 2.
        var diagonal = GazeSolver.Target(new PointD(Eye.X + 5000, Eye.Y + 5000), Eye, 3, 2, Reach);

        Assert.Equal(1, Ellipse(diagonal, 3, 2), 6);
        Assert.Equal(diagonal.X, diagonal.Y, 9); // direction preserved (45 degrees)
        Assert.Equal(3, GazeSolver.Target(new PointD(Eye.X + 5000, Eye.Y), Eye, 3, 2, Reach).X, 9);
        Assert.Equal(2, GazeSolver.Target(new PointD(Eye.X, Eye.Y + 5000), Eye, 3, 2, Reach).Y, 9);
    }

    [Theory]
    [InlineData(double.NaN, 0)]
    [InlineData(0, double.PositiveInfinity)]
    public void Non_finite_pointer_gives_zero(double px, double py)
    {
        Assert.Equal(new PointD(0, 0), GazeSolver.Target(new PointD(px, py), Eye, Gaze, Gaze, Reach));
    }
}

public sealed class GazeSpringTests
{
    private static GazeSpring Spec() => new(260, 0.82);

    [Fact]
    public void Starts_at_rest_at_origin()
    {
        var spring = Spec();

        Assert.Equal(new PointD(0, 0), spring.Position);
        Assert.Equal(new PointD(0, 0), spring.Velocity);
    }

    [Fact]
    public void Converges_to_target_at_60fps()
    {
        var spring = Spec();
        spring.Target = new PointD(3.5, -2);

        for (var i = 0; i < 120; i++)
            spring.Step(TimeSpan.FromSeconds(1.0 / 60));

        Assert.Equal(3.5, spring.Position.X, 2);
        Assert.Equal(-2, spring.Position.Y, 2);
        Assert.True(Math.Abs(spring.Velocity.X) < 0.05);
    }

    [Fact]
    public void Moves_partway_after_one_frame()
    {
        var spring = Spec();
        spring.Target = new PointD(3.5, 0);

        var p = spring.Step(TimeSpan.FromMilliseconds(50));

        Assert.True(p.X > 0 && p.X < 3.5, $"x={p.X}");
        Assert.Equal(p, spring.Position);
    }

    [Fact]
    public void Overshoot_is_slight_and_bounded_under_10_percent()
    {
        var spring = Spec();
        spring.Target = new PointD(3.5, 0);
        var max = 0.0;

        for (var i = 0; i < 180; i++)
        {
            var p = spring.Step(TimeSpan.FromSeconds(1.0 / 60));
            Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));
            max = Math.Max(max, p.X);
        }

        Assert.True(max < 3.5 * 1.10, $"overshoot too large: {max}");
        // Damping 0.82 is a ratio (underdamped): analytic overshoot ~1.1%.
        Assert.True(max > 3.5 * 1.002, $"expected slight underdamped overshoot, max={max}");
    }

    [Theory]
    [InlineData(250)]
    [InlineData(1000)]
    [InlineData(5000)]
    public void Large_dt_is_sub_stepped_and_stable(int ms)
    {
        var spring = Spec();
        spring.Target = new PointD(-3.5, 3.5);

        var p = spring.Step(TimeSpan.FromMilliseconds(ms));

        Assert.True(double.IsFinite(p.X) && double.IsFinite(p.Y));
        Assert.True(Math.Abs(p.X) < 3.5 * 1.10 && Math.Abs(p.Y) < 3.5 * 1.10, $"unstable: {p}");
        if (ms >= 1000)
        {
            Assert.Equal(-3.5, p.X, 2);
            Assert.Equal(3.5, p.Y, 2);
        }
    }

    [Fact]
    public void One_big_step_matches_many_small_steps()
    {
        var a = Spec();
        var b = Spec();
        a.Target = b.Target = new PointD(3.5, 1);

        a.Step(TimeSpan.FromMilliseconds(400));
        for (var i = 0; i < 100; i++)
            b.Step(TimeSpan.FromMilliseconds(4));

        Assert.Equal(b.Position.X, a.Position.X, 3);
        Assert.Equal(b.Position.Y, a.Position.Y, 3);
    }

    [Fact]
    public void Zero_dt_does_not_move_and_negative_dt_throws()
    {
        var spring = Spec();
        spring.Target = new PointD(3.5, 0);

        Assert.Equal(new PointD(0, 0), spring.Step(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => spring.Step(TimeSpan.FromMilliseconds(-1)));
    }

    [Fact]
    public void SnapTo_jumps_with_zero_velocity()
    {
        var spring = Spec();
        spring.Target = new PointD(3.5, 0);
        spring.Step(TimeSpan.FromMilliseconds(30));

        spring.SnapTo(new PointD(1, 2));

        Assert.Equal(new PointD(1, 2), spring.Position);
        Assert.Equal(new PointD(0, 0), spring.Velocity);
    }
}
