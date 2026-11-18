using System.Text.Json;
using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

// Pointer lane acceptance (plan U3): the blue pointer flies to the target, then the ring is drawn.
public sealed class PointerFlightPlannerTests
{
    private static readonly DisplayInfo Primary = new("primary", "Primary", new RectD(0, 0, 1920, 1080), 1.0);
    private static readonly DisplayInfo Right150 = new("right", "Right", new RectD(1920, 0, 2560, 1440), 1.5);
    private static readonly DisplayInfo Left150 = new("left", "Left", new RectD(-1920, -200, 1920, 1080), 1.5);

    // PF-001
    [Fact]
    public void A_flight_starts_on_the_cursor_and_ends_exactly_on_the_target_centre()
    {
        var start = new PointD(100, 100);

        var plan = PointerFlightPlanner.Plan(start, new RectD(900, 500, 100, 40), Primary);

        Assert.NotNull(plan);
        Assert.Equal(new PointD(950, 520), plan.Anchor);
        Assert.Equal(new PointerFrame(0, start), plan.Frames[0]);
        Assert.Equal(new PointerFrame(plan.DurationMs, new PointD(950, 520)), plan.Frames[^1]);
        Assert.Equal("primary", plan.DisplayId);
        Assert.Equal(new RectD(900, 500, 100, 40), plan.Target);
    }

    // PF-002
    [Theory]
    [InlineData(0, 1.0, 280)]
    [InlineData(10, 1.0, 282.5)]
    [InlineData(400, 1.0, 380)]
    [InlineData(600, 1.5, 380)]
    [InlineData(1360, 1.0, 620)]
    [InlineData(4000, 1.0, 620)]
    [InlineData(4000, 1.5, 620)]
    public void Duration_scales_with_dip_distance_and_is_clamped_to_280_620_ms(double distancePx, double scale, double expectedMs)
    {
        Assert.Equal(expectedMs, PointerFlightPlanner.DurationFor(distancePx, scale), 9);
    }

    // PF-003
    [Fact]
    public void Duration_is_monotonic_in_distance_and_never_leaves_its_bounds()
    {
        var previous = 0d;
        for (var distance = 0d; distance <= 5000; distance += 37)
        {
            var duration = PointerFlightPlanner.DurationFor(distance, 1.25);
            Assert.InRange(duration, PointerFlightPlanner.MinimumDurationMs, PointerFlightPlanner.MaximumDurationMs);
            Assert.True(duration >= previous, $"duration fell at {distance}px");
            previous = duration;
        }

        Assert.Equal(280d, PointerFlightPlanner.MinimumDurationMs);
        Assert.Equal(620d, PointerFlightPlanner.MaximumDurationMs);
    }

