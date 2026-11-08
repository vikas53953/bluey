using System.Collections.Concurrent;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class AudioBacklogTests
{
    [Fact]
    public async Task Blocked_transport_rejects_ninth_pending_frame_and_terminates_cleanly()
    {
        var transportStarted = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCancelled = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransport = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentFrames = new ConcurrentQueue<byte[]>();
        var failureRaised = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureCount = 0;

        async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            sentFrames.Enqueue(frame);
            transportStarted.TrySetResult(frame);
            try
            {
                await releaseTransport.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                transportCancelled.TrySetResult(null);
                throw;
            }
        }

        var queue = new AudioUploadQueue(SendAsync);
        queue.Failed += message =>
        {
            Interlocked.Increment(ref failureCount);
            failureRaised.TrySetResult(message);
        };

        try
        {
            var firstFrame = CreateFrame(0);
            Assert.True(queue.TryEnqueue(firstFrame));

            var startedFrame = await transportStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(firstFrame, startedFrame);

            for (var index = 0; index < 8; index++)
            {
                Assert.True(queue.TryEnqueue(CreateFrame((byte)(index + 1))), $"pending frame {index + 1} should be accepted");
            }

            Assert.False(queue.TryEnqueue(CreateFrame(9)));

            var failureMessage = await failureRaised.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(string.IsNullOrWhiteSpace(failureMessage));
            Assert.False(queue.TryEnqueue(CreateFrame(10)));

            await transportCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await queue.Completion.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref failureCount));
            var calls = sentFrames.ToArray();
            Assert.Single(calls);
            Assert.Equal(firstFrame, calls[0]);

            releaseTransport.TrySetResult(null);
            await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            releaseTransport.TrySetResult(null);
            await queue.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    private static byte[] CreateFrame(byte marker)
    {
        var frame = new byte[960];
        Array.Fill(frame, marker);
        return frame;
    }
}
