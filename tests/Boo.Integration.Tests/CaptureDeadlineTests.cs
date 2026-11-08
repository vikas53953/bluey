using Boo.Core;
using Boo.Windows;

namespace Boo.Integration.Tests;

public sealed class CaptureDeadlineTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Missing_frame_deadline_cancels_backend_and_waits_for_cleanup_before_limitation()
    {
        var backend = new MissingFrameBackend();
        var capture = new ScreenCapture(backend, TimeSpan.FromMilliseconds(100));
        var captureTask = capture.CaptureAsync(
            new DisplayInfo("display-1", "Fixture display", new RectD(0, 0, 1920, 1080), 1),
            new WindowIdentity(1234, 5678, "FixtureEditor", new RectD(20, 30, 800, 600)),
            new[] { 9876L },
            generation: 41,
            CancellationToken.None);

        try
        {
            var firstSignal = await Task.WhenAny(backend.CancellationObserved.Task, captureTask)
                .WaitAsync(TestWait);
            if (!ReferenceEquals(firstSignal, backend.CancellationObserved.Task))
            {
                await captureTask;
                throw new InvalidOperationException("Capture completed before the backend observed deadline cancellation.");
            }

            Assert.True(backend.CancellationObserved.Task.IsCompletedSuccessfully);
            Assert.False(captureTask.IsCompleted,
                "The public capture call must remain pending while backend cleanup is still running.");
            Assert.False(backend.CleanupCompleted.Task.IsCompleted,
                "The fake backend must still be inside its delayed cleanup phase.");

            backend.ReleaseCleanup();

            var limitation = await Assert.ThrowsAsync<CaptureLimitationException>(() => captureTask);
            Assert.False(string.IsNullOrWhiteSpace(limitation.Message));
            Assert.Contains("capture", limitation.Message, StringComparison.OrdinalIgnoreCase);
            Assert.True(backend.CleanupCompleted.Task.IsCompletedSuccessfully,
                "The caller must observe the limitation only after backend cleanup completes.");
        }
        finally
        {
            // Release the fake cleanup gate before observing/disposing any test
            // state so a failed assertion cannot strand the backend task.
            backend.ReleaseCleanup();
            try
            {
                await captureTask.WaitAsync(TestWait);
            }
            catch
            {
                // Preserve the primary assertion or expected limitation failure.
            }
        }
    }

    private sealed class MissingFrameBackend : IFrameCaptureBackend
    {
        public TaskCompletionSource<bool> CancellationObserved { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource<bool> CleanupCompleted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskCompletionSource<object?> CleanupRelease { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public void ReleaseCleanup() => CleanupRelease.TrySetResult(null);

        public async Task<CapturedFrame> CaptureAsync(
            DisplayInfo display,
            WindowIdentity target,
            IReadOnlyList<long> ownedWindows,
            long generation,
            CancellationToken cancellationToken)
        {
            try
            {
                // No frame ever arrives. The deadline must reach this public
                // OS seam and the fake honors it by leaving frame acquisition.
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                CancellationObserved.TrySetResult(true);

                // Simulate native/session cleanup that cannot finish until the
                // test releases it. The service must keep the call alive here.
                await CleanupRelease.Task;
                CleanupCompleted.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("The missing-frame backend must not produce a frame.");
        }
    }
}
