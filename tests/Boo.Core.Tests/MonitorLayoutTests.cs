using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

/// <summary>
/// Multi-monitor and mixed-DPI placement maths used by the notch, the panel
/// and the annotation overlay.  All rectangles are physical pixels unless a
/// name says DIP.
/// </summary>
public sealed class MonitorLayoutTests
{
    // Primary 1920x1080 @100%, secondary 2560x1440 @150% to the LEFT and
    // slightly ABOVE the primary (negative origin on both axes).
    private static readonly MonitorSnapshot Primary = Monitor(@"\\.\DISPLAY1", 0, 0, 1920, 1080, 1.0, primary: true,
        work: new RectD(0, 0, 1920, 1040));
    private static readonly MonitorSnapshot LeftHigh = Monitor(@"\\.\DISPLAY2", -2560, -360, 2560, 1440, 1.5,
        work: new RectD(-2560, -360, 2560, 1380));
    private static readonly MonitorSnapshot Above = Monitor(@"\\.\DISPLAY3", 0, -1200, 1920, 1200, 1.25,
        work: new RectD(0, -1200, 1920, 1200));
    private static readonly IReadOnlyList<MonitorSnapshot> Layout = new[] { LeftHigh, Primary, Above };

    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(120u, 1.25)]
    [InlineData(144u, 1.5)]
    [InlineData(168u, 1.75)]
    [InlineData(192u, 2.0)]
    [InlineData(0u, 1.0)] // a failed DPI query must not produce a zero scale
    public void Dpi_converts_to_scale(uint dpi, double expected)
    {
        Assert.Equal(expected, MonitorLayout.ScaleFromDpi(dpi), 6);
    }

    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void Invalid_scales_fall_back_to_one(double scale)
    {
        Assert.Equal(1d, MonitorLayout.SanitizeScale(scale));
    }

    [Theory]
    [InlineData(@"\\.\DISPLAY2", @"\\.\DISPLAY2")]
    [InlineData(@"\\.\display2", @"\\.\DISPLAY2")] // Windows device names are case-insensitive
    [InlineData(@"\\.\DISPLAY9", @"\\.\DISPLAY1")] // unplugged -> primary
    [InlineData(null, @"\\.\DISPLAY1")]
    [InlineData("", @"\\.\DISPLAY1")]
    public void Resolve_prefers_the_requested_display_then_the_primary(string? requested, string expected)
    {
        Assert.Equal(expected, MonitorLayout.Resolve(Layout, requested)!.Display.Id);
    }

    [Fact]
    public void Resolve_uses_the_first_display_when_none_is_marked_primary()
    {
        var monitors = new[] { LeftHigh with { IsPrimary = false }, Above };
        Assert.Equal(LeftHigh.Display.Id, MonitorLayout.Resolve(monitors, @"\\.\GONE")!.Display.Id);
    }

    [Fact]
    public void Resolve_returns_null_when_no_display_is_attached()
    {
        Assert.Null(MonitorLayout.Resolve(Array.Empty<MonitorSnapshot>(), @"\\.\DISPLAY1"));
        Assert.Null(MonitorLayout.Resolve(null!, null));
    }

    [Theory]
    [InlineData(10, 10, @"\\.\DISPLAY1")]
    [InlineData(-1, 0, @"\\.\DISPLAY2")]       // first pixel left of primary
    [InlineData(-2560, -360, @"\\.\DISPLAY2")] // top-left corner of the negative monitor
    [InlineData(0, -1, @"\\.\DISPLAY3")]       // first pixel above primary
    [InlineData(1919, 1079, @"\\.\DISPLAY1")]
    [InlineData(1920, 500, @"\\.\DISPLAY1")]   // right edge is exclusive -> nearest is primary
    [InlineData(-5000, 0, @"\\.\DISPLAY2")]    // off-desktop point snaps to nearest monitor
    [InlineData(500, -5000, @"\\.\DISPLAY3")]
    [InlineData(-100, 1200, @"\\.\DISPLAY2")]  // gap below the left monitor
    public void Monitor_at_point_uses_half_open_bounds_and_falls_back_to_nearest(double x, double y, string expected)
    {
        Assert.Equal(expected, MonitorLayout.MonitorAt(Layout, x, y)!.Display.Id);
    }

    [Theory]
    // X, Y, W, H -> snapped X, Y, W, H.  Edges round half up (toward +infinity) so
    // a centred window never drifts by one pixel between left and right halves.
    [InlineData(822.5, 0, 275, 10, 823, 0, 275, 10)]
    [InlineData(-1087.5, -360, 385, 14, -1087, -360, 385, 14)]
    [InlineData(10.4, 20.6, 100.2, 50.2, 10, 21, 101, 50)]
    public void Snap_to_pixels_rounds_edges_not_sizes(double x, double y, double w, double h,
        double ex, double ey, double ew, double eh)
    {
        Assert.Equal(new RectD(ex, ey, ew, eh), MonitorLayout.SnapToPixels(new RectD(x, y, w, h)));
    }

    [Theory]
    // Mark at physical (-2260,-60) 300x150 on the 150% left monitor -> local DIP (200,200) 200x100.
    [InlineData(-2260, -60, 300, 150, -2560, -360, 1.5, 200, 200, 200, 100)]
    // Same physical rect read with the PRIMARY's 100% scale would land at 300,300: this is the bug class.
    [InlineData(-2260, -60, 300, 150, -2560, -360, 1.0, 300, 300, 300, 150)]
    // 125% monitor above primary.
    [InlineData(250, -950, 125, 125, 0, -1200, 1.25, 200, 200, 100, 100)]
    // 175% monitor
    [InlineData(1750, 350, 350, 175, 0, 0, 1.75, 1000, 200, 200, 100)]
    public void Physical_rect_converts_to_monitor_local_dips(
        double x, double y, double w, double h, double ox, double oy, double scale,
        double ex, double ey, double ew, double eh)
    {
        var actual = MonitorLayout.ToLocalDip(new RectD(x, y, w, h), new RectD(ox, oy, 4000, 4000), scale);
        Assert.Equal(ex, actual.X, 6);
        Assert.Equal(ey, actual.Y, 6);
        Assert.Equal(ew, actual.Width, 6);
        Assert.Equal(eh, actual.Height, 6);
    }

    [Fact]
    public void Notch_is_centred_on_a_negative_origin_monitor_at_its_own_scale()
    {
        var actual = MonitorLayout.NotchBounds(LeftHigh, widthDip: 220, heightDip: 8, offsetDip: 0);

        // 220 DIP * 1.5 = 330 px, centred in [-2560, 0) -> X = -1445.
        Assert.Equal(new RectD(-1445, -360, 330, 12), actual);
    }

    [Theory]
    // width 1920 @125%: 275 px wide, (1920-275)/2 = 822.5 -> snapped 823.
    [InlineData(1.25, 823, 275, 10)]
    // width 1920 @175%: 385 px, (1920-385)/2 = 767.5 -> 768. 8 DIP -> 14 px.
    [InlineData(1.75, 768, 385, 14)]
    [InlineData(1.0, 850, 220, 8)]
    public void Notch_bounds_are_whole_pixels_at_fractional_scales(double scale, double ex, double ew, double eh)
    {
        var monitor = Monitor("m", 0, 0, 1920, 1080, scale, primary: true);
        var actual = MonitorLayout.NotchBounds(monitor, 220, 8, 0);
        Assert.Equal(new RectD(ex, 0, ew, eh), actual);
        Assert.Equal(actual.X, Math.Round(actual.X));
        Assert.Equal(actual.Width, Math.Round(actual.Width));
    }

    [Fact]
    public void Notch_sits_below_a_taskbar_docked_at_the_top()
    {
        var topTaskbar = Monitor("top", 0, 0, 1920, 1080, 1.0, primary: true, work: new RectD(0, 48, 1920, 1032));

        var actual = MonitorLayout.NotchBounds(topTaskbar, 220, 8, offsetDip: 4);

        Assert.Equal(new RectD(850, 52, 220, 8), actual);
    }

    [Fact]
    public void Notch_stays_on_the_top_edge_with_an_auto_hide_or_bottom_taskbar()
    {
        // Auto-hide leaves the work area equal to the monitor bounds.
        var autoHide = Monitor("auto", 0, 0, 1920, 1080, 1.0, primary: true);
        Assert.Equal(0, MonitorLayout.NotchBounds(autoHide, 220, 8, 0).Y);
        Assert.Equal(0, MonitorLayout.NotchBounds(Primary, 220, 8, 0).Y);
    }

    [Fact]
    public void Notch_ignores_a_bogus_work_area_outside_the_monitor()
    {
        var bogus = Monitor("bogus", 0, 0, 1920, 1080, 1.0, primary: true, work: new RectD(5000, 5000, 0, 0));
        Assert.Equal(new RectD(850, 0, 220, 8), MonitorLayout.NotchBounds(bogus, 220, 8, 0));
    }

    [Fact]
    public void Notch_on_a_taskbar_top_negative_origin_monitor_uses_its_work_area_top()
    {
        var m = Monitor("neg", -1920, -1080, 1920, 1080, 1.25, work: new RectD(-1920, -1020, 1920, 1020));
        var actual = MonitorLayout.NotchBounds(m, 220, 8, 0);
        Assert.Equal(-1020, actual.Y);
        Assert.Equal(-1920 + 823, actual.X);
    }

    [Fact]
    public void Panel_is_centred_in_the_work_area_of_a_negative_origin_monitor()
    {
        var placement = MonitorLayout.CenterInWorkArea(LeftHigh.WorkArea, widthDip: 800, heightDip: 600, scale: 1.5);

        Assert.Equal(800, placement.WidthDip);
        Assert.Equal(600, placement.HeightDip);
        // 1200x900 px centred in x [-2560,0), y [-360, 1020)
        Assert.Equal(new RectD(-1880, -120, 1200, 900), placement.PhysicalBounds);
    }

    [Fact]
    public void Panel_larger_than_the_work_area_is_shrunk_and_pinned_inside_it()
    {
        // 1366x728 work area @125% = 1092.8 x 582.4 DIP.
        var work = new RectD(0, 40, 1366, 728);
        var placement = MonitorLayout.CenterInWorkArea(work, widthDip: 1200, heightDip: 900, scale: 1.25);

        Assert.Equal(1366 / 1.25, placement.WidthDip, 6);
        Assert.Equal(728 / 1.25, placement.HeightDip, 6);
        Assert.Equal(work, placement.PhysicalBounds);
    }

    [Fact]
    public void Panel_placement_always_lies_inside_the_work_area_at_odd_scales()
    {
        foreach (var scale in new[] { 1.0, 1.25, 1.5, 1.75, 2.0, 2.25 })
        {
            var work = new RectD(-1281, -17, 1279, 1001);
            var p = MonitorLayout.CenterInWorkArea(work, 777.7, 555.5, scale).PhysicalBounds;
            Assert.True(p.X >= work.X, $"left at {scale}");
            Assert.True(p.Y >= work.Y, $"top at {scale}");
            Assert.True(p.X + p.Width <= work.X + work.Width, $"right at {scale}");
            Assert.True(p.Y + p.Height <= work.Y + work.Height, $"bottom at {scale}");
            Assert.Equal(p.X, Math.Round(p.X));
            Assert.Equal(p.Width, Math.Round(p.Width));
        }
    }

    [Fact]
    public void Panel_placement_with_an_invalid_scale_uses_one()
    {
        var placement = MonitorLayout.CenterInWorkArea(new RectD(0, 0, 1000, 800), 400, 300, double.NaN);
        Assert.Equal(new RectD(300, 250, 400, 300), placement.PhysicalBounds);
    }

    [Fact]
    public void Overlay_surface_is_the_captured_display_when_it_is_still_attached_unchanged()
    {
        var captured = LeftHigh.Display;
        var surface = MonitorLayout.OverlaySurface(captured, Layout);
        Assert.NotNull(surface);
        Assert.Equal(captured.PixelBounds, surface!.Display.PixelBounds);
    }

    [Fact]
    public void Overlay_surface_is_refused_when_the_captured_display_was_unplugged()
    {
        var captured = new DisplayInfo(@"\\.\DISPLAY7", "gone", new RectD(3840, 0, 1920, 1080), 1);
        Assert.Null(MonitorLayout.OverlaySurface(captured, Layout));
    }

    [Fact]
    public void Overlay_surface_is_refused_when_the_display_was_rearranged_since_capture()
    {
        // Marks were mapped against the old origin; drawing them now would point at the wrong pixels.
        var captured = LeftHigh.Display with { PixelBounds = new RectD(1920, 0, 2560, 1440) };
        Assert.Null(MonitorLayout.OverlaySurface(captured, Layout));
    }

    [Fact]
    public void Overlay_surface_uses_the_live_monitor_scale_not_the_captured_one()
    {
        // Captured records were historically built with Scale = 1; the live 150% must win.
        var captured = LeftHigh.Display with { Scale = 1 };
        Assert.Equal(1.5, MonitorLayout.OverlaySurface(captured, Layout)!.Display.Scale);
    }

    [Fact]
    public void Live_display_replaces_a_stale_cached_record_for_the_same_id()
    {
        var stale = new DisplayInfo(@"\\.\DISPLAY2", "Display 2", new RectD(1920, 0, 2560, 1440), 1);
        var live = MonitorLayout.Resolve(Layout, stale.Id)!;
        Assert.Equal(new RectD(-2560, -360, 2560, 1440), live.Display.PixelBounds);
        Assert.Equal(1.5, live.Display.Scale);
    }

    [Theory]
    // bounds top, work top -> anchor top
    [InlineData(0, 0, 0)]          // bottom taskbar / auto-hide
    [InlineData(0, 40, 40)]        // top taskbar 40 px
    [InlineData(-360, -300, -300)] // negative-origin monitor with a top taskbar
    [InlineData(0, 2000, 0)]       // work area below the monitor is bogus -> ignored
    public void Notch_anchor_moves_the_top_edge_only_for_a_top_docked_taskbar(double boundsTop, double workTop, double expectedTop)
    {
        var m = Monitor("m", -100, boundsTop, 1920, 1080, 1.0, work: new RectD(-100, workTop, 1920, 1000));
        var anchor = MonitorLayout.NotchAnchor(m);
        Assert.Equal(expectedTop, anchor.Y);
        Assert.Equal(-100, anchor.X);
        Assert.Equal(1920, anchor.Width);
        Assert.Equal(boundsTop + 1080, anchor.Y + anchor.Height);
    }

    private static MonitorSnapshot Monitor(string id, double x, double y, double w, double h, double scale,
        bool primary = false, RectD? work = null)
    {
        var bounds = new RectD(x, y, w, h);
        return new MonitorSnapshot(new DisplayInfo(id, id, bounds, scale), work ?? bounds, primary);
    }
}
