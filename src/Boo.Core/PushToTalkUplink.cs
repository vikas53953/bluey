using System.Threading.Channels;

namespace Boo.Core;

/// <summary>
/// Microphone frames for one push-to-talk turn. Frames captured before the realtime connection is ready are
/// buffered (up to a cap) and sent in order once <see cref="Attach"/> supplies the transport. Sends are never
/// cancelled mid-frame (cancelling a WebSocket send aborts the socket); <see cref="Discard"/> only stops between frames.
/// Thread-safe: <see cref="Append"/> is called from the audio thread.
/// </summary>
public sealed class PushToTalkUplink
{
    /// <summary>About 130 s of 24 kHz mono PCM16, a little above the hold-to-talk safety cap.</summary>
    public const long DefaultMaxBufferedBytes = 24_000L * 2 * 130;

    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions { SingleReader = true });
    private readonly TaskCompletionSource<Task> _pump = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly long _maxBufferedBytes;
    private long _bufferedBytes;
    private long _sentBytes;
    private volatile bool _discarded;
    private int _attached;

    public PushToTalkUplink(long maxBufferedBytes = DefaultMaxBufferedBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxBufferedBytes);
        _maxBufferedBytes = maxBufferedBytes;
    }

    /// <summary>Bytes handed to the transport so far.</summary>
    public long SentBytes => Interlocked.Read(ref _sentBytes);

    public bool IsAttached => Volatile.Read(ref _attached) != 0;

    /// <summary>Queues one captured frame. Returns false once completed, discarded or over the buffer cap.</summary>
    public bool Append(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (_discarded || pcm.Length == 0)
        {
            return false;
        }

        if (Interlocked.Add(ref _bufferedBytes, pcm.Length) > _maxBufferedBytes)
        {
            Interlocked.Add(ref _bufferedBytes, -pcm.Length);
            return false;
        }

        if (_frames.Writer.TryWrite(pcm))
        {
            return true;
        }

        Interlocked.Add(ref _bufferedBytes, -pcm.Length);
        return false;
    }

    /// <summary>Starts sending queued and future frames, in order, through <paramref name="send"/>. Only the first call counts.</summary>
    public void Attach(Func<byte[], CancellationToken, Task> send)
    {
        ArgumentNullException.ThrowIfNull(send);
        if (Interlocked.Exchange(ref _attached, 1) != 0)
        {
            return;
        }

        _pump.TrySetResult(PumpAsync(send));
    }

    /// <summary>
    /// Stops accepting frames and waits until everything appended has been sent. Returns the bytes sent for the turn,
    /// or 0 when discarded. Waits for <see cref="Attach"/> if the transport isn't ready yet.
    /// </summary>
    public async Task<long> CompleteAsync(CancellationToken cancellationToken)
    {
        _frames.Writer.TryComplete();
        var pump = await _pump.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        await pump.WaitAsync(cancellationToken).ConfigureAwait(false);
        return _discarded ? 0 : SentBytes;
    }

    /// <summary>Drops all unsent frames. Returns a task that completes once no send is in flight.</summary>
    public Task Discard()
    {
        _discarded = true;
        _frames.Writer.TryComplete();
        while (_frames.Reader.TryRead(out _))
        {
        }

        if (Interlocked.Exchange(ref _attached, 1) == 0)
        {
            _pump.TrySetResult(Task.CompletedTask);
        }

        return _pump.Task.IsCompleted ? _pump.Task.Result : Task.CompletedTask;
    }

    private async Task PumpAsync(Func<byte[], CancellationToken, Task> send)
    {
        await Task.Yield();
        while (await _frames.Reader.WaitToReadAsync().ConfigureAwait(false))
        {
            while (!_discarded && _frames.Reader.TryRead(out var frame))
            {
                Interlocked.Add(ref _bufferedBytes, -frame.Length);
                await send(frame, CancellationToken.None).ConfigureAwait(false);
                Interlocked.Add(ref _sentBytes, frame.Length);
            }

            if (_discarded)
            {
                return;
            }
        }
    }
}
