namespace Boo.Core;

/// <summary>
/// A live monitor as Windows reports it right now: physical-pixel bounds and
/// work area plus the monitor's own effective DPI scale.
/// </summary>
public sealed record MonitorSnapshot(DisplayInfo Display, RectD WorkArea, bool IsPrimary);

/// <summary>A window placement in physical pixels plus the DIP size WPF should use.</summary>
public sealed record WindowPlacement(RectD PhysicalBounds, double WidthDip, double HeightDip);

/// <summary>
/// Pure multi-monitor / per-monitor-DPI maths.  Every physical-pixel to DIP
/// conversion uses the scale of the monitor the rectangle lives on, never the
/// DPI of whatever monitor a window happened to be on before it moved.
/// </summary>
public static class MonitorLayout
{
    public const double BaseDpi = 96d;

    public static double ScaleFromDpi(uint dpi) => dpi == 0 ? 1d : dpi / BaseDpi;

    public static double SanitizeScale(double scale) => double.IsFinite(scale) && scale > 0 ? scale : 1d;

    /// <summary>The live monitor with this device id (case-insensitive), or null.</summary>
    public static MonitorSnapshot? Find(IReadOnlyList<MonitorSnapshot>? monitors, string? id)
    {
        if (monitors is null || string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        return monitors.FirstOrDefault(m => m is not null &&
            string.Equals(m.Display.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The requested monitor if it is still attached, otherwise the primary,
    /// otherwise the first attached monitor; null only when none is attached.
    /// </summary>
    public static MonitorSnapshot? Resolve(IReadOnlyList<MonitorSnapshot>? monitors, string? preferredId)
    {
        if (monitors is null || monitors.Count == 0)
        {
            return null;
        }

        return Find(monitors, preferredId)
            ?? monitors.FirstOrDefault(m => m is not null && m.IsPrimary)
            ?? monitors.FirstOrDefault(m => m is not null);
    }

    /// <summary>
    /// The monitor containing the physical point (half-open bounds, like
    /// MonitorFromPoint), else the nearest one.
    /// </summary>
    public static MonitorSnapshot? MonitorAt(IReadOnlyList<MonitorSnapshot>? monitors, double x, double y)
    {
        if (monitors is null || monitors.Count == 0 || !double.IsFinite(x) || !double.IsFinite(y))
        {
            return null;
        }

        MonitorSnapshot? nearest = null;
        var best = double.PositiveInfinity;
        foreach (var monitor in monitors)
        {
            if (monitor is null)
            {
                continue;
            }

            var b = monitor.Display.PixelBounds;
            if (x >= b.X && x < b.X + b.Width && y >= b.Y && y < b.Y + b.Height)
            {
                return monitor;
            }

            var dx = Math.Max(Math.Max(b.X - x, 0), x - (b.X + b.Width));
            var dy = Math.Max(Math.Max(b.Y - y, 0), y - (b.Y + b.Height));
            var distance = dx * dx + dy * dy;
            if (distance < best)
            {
                best = distance;
                nearest = monitor;
            }
        }

        return nearest;
    }

    /// <summary>
    /// Rounds the edges (not the size) to whole pixels with round-half-up, so
    /// the result is translation-invariant across negative origins.
    /// </summary>
    public static RectD SnapToPixels(RectD rect)
    {
        var left = RoundHalfUp(rect.X);
        var top = RoundHalfUp(rect.Y);
        var right = RoundHalfUp(rect.X + rect.Width);
        var bottom = RoundHalfUp(rect.Y + rect.Height);
        return new RectD(left, top, right - left, bottom - top);
    }

    /// <summary>Physical rect to DIPs relative to the given monitor's origin, at that monitor's scale.</summary>
    public static RectD ToLocalDip(RectD physical, RectD monitorBounds, double scale)
    {
        var s = SanitizeScale(scale);
        return new RectD(
            (physical.X - monitorBounds.X) / s,
            (physical.Y - monitorBounds.Y) / s,
            physical.Width / s,
            physical.Height / s);
    }

    /// <summary>
    /// Notch rectangle centred on the monitor, at the monitor's own scale,
    /// below a top-docked taskbar, snapped to whole pixels.
    /// </summary>
    public static RectD NotchBounds(MonitorSnapshot monitor, double widthDip, double heightDip, double offsetDip)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var anchored = monitor.Display with
        {
            PixelBounds = NotchAnchor(monitor),
            Scale = SanitizeScale(monitor.Display.Scale)
        };
        return SnapToPixels(DisplayGeometry.NotchBounds(anchored, widthDip, heightDip, offsetDip));
    }

    /// <summary>
    /// The rectangle a top-centred notch is laid out in: the monitor's full
    /// width (so it stays centred on the screen) with its top edge moved below
    /// a taskbar docked at the top.  An auto-hide or bottom/side taskbar leaves
    /// the top edge at the monitor top; a bogus work area is ignored.
    /// </summary>
    public static RectD NotchAnchor(MonitorSnapshot monitor)
    {
        ArgumentNullException.ThrowIfNull(monitor);
        var bounds = monitor.Display.PixelBounds;
        var top = bounds.Y;
        var work = monitor.WorkArea;
        if (IsUsable(work) && work.Y > bounds.Y && work.Y < bounds.Y + bounds.Height &&
            work.X < bounds.X + bounds.Width && work.X + work.Width > bounds.X)
        {
            top = work.Y;
        }

        return new RectD(bounds.X, top, bounds.Width, bounds.Height - (top - bounds.Y));
    }

    /// <summary>
    /// Centres a window in a work area at the work area's monitor scale,
    /// shrinking it so it never spills outside, snapped to whole pixels.
    /// </summary>
    public static WindowPlacement CenterInWorkArea(RectD workArea, double widthDip, double heightDip, double scale)
    {
        if (!IsUsable(workArea))
        {
            throw new ArgumentException("The work area must be finite and have positive dimensions.", nameof(workArea));
        }

        var s = SanitizeScale(scale);
        var maxWidthDip = workArea.Width / s;
        var maxHeightDip = workArea.Height / s;
        var w = double.IsFinite(widthDip) && widthDip > 0 ? Math.Min(widthDip, maxWidthDip) : maxWidthDip;
        var h = double.IsFinite(heightDip) && heightDip > 0 ? Math.Min(heightDip, maxHeightDip) : maxHeightDip;
        var widthPx = w * s;
        var heightPx = h * s;
        var raw = new RectD(
            workArea.X + (workArea.Width - widthPx) / 2,
            workArea.Y + (workArea.Height - heightPx) / 2,
            widthPx,
            heightPx);
        var snapped = SnapToPixels(raw);

        // Rounding may push one edge a pixel outside; pull it back inside.
        var left = Math.Max(snapped.X, workArea.X);
        var top = Math.Max(snapped.Y, workArea.Y);
        var right = Math.Min(snapped.X + snapped.Width, workArea.X + workArea.Width);
        var bottom = Math.Min(snapped.Y + snapped.Height, workArea.Y + workArea.Height);
        return new WindowPlacement(new RectD(left, top, right - left, bottom - top), w, h);
    }

    /// <summary>
    /// The live monitor an annotation overlay may cover.  Marks were mapped
    /// against <paramref name="captured"/>'s pixel bounds, so the overlay is
    /// refused if that display is gone or was moved/resized since capture.
    /// The returned display carries the live (correct) DPI scale.
    /// </summary>
    public static MonitorSnapshot? OverlaySurface(DisplayInfo captured, IReadOnlyList<MonitorSnapshot>? monitors)
    {
        ArgumentNullException.ThrowIfNull(captured);
        var live = Find(monitors, captured.Id);
        if (live is null || live.Display.PixelBounds != captured.PixelBounds || !IsUsable(live.Display.PixelBounds))
        {
            return null;
        }

        return live with { Display = live.Display with { Scale = SanitizeScale(live.Display.Scale) } };
    }

    private static double RoundHalfUp(double value) => Math.Floor(value + 0.5);

    private static bool IsUsable(RectD rect) =>
        double.IsFinite(rect.X) && double.IsFinite(rect.Y) &&
        double.IsFinite(rect.Width) && double.IsFinite(rect.Height) &&
        rect.Width > 0 && rect.Height > 0;
}
