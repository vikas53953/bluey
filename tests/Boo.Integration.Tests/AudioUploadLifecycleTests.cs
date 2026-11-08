using System.Collections.Concurrent;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class AudioUploadLifecycleTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Accepted_frames_are_cloned_and_sent_in_order_by_a_single_writer()
    {
        var entered = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var releases = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var completed = Enumerable.Range(0, 3)
            .Select(_ => new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously))
            .ToArray();
        var sentFrames = new ConcurrentQueue<byte[]>();
        var sendCount = 0;
        var activeSends = 0;
        var maximumActiveSends = 0;
        var failureCount = 0;

        async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            var index = Interlocked.Increment(ref sendCount) - 1;
            if (index >= releases.Length)
                throw new InvalidOperationException("unexpected extra audio send");

            var active = Interlocked.Increment(ref activeSends);
            UpdateMaximum(ref maximumActiveSends, active);
            sentFrames.Enqueue(frame.ToArray());
            entered[index].TrySetResult(true);
            try
            {
                await releases[index].Task.WaitAsync(cancellationToken);
            }
            finally
            {
                Interlocked.Decrement(ref activeSends);
                completed[index].TrySetResult(true);
            }
        }

        var queue = new AudioUploadQueue(SendAsync);
        queue.Failed += _ => Interlocked.Increment(ref failureCount);

        try
        {
            var first = CreateFrame(1);
            Assert.True(queue.TryEnqueue(first));
            await entered[0].Task.WaitAsync(Timeout);
            first[0] = 99;

            var second = CreateFrame(2);
            var third = CreateFrame(3);
            Assert.True(queue.TryEnqueue(second));
            Assert.True(queue.TryEnqueue(third));
            second[0] = 98;
            third[0] = 97;

            releases[0].TrySetResult(null);
            await entered[1].Task.WaitAsync(Timeout);
            releases[1].TrySetResult(null);
            await entered[2].Task.WaitAsync(Timeout);
            releases[2].TrySetResult(null);
            await completed[2].Task.WaitAsync(Timeout);

            queue.Stop();
            await queue.Completion.WaitAsync(Timeout);

            var calls = sentFrames.ToArray();
            Assert.Equal(3, calls.Length);
            Assert.Equal(CreateFrame(1), calls[0]);
            Assert.Equal(CreateFrame(2), calls[1]);
            Assert.Equal(CreateFrame(3), calls[2]);
            Assert.Equal(1, Volatile.Read(ref maximumActiveSends));
            Assert.Equal(0, Volatile.Read(ref failureCount));
        }
        finally
        {
            foreach (var release in releases)
                release.TrySetResult(null);
            queue.Stop();
            await DisposeSafelyAsync(queue);
        }
    }

    [Fact]
    public async Task Invalid_frames_are_validated_before_and_after_stop()
    {
        var queue = new AudioUploadQueue(static (_, _) => Task.CompletedTask);

        try
        {
            Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
            Assert.Throws<ArgumentException>(() => queue.TryEnqueue(Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => queue.TryEnqueue(new byte[3]));
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.TryEnqueue(new byte[4_802]));

            queue.Stop();

            Assert.Throws<ArgumentNullException>(() => queue.TryEnqueue(null!));
            Assert.Throws<ArgumentException>(() => queue.TryEnqueue(Array.Empty<byte>()));
            Assert.Throws<ArgumentException>(() => queue.TryEnqueue(new byte[3]));
            Assert.Throws<ArgumentOutOfRangeException>(() => queue.TryEnqueue(new byte[4_802]));
            Assert.False(queue.TryEnqueue(CreateFrame(7)));
        }
        finally
        {
            await DisposeSafelyAsync(queue);
        }
    }

    [Fact]
    public async Task Explicit_stop_cancels_inflight_and_discards_pending_frames_without_failure()
    {
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentFrames = new ConcurrentQueue<byte[]>();
        var failureCount = 0;

        async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            sentFrames.Enqueue(frame);
            entered.TrySetResult(true);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult(true);
                throw;
            }
        }

        var queue = new AudioUploadQueue(SendAsync);
        queue.Failed += _ => Interlocked.Increment(ref failureCount);

        try
        {
            Assert.True(queue.TryEnqueue(CreateFrame(1)));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(queue.TryEnqueue(CreateFrame(2)));
            Assert.True(queue.TryEnqueue(CreateFrame(3)));

            queue.Stop();

            Assert.False(queue.TryEnqueue(CreateFrame(4)));
            await cancelled.Task.WaitAsync(Timeout);
            await queue.Completion.WaitAsync(Timeout);

            Assert.Single(sentFrames);
            Assert.Equal(0, Volatile.Read(ref failureCount));
        }
        finally
        {
            release.TrySetResult(null);
            queue.Stop();
            await DisposeSafelyAsync(queue);
        }
    }

    [Fact]
    public async Task Parent_cancellation_cancels_inflight_and_discards_pending_frames_without_failure()
    {
        var parent = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentFrames = new ConcurrentQueue<byte[]>();
        var failureCount = 0;

        async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            sentFrames.Enqueue(frame);
            entered.TrySetResult(true);
            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                cancelled.TrySetResult(true);
                throw;
            }
        }

        var queue = new AudioUploadQueue(SendAsync, parent.Token);
        queue.Failed += _ => Interlocked.Increment(ref failureCount);

        try
        {
            Assert.True(queue.TryEnqueue(CreateFrame(1)));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(queue.TryEnqueue(CreateFrame(2)));
            Assert.True(queue.TryEnqueue(CreateFrame(3)));

            parent.Cancel();

            Assert.False(queue.TryEnqueue(CreateFrame(4)));
            await cancelled.Task.WaitAsync(Timeout);
            await queue.Completion.WaitAsync(Timeout);

            Assert.Single(sentFrames);
            Assert.Equal(0, Volatile.Read(ref failureCount));
        }
        finally
        {
            release.TrySetResult(null);
            queue.Stop();
            await DisposeSafelyAsync(queue);
            parent.Dispose();
        }
    }

    [Fact]
    public async Task Transport_failure_is_sanitized_and_published_once()
    {
        const string rawProviderError = "provider-secret-raw-error";
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var failureRaised = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sentFrames = new ConcurrentQueue<byte[]>();
        var failureCount = 0;

        async Task SendAsync(byte[] frame, CancellationToken cancellationToken)
        {
            sentFrames.Enqueue(frame);
            entered.TrySetResult(true);
            await releaseFailure.Task.WaitAsync(cancellationToken);
            throw new InvalidOperationException(rawProviderError);
        }

        var queue = new AudioUploadQueue(SendAsync);
        queue.Failed += message =>
        {
            Interlocked.Increment(ref failureCount);
            failureRaised.TrySetResult(message);
        };

        try
        {
            Assert.True(queue.TryEnqueue(CreateFrame(1)));
            await entered.Task.WaitAsync(Timeout);
            Assert.True(queue.TryEnqueue(CreateFrame(2)));
            Assert.True(queue.TryEnqueue(CreateFrame(3)));

            releaseFailure.TrySetResult(null);
            var message = await failureRaised.Task.WaitAsync(Timeout);
            await queue.Completion.WaitAsync(Timeout);

            Assert.Equal(1, Volatile.Read(ref failureCount));
            Assert.Equal("Audio upload failed.", message);
            Assert.DoesNotContain(rawProviderError, message, StringComparison.Ordinal);
            Assert.False(queue.TryEnqueue(CreateFrame(4)));
            Assert.Single(sentFrames);
        }
        finally
        {
            releaseFailure.TrySetResult(null);
            queue.Stop();
            await DisposeSafelyAsync(queue);
        }
    }

    [Fact]
    public async Task Pre_cancelled_constructor_and_repeated_stop_dispose_terminate_cleanly()
    {
        var parent = new CancellationTokenSource();
        parent.Cancel();
        var sendCount = 0;
        var failureCount = 0;
        var queue = new AudioUploadQueue((_, _) =>
        {
            Interlocked.Increment(ref sendCount);
            return Task.CompletedTask;
        }, parent.Token);
        queue.Failed += _ => Interlocked.Increment(ref failureCount);

        try
        {
            Assert.False(queue.TryEnqueue(CreateFrame(1)));
            queue.Stop();
            queue.Stop();
            await queue.Completion.WaitAsync(Timeout);

            await queue.DisposeAsync().AsTask().WaitAsync(Timeout);
            await queue.DisposeAsync().AsTask().WaitAsync(Timeout);

            Assert.Equal(0, Volatile.Read(ref sendCount));
            Assert.Equal(0, Volatile.Read(ref failureCount));
        }
        finally
        {
            await DisposeSafelyAsync(queue);
            parent.Dispose();
        }
    }

    [Fact]
    public async Task Stop_does_not_wait_for_a_blocked_transport_cancellation_callback()
    {
        var transportEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportToken = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCancellation = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseTransport = new TaskCompletionSource<object?>(TaskCreationOptions.RunContinuationsAsynchronously);
        var transportCancelled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationTokenRegistration cancellationRegistration = default;
        var registrationCreated = false;

        async Task SendAsync(byte[] _, CancellationToken cancellationToken)
        {
            transportToken.TrySetResult(cancellationToken);
            transportEntered.TrySetResult(true);
            try
            {
                await releaseTransport.Task.WaitAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                transportCancelled.TrySetResult(true);
                throw;
            }
        }

        var queue = new AudioUploadQueue(SendAsync);
        Task? stopTask = null;

        try
        {
            Assert.True(queue.TryEnqueue(CreateFrame(1)));
            await transportEntered.Task.WaitAsync(Timeout);
            var token = await transportToken.Task.WaitAsync(Timeout);
            cancellationRegistration = token.Register(() =>
            {
                cancellationEntered.TrySetResult(true);
                releaseCancellation.Task.GetAwaiter().GetResult();
            });
            registrationCreated = true;

            var stopStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var stopReturned = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            stopTask = Task.Run(() =>
            {
                stopStarted.TrySetResult(true);
                queue.Stop();
                stopReturned.TrySetResult(true);
            });

            await stopStarted.Task.WaitAsync(Timeout);
            await cancellationEntered.Task.WaitAsync(Timeout);
            await stopReturned.Task.WaitAsync(Timeout);
            releaseCancellation.TrySetResult(null);
            await transportCancelled.Task.WaitAsync(Timeout);
            await queue.Completion.WaitAsync(Timeout);
        }
        finally
        {
            releaseCancellation.TrySetResult(null);
            releaseTransport.TrySetResult(null);
            if (stopTask is not null)
                await AwaitSafelyAsync(stopTask);
            if (registrationCreated)
            {
                try { cancellationRegistration.Dispose(); }
                catch { /* preserve the primary assertion or lifecycle failure */ }
            }
            await DisposeSafelyAsync(queue);
        }
    }

    private static byte[] CreateFrame(byte marker)
    {
        var frame = new byte[960];
        Array.Fill(frame, marker);
        return frame;
    }

    private static void UpdateMaximum(ref int target, int value)
    {
        while (true)
        {
            var current = Volatile.Read(ref target);
            if (value <= current || Interlocked.CompareExchange(ref target, value, current) == current)
                return;
        }
    }

    private static async Task DisposeSafelyAsync(AudioUploadQueue queue)
    {
        try
        {
            await queue.DisposeAsync().AsTask().WaitAsync(Timeout);
        }
        catch
        {
            // Preserve the primary assertion or lifecycle failure.
        }
    }

    private static async Task AwaitSafelyAsync(Task task)
    {
        try
        {
            await task.WaitAsync(Timeout);
        }
        catch
        {
            // Preserve the primary assertion or lifecycle failure.
        }
    }
}
