using System.Runtime.InteropServices;
using Boo.Core;
using Windows.Foundation;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;

namespace Boo.Windows;

internal sealed class WindowsFrameCaptureBackend : IFrameCaptureBackend, IDisposable
{
    private const uint WdaExcludeFromCapture = CaptureExclusionPolicy.ExcludeFromCapture;

    // One Direct3D device for the life of the backend. Creating a device (adapter enumeration, driver load,
    // WinRT bridge) costs tens of milliseconds and megabytes per call, and a focus session captures every
    // 30 s for 25 minutes. ScreenCapture serialises captures through its lifetime gate, so this needs no lock
    // beyond the dispose flag; any failure after the device stage drops it so the next capture starts clean
    // (device removed, driver reset, GPU switch).
    private readonly TimeProvider _clock;
    private NativeCaptureDevice? _device;
    private bool _disposed;

    public WindowsFrameCaptureBackend(TimeProvider? clock = null) => _clock = clock ?? TimeProvider.System;

    public async Task<CapturedFrame> CaptureAsync(
        DisplayInfo display,
        WindowIdentity target,
        IReadOnlyList<long> ownedWindows,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(ownedWindows);

        if (ownedWindows.Count == 0 || ownedWindows.Any(handle => handle == 0))
        {
            throw new CaptureLimitationException(
                "Screen capture requires at least one nonzero Boo-owned window handle.");
        }

        var affinity = new List<WindowAffinity>(ownedWindows.Count);
        Exception? operationError = null;
        var deviceUsed = false;
        var stage = "exclude Boo's windows from capture";

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            ApplyCaptureExclusion(ownedWindows, affinity);
            cancellationToken.ThrowIfCancellationRequested();

            stage = "find the selected display";
            var monitor = FindMonitor(display.Id);
            if (monitor == IntPtr.Zero)
            {
                throw new CaptureLimitationException($"The selected display '{display.Id}' is no longer available.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            stage = "open the selected display for capture";
            var item = CaptureInterop.CreateItemForMonitor(monitor);
            cancellationToken.ThrowIfCancellationRequested();

            stage = "create the graphics capture device";
            ObjectDisposedException.ThrowIf(_disposed, this);
            var d3d = _device ??= NativeCaptureDevice.Create();
            deviceUsed = true;
            cancellationToken.ThrowIfCancellationRequested();

            var frameSize = item.Size;
            if (frameSize.Width <= 0 || frameSize.Height <= 0)
            {
                throw new CaptureLimitationException("Windows Graphics Capture returned an empty display frame.");
            }

            stage = "prepare the screen frame pool";
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                d3d.Device,
                DirectXPixelFormat.B8G8R8A8UIntNormalized,
                2,
                frameSize);
            cancellationToken.ThrowIfCancellationRequested();

            stage = "create the screen capture session";
            using var session = pool.CreateCaptureSession(item);
            cancellationToken.ThrowIfCancellationRequested();

            // CaptureOneFrameAsync registers cancellation before StartCapture
            // and owns late-frame disposal while the pool is being torn down.
            stage = "acquire a screen frame";
            using var frame = await CaptureOneFrameAsync(pool, session, cancellationToken)
                .ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            stage = "read the captured screen frame";
            var jpeg = d3d.ReadJpeg(frame.Surface, frame.ContentSize.Width, frame.ContentSize.Height);
            // Readback can outlive the frame-arrival callback. Do not return a
            // successful frame after the deadline or caller cancellation.
            cancellationToken.ThrowIfCancellationRequested();

            var context = new ScreenContext(
                Guid.NewGuid(),
                generation,
                display,
                frame.ContentSize.Width,
                frame.ContentSize.Height,
                target,
                _clock.GetUtcNow());
            stage = "finish native capture cleanup";
            return new CapturedFrame(context, jpeg);
        }
        catch (Exception error)
        {
            operationError = error;
            if (deviceUsed && error is not OperationCanceledException)
            {
                DropDevice(); // a fresh device next time; this one may be lost or in a bad state.
            }
            if (error is OperationCanceledException or CaptureLimitationException)
            {
                throw;
            }
            throw new CaptureLimitationException(
                $"Windows could not {stage} ({error.GetType().Name}, error 0x{error.HResult:X8}). No frame was sent.", error);
        }
        finally
        {
            // This runs after the using declarations above have disposed the
            // frame, session, pool, and native device. Restore every recorded
            // affinity even if one restoration fails.
            try
            {
                RestoreCaptureAffinity(affinity);
            }
            catch (Exception restoreError)
            {
                if (operationError is null)
                {
                    throw new CaptureLimitationException(
                        "Screen capture could not restore Boo's window capture exclusion after cleanup.",
                        restoreError);
                }

                throw new CaptureLimitationException(
                    "Screen capture failed during capture cleanup, including window capture-exclusion restoration.",
                    new AggregateException(operationError, restoreError));
            }
        }
    }

