using System.IO;
using System.Text.Json;
using Boo.Core;
using NAudio.Wave;

namespace Boo.Windows;

/// <summary>Opt-in synthetic CI devices; the normal AudioSession still owns upload and playback buffering.</summary>
public sealed class HostedVoiceAudio : IDisposable
{
    public const string InputVariable = HostedVoiceOptions.InputVariable;
    private readonly byte[] _pcm;
    private readonly string _evidence;
    private readonly object _gate = new();
    private System.Threading.Timer? _capture, _playback;
    private long _inputBytes, _renderedBytes, _nonzeroBytes;
    private int _starts, _offset;
    private bool _disposed;

    internal HostedVoiceAudio(string input, string evidence)
    {
        using var wave = new WaveFileReader(input);
        if (wave.WaveFormat.Encoding != WaveFormatEncoding.Pcm || wave.WaveFormat.SampleRate != 24000
            || wave.WaveFormat.BitsPerSample != 16 || wave.WaveFormat.Channels != 1
            || wave.Length < 4800 || wave.Length > 8 * 48000)
            throw new InvalidOperationException("QA voice requires 0.1-8 seconds of 24kHz PCM16 mono WAV.");
        _pcm = new byte[checked((int)wave.Length)];
        wave.ReadExactly(_pcm);
        _evidence = evidence;
    }

    public static HostedVoiceAudio? FromEnvironment() => HostedVoiceOptions.FromEnvironment() is { } options
        ? new HostedVoiceAudio(options.Input, options.Evidence) : null;

    public void Start(Action<byte[]> captured)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (++_starts != 1) throw new InvalidOperationException("QA voice budget permits only one capture per audio session.");
            _capture = new System.Threading.Timer(_ =>
            {
                lock (_gate)
                {
                    if (_disposed || _capture is null) return;
                    var frame = _pcm[_offset..Math.Min(_offset + 960, _pcm.Length)];
                    captured(frame);
                    _inputBytes += frame.Length;
                    _offset += frame.Length;
                    Save();
                    if (_offset == _pcm.Length) StopCapture();
                }
            }, null, 20, 20);
        }
    }

    /// <summary>Consume the same AudioSession.Play buffer; count only bytes actually read by this virtual device.</summary>
    public void StartPlayback(BufferedWaveProvider buffer)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _playback = new System.Threading.Timer(_ =>
            {
                lock (_gate)
                {
                    if (_disposed || _playback is null) return;
                    var bytes = new byte[960];
                    var read = buffer.Read(bytes, 0, bytes.Length);
                    _renderedBytes += read;
                    _nonzeroBytes += bytes.Take(read).Count(b => b != 0);
                    Save();
                }
            }, null, 20, 20);
        }
    }
    public void StopCapture() { lock (_gate) { _capture?.Dispose(); _capture = null; } }
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(_evidence))!);
        File.AppendAllText(_evidence, JsonSerializer.Serialize(new
        {
            inputBytes = _inputBytes, renderedBytes = _renderedBytes, nonzeroBytes = _nonzeroBytes,
            captureStarts = _starts, expectedInputBytes = _pcm.Length,
            output = "virtual BufferedWaveProvider consumer; physical speakers UNVERIFIED"
        }) + "\n");
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            StopCapture();
            _playback?.Dispose();
            _playback = null;
        }
    }
}
