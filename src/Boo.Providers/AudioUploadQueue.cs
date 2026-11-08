namespace Boo.Providers;

public sealed class AudioUploadQueue : IAsyncDisposable
{
    private const int MaxPendingFrames = 8;
    private const int MaxFrameBytes = 4_800;
    private const string SafeFailureMessage = "Audio upload failed.";

    private readonly object _gate = new();
    private readonly Queue<byte[]> _pending = new();
    private readonly Func<byte[], CancellationToken, Task> _send;
    private readonly SemaphoreSlim _wake = new(0);
    private readonly CancellationTokenSource _transportCancellation = new();
    private readonly TaskCompletionSource<bool> _cancellationCompleted =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Task _completion;
    private CancellationTokenRegistration _parentRegistration;
    private Action<string>? _failed;
    private Task? _disposeTask;
    private bool _stopped;
    private bool _failureRaised;
    private bool _cancellationStarted;

    /// <param name="send">An asynchronous transport that returns promptly and honors cancellation; send starts serialize with Stop.</param>
    public AudioUploadQueue(Func<byte[], CancellationToken, Task> send, CancellationToken cancellationToken = default)
    {
        _send = send ?? throw new ArgumentNullException(nameof(send));
        _completion = RunAsync();
        _parentRegistration = cancellationToken.Register(static state => ((AudioUploadQueue)state!).Stop(), this);
    }

    public event Action<string>? Failed
    {
        add
        {
            lock (_gate)
                _failed += value;
        }
        remove
        {
            lock (_gate)
                _failed -= value;
        }
    }

    public Task Completion => _completion;

    public bool TryEnqueue(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length == 0 || (pcm.Length & 1) != 0)
            throw new ArgumentException("PCM16 audio must contain complete samples.", nameof(pcm));
        if (pcm.Length > MaxFrameBytes)
            throw new ArgumentOutOfRangeException(nameof(pcm), "PCM16 audio frames are limited to 4,800 bytes.");

        var accepted = false;
        var cancel = false;
        var publishFailure = false;
        lock (_gate)
        {
            if (_stopped)
                return false;

            if (_pending.Count >= MaxPendingFrames)
            {
                _stopped = true;
                _pending.Clear();
                cancel = BeginCancellationLocked();
                publishFailure = !_failureRaised;
                _failureRaised = true;
            }
            else
            {
                _pending.Enqueue(pcm.ToArray());
                accepted = true;
            }
        }

        WakeWorker();
        if (cancel)
            CancelTransport();
        if (publishFailure)
            PublishFailure();
        return accepted;
    }

    public void Stop()
    {
        var cancel = false;
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            _pending.Clear();
            cancel = BeginCancellationLocked();
        }

        WakeWorker();
        if (cancel)
            CancelTransport();
    }

    public ValueTask DisposeAsync()
    {
        TaskCompletionSource<bool>? owner = null;
        Task disposal;
        lock (_gate)
        {
            if (_disposeTask is null)
            {
                owner = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _disposeTask = owner.Task;
            }
            disposal = _disposeTask;
        }
        if (owner is not null)
            _ = DisposeCoreAsync(owner);
        return new ValueTask(disposal);
    }

    private async Task DisposeCoreAsync(TaskCompletionSource<bool> completion)
    {
        try
        {
            Stop();
            await _completion.ConfigureAwait(false);
            await _cancellationCompleted.Task.ConfigureAwait(false);
            await _parentRegistration.DisposeAsync().ConfigureAwait(false);
            _transportCancellation.Dispose();
            _wake.Dispose();
            completion.TrySetResult(true);
        }
        catch (Exception error)
        {
            completion.TrySetException(error);
        }
    }

    private async Task RunAsync()
    {
        try
        {
            while (true)
            {
                Task? wakeTask = null;
                Task? sendTask = null;
                lock (_gate)
                {
                    if (_stopped)
                        return;

                    if (_pending.Count == 0)
                    {
                        wakeTask = _wake.WaitAsync();
                    }
                    else
                    {
                        var frame = _pending.Dequeue();
                        try
                        {
                            sendTask = _send(frame, _transportCancellation.Token)
                                ?? throw new InvalidOperationException("The audio transport returned no task.");
                        }
                        catch (Exception exception)
                        {
                            sendTask = Task.FromException(exception);
                        }
                    }
                }

                if (wakeTask is not null)
                {
                    await wakeTask.ConfigureAwait(false);
                    continue;
                }

                try
                {
                    await sendTask!.ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_transportCancellation.IsCancellationRequested)
                {
                }
                catch
                {
                    FailTransport();
                }
            }
        }
        catch (OperationCanceledException) when (_transportCancellation.IsCancellationRequested)
        {
        }
        catch
        {
            FailTransport();
        }
    }

    private void FailTransport()
    {
        var cancel = false;
        var publishFailure = false;
        lock (_gate)
        {
            if (_stopped)
                return;

            _stopped = true;
            _pending.Clear();
            cancel = BeginCancellationLocked();
            publishFailure = !_failureRaised;
            _failureRaised = true;
        }

        WakeWorker();
        if (cancel)
            CancelTransport();
        if (publishFailure)
            PublishFailure();
    }

    private bool BeginCancellationLocked()
    {
        if (_cancellationStarted)
            return false;
        _cancellationStarted = true;
        return true;
    }

    private void CancelTransport() => _ = CancelTransportAsync();

    private async Task CancelTransportAsync()
    {
        try
        {
            await _transportCancellation.CancelAsync().ConfigureAwait(false);
        }
        catch
        {
        }
        finally
        {
            _cancellationCompleted.TrySetResult(true);
        }
    }

    private void WakeWorker()
    {
        try { _wake.Release(); }
        catch (ObjectDisposedException) { }
    }

    private void PublishFailure()
    {
        Action<string>? handlers;
        lock (_gate)
            handlers = _failed;
        if (handlers is null)
            return;

        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<string>)handler)(SafeFailureMessage); }
            catch { }
        }
    }
}