    // PF-004
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0.25, 0.0625)]
    [InlineData(0.5, 0.5)]
    [InlineData(0.75, 0.9375)]
    [InlineData(1, 1)]
    [InlineData(-1, 0)]
    [InlineData(2, 1)]
    public void Ease_is_cubic_ease_in_out_and_clamped(double t, double expected)
    {
        Assert.Equal(expected, PointerFlightPlanner.Ease(t), 12);
    }

    // PF-005
    [Fact]
    public void Frames_are_time_ordered_move_toward_the_anchor_and_ease_in_and_out()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(1500, 900, 100, 40), Primary);

        Assert.NotNull(plan);
        Assert.True(plan.Frames.Count >= 10);
        for (var i = 1; i < plan.Frames.Count; i++)
        {
            var gap = plan.Frames[i].TimeMs - plan.Frames[i - 1].TimeMs;
            Assert.True(gap > 0, "frame times must strictly increase");
            Assert.True(gap <= PointerFlightPlanner.DefaultFrameIntervalMs + 1e-9, "frame gap exceeds the frame interval");
            Assert.True(
                Distance(plan.Frames[i].Position, plan.Anchor) <= Distance(plan.Frames[i - 1].Position, plan.Anchor) + 1e-9,
                "the pointer moved away from the anchor");
        }

        var firstStep = Distance(plan.Frames[0].Position, plan.Frames[1].Position);
        var middle = plan.Frames.Count / 2;
        var middleStep = Distance(plan.Frames[middle - 1].Position, plan.Frames[middle].Position);
        var lastStep = Distance(plan.Frames[^2].Position, plan.Frames[^1].Position);
        Assert.True(firstStep < middleStep, "flight must ease in");
        Assert.True(lastStep < middleStep, "flight must ease out");
    }

    // PF-006
    [Fact]
    public void Frames_sample_every_interval_and_add_a_final_frame_at_the_exact_duration()
    {
        // 400 px at 100% => 380 ms.
        var plan = PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(450, 80, 100, 40), Primary, frameIntervalMs: 100);

        Assert.NotNull(plan);
        Assert.Equal(380d, plan.DurationMs, 9);
        Assert.Equal(new[] { 0d, 100d, 200d, 300d, 380d }, plan.Frames.Select(frame => frame.TimeMs).ToArray());
        foreach (var frame in plan.Frames)
        {
            Assert.Equal(plan.PositionAt(frame.TimeMs), frame.Position);
        }

        Assert.Equal(new PointD(500, 100), plan.Frames[^1].Position);
    }

    // PF-007
    [Fact]
    public void Planning_is_deterministic()
    {
        var first = PointerFlightPlanner.Plan(new PointD(-1800, -150), new RectD(-600, 600, 300, 90), Left150);
        var second = PointerFlightPlanner.Plan(new PointD(-1800, -150), new RectD(-600, 600, 300, 90), Left150);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first.DurationMs, second.DurationMs);
        Assert.Equal(first.Frames, second.Frames);
    }

    // PF-008
    [Fact]
    public void Position_at_follows_the_ease_and_is_clamped_to_start_and_anchor()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(450, 80, 100, 40), Primary);

        Assert.NotNull(plan);
        Assert.Equal(new PointD(100, 100), plan.PositionAt(-5));
        Assert.Equal(new PointD(100, 100), plan.PositionAt(0));
        Assert.Equal(new PointD(500, 100), plan.PositionAt(plan.DurationMs));
        Assert.Equal(new PointD(500, 100), plan.PositionAt(plan.DurationMs + 100));
        var half = plan.PositionAt(plan.DurationMs / 2);
        Assert.Equal(300d, half.X, 9);
        Assert.Equal(100d, half.Y, 9);
    }

    // PF-009
    [Fact]
    public void Zero_distance_produces_a_single_frame_on_the_anchor()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(950, 520), new RectD(900, 500, 100, 40), Primary);

        Assert.NotNull(plan);
        Assert.Equal(0d, plan.DurationMs);
        Assert.Equal(new[] { new PointerFrame(0, new PointD(950, 520)) }, plan.Frames);
    }

    // PF-010
    [Fact]
    public void A_sub_pixel_distance_counts_as_zero_distance()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(950.3, 520), new RectD(900, 500, 100, 40), Primary);

        Assert.NotNull(plan);
        Assert.Single(plan.Frames);
        Assert.Equal(new PointD(950, 520), plan.Frames[0].Position);
    }

    // PF-011 (R3: no guessed mark)
    [Theory]
    [InlineData(2000, 100, 50, 50)]
    [InlineData(-200, 100, 50, 50)]
    [InlineData(100, 1080, 50, 50)]
    [InlineData(100, 100, 0, 50)]
    [InlineData(100, 100, 50, 0)]
    [InlineData(100, 100, -50, 50)]
    [InlineData(double.NaN, 100, 50, 50)]
    [InlineData(100, 100, double.PositiveInfinity, 50)]
    public void An_off_display_empty_or_non_finite_target_produces_no_flight(double x, double y, double width, double height)
    {
        Assert.Null(PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(x, y, width, height), Primary));
    }

    // PF-012
    [Fact]
    public void A_partly_off_display_target_lands_on_its_visible_part()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(1900, 500, 100, 40), Primary);

        Assert.NotNull(plan);
        Assert.Equal(new RectD(1900, 500, 20, 40), plan.Target);
        Assert.Equal(new PointD(1910, 520), plan.Anchor);
    }

    // PF-013
    [Fact]
    public void A_cursor_on_another_monitor_enters_from_the_target_display_edge_and_never_leaves_it()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(2500, 700), new RectD(100, 100, 200, 60), Primary);

        Assert.NotNull(plan);
        Assert.Equal(new PointD(1919, 700), plan.Start);
        Assert.Equal(new PointD(1919, 700), plan.Frames[0].Position);
        Assert.Equal(new PointD(200, 130), plan.Anchor);
        AssertInside(plan, Primary);
    }

    // PF-014 (150% scaling, negative origin)
    [Fact]
    public void A_flight_on_a_negative_origin_150_percent_display_stays_inside_it_and_uses_dip_distance()
    {
        var start = new PointD(-1800, -150);

        var plan = PointerFlightPlanner.Plan(start, new RectD(-600, 600, 300, 90), Left150);

        Assert.NotNull(plan);
        Assert.Equal(new PointD(-450, 645), plan.Anchor);
        var distance = Distance(start, plan.Anchor);
        Assert.Equal(PointerFlightPlanner.DurationFor(distance, 1.5), plan.DurationMs, 9);
        Assert.Equal(280 + 0.25 * distance / 1.5, plan.DurationMs, 9);
        AssertInside(plan, Left150);
    }

    // PF-015 (second monitor at 150%)
    [Fact]
    public void A_flight_on_a_second_monitor_lands_on_the_target_in_physical_pixels()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(2000, 50), new RectD(3000, 900, 180, 60), Right150);

        Assert.NotNull(plan);
        Assert.Equal("right", plan.DisplayId);
        Assert.Equal(new PointD(3090, 930), plan.Anchor);
        Assert.Equal(new PointD(3090, 930), plan.Frames[^1].Position);
        AssertInside(plan, Right150);
    }

    // PF-016 wide targets: nearest point at least 240 DIP inside the edges.
    [Theory]
    [InlineData(1500, 100, 1460, 520)]
    [InlineData(200, 100, 340, 520)]
    [InlineData(900, 1000, 900, 520)]
    public void A_very_wide_target_lands_on_the_nearest_point_inset_240_dip(double startX, double startY, double anchorX, double anchorY)
    {
        var target = new RectD(100, 500, 1600, 40);

        Assert.Equal(new PointD(anchorX, anchorY), PointerFlightPlanner.Anchor(new PointD(startX, startY), target, 1.0));
        var plan = PointerFlightPlanner.Plan(new PointD(startX, startY), target, Primary);
        Assert.NotNull(plan);
        Assert.Equal(new PointD(anchorX, anchorY), plan.Anchor);
    }

    // PF-017
    [Fact]
    public void The_wide_target_inset_scales_with_dpi()
    {
        // inset = min(2000 / 2, 240 * 1.5) = 360 px.
        var anchor = PointerFlightPlanner.Anchor(new PointD(1950, 100), new RectD(2000, 300, 2000, 60), 1.5);

        Assert.Equal(new PointD(2360, 330), anchor);
    }

    // PF-018
    [Theory]
    [InlineData(1000, 0)]
    [InlineData(-500, 900)]
    public void Targets_up_to_480_dip_wide_always_land_on_their_centre(double startX, double startY)
    {
        Assert.Equal(new PointD(240, 20), PointerFlightPlanner.Anchor(new PointD(startX, startY), new RectD(0, 0, 480, 40), 1.0));
        Assert.Equal(new PointD(360, 30), PointerFlightPlanner.Anchor(new PointD(startX, startY), new RectD(0, 0, 720, 60), 1.5));
    }

    // PF-019
    [Fact]
    public void The_ring_stroke_follows_the_flight_for_draw_stroke_ms()
    {
        var plan = PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(900, 500, 100, 40), Primary);
        var custom = PointerFlightPlanner.Plan(new PointD(100, 100), new RectD(900, 500, 100, 40), Primary, ringStrokeMs: 300);

        Assert.NotNull(plan);
        Assert.NotNull(custom);
        Assert.Equal(620d, plan.RingStrokeMs);
        Assert.Equal(300d, custom.RingStrokeMs);
    }

    // PF-020
    [Fact]
    public void The_ring_stroke_default_matches_the_frozen_design_spec()
    {
        using var spec = JsonDocument.Parse(File.ReadAllText(FindSpec()));
        var drawStrokeMs = spec.RootElement.GetProperty("timings").GetProperty("drawStrokeMs").GetDouble();

        Assert.Equal(PointerFlightPlanner.DefaultRingStrokeMs, drawStrokeMs);
        Assert.Equal(drawStrokeMs, new PointerSequenceTimings().DrawStrokeMs);
    }

    // PF-021
    [Fact]
    public void Invalid_display_start_or_intervals_are_rejected()
    {
        var target = new RectD(100, 100, 50, 50);
        Assert.ThrowsAny<ArgumentException>(() => PointerFlightPlanner.Plan(new PointD(0, 0), target, Primary with { Scale = 0 }));
        Assert.ThrowsAny<ArgumentException>(() => PointerFlightPlanner.Plan(new PointD(0, 0), target, Primary with { PixelBounds = new RectD(0, 0, double.NaN, 1080) }));
        Assert.ThrowsAny<ArgumentException>(() => PointerFlightPlanner.Plan(new PointD(double.NaN, 0), target, Primary));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointerFlightPlanner.Plan(new PointD(0, 0), target, Primary, frameIntervalMs: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointerFlightPlanner.Plan(new PointD(0, 0), target, Primary, frameIntervalMs: double.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => PointerFlightPlanner.Plan(new PointD(0, 0), target, Primary, ringStrokeMs: -1));
    }

    private static void AssertInside(PointerFlightPlan plan, DisplayInfo display)
    {
        var bounds = display.PixelBounds;
        foreach (var frame in plan.Frames)
        {
            Assert.InRange(frame.Position.X, bounds.X, bounds.X + bounds.Width - 1);
            Assert.InRange(frame.Position.Y, bounds.Y, bounds.Y + bounds.Height - 1);
        }
    }

    private static double Distance(PointD a, PointD b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));

    private static string FindSpec()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            var candidate = Path.Combine(directory.FullName, "docs", "design", "boo-notch-spec.json");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        throw new FileNotFoundException("docs/design/boo-notch-spec.json was not found above the test output directory.");
    }
}
