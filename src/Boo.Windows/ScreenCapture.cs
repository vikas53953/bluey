using Boo.Core;

namespace Boo.Windows;

public sealed class ScreenCapture : IDisposable
{
    private const double MaximumTimeoutMilliseconds = 4_294_967_294d;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    private readonly IFrameCaptureBackend _backend;
    private readonly TimeSpan _timeout;
    private readonly SemaphoreSlim _lifetimeGate = new(1, 1);
    private static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(2);
    private bool _disposed;
    private volatile bool _disposeDeferred;
    private int _backendDisposed;

    public ScreenCapture()
        : this(new WindowsFrameCaptureBackend(), DefaultTimeout)
    {
    }

    public ScreenCapture(IFrameCaptureBackend backend, TimeSpan? timeout = null)
    {
        ArgumentNullException.ThrowIfNull(backend);

        var effectiveTimeout = timeout ?? DefaultTimeout;
        ValidateTimeout(effectiveTimeout);

        _backend = backend;
        _timeout = effectiveTimeout;
    }

    public async Task<CapturedFrame> CaptureAsync(
        DisplayInfo display,
        WindowIdentity target,
        IReadOnlyList<long> ownedWindows,
        long generation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ownedWindows);
        var ownedWindowSnapshot = ownedWindows.ToArray();

        ArgumentNullException.ThrowIfNull(display);
        ArgumentNullException.ThrowIfNull(target);
        ValidateCaptureArguments(display, target, ownedWindowSnapshot);

        ObjectDisposedException.ThrowIf(_disposed, this);
        await _lifetimeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(_timeout);

            try
            {
                var frame = await _backend.CaptureAsync(
                    display,
                    target,
                    ownedWindowSnapshot,
                    generation,
                    deadline.Token).ConfigureAwait(false);

                ThrowIfCaptureCancellationRequested(cancellationToken, deadline.Token);
                return frame;
            }
            catch (OperationCanceledException error)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(cancellationToken);
                }

                if (deadline.IsCancellationRequested)
                {
                    throw new CaptureLimitationException(
                        "Screen capture was limited because no frame arrived before the capture deadline.",
                        error);
                }

                throw;
            }
        }
        finally
        {
            // The backend task is awaited above, so this gate is not released
            // while a canceled backend is still cleaning up native resources.
            _lifetimeGate.Release();
            if (_disposeDeferred)
            {
                // Dispose() gave up waiting for this capture; it is now safe to release the device.
                DisposeBackend();
            }
        }
    }

    /// <summary>
    /// Releases backend resources held between captures (the cached Direct3D device). Waits briefly for an
    /// in-flight capture to finish so the device is never released under a native readback; callers cancel
    /// their tokens first, so the wait is normally immediate.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        var held = _lifetimeGate.Wait(DisposeWait);
        if (!held)
        {
            // A capture is still inside the backend (possibly in a native readback). Releasing the Direct3D
            // device under it would be a use-after-free, so hand the disposal to that capture: it disposes the
            // backend right after it leaves the gate. Re-check once so a release that raced the timeout is not
            // missed; the semaphore itself is left to the collector.
            _disposeDeferred = true;
            if (_lifetimeGate.Wait(0))
            {
                try
                {
                    DisposeBackend();
                }
                finally
                {
                    _lifetimeGate.Release();
                }
            }
            return;
        }

        try
        {
            DisposeBackend();
        }
        finally
        {
            _lifetimeGate.Release();
            _lifetimeGate.Dispose();
        }
    }

    private void DisposeBackend()
    {
        if (Interlocked.Exchange(ref _backendDisposed, 1) == 0)
        {
            (_backend as IDisposable)?.Dispose();
        }
    }

    private static void ValidateTimeout(TimeSpan timeout)
    {
        var milliseconds = timeout.TotalMilliseconds;
        if (milliseconds < 1 || milliseconds > MaximumTimeoutMilliseconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                timeout,
                "Capture timeout must be between 1 and 4,294,967,294 milliseconds.");
        }
    }

    private static void ThrowIfCaptureCancellationRequested(
        CancellationToken callerCancellationToken,
        CancellationToken deadlineToken)
    {
        if (callerCancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(callerCancellationToken);
        }

        if (deadlineToken.IsCancellationRequested)
        {
            throw new CaptureLimitationException(
                "Screen capture was limited because no frame arrived before the capture deadline.");
        }
    }

    private static void ValidateCaptureArguments(
        DisplayInfo display,
        WindowIdentity target,
        IReadOnlyList<long> ownedWindows)
    {
        if (ownedWindows.Count == 0)
        {
            throw new CaptureLimitationException(
                "Screen capture requires Boo's owned windows so recursive capture can be excluded.");
        }

        if (ownedWindows.Any(handle => handle == 0))
        {
            throw new CaptureLimitationException(
                "Screen capture requires every Boo-owned window handle to be nonzero.");
        }

        if (string.IsNullOrWhiteSpace(display.Id) ||
            !double.IsFinite(display.PixelBounds.X) ||
            !double.IsFinite(display.PixelBounds.Y) ||
            !double.IsFinite(display.PixelBounds.Width) ||
            !double.IsFinite(display.PixelBounds.Height) ||
            display.PixelBounds.Width <= 0 ||
            display.PixelBounds.Height <= 0 ||
            !double.IsFinite(display.Scale) ||
            display.Scale <= 0)
        {
            throw new ArgumentException("The selected display geometry is invalid.", nameof(display));
        }

        if (target.Handle == 0 ||
            target.ProcessId <= 0 ||
            target.Bounds.Width <= 0 ||
            target.Bounds.Height <= 0 ||
            !double.IsFinite(target.Bounds.X) ||
            !double.IsFinite(target.Bounds.Y) ||
            !double.IsFinite(target.Bounds.Width) ||
            !double.IsFinite(target.Bounds.Height))
        {
            throw new CaptureLimitationException("Screen capture requires a live, identified external target.");
        }

        if (ownedWindows.Contains(target.Handle))
        {
            throw new CaptureLimitationException("Boo refuses to capture a Boo-owned target window.");
        }
    }
}
