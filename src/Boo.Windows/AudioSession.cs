using NAudio.Wave;

namespace Boo.Windows;

/// <summary>
/// Explicitly activated, memory-only mono PCM audio capture and playback.
/// </summary>
public sealed class AudioSession : IDisposable
{
    private static readonly WaveFormat PcmFormat = new(24000, 16, 1);
    private readonly object _gate = new();
    private readonly SynchronizationContext? _context;
    private WaveInEvent? _capture;
    private WaveOutEvent? _playback;
    private BufferedWaveProvider? _playbackBuffer;
    private bool _disposed;
    private Action<byte[]>? _captured;
    private Action<string>? _failed;

    public AudioSession() => _context = SynchronizationContext.Current;

    public event Action<byte[]>? Captured
    {
        add => _captured += value;
        remove => _captured -= value;
    }

    public event Action<string>? Failed
    {
        add => _failed += value;
        remove => _failed -= value;
    }

    public IReadOnlyList<string> InputDevices()
    {
        try
        {
            var names = new List<string>(WaveIn.DeviceCount);
            for (var index = 0; index < WaveIn.DeviceCount; index++)
                names.Add(WaveIn.GetCapabilities(index).ProductName);
            return names;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public void Start(int device = -1)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
        }
        StopCaptureOnly();
        try
        {
            var count = WaveIn.DeviceCount;
            if (count == 0)
                throw new InvalidOperationException("No microphone is available.");
            if (device < 0)
                device = 0;
            if (device >= count)
                throw new ArgumentOutOfRangeException(nameof(device));

            var waveIn = new WaveInEvent
            {
                DeviceNumber = device,
                WaveFormat = PcmFormat,
                BufferMilliseconds = 20
            };
            waveIn.DataAvailable += OnDataAvailable;
            waveIn.RecordingStopped += OnRecordingStopped;
            lock (_gate)
            {
                ThrowIfDisposed();
                _capture = waveIn;
            }
            waveIn.StartRecording();
        }
        catch (Exception)
        {
            StopCaptureOnly();
            PublishFailure("Microphone capture could not start.");
        }
    }

    public void Play(byte[] pcm)
    {
        ArgumentNullException.ThrowIfNull(pcm);
        if (pcm.Length % 2 != 0)
            throw new ArgumentException("PCM16 audio must contain complete samples.", nameof(pcm));
        try
        {
            lock (_gate)
            {
                ThrowIfDisposed();
                if (_playbackBuffer is null)
                {
                    _playbackBuffer = new BufferedWaveProvider(PcmFormat)
                    {
                        DiscardOnBufferOverflow = true,
                        ReadFully = false
                    };
                    _playback = new WaveOutEvent();
                    _playback.Init(_playbackBuffer);
                }
                if (pcm.Length > 0)
                    _playbackBuffer.AddSamples(pcm, 0, pcm.Length);
                _playback!.Play();
            }
        }
        catch (ObjectDisposedException)
        {
            throw;
        }
        catch
        {
            lock (_gate)
            {
                _playback?.Dispose();
                _playback = null;
                _playbackBuffer = null;
            }
            PublishFailure("Audio playback could not start.");
        }
    }

    public void ClearPlayback()
    {
        lock (_gate)
        {
            _playback?.Stop();
            _playbackBuffer?.ClearBuffer();
        }
    }

    public void Stop()
    {
        StopCaptureOnly();
        ClearPlayback();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed)
                return;
            _disposed = true;
        }
        StopCaptureOnly();
        ClearPlayback();
        lock (_gate)
        {
            _playback?.Dispose();
            _playback = null;
            _playbackBuffer = null;
        }
    }

    private void StopCaptureOnly()
    {
        WaveInEvent? capture;
        lock (_gate)
        {
            capture = _capture;
            _capture = null;
        }
        if (capture is null)
            return;
        capture.DataAvailable -= OnDataAvailable;
        capture.RecordingStopped -= OnRecordingStopped;
        try { capture.StopRecording(); }
        catch { /* device removal is reported through the readable lifecycle state */ }
        finally { capture.Dispose(); }
    }

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(sender, _capture) || args.BytesRecorded <= 0)
                return;
        }
        var bytes = new byte[args.BytesRecorded];
        Buffer.BlockCopy(args.Buffer, 0, bytes, 0, args.BytesRecorded);
        PublishCaptured(bytes);
    }

    private void OnRecordingStopped(object? sender, StoppedEventArgs args)
    {
        if (args.Exception is not null)
            PublishFailure("Microphone capture stopped unexpectedly.");
    }

    private void PublishFailure(string message) => Publish(_failed, message);

    private void PublishCaptured(byte[] bytes)
    {
        var handlers = _captured;
        if (handlers is null)
            return;
        foreach (var handler in handlers.GetInvocationList())
        {
            try { ((Action<byte[]>)handler)(bytes); }
            catch { }
        }
    }

    private void Publish<T>(Action<T>? handlers, T value)
    {
        if (handlers is null)
            return;
        void Invoke()
        {
            try { handlers(value); }
            catch { /* UI/event subscribers cannot break audio lifecycle */ }
        }
        if (_context is null)
            Invoke();
        else
            _context.Post(_ => Invoke(), null);
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(AudioSession));
    }
}

