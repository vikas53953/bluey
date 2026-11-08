using System.IO;
using Boo.Core;
using NAudio.Wave;

namespace Boo.Windows;

/// <summary>
/// Plays Boo's chirp (ChirpSynth) without blocking the caller. Every request goes through
/// <see cref="ChirpPolicy"/>: muted, listening or within the 2 s cooldown means silence.
/// Playback failures (no output device, driver errors) are swallowed: a chirp is never worth
/// interrupting the user over.
/// </summary>
public sealed class ChirpPlayer
{
    private static readonly Lazy<byte[]> Wav = new(() => ChirpSynth.RenderWav(ChirpKind.Nudge));
    private readonly ChirpPolicy _policy = new();
    private readonly Func<DateTimeOffset> _clock;

    public ChirpPlayer(Func<DateTimeOffset>? clock = null) => _clock = clock ?? (() => DateTimeOffset.UtcNow);

    public bool PlayNudge(bool chirpsEnabled, bool microphoneListening) =>
        Play(ChirpKind.Nudge, chirpsEnabled, microphoneListening);

    public bool PlaySessionEnd(bool chirpsEnabled, bool microphoneListening) =>
        Play(ChirpKind.SessionEnd, chirpsEnabled, microphoneListening);

    private bool Play(ChirpKind kind, bool chirpsEnabled, bool microphoneListening)
    {
        _ = kind; // Both kinds share the same rising chirp by design.
        if (!_policy.TryPlay(chirpsEnabled, microphoneListening, _clock()))
            return false;

        WaveOutEvent? output = null;
        WaveFileReader? reader = null;
        try
        {
            reader = new WaveFileReader(new MemoryStream(Wav.Value, writable: false));
            output = new WaveOutEvent();
            var ownedOutput = output;
            var ownedReader = reader;
            output.PlaybackStopped += (_, _) =>
            {
                ownedOutput.Dispose();
                ownedReader.Dispose();
            };
            output.Init(reader);
            output.Play();
            return true;
        }
        catch (Exception)
        {
            output?.Dispose();
            reader?.Dispose();
            return false;
        }
    }
}
