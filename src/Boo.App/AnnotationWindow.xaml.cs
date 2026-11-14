using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Controls;
using Boo.Core;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace Boo.App;

/// <summary>
/// A separate, click-through HWND for verified screen marks.  It never owns
/// focus and is discarded whenever the observation or display changes.
/// Boo's blue pointer first flies from the cursor to the first mark
/// (<see cref="PointerFlightPlanner"/>), then the marks are stroked in
/// (<see cref="PointerAnnotationSequencer"/>), held, and cleared. Scroll,
/// resize/move of the target, a foreground change, or a display change
/// clears everything immediately.
/// </summary>
public partial class AnnotationWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;
    private const int WmMouseActivate = 0x21;
    private const int MaNoActivate = 3;
    private const int WhMouseLl = 14;
    private const int WmMouseWheel = 0x020A;
    private const int WmMouseHWheel = 0x020E;
    private const double MarkStrokeThickness = 3;
    private static readonly MediaColor MarkColor = MediaColor.FromRgb(243, 181, 155);
    private static readonly MediaColor PointerColor = MediaColor.FromRgb(0x42, 0x54, 0xD6);

    private readonly ScreenContext _context;
    private DisplayInfo _display;
    private readonly IReadOnlyList<PhysicalMark> _marks;
    private readonly PointerAnnotationSequencer _sequencer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly List<StrokedMark> _rendered = new();
    private readonly List<UIElement> _labels = new();
    private readonly LowLevelMouseProc _mouseProc;
    private System.Windows.Shapes.Path? _pointer;
    private IntPtr _handle;
    private IntPtr _mouseHook;
    private IntPtr _foregroundAtBegin;
    private IntPtr _watchedWindow;
    private NativeRect _watchedRectAtBegin;
    private bool _rendering;
    private volatile bool _scrolled;
    private volatile bool _displayChanged;

    public bool CaptureExclusionAvailable { get; private set; }

    /// <summary>False when the captured display is unplugged or was rearranged; the caller must not Show().</summary>
    public bool DisplayAvailable { get; private set; }

    /// <summary>The AE1 path: validated marks in captured-frame pixels; the pointer flies to the first one.</summary>
    public AnnotationWindow(ScreenContext context, IReadOnlyList<AnnotationMark> marks)
        : this(context, MapMarks(context, marks), new PointerSequenceTimings(HoldMs: 10_000))
    {
    }

    /// <summary>
    /// Windows "Animation effects" off or Boo's Reduced motion: the pointer does not fly and the ring is not
    /// stroked over time; the validated marks simply appear at the target (same path as "no on-display anchor").
    /// Set before the window is shown.
    /// </summary>
    public bool ReducedMotion { get; set; }

    private AnnotationWindow(ScreenContext context, IReadOnlyList<PhysicalMark> marks, PointerSequenceTimings timings)
    {
        _context = context;
        _display = context.Display;
        _marks = marks;
        _sequencer = new PointerAnnotationSequencer(timings);
        _mouseProc = OnLowLevelMouse;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => StartSequence();
        Closed += (_, _) => StopSequence(PointerClearReason.Stop);
    }

    /// <summary>
    /// The focus nudge path: a ring around an OCR-matched box that is already in
    /// physical virtual-screen pixels on <paramref name="context"/>'s display.
    /// </summary>
    public static AnnotationWindow ForPhysicalTarget(ScreenContext context, RectD physicalTarget)
    {
        ArgumentNullException.ThrowIfNull(context);
        return new AnnotationWindow(
            context,
            new[] { new PhysicalMark("ring", Inflate(physicalTarget, 6 * context.Display.Scale), string.Empty, null) },
            new PointerSequenceTimings());
    }

    /// <summary>Clears the pointer and marks immediately (Stop, scroll, resize, foreground, display).</summary>
    public void ClearPointer(PointerClearReason reason) => StopSequence(reason);

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(_handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExstyle, new IntPtr(style | WsExTransparent | WsExNoActivate | WsExToolWindow));
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProc);
        CaptureExclusionAvailable = SetWindowDisplayAffinity(_handle, App.CaptureExclusion.DisplayAffinity);
        PositionOnDisplay();
    }

    private void PositionOnDisplay()
    {
        // Marks were mapped against the captured display's pixel bounds: refuse the overlay if
        // that display is gone or was rearranged, rather than drawing on the wrong pixels.
        var surface = MonitorLayout.OverlaySurface(_display, LiveMonitors.Snapshot());
        if (surface is null)
        {
            DisplayAvailable = false;
            return;
        }

        // Use the live per-monitor scale (pointer inset/duration, ring inflate) rather than a
        // captured record that may carry a placeholder scale.
        _display = _display with { Scale = surface.Display.Scale };
        DisplayAvailable = true;
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        // Size the HWND in physical pixels. Converting via GetDpiForWindow here used the DPI
        // of the monitor the new window was created on (usually the primary), so on a mixed-DPI
        // setup the overlay came out 1.5x too large or too small on the target monitor.
        var bounds = MonitorLayout.SnapToPixels(_display.PixelBounds);
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var dpiBefore = GetDpiForWindow(_handle);
            SetWindowPos(_handle, IntPtr.Zero, (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height,
                SwpNoActivate | SwpNoZOrder);
            if (GetDpiForWindow(_handle) == dpiBefore)
            {
                break;
            }
            // Crossing to a monitor with another DPI makes WPF apply the WM_DPICHANGED suggested
            // rect; place again now that the window lives on the target monitor.
        }

        // Keep WPF's DIP properties in sync so Show() does not re-apply a stale size. The window
        // is now on the target monitor, so its DPI is that monitor's DPI.
        var windowScale = MonitorLayout.ScaleFromDpi(GetDpiForWindow(_handle));
        Left = bounds.X / windowScale;
        Top = bounds.Y / windowScale;
        Width = bounds.Width / windowScale;
        Height = bounds.Height / windowScale;
    }

    private void StartSequence()
    {
        RenderMarks();
        if (_marks.Count == 0)
        {
            return;
        }

        if (ReducedMotion)
        {
            ApplyMarkProgress(1);
            return;
        }

        PointerFlightPlan? plan = null;
        try
        {
            // PerMonitorV2 (app.manifest): GetCursorPos is already physical virtual-screen
            // pixels, the same space as DisplayInfo.PixelBounds and the mapped marks.
            if (GetCursorPos(out var cursor))
            {
                plan = PointerFlightPlanner.Plan(new PointD(cursor.X, cursor.Y), _marks[0].Bounds, _display);
            }
        }
        catch (ArgumentException)
        {
            plan = null;
        }

        if (plan is null || !_sequencer.Begin(Math.Max(1, _context.Generation), plan, _clock.Elapsed.TotalMilliseconds))
        {
            // No on-display anchor: never guess a pointer position; validated marks show as before.
            ApplyMarkProgress(1);
            return;
        }

        _pointer = CreatePointer();
        MarksCanvas.Children.Add(_pointer);
        ApplyMarkProgress(0);
        WatchForInvalidation();
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
        OnRendering(this, EventArgs.Empty);
    }

    private void OnRendering(object? sender, EventArgs e)
    {
        if (!_rendering)
        {
            return;
        }

        if (DetectInvalidation() is { } invalidated)
        {
            StopSequence(invalidated);
            return;
        }

        var frame = _sequencer.Tick(_clock.Elapsed.TotalMilliseconds);
        if (frame is null || frame.Phase is PointerPhase.Clear or PointerPhase.Idle)
        {
            StopSequence(PointerClearReason.Completed);
            return;
        }

        if (_pointer is not null)
        {
            var scale = WindowScale();
            Canvas.SetLeft(_pointer, (frame.Pointer.X - _display.PixelBounds.X) / scale);
            Canvas.SetTop(_pointer, (frame.Pointer.Y - _display.PixelBounds.Y) / scale);
            _pointer.Visibility = Visibility.Visible;
        }

        ApplyMarkProgress(frame.Phase is PointerPhase.DrawRing or PointerPhase.Hold ? frame.RingProgress : 0);
    }

    private void StopSequence(PointerClearReason reason)
    {
        var wasRunning = _rendering;
        _sequencer.Clear(reason);
        if (_rendering)
        {
            CompositionTarget.Rendering -= OnRendering;
            _rendering = false;
        }

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        if (wasRunning)
        {
            MarksCanvas.Children.Clear();
            _rendered.Clear();
            _labels.Clear();
            _pointer = null;
        }
    }

    private void WatchForInvalidation()
    {
        _foregroundAtBegin = GetForegroundWindow();
        _watchedWindow = _context.Target.Handle != 0 ? new IntPtr(_context.Target.Handle) : _foregroundAtBegin;
        if (_watchedWindow != IntPtr.Zero && !GetWindowRect(_watchedWindow, out _watchedRectAtBegin))
        {
            _watchedWindow = IntPtr.Zero;
        }

        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
        _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, GetModuleHandle(null), 0);
    }

    private PointerClearReason? DetectInvalidation()
    {
        if (_displayChanged)
        {
            return PointerClearReason.DisplayChanged;
        }

        if (_scrolled)
        {
            return PointerClearReason.Scroll;
        }

        var foreground = GetForegroundWindow();
        if (foreground != IntPtr.Zero && foreground != _foregroundAtBegin && !IsBooWindow(foreground))
        {
            return PointerClearReason.ForegroundChanged;
        }

        if (_watchedWindow != IntPtr.Zero
            && (!GetWindowRect(_watchedWindow, out var rect) || rect != _watchedRectAtBegin))
        {
            return PointerClearReason.Resize;
        }

        return null;
    }

    private static bool IsBooWindow(IntPtr hwnd)
    {
        GetWindowThreadProcessId(hwnd, out var processId);
        return processId == (uint)Environment.ProcessId;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => _displayChanged = true;

    private IntPtr OnLowLevelMouse(int code, IntPtr wParam, IntPtr lParam)
    {
        // Keep the hook body minimal: flag it and let the render tick clear.
        var message = wParam.ToInt64();
        if (code >= 0 && (message == WmMouseWheel || message == WmMouseHWheel))
        {
            _scrolled = true;
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private void RenderMarks()
    {
        MarksCanvas.Children.Clear();
        _rendered.Clear();
        _labels.Clear();
        var scale = WindowScale();
        var stroke = new SolidColorBrush(MarkColor);
        foreach (var mark in _marks)
        {
            var x = (mark.Bounds.X - _display.PixelBounds.X) / scale;
            var y = (mark.Bounds.Y - _display.PixelBounds.Y) / scale;
            var width = mark.Bounds.Width / scale;
            var height = mark.Bounds.Height / scale;
            if (mark.Kind.Equals("ring", StringComparison.OrdinalIgnoreCase))
            {
                var ellipse = new Ellipse
                {
                    Width = width,
                    Height = height,
                    Stroke = stroke,
                    StrokeThickness = MarkStrokeThickness,
                    StrokeDashCap = PenLineCap.Round,
                    Fill = MediaBrushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ellipse, x);
                Canvas.SetTop(ellipse, y);
                MarksCanvas.Children.Add(ellipse);
                _rendered.Add(new StrokedMark(ellipse, EllipsePerimeter(width / 2, height / 2)));
            }
            else if (mark.Kind.Equals("arrow", StringComparison.OrdinalIgnoreCase))
            {
                var tail = mark.Tail ?? new PointD(mark.Bounds.X, mark.Bounds.Y);
                var line = new Line
                {
                    X1 = (tail.X - _display.PixelBounds.X) / scale,
                    Y1 = (tail.Y - _display.PixelBounds.Y) / scale,
                    X2 = x + width / 2,
                    Y2 = y + height / 2,
                    Stroke = stroke,
                    StrokeThickness = MarkStrokeThickness,
                    StrokeDashCap = PenLineCap.Round,
                    IsHitTestVisible = false
                };
                MarksCanvas.Children.Add(line);
                _rendered.Add(new StrokedMark(line, Math.Sqrt(Math.Pow(line.X2 - line.X1, 2) + Math.Pow(line.Y2 - line.Y1, 2))));
            }

            if (!string.IsNullOrWhiteSpace(mark.Label))
            {
                var label = new Border
                {
                    Background = new SolidColorBrush(MediaColor.FromArgb(230, 23, 23, 25)),
                    BorderBrush = stroke,
                    BorderThickness = new Thickness(1),
                    CornerRadius = new CornerRadius(7),
                    Padding = new Thickness(7, 3, 7, 3),
                    Child = new TextBlock
                    {
                        Text = mark.Label,
                        Foreground = MediaBrushes.White,
                        FontFamily = new System.Windows.Media.FontFamily("Segoe UI"),
                        FontSize = 12
                    },
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(label, x);
                Canvas.SetTop(label, Math.Max(0, y - 30));
                MarksCanvas.Children.Add(label);
                _labels.Add(label);
            }
        }
    }

    /// <summary>Strokes every mark to <paramref name="progress"/> (0..1) of its length; labels fade in with it.</summary>
    private void ApplyMarkProgress(double progress)
    {
        progress = Math.Clamp(progress, 0, 1);
        foreach (var mark in _rendered)
        {
            if (progress <= 0)
            {
                mark.Shape.Visibility = Visibility.Hidden;
                continue;
            }

            mark.Shape.Visibility = Visibility.Visible;
            if (progress >= 1 || mark.Length <= 0)
            {
                mark.Shape.StrokeDashArray = null;
                continue;
            }

            // Dash units are multiples of the stroke thickness.
            var total = mark.Length / MarkStrokeThickness;
            mark.Shape.StrokeDashArray = new DoubleCollection { total * progress, total + 1 };
        }

        foreach (var label in _labels)
        {
            label.Opacity = progress;
            label.Visibility = progress <= 0 ? Visibility.Hidden : Visibility.Visible;
        }
    }

    private static System.Windows.Shapes.Path CreatePointer()
    {
        // Riley-style arrow cursor; the tip is the path origin so Canvas.Left/Top is the hotspot.
        var geometry = Geometry.Parse("M0,0 L0,17 L4.6,12.9 L7.6,19.6 L10.5,18.4 L7.5,11.8 L13.2,11.6 Z");
        geometry.Freeze();
        return new System.Windows.Shapes.Path
        {
            Data = geometry,
            Fill = new SolidColorBrush(PointerColor),
            Stroke = MediaBrushes.White,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Effect = new DropShadowEffect
            {
                Color = Colors.Black,
                BlurRadius = 6,
                ShadowDepth = 1.5,
                Direction = 270,
                Opacity = 0.35
            },
            IsHitTestVisible = false,
            Visibility = Visibility.Hidden
        };
    }

    // Ramanujan's approximation.
    private static double EllipsePerimeter(double a, double b) =>
        Math.PI * (3 * (a + b) - Math.Sqrt((3 * a + b) * (a + 3 * b)));

    private static IReadOnlyList<PhysicalMark> MapMarks(ScreenContext context, IReadOnlyList<AnnotationMark> marks)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(marks);
        var mapped = new List<PhysicalMark>(marks.Count);
        foreach (var mark in marks)
        {
            var bounds = AnnotationValidator.MapToDisplay(mark.Bounds, context);
            PointD? tail = null;
            if (mark.Kind.Equals("arrow", StringComparison.OrdinalIgnoreCase))
            {
                var end = AnnotationValidator.MapToDisplay(new RectD(mark.EndX, mark.EndY, 0, 0), context);
                tail = new PointD(mark.EndX == 0 ? bounds.X : end.X, mark.EndY == 0 ? bounds.Y : end.Y);
            }

            mapped.Add(new PhysicalMark(mark.Kind, bounds, mark.Label, tail));
        }

        return mapped;
    }

    private static RectD Inflate(RectD rect, double by) =>
        new(rect.X - by, rect.Y - by, rect.Width + 2 * by, rect.Height + 2 * by);

    private double WindowScale()
    {
        return _handle == IntPtr.Zero ? 1d : Math.Max(1d, GetDpiForWindow(_handle) / 96d);
    }

    private static IntPtr WindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WmMouseActivate)
        {
            handled = true;
            return new IntPtr(MaNoActivate);
        }
        return IntPtr.Zero;
    }

    /// <summary>A mark in physical virtual-screen pixels.</summary>
    private sealed record PhysicalMark(string Kind, RectD Bounds, string Label, PointD? Tail);

    private sealed record StrokedMark(Shape Shape, double Length);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    private delegate IntPtr LowLevelMouseProc(int code, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hWnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