    /// <summary>Releases the cached Direct3D device (app exit or after a capture error).</summary>
    public void Dispose()
    {
        _disposed = true;
        DropDevice();
    }

    private void DropDevice()
    {
        var device = _device;
        _device = null;
        device?.Dispose();
    }

    private static async Task<Direct3D11CaptureFrame> CaptureOneFrameAsync(
        Direct3D11CaptureFramePool pool,
        GraphicsCaptureSession session,
        CancellationToken cancellationToken)
    {
        var frameSource = new TaskCompletionSource<Direct3D11CaptureFrame>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        TypedEventHandler<Direct3D11CaptureFramePool, object> handler = (_, _) =>
        {
            try
            {
                var frame = pool.TryGetNextFrame();
                if (frame is not null && !frameSource.TrySetResult(frame))
                {
                    // Cancellation may win just before a frame arrives. A
                    // late frame must not survive pool/session cleanup.
                    frame.Dispose();
                }
            }
            catch (Exception error)
            {
                frameSource.TrySetException(error);
            }
        };

        pool.FrameArrived += handler;
        using var registration = cancellationToken.Register(
            () => frameSource.TrySetCanceled(cancellationToken));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            session.StartCapture();
            return await frameSource.Task.ConfigureAwait(false);
        }
        finally
        {
            pool.FrameArrived -= handler;
        }
    }

    private static void ApplyCaptureExclusion(
        IReadOnlyList<long> ownedWindows,
        ICollection<WindowAffinity> affinity)
    {
        foreach (var rawHandle in ownedWindows.Distinct())
        {
            var hwnd = new IntPtr(rawHandle);
            if (!GetWindowDisplayAffinity(hwnd, out var prior))
            {
                throw new CaptureLimitationException(
                    $"Boo could not inspect display-capture exclusion for owned window {rawHandle}.");
            }

            // Hidden windows are never in the frame, and already-excluded ones need no change: touching either
            // is skipped (see CaptureExclusionPolicy for the blank-window bug this avoids).
            if (!CaptureExclusionPolicy.NeedsTemporaryExclusion(IsWindowVisible(hwnd), prior))
            {
                continue;
            }

            affinity.Add(new WindowAffinity(hwnd, prior));
            if (!SetWindowDisplayAffinity(hwnd, WdaExcludeFromCapture))
            {
                var error = Marshal.GetLastWin32Error();
                throw new CaptureLimitationException(
                    $"Boo paused screen capture because Windows could not exclude owned window {rawHandle} from capture (Win32 {error}).");
            }
        }
    }

    private static void RestoreCaptureAffinity(IReadOnlyList<WindowAffinity> windows)
    {
        List<Exception>? failures = null;
        foreach (var window in windows)
        {
            if (SetWindowDisplayAffinity(window.Handle, window.Previous))
            {
                continue;
            }

            failures ??= new List<Exception>();
            var error = Marshal.GetLastWin32Error();
            failures.Add(new InvalidOperationException(
                $"Windows could not restore display-capture exclusion for owned window {window.Handle} (Win32 {error})."));
        }

        if (failures is not null)
        {
            throw new CaptureLimitationException(
                "Windows could not restore one or more Boo-owned window capture exclusions.",
                new AggregateException(failures));
        }
    }

    private static IntPtr FindMonitor(string displayId)
    {
        IntPtr match = IntPtr.Zero;
        bool Find(IntPtr monitor, IntPtr hdc, ref NativeRect rect, IntPtr data)
        {
            var info = new NativeMonitorInfo { CbSize = Marshal.SizeOf<NativeMonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) &&
                string.Equals(info.DeviceName.TrimEnd('\0'), displayId, StringComparison.Ordinal))
            {
                match = monitor;
                return false;
            }

            return true;
        }

        _ = EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, Find, IntPtr.Zero);
        return match;
    }

    private readonly record struct WindowAffinity(IntPtr Handle, uint Previous);

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
    private static extern bool EnumDisplayMonitors(
        IntPtr hdc,
        IntPtr clip,
        MonitorEnumDelegate callback,
        IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(
        IntPtr monitor,
        ref NativeMonitorInfo info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(
        IntPtr hwnd,
        out uint affinity);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(
        IntPtr hwnd,
        uint affinity);

    private delegate bool MonitorEnumDelegate(
        IntPtr monitor,
        IntPtr hdcMonitor,
        ref NativeRect monitorRect,
        IntPtr data);
}
