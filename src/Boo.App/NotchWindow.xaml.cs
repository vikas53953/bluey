using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Boo.Core;
using Forms = System.Windows.Forms;
using MediaSystemColors = System.Windows.SystemColors;

namespace Boo.App;

public partial class NotchWindow : Window
{
    private const int GwlExstyle = -20;
    private const int WsExNoActivate = 0x08000000;
    private const int WsExToolWindow = 0x00000080;
    private const int WmMouseActivate = 0x0021;
    private const int WmHotKey = 0x0312;
    private const int MaNoActivate = 3;
    private const int WmDisplayChange = 0x007E;
    private const int WmDpiChanged = 0x02E0;
    private const int EmergencyHotKeyId = 0xB00;
    private const int OpenHotKeyId = 0xB01;
    private const uint ModControl = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint EmergencyKey = 0x7B;
    private readonly Action _onOpenPanel;
    private readonly Action _onStop;
    private readonly Func<DisplayInfo?> _selectedDisplay;
    private readonly Func<double> _verticalOffset;
    private readonly Action _onDisplayLayoutChanged;
    private readonly PresenceController _presence = new();
    private NotchAppearance _appearance = new();
    private bool _captureActive;
    private string? _captureDisplayName;
    private bool _microphoneActive;
    private string? _status;
    private IntPtr _handle;
    private IntPtr _lastExternalHandle;
    private int _lastExternalProcess;
    private bool _positioning;
    public bool CaptureExclusionAvailable { get; private set; } = true;
    public bool EmergencyStopRegistered { get; private set; }
    public bool OpenShortcutRegistered { get; private set; }

    public NotchWindow(Action onOpenPanel, Action onStop, Func<DisplayInfo?> selectedDisplay,
        NotchAppearance appearance, Action onDisplayLayoutChanged, Func<double>? verticalOffset = null)
    {
        _onOpenPanel = onOpenPanel;
        _onStop = onStop;
        _selectedDisplay = selectedDisplay;
        _onDisplayLayoutChanged = onDisplayLayoutChanged;
        _verticalOffset = verticalOffset ?? (() => 0d);
        _appearance = appearance;
        InitializeComponent();
        ApplyAppearance();
        SystemParameters.StaticPropertyChanged += SystemParameters_OnStaticPropertyChanged;
        SourceInitialized += OnSourceInitialized;
        LocationChanged += (_, _) => CaptureExternalTarget();
        IsVisibleChanged += (_, _) => CaptureExternalTarget();
        Closed += (_, _) =>
        {
            if (EmergencyStopRegistered)
            {
                UnregisterHotKey(_handle, EmergencyHotKeyId);
            }
            if (OpenShortcutRegistered)
            {
                UnregisterHotKey(_handle, OpenHotKeyId);
            }
            SystemParameters.StaticPropertyChanged -= SystemParameters_OnStaticPropertyChanged;
        };
        UpdateVisuals();
    }

    public void ShowNotch()
    {
        if (!IsVisible)
        {
            Show();
        }
        PositionOnDisplay(_selectedDisplay());
    }

    public void PositionOnDisplay(DisplayInfo? display)
    {
        if (_positioning)
        {
            return;
        }

        _positioning = true;
        try
        {
            var requestedScreen = display is null ? null : FindScreen(display.Id);
            var screen = requestedScreen ?? Forms.Screen.PrimaryScreen;
            if (screen is null)
            {
                return;
            }

            var info = requestedScreen is not null && display is not null
                ? display
                : new DisplayInfo(
                    screen.DeviceName,
                    screen.Primary ? "Primary display" : screen.DeviceName,
                    new RectD(screen.Bounds.Left, screen.Bounds.Top, screen.Bounds.Width, screen.Bounds.Height),
                    1d);
            var bounds = DisplayGeometry.NotchBounds(info, Width, Height, _verticalOffset());
            if (_handle == IntPtr.Zero)
            {
                Left = bounds.X / info.Scale;
                Top = bounds.Y / info.Scale;
                return;
            }

            PlacePhysical(bounds);
            var actualScale = GetDpiForWindow(_handle) / 96d;
            if (double.IsFinite(actualScale) && actualScale > 0 && Math.Abs(actualScale - info.Scale) > 0.01)
            {
                var actualInfo = info with { Scale = actualScale };
                bounds = DisplayGeometry.NotchBounds(actualInfo, Width, Height, _verticalOffset());
                PlacePhysical(bounds);
            }
        }
        finally
        {
            _positioning = false;
        }
    }

