using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Boo.Core;

namespace Boo.Windows;

/// <summary>
/// Tracks the last usable external foreground window and exposes physical
/// monitor geometry. The observer never treats a Boo-owned HWND as a target.
/// </summary>
public sealed class TargetObserver : IDisposable
{
    private const uint EventSystemForeground = 0x0003;
    private const uint WineventOutOfContext = 0;
    private const uint WdaMonitor = 1;
    private const uint WdaExcludeFromCapture = 0x11;
    private const int DwmwaCloaked = 14;

    private readonly object _gate = new();
    private readonly WinEventDelegate _winEventDelegate;
    private readonly int _booProcessId;
    private IntPtr _winEventHook;
    private WindowIdentity? _retainedTarget;
    private string? _retainedDisplayId;
    private string _unavailableReason = "No external window was recorded before Boo opened.";
    private bool _disposed;

    public TargetObserver()
    {
        _booProcessId = Environment.ProcessId;
        _winEventDelegate = OnForegroundChanged;
        _winEventHook = SetWinEventHook(
            EventSystemForeground,
            EventSystemForeground,
            IntPtr.Zero,
            _winEventDelegate,
            0,
            0,
            WineventOutOfContext);

        if (_winEventHook == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Boo could not observe foreground-window changes.");
        }

        SeedCurrentForeground();
    }

    public IReadOnlyList<DisplayInfo> Displays()
    {
        ThrowIfDisposed();
        var displays = new List<DisplayInfo>();
        Win32Exception? displayFailure = null;
        bool AddMonitor(IntPtr monitor, IntPtr hdcMonitor, ref NativeRect monitorRect, IntPtr data)
        {
            try
            {
                displays.Add(ReadDisplay(monitor));
                return true;
            }
            catch (Win32Exception error)
            {
                displayFailure = error;
                return false;
            }
        }

        var enumerated = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, AddMonitor, IntPtr.Zero);
        if (displayFailure is not null)
        {
            throw displayFailure;
        }

