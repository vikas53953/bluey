using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Controls;
using Boo.Core;
using Forms = System.Windows.Forms;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace Boo.App;

/// <summary>
/// A separate, click-through HWND for verified screen marks.  It never owns
/// focus and is discarded whenever the observation or display changes.
/// </summary>
public partial class AnnotationWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExTransparent = 0x20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x80;
    private const int WmMouseActivate = 0x21;
    private const int MaNoActivate = 3;
    private readonly ScreenContext _context;
    private readonly DisplayInfo _display;
    private readonly IReadOnlyList<AnnotationMark> _marks;
    private IntPtr _handle;
    public bool CaptureExclusionAvailable { get; private set; }

    public AnnotationWindow(ScreenContext context, IReadOnlyList<AnnotationMark> marks)
    {
        _context = context;
        _display = context.Display;
        _marks = marks;
        InitializeComponent();
        SourceInitialized += OnSourceInitialized;
        Loaded += (_, _) => RenderMarks();
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(_handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExstyle, new IntPtr(style | WsExTransparent | WsExNoActivate | WsExToolWindow));
        HwndSource.FromHwnd(_handle)?.AddHook(WindowProc);
        CaptureExclusionAvailable = SetWindowDisplayAffinity(_handle, 0x11);
        PositionOnDisplay();
    }

    private void PositionOnDisplay()
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(item =>
            string.Equals(item.DeviceName, _display.Id, StringComparison.OrdinalIgnoreCase));
        if (screen is null)
        {
            Close();
            return;
        }

        var scale = _handle == IntPtr.Zero ? 1d : Math.Max(1d, GetDpiForWindow(_handle) / 96d);
        Left = screen.Bounds.Left / scale;
        Top = screen.Bounds.Top / scale;
        Width = screen.Bounds.Width / scale;
        Height = screen.Bounds.Height / scale;
    }

    private void RenderMarks()
    {
        MarksCanvas.Children.Clear();
        foreach (var mark in _marks)
        {
            var mapped = AnnotationValidator.MapToDisplay(mark.Bounds, _context);
            var x = (mapped.X - _display.PixelBounds.X) / WindowScale();
            var y = (mapped.Y - _display.PixelBounds.Y) / WindowScale();
            var width = mapped.Width / WindowScale();
            var height = mapped.Height / WindowScale();
            if (mark.Kind.Equals("ring", StringComparison.OrdinalIgnoreCase))
            {
                var ellipse = new Ellipse
                {
                    Width = width,
                    Height = height,
                    Stroke = new SolidColorBrush(MediaColor.FromRgb(243, 181, 155)),
                    StrokeThickness = 3,
                    Fill = MediaBrushes.Transparent,
                    IsHitTestVisible = false
                };
                Canvas.SetLeft(ellipse, x);
                Canvas.SetTop(ellipse, y);
                MarksCanvas.Children.Add(ellipse);
            }
            else if (mark.Kind.Equals("arrow", StringComparison.OrdinalIgnoreCase))
            {
                var line = new Line
                {
                    X1 = mark.EndX == 0 ? x : (AnnotationValidator.MapToDisplay(new RectD(mark.EndX, mark.EndY, 0, 0), _context).X - _display.PixelBounds.X) / WindowScale(),
                    Y1 = mark.EndY == 0 ? y : (AnnotationValidator.MapToDisplay(new RectD(mark.EndX, mark.EndY, 0, 0), _context).Y - _display.PixelBounds.Y) / WindowScale(),
                    X2 = x + width / 2,
                    Y2 = y + height / 2,
                    Stroke = new SolidColorBrush(MediaColor.FromRgb(243, 181, 155)),
                    StrokeThickness = 3,
                    IsHitTestVisible = false
                };
                MarksCanvas.Children.Add(line);
            }

            if (!string.IsNullOrWhiteSpace(mark.Label))
            {
                var label = new Border
                {
                    Background = new SolidColorBrush(MediaColor.FromArgb(230, 23, 23, 25)),
                    BorderBrush = new SolidColorBrush(MediaColor.FromRgb(243, 181, 155)),
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
            }
        }
    }

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

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);
}
