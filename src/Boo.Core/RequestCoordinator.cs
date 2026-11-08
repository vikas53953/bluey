namespace Boo.Core;

public sealed class RequestCoordinator : IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _currentCancellation;
    private long _generation;
    private bool _stopped;
    private bool _disposed;

    public RequestLease Begin()
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            CancelCurrent();
            _generation = checked(_generation + 1);
            _currentCancellation = new CancellationTokenSource();
            _stopped = false;
            return new RequestLease(_generation, _currentCancellation.Token);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _stopped = true;
            CancelCurrent();
        }
    }

    public bool IsCurrent(long generation)
    {
        lock (_gate)
        {
            return IsCurrentUnderLock(generation);
        }
    }

    public bool TryCommit(long generation, Action effect)
    {
        ArgumentNullException.ThrowIfNull(effect);
        lock (_gate)
        {
            if (!IsCurrentUnderLock(generation))
            {
                return false;
            }

            // Keep Stop and the effect on one serialized boundary.  A commit
            // that has entered this section is allowed to finish; Stop then
            // cancels the generation and no later commit can enter.
            effect();
            return true;
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

            _stopped = true;
            CancelCurrent();
            _disposed = true;
        }
    }

    private bool IsCurrentUnderLock(long generation) =>
        !_disposed && !_stopped && generation == _generation &&
        _currentCancellation is not null && !_currentCancellation.IsCancellationRequested;

    private void CancelCurrent()
    {
        if (_currentCancellation is null)
        {
            return;
        }

        _currentCancellation.Cancel();
        _currentCancellation.Dispose();
        _currentCancellation = null;
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(RequestCoordinator));
        }
    }
}