        if (!enumerated)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not enumerate displays.");
        }

        return displays;
    }

    public WindowIdentity? Snapshot(DisplayInfo display) => Snapshot(display, out _);

    public WindowIdentity? Snapshot(DisplayInfo display, out string? limitation)
    {
        ArgumentNullException.ThrowIfNull(display);
        ThrowIfDisposed();
        limitation = null;

        lock (_gate)
        {
            var foreground = GetForegroundWindow();
            var foregroundIsBoo = IsBooWindow(foreground);

            if (!foregroundIsBoo && foreground != IntPtr.Zero)
            {
                var current = ReadWindow(foreground);
                if (current is null)
                {
                    limitation = "Windows could not identify the foreground window.";
                }
                else if (!IsOnDisplay(current, display))
                {
                    limitation = "The foreground window is not on the selected display.";
                }
                else if (IsProtectedWindow(foreground))
                {
                    limitation = "The foreground window is protected or hidden by Windows.";
                }
                else
                {
                    Retain(current, display.Id);
                    return current;
                }

                _unavailableReason = limitation;
                _retainedTarget = null;
                _retainedDisplayId = null;
                return null;
            }

            if (_retainedTarget is not { } retained)
            {
                limitation = _unavailableReason;
            }
            else if (!string.Equals(_retainedDisplayId, display.Id, StringComparison.Ordinal))
            {
                limitation = "The previously selected window belongs to a different display.";
            }
            else if (IsCurrentWindow(retained, display.Id, out limitation))
            {
                return retained;
            }

            _unavailableReason = limitation ?? "The selected window is no longer available.";
            _retainedTarget = null;
            _retainedDisplayId = null;
            return null;
        }
    }

    public bool IsCurrent(WindowIdentity target)
    {
        ThrowIfDisposed();
        if (!IsValidIdentity(target))
        {
            return false;
        }

        lock (_gate)
        {
            if (_retainedTarget is not { } retained || !SameIdentity(retained, target))
            {
                return false;
            }

            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || (!IsBooWindow(foreground) && foreground.ToInt64() != target.Handle))
            {
                return false;
            }

            return IsCurrentWindow(target, _retainedDisplayId, out _);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _retainedTarget = null;
            _retainedDisplayId = null;
            if (_winEventHook != IntPtr.Zero)
            {
                UnhookWinEvent(_winEventHook);
                _winEventHook = IntPtr.Zero;
            }
        }
        GC.SuppressFinalize(this);
    }

    private void OnForegroundChanged(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime)
    {
        if (eventType != EventSystemForeground || hwnd == IntPtr.Zero || idObject != 0 || idChild != 0)
        {
            return;
        }

        lock (_gate)
        {
            if (_disposed || IsBooWindow(hwnd))
            {
                return;
            }

            var identity = ReadWindow(hwnd);
            if (identity is null || IsProtectedWindow(hwnd))
            {
                return;
            }

            var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var display = ReadDisplay(monitor);
                Retain(identity, display.Id);
            }
            catch (Win32Exception error)
            {
                // A display/DPI read failure must invalidate the old target;
                // retaining it would turn a stale observation into a guess.
                _retainedTarget = null;
                _retainedDisplayId = null;
                _unavailableReason = $"Windows could not read the foreground display geometry (error 0x{error.NativeErrorCode:X8}).";
            }
        }
    }

    private void Retain(WindowIdentity target, string displayId)
    {
        _retainedTarget = target;
        _retainedDisplayId = displayId;
    }

    private void SeedCurrentForeground()
    {
        lock (_gate)
        {
            var foreground = GetForegroundWindow();
            if (foreground == IntPtr.Zero || IsBooWindow(foreground) || IsProtectedWindow(foreground))
            {
                _unavailableReason = "No accessible external window was in front when Boo opened.";
                return;
            }

            var identity = ReadWindow(foreground);
            var monitor = MonitorFromWindow(foreground, MonitorDefaultToNearest);
            if (identity is null || monitor == IntPtr.Zero)
            {
                _unavailableReason = "Windows could not identify the external window or its monitor when Boo opened.";
                return;
            }

            try
            {
                Retain(identity, ReadDisplay(monitor).Id);
            }
            catch (Win32Exception error)
            {
                // A transient display/DPI failure leaves no guessed target.
                _retainedTarget = null;
                _retainedDisplayId = null;
                _unavailableReason = $"Windows could not read the initial display geometry (error 0x{error.NativeErrorCode:X8}).";
            }
        }
    }

    private bool IsCurrentWindow(WindowIdentity target, string? expectedDisplayId, out string? limitation)
    {
        limitation = null;
        var hwnd = new IntPtr(target.Handle);
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || IsBooWindow(hwnd) || IsProtectedWindow(hwnd))
        {
            limitation = "The selected window closed, became protected, or is no longer an external window.";
            return false;
        }

        var current = ReadWindow(hwnd);
        if (current is not { } identity || !SameIdentity(identity, target))
        {
            limitation = "The selected window moved, resized, or changed identity after it was observed.";
            return false;
        }

        if (expectedDisplayId is null)
        {
            limitation = "The selected window has no recorded display.";
            return false;
        }

        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            limitation = "Windows could not find the selected window's monitor.";
            return false;
        }

        try
        {
            if (string.Equals(ReadDisplay(monitor).Id, expectedDisplayId, StringComparison.Ordinal))
            {
                return true;
            }
            limitation = "The selected window moved to a different display.";
            return false;
        }
        catch (Win32Exception error)
        {
            limitation = $"Windows could not recheck the selected display geometry (error 0x{error.NativeErrorCode:X8}).";
            return false;
        }
    }

    private WindowIdentity? ReadWindow(IntPtr hwnd)
    {
        if (!IsWindow(hwnd) || IsBooWindow(hwnd) || !GetWindowRect(hwnd, out var rect))
        {
            return null;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == 0 || processId == _booProcessId)
        {
            return null;
        }

        string processName;
        try
        {
            processName = Process.GetProcessById((int)processId).ProcessName;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        var bounds = ToRect(rect);
        return bounds.Width > 0 && bounds.Height > 0
            ? new WindowIdentity(hwnd.ToInt64(), (int)processId, processName, bounds)
            : null;
    }

    private DisplayInfo ReadDisplay(IntPtr monitor)
    {
        var info = new NativeMonitorInfo { CbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not read monitor geometry.");
        }

        var dpiX = 96u;
        var dpiY = 96u;
        var dpiResult = GetDpiForMonitor(monitor, MonitorDpiType.Effective, ref dpiX, ref dpiY);
        if (dpiResult != 0 || dpiX == 0)
        {
            throw new Win32Exception((int)dpiResult, "Windows could not read per-monitor DPI.");
        }

        var id = info.DeviceName.TrimEnd('\0');
        return new DisplayInfo(id, id, ToRect(info.Monitor), dpiX / 96d);
    }

    private static bool IsOnDisplay(WindowIdentity target, DisplayInfo display)
    {
        var monitor = MonitorFromWindow(new IntPtr(target.Handle), MonitorDefaultToNearest);
        if (monitor == IntPtr.Zero)
        {
            return false;
        }

        var info = new NativeMonitorInfo { CbSize = Marshal.SizeOf<NativeMonitorInfo>() };
        return GetMonitorInfo(monitor, ref info) &&
            string.Equals(info.DeviceName.TrimEnd('\0'), display.Id, StringComparison.Ordinal);
    }

    private bool IsBooWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        GetWindowThreadProcessId(hwnd, out var processId);
        if (processId == _booProcessId)
        {
            return true;
        }

        return false;
    }

    private static bool IsProtectedWindow(IntPtr hwnd)
    {
        if (GetWindowDisplayAffinity(hwnd, out var affinity) && affinity is WdaMonitor or WdaExcludeFromCapture)
        {
            return true;
        }

        return DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    private static bool IsValidIdentity(WindowIdentity target) =>
        target.Handle != 0 && target.ProcessId > 0 && !string.IsNullOrWhiteSpace(target.ProcessName) &&
        double.IsFinite(target.Bounds.X) && double.IsFinite(target.Bounds.Y) &&
        double.IsFinite(target.Bounds.Width) && double.IsFinite(target.Bounds.Height) &&
        target.Bounds.Width > 0 && target.Bounds.Height > 0;

    private static bool SameIdentity(WindowIdentity left, WindowIdentity right) =>
        left.Handle == right.Handle && left.ProcessId == right.ProcessId &&
        string.Equals(left.ProcessName, right.ProcessName, StringComparison.OrdinalIgnoreCase) &&
        left.Bounds == right.Bounds;

    private static RectD ToRect(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    private delegate bool MonitorEnumDelegate(IntPtr monitor, IntPtr hdcMonitor, ref NativeRect monitorRect, IntPtr data);
    private delegate void WinEventDelegate(IntPtr hook, uint eventType, IntPtr hwnd, int idObject, int idChild, uint eventThread, uint eventTime);

    private enum MonitorDpiType
    {
        Effective = 0
    }

    private const uint MonitorDefaultToNearest = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NativeMonitorInfo
    {
        public int CbSize;
        public NativeRect Monitor;
        public NativeRect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumDelegate callback, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref NativeMonitorInfo info);

    [DllImport("shcore.dll", SetLastError = true)]
    private static extern int GetDpiForMonitor(IntPtr monitor, MonitorDpiType type, ref uint dpiX, ref uint dpiY);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module, WinEventDelegate callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint processId);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr hwnd, out uint affinity);

    [DllImport("dwmapi.dll", SetLastError = true)]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int valueSize);
}
