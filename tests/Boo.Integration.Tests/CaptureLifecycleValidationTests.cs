using Boo.Core;
using Boo.Windows;

namespace Boo.Integration.Tests;

public sealed class CaptureLifecycleValidationTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Capture_serializes_backend_lifetimes_through_cleanup()
    {
        var firstCleanupRelease = NewGate();
        ControlledBackend? backend = null;
        backend = new ControlledBackend(async (number, call, _) =>
        {
            if (number == 1)
            {
                await firstCleanupRelease.Task;
                call.CleanupSequence = backend!.NextEventSequence();
            }

            call.CompletionSequence = backend!.NextEventSequence();
            return CreateFrame(call);
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromSeconds(5));
        var firstTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1001L }, 1, CancellationToken.None);
        var firstCall = await backend.WaitForCallAsync(1);
        var secondTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1002L }, 2, CancellationToken.None);

        try
        {
            firstCleanupRelease.TrySetResult(null);

            await firstTask.WaitAsync(TestWait);
            var secondCall = await backend.WaitForCallAsync(2);
            await secondTask.WaitAsync(TestWait);

            Assert.True(firstCall.CleanupSequence > 0);
            Assert.True(firstCall.CompletionSequence > firstCall.CleanupSequence);
            Assert.True(secondCall.StartSequence > firstCall.CompletionSequence,
                "The next backend lifetime must not start until the previous cleanup and completion finish.");
        }
        finally
        {
            firstCleanupRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(secondTask);
        }
    }

    [Fact]
    public async Task Caller_cancellation_while_queued_never_enters_backend()
    {
        var firstRelease = NewGate();
        var backend = new ControlledBackend(async (number, call, _) =>
        {
            if (number == 1)
                await firstRelease.Task;

            return CreateFrame(call);
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromSeconds(5));
        var firstTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1001L }, 1, CancellationToken.None);
        await backend.WaitForCallAsync(1);
        using var callerCancellation = new CancellationTokenSource();
        var queuedTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1002L }, 2, callerCancellation.Token);

        try
        {
            callerCancellation.Cancel();

            await Assert.ThrowsAsync<OperationCanceledException>(() => queuedTask.WaitAsync(TestWait));
            Assert.Equal(1, backend.CallCount);

            firstRelease.TrySetResult(null);
            await firstTask.WaitAsync(TestWait);
        }
        finally
        {
            firstRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(queuedTask);
        }
    }

    [Fact]
    public async Task Deadline_starts_after_acquiring_the_backend_lifetime()
    {
        var firstRelease = NewGate();
        var backend = new ControlledBackend(async (number, call, cancellationToken) =>
        {
            if (number == 1)
                await firstRelease.Task;

            call.TokenWasCanceledOnEntry = cancellationToken.IsCancellationRequested;
            return CreateFrame(call);
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromMilliseconds(100));
        var firstTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1001L }, 1, CancellationToken.None);
        await backend.WaitForCallAsync(1);
        var queuedTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1002L }, 2, CancellationToken.None);

        try
        {
            // The wait is deliberately longer than the injected deadline. It
            // is only a bounded watchdog: this test does not assert elapsed
            // duration, only that a queued call remains queued.
            await Task.Delay(TimeSpan.FromMilliseconds(300)).WaitAsync(TestWait);
            Assert.False(queuedTask.IsCompleted,
                "A queued call must not receive a deadline before it acquires the backend lifetime.");
            Assert.Equal(1, backend.CallCount);

            firstRelease.TrySetResult(null);
            await Assert.ThrowsAsync<CaptureLimitationException>(() => firstTask.WaitAsync(TestWait));
            var secondCall = await backend.WaitForCallAsync(2);
            await queuedTask.WaitAsync(TestWait);
            Assert.False(secondCall.TokenWasCanceledOnEntry);
        }
        finally
        {
            firstRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(queuedTask);
        }
    }

    [Fact]
    public async Task Owned_window_snapshot_is_copied_before_waiting_for_the_lifetime()
    {
        var firstRelease = NewGate();
        var backend = new ControlledBackend(async (number, call, _) =>
        {
            if (number == 1)
                await firstRelease.Task;

            return CreateFrame(call);
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromSeconds(5));
        var firstTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1001L }, 1, CancellationToken.None);
        await backend.WaitForCallAsync(1);
        var ownedWindows = new List<long> { 2001L };
        var queuedTask = capture.CaptureAsync(ValidDisplay, ValidTarget, ownedWindows, 2, CancellationToken.None);

        try
        {
            ownedWindows[0] = 2999L;
            ownedWindows.Add(3000L);
            firstRelease.TrySetResult(null);

            await firstTask.WaitAsync(TestWait);
            var secondCall = await backend.WaitForCallAsync(2);
            await queuedTask.WaitAsync(TestWait);
            Assert.Equal(new[] { 2001L }, secondCall.OwnedWindows);
        }
        finally
        {
            firstRelease.TrySetResult(null);
            await ObserveSafelyAsync(firstTask);
            await ObserveSafelyAsync(queuedTask);
        }
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_deadline_and_caller_cancel_overlap()
    {
        var cleanupRelease = NewGate();
        var callerCancellation = new CancellationTokenSource();
        var backend = new ControlledBackend(async (_, call, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                call.CancellationObserved.TrySetResult(true);
                await cleanupRelease.Task;
                call.CleanupCompleted.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("The overlap backend must not return a frame.");
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromMilliseconds(100));
        var captureTask = capture.CaptureAsync(
            ValidDisplay,
            ValidTarget,
            new[] { 1001L },
            1,
            callerCancellation.Token);

        try
        {
            var call = await backend.WaitForCallAsync(1);
            await call.CancellationObserved.Task.WaitAsync(TestWait);
            callerCancellation.Cancel();
            cleanupRelease.TrySetResult(null);

            var error = await Assert.ThrowsAsync<OperationCanceledException>(() => captureTask);
            Assert.IsNotType<CaptureLimitationException>(error);
            Assert.Equal(callerCancellation.Token, error.CancellationToken);
            Assert.True(call.CleanupCompleted.Task.IsCompletedSuccessfully);
        }
        finally
        {
            cleanupRelease.TrySetResult(null);
            await ObserveSafelyAsync(captureTask);
            callerCancellation.Dispose();
        }
    }

    [Fact]
    public async Task Late_frame_from_backend_ignoring_cancellation_is_rejected()
    {
        var lateFrameRelease = NewGate();
        var backend = new ControlledBackend(async (_, call, cancellationToken) =>
        {
            using var registration = cancellationToken.Register(() => call.CancellationObserved.TrySetResult(true));
            await lateFrameRelease.Task;
            call.CleanupCompleted.TrySetResult(true);
            return CreateFrame(call);
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromMilliseconds(100));
        var captureTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1001L }, 1, CancellationToken.None);

        try
        {
            var call = await backend.WaitForCallAsync(1);
            await call.CancellationObserved.Task.WaitAsync(TestWait);
            lateFrameRelease.TrySetResult(null);

            var error = await Assert.ThrowsAsync<CaptureLimitationException>(() => captureTask);
            Assert.Contains("capture", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(call.CleanupCompleted.Task.IsCompletedSuccessfully);
        }
        finally
        {
            lateFrameRelease.TrySetResult(null);
            await ObserveSafelyAsync(captureTask);
        }
    }

    [Fact]
    public async Task Backend_cleanup_failure_never_becomes_success()
    {
        var cleanupRelease = NewGate();
        var backend = new ControlledBackend(async (_, call, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                call.CancellationObserved.TrySetResult(true);
                await cleanupRelease.Task;
                call.CleanupCompleted.TrySetResult(true);
                throw new InvalidOperationException("fixture cleanup failed");
            }

            throw new InvalidOperationException("The cleanup-failure backend must not return a frame.");
        });
        var capture = new ScreenCapture(backend, TimeSpan.FromMilliseconds(100));
        var captureTask = capture.CaptureAsync(ValidDisplay, ValidTarget, new[] { 1001L }, 1, CancellationToken.None);

        try
        {
            var call = await backend.WaitForCallAsync(1);
            await call.CancellationObserved.Task.WaitAsync(TestWait);
            cleanupRelease.TrySetResult(null);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => captureTask);
            Assert.Equal("fixture cleanup failed", error.Message);
            Assert.True(call.CleanupCompleted.Task.IsCompletedSuccessfully);
        }
        finally
        {
            cleanupRelease.TrySetResult(null);
            await ObserveSafelyAsync(captureTask);
        }
    }

    [Fact]
    public async Task Invalid_capture_inputs_are_rejected_before_backend()
    {
        var cases = new[]
        {
            (Name: "empty owned-window list", Display: ValidDisplay, Target: ValidTarget,
                OwnedWindows: Array.Empty<long>(), Expected: typeof(CaptureLimitationException)),
            (Name: "zero owned-window handle", Display: ValidDisplay, Target: ValidTarget,
                OwnedWindows: new[] { 0L }, Expected: typeof(CaptureLimitationException)),
            (Name: "owned target", Display: ValidDisplay, Target: ValidTarget,
                OwnedWindows: new[] { ValidTarget.Handle }, Expected: typeof(CaptureLimitationException)),
            (Name: "zero display width", Display: ValidDisplay with { PixelBounds = new RectD(0, 0, 0, 1080) },
                Target: ValidTarget, OwnedWindows: new[] { 1001L }, Expected: typeof(ArgumentException)),
            (Name: "nonfinite display scale", Display: ValidDisplay with { Scale = double.NaN },
                Target: ValidTarget, OwnedWindows: new[] { 1001L }, Expected: typeof(ArgumentException)),
            (Name: "zero target width", Display: ValidDisplay,
                Target: ValidTarget with { Bounds = new RectD(20, 30, 0, 600) },
                OwnedWindows: new[] { 1001L }, Expected: typeof(CaptureLimitationException))
        };

        foreach (var testCase in cases)
        {
            var backend = new ControlledBackend(static (_, _, _) =>
                Task.FromException<CapturedFrame>(new InvalidOperationException("backend must not be called")));
            var capture = new ScreenCapture(backend, TimeSpan.FromSeconds(1));
            var operation = capture.CaptureAsync(
                testCase.Display,
                testCase.Target,
                testCase.OwnedWindows,
                generation: 1,
                CancellationToken.None);

            if (testCase.Expected == typeof(CaptureLimitationException))
                await Assert.ThrowsAsync<CaptureLimitationException>(() => operation);
            else
                await Assert.ThrowsAsync<ArgumentException>(() => operation);

            Assert.Equal(0, backend.CallCount);
        }
    }

    [Fact]
    public void Timeout_bounds_are_rejected_at_the_public_constructor()
    {
        var backend = new ControlledBackend(static (_, _, _) =>
            Task.FromException<CapturedFrame>(new InvalidOperationException("backend must not be called")));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScreenCapture(backend, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ScreenCapture(backend, TimeSpan.FromMilliseconds(4_294_967_295d)));
    }

    private static readonly DisplayInfo ValidDisplay =
        new("display-1", "Fixture display", new RectD(0, 0, 1920, 1080), 1);

    private static readonly WindowIdentity ValidTarget =
        new(1234, 5678, "FixtureEditor", new RectD(20, 30, 800, 600));

    private static TaskCompletionSource<object?> NewGate() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static CapturedFrame CreateFrame(BackendCall call) =>
        new(
            new ScreenContext(
                Guid.NewGuid(),
                call.Generation,
                call.Display,
                1920,
                1080,
                call.Target,
                DateTimeOffset.UtcNow),
            new byte[] { 1, 2, 3 });

    private static async Task ObserveSafelyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(TestWait);
        }
        catch
        {
            // Preserve the primary assertion while keeping every fake
            // backend lifetime bounded during failure cleanup.
        }
    }

    private sealed class ControlledBackend : IFrameCaptureBackend
    {
        private readonly Func<int, BackendCall, CancellationToken, Task<CapturedFrame>> _behavior;
        private readonly TaskCompletionSource<BackendCall>[] _started =
            Enumerable.Range(0, 8)
                .Select(_ => new TaskCompletionSource<BackendCall>(TaskCreationOptions.RunContinuationsAsynchronously))
                .ToArray();
        private int _callCount;
        private int _sequence;

        public ControlledBackend(Func<int, BackendCall, CancellationToken, Task<CapturedFrame>> behavior)
        {
            _behavior = behavior;
        }

        public int CallCount => Volatile.Read(ref _callCount);

        public int NextEventSequence() => Interlocked.Increment(ref _sequence);

        public Task<BackendCall> WaitForCallAsync(int number) =>
            _started[number - 1].Task.WaitAsync(TestWait);

        public Task<CapturedFrame> CaptureAsync(
            DisplayInfo display,
            WindowIdentity target,
            IReadOnlyList<long> ownedWindows,
            long generation,
            CancellationToken cancellationToken)
        {
            var number = Interlocked.Increment(ref _callCount);
            var call = new BackendCall(
                display,
                target,
                ownedWindows.ToArray(),
                generation,
                cancellationToken,
                Interlocked.Increment(ref _sequence));
            if (number <= _started.Length)
                _started[number - 1].TrySetResult(call);

            return _behavior(number, call, cancellationToken);
        }
    }

    private sealed class BackendCall
    {
        public BackendCall(
            DisplayInfo display,
            WindowIdentity target,
            long[] ownedWindows,
            long generation,
            CancellationToken cancellationToken,
            int startSequence)
        {
            Display = display;
            Target = target;
            OwnedWindows = ownedWindows;
            Generation = generation;
            CancellationToken = cancellationToken;
            StartSequence = startSequence;
        }

        public DisplayInfo Display { get; }
        public WindowIdentity Target { get; }
        public long[] OwnedWindows { get; }
        public long Generation { get; }
        public CancellationToken CancellationToken { get; }
        public int StartSequence { get; }
        public int CleanupSequence { get; set; }
        public int CompletionSequence { get; set; }
        public bool TokenWasCanceledOnEntry { get; set; }
        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