    public void SetStatus(string status)
    {
        _status = string.IsNullOrWhiteSpace(status) ? null : status.Trim();
        UpdateVisuals();
    }

    public void SetAppearance(NotchAppearance appearance)
    {
        _appearance = appearance;
        ApplyAppearance();
    }

    public void SetCaptureActivity(bool active, string? displayName)
    {
        _captureActive = active;
        _captureDisplayName = active ? displayName : null;
        UpdateVisuals(displayName);
    }

    public void SetMicrophoneActivity(bool active)
    {
        _microphoneActive = active;
        UpdateVisuals();
    }

    public void RestoreExternalFocus()
    {
        if (_lastExternalHandle != IntPtr.Zero && IsWindow(_lastExternalHandle)
            && GetWindowProcessId(_lastExternalHandle) == _lastExternalProcess)
        {
            SetForegroundWindow(_lastExternalHandle);
        }
    }

    private void OpenButton_OnClick(object sender, RoutedEventArgs e)
    {
        CaptureExternalTarget();
        _onOpenPanel();
    }

    private void NotchWindow_OnMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _presence.SetPointerInside(true);
        UpdateVisuals();
    }

    private void NotchWindow_OnMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _presence.SetPointerInside(false);
        UpdateVisuals();
    }

    private void NotchSurface_OnMouseRightButtonUp(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _onStop();
        e.Handled = true;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        var style = GetWindowLongPtr(_handle, GwlExstyle).ToInt64();
        SetWindowLongPtr(_handle, GwlExstyle, new IntPtr(style | WsExNoActivate | WsExToolWindow));
        var source = HwndSource.FromHwnd(_handle);
        source?.AddHook(EmergencyWindowProc);
        EmergencyStopRegistered = RegisterHotKey(_handle, EmergencyHotKeyId, ModControl | ModAlt, EmergencyKey);
        OpenShortcutRegistered = RegisterHotKey(_handle, OpenHotKeyId, ModControl | ModAlt, 0x42);
        if (!EmergencyStopRegistered)
        {
            SetStatus("Tray Stop ready");
        }
        if (!OpenShortcutRegistered)
        {
            SetStatus("Open via tray");
        }
        CaptureExclusionAvailable = SetWindowDisplayAffinity(_handle, 0x11); // WDA_EXCLUDEFROMCAPTURE
        if (!CaptureExclusionAvailable)
        {
            SetStatus("Capture paused");
        }
        PositionOnDisplay(_selectedDisplay());
    }

    private void UpdateVisuals(string? displayName = null)
    {
        _presence.SetCaptureActive(_captureActive);
        _presence.SetMicrophoneActive(_microphoneActive);
        var revealed = _presence.IsRevealed;
        var activity = new List<string>();
        if (_captureActive)
        {
            activity.Add($"Screen shared · {_captureDisplayName ?? displayName ?? "selected display"}");
        }
        if (_microphoneActive)
        {
            activity.Add("Microphone active");
        }
        if (activity.Count == 0 && revealed && !string.IsNullOrWhiteSpace(_status))
        {
            activity.Add(_status);
        }

        NotchBody.Visibility = revealed ? Visibility.Visible : Visibility.Collapsed;
        ActiveDisclosureText.Text = string.Join(Environment.NewLine, activity);
        ActiveDisclosure.Visibility = activity.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        NotchSurface.Measure(new System.Windows.Size(Width, double.PositiveInfinity));
        Height = revealed ? Math.Max(44, NotchSurface.DesiredSize.Height) : 8;
        PositionOnDisplay(_selectedDisplay());
    }

    private void PlacePhysical(RectD bounds)
    {
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        SetWindowPos(_handle, IntPtr.Zero,
            (int)Math.Round(bounds.X),
            (int)Math.Round(bounds.Y),
            (int)Math.Round(bounds.Width),
            (int)Math.Round(bounds.Height),
            SwpNoActivate | SwpNoZOrder);
    }

    private void ApplyAppearance()
    {
        if (SystemParameters.HighContrast)
        {
            NotchBody.Background = MediaSystemColors.WindowBrush;
            LeftEye.Fill = MediaSystemColors.WindowTextBrush;
            RightEye.Fill = MediaSystemColors.WindowTextBrush;
        }
        else
        {
            NotchBody.Background = ToBrush(_appearance.BodyColor, System.Windows.Media.Brushes.Black);
            LeftEye.Fill = ToBrush(_appearance.EyeColor, System.Windows.Media.Brushes.White);
            RightEye.Fill = ToBrush(_appearance.EyeColor, System.Windows.Media.Brushes.White);
        }
        ActiveDisclosure.Background = SystemParameters.HighContrast ? MediaSystemColors.WindowBrush : System.Windows.Media.Brushes.White;
        ActiveDisclosure.BorderBrush = SystemParameters.HighContrast ? MediaSystemColors.ActiveBorderBrush : System.Windows.Media.Brushes.Black;
        ActiveDisclosureText.Foreground = SystemParameters.HighContrast ? MediaSystemColors.WindowTextBrush : System.Windows.Media.Brushes.Black;
    }

    private void SystemParameters_OnStaticPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (string.Equals(e.PropertyName, nameof(SystemParameters.HighContrast), StringComparison.Ordinal))
        {
            ApplyAppearance();
        }
    }

    private static System.Windows.Media.Brush ToBrush(string value, System.Windows.Media.Brush fallback)
    {
        try
        {
            var brush = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(value)!;
            brush.Freeze();
            return brush;
        }
        catch (Exception) when (value is not null)
        {
            return fallback;
        }
    }

    private void CaptureExternalTarget()
    {
        var foreground = GetForegroundWindow();
        if (foreground == IntPtr.Zero || foreground == _handle
            || GetWindowProcessId(foreground) == Environment.ProcessId
            || IsOwnedWindow(foreground))
        {
            return;
        }

        _lastExternalHandle = foreground;
        _lastExternalProcess = GetWindowProcessId(foreground);
    }

    private bool IsOwnedWindow(IntPtr candidate)
    {
        return candidate == _handle || (GetWindow(candidate, 4) == _handle);
    }

    private static Forms.Screen? FindScreen(string id)
    {
        return Forms.Screen.AllScreens.FirstOrDefault(screen =>
            string.Equals(screen.DeviceName, id, StringComparison.OrdinalIgnoreCase));
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

    private IntPtr EmergencyWindowProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg is WmDisplayChange or WmDpiChanged)
        {
            _onDisplayLayoutChanged();
            PositionOnDisplay(_selectedDisplay());
            handled = false;
            return IntPtr.Zero;
        }
        if (msg == WmHotKey && wParam.ToInt32() == OpenHotKeyId)
        {
            CaptureExternalTarget();
            _onOpenPanel();
            handled = true;
            return IntPtr.Zero;
        }
        if (msg == WmHotKey && wParam.ToInt32() == EmergencyHotKeyId)
        {
            _onStop();
            handled = true;
            return IntPtr.Zero;
        }
        return WindowProc(hwnd, msg, wParam, lParam, ref handled);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hWnd, uint command);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    private static int GetWindowProcessId(IntPtr hWnd)
    {
        GetWindowThreadProcessId(hWnd, out var processId);
        return unchecked((int)processId);
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowDisplayAffinity(IntPtr hWnd, uint affinity);

    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
