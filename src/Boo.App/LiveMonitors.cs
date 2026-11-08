using System.Runtime.InteropServices;
using Boo.Core;
using Forms = System.Windows.Forms;

namespace Boo.App;

/// <summary>
/// Thin Win32 glue: the attached monitors with each monitor's OWN effective
/// DPI (GetDpiForMonitor), never the DPI of a window that has not moved yet.
/// All placement maths lives in <see cref="MonitorLayout"/>.
/// </summary>
internal static class LiveMonitors
{
    private const uint MonitorDefaultToNearest = 2;

    public static IReadOnlyList<MonitorSnapshot> Snapshot()
    {
        var list = new List<MonitorSnapshot>();
        foreach (var screen in Forms.Screen.AllScreens)
        {
            list.Add(ToSnapshot(screen));
        }
        return list;
    }

    public static MonitorSnapshot ToSnapshot(Forms.Screen screen)
    {
        var work = screen.WorkingArea;
        return new MonitorSnapshot(
            ToDisplayInfo(screen),
            new RectD(work.Left, work.Top, work.Width, work.Height),
            screen.Primary);
    }

    public static DisplayInfo ToDisplayInfo(Forms.Screen screen)
    {
        var bounds = screen.Bounds;
        return new DisplayInfo(
            screen.DeviceName,
            screen.Primary ? "Primary display" : screen.DeviceName,
            new RectD(bounds.Left, bounds.Top, bounds.Width, bounds.Height),
            ScaleFor(screen));
    }

    public static double ScaleFor(Forms.Screen screen)
    {
        try
        {
            var bounds = screen.Bounds;
            var centre = new NativePoint
            {
                X = bounds.Left + bounds.Width / 2,
                Y = bounds.Top + bounds.Height / 2
            };
            var monitor = MonitorFromPoint(centre, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return 1d;
            }

            uint dpiX = 0, dpiY = 0;
            return GetDpiForMonitor(monitor, 0 /* MDT_EFFECTIVE_DPI */, out dpiX, out dpiY) == 0
                ? MonitorLayout.ScaleFromDpi(dpiX)
                : 1d;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            return 1d;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);
}
