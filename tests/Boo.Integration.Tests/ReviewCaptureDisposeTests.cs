using Boo.Core;
using Boo.Windows;

namespace Boo.Integration.Tests;

/// <summary>
/// Review follow-up: ScreenCapture.Dispose() waits up to two seconds for an in-flight capture, but used to
/// release the shared Direct3D device anyway when the wait timed out, while the backend could still be inside a
/// native readback. The device must only be released once the capture has left the gate.
/// </summary>
public sealed class ReviewCaptureDisposeTests
{
    private static readonly TimeSpan TestWait = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Dispose_during_a_long_capture_defers_backend_disposal_until_the_capture_finishes()
    {
        var backend = new BlockingBackend();
        var capture = new ScreenCapture(backend, TimeSpan.FromSeconds(30));
        var captureTask = capture.CaptureAsync(
            new DisplayInfo("display-1", "Fixture display", new RectD(0, 0, 1920, 1080), 1),
            new WindowIdentity(1234, 5678, "FixtureEditor", new RectD(20, 30, 800, 600)),
            new[] { 9876L },
            generation: 7,
            CancellationToken.None);
        await backend.Entered.Task.WaitAsync(TestWait);

        try
        {
            // Dispose blocks for its two-second grace period on another thread, then gives up.
            var disposeTask = Task.Run(capture.Dispose);
            await disposeTask.WaitAsync(TestWait);

            Assert.False(backend.Disposed, "The backend must not be disposed while a capture is still inside it.");
            Assert.False(captureTask.IsCompleted);

            backend.Release();
            var frame = await captureTask.WaitAsync(TestWait);
            Assert.Equal(7, frame.Context.Generation);
            Assert.True(backend.Disposed, "The finishing capture must dispose the backend that Dispose() handed to it.");
            Assert.Equal(1, backend.DisposeCount);
        }
        finally
        {
            backend.Release();
        }
    }

    [Fact]
    public async Task Dispose_with_no_capture_in_flight_disposes_the_backend_once()
    {
        var backend = new BlockingBackend();
        var capture = new ScreenCapture(backend, TimeSpan.FromSeconds(30));
        capture.Dispose();
        capture.Dispose();
        Assert.Equal(1, backend.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => capture.CaptureAsync(
            new DisplayInfo("display-1", "Fixture display", new RectD(0, 0, 1920, 1080), 1),
            new WindowIdentity(1234, 5678, "FixtureEditor", new RectD(20, 30, 800, 600)),
            new[] { 9876L },
            generation: 1,
            CancellationToken.None));
    }

    private sealed class BlockingBackend : IFrameCaptureBackend, IDisposable
    {
        private readonly TaskCompletionSource<object?> _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _disposeCount;

        public TaskCompletionSource<bool> Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Disposed => Volatile.Read(ref _disposeCount) > 0;

        public int DisposeCount => Volatile.Read(ref _disposeCount);

        public void Release() => _release.TrySetResult(null);

        public async Task<CapturedFrame> CaptureAsync(
            DisplayInfo display,
            WindowIdentity target,
            IReadOnlyList<long> ownedWindows,
            long generation,
            CancellationToken cancellationToken)
        {
            Entered.TrySetResult(true);
            await _release.Task.ConfigureAwait(false);
            if (Disposed)
            {
                throw new InvalidOperationException("The backend was disposed while a capture was still using it.");
            }

            var context = new ScreenContext(Guid.NewGuid(), generation, display, 1920, 1080, target, DateTimeOffset.UtcNow);
            return new CapturedFrame(context, new byte[] { 1, 2, 3 });
        }

        public void Dispose() => Interlocked.Increment(ref _disposeCount);
    }
}
