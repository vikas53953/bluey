namespace Boo.Core;

/// <summary>
/// Which moment a chirp marks. Design decision (docs/design/boo-notch-spec.json, chosen.chirp):
/// the chirp plays on "nudge" and on "happy" (session end). Both kinds use the SAME rising
/// two-tone chirp (880 Hz then 1320 Hz); session end is deliberately not a falling or
/// different chirp so the user learns one sound. <see cref="ChirpSynth.Render"/> must return
/// identical samples for both kinds.
/// </summary>
public enum ChirpKind
{
    Nudge,
    SessionEnd
}

/// <summary>
/// Pure PCM synthesiser for Boo's chirp: 16-bit signed mono samples.
/// Layout: tone 1 (880 Hz, 90 ms) + 40 ms digital silence + tone 2 (1320 Hz, 90 ms) = 220 ms.
/// Each tone has a 5 ms linear (or smoother) fade in and fade out so there are no clicks.
/// Segment lengths in samples are sampleRate * ms / 1000 (rounded), and the total length is
/// exactly sampleRate * 220 / 1000 samples for 16000, 44100 and 48000 Hz.
/// Peak absolute amplitude must not exceed volume * short.MaxValue.
/// </summary>
public static class ChirpSynth
{
    public const int DefaultSampleRate = 44100;

    /// <summary>Spec gain: "gain 0.12" (chosen.chirp.spec).</summary>
    public const double DefaultVolume = 0.12;

    public const double FirstToneHz = 880;
    public const double SecondToneHz = 1320;
    public const int ToneMilliseconds = 90;
    public const int GapMilliseconds = 40;
    public const int FadeMilliseconds = 5;
    public const int TotalMilliseconds = ToneMilliseconds + GapMilliseconds + ToneMilliseconds;

    /// <summary>
    /// Renders the chirp. Throws <see cref="ArgumentOutOfRangeException"/> when sampleRate is not
    /// between 8000 and 192000, or volume is NaN, negative or greater than 1.
    /// </summary>
    public static short[] Render(ChirpKind kind, int sampleRate = DefaultSampleRate, double volume = DefaultVolume)
    {
        // Both kinds intentionally render the same rising chirp (see ChirpKind docs).
        _ = kind;
        if (sampleRate is < 8000 or > 192000)
            throw new ArgumentOutOfRangeException(nameof(sampleRate), sampleRate, "Sample rate must be between 8000 and 192000 Hz.");
        if (double.IsNaN(volume) || volume < 0 || volume > 1)
            throw new ArgumentOutOfRangeException(nameof(volume), volume, "Volume must be between 0 and 1.");

        var tone = SamplesFor(sampleRate, ToneMilliseconds);
        var gap = SamplesFor(sampleRate, GapMilliseconds);
        var fade = SamplesFor(sampleRate, FadeMilliseconds);
        var pcm = new short[tone + gap + tone];
        WriteTone(pcm, 0, tone, fade, FirstToneHz, sampleRate, volume);
        WriteTone(pcm, tone + gap, tone, fade, SecondToneHz, sampleRate, volume);
        return pcm;
    }

    private static int SamplesFor(int sampleRate, int milliseconds) =>
        (int)Math.Round(sampleRate * milliseconds / 1000.0, MidpointRounding.AwayFromZero);

    private static void WriteTone(short[] pcm, int start, int length, int fade, double hz, int sampleRate, double volume)
    {
        var amplitude = volume * short.MaxValue;
        var last = length - 1;
        for (var i = 0; i < length; i++)
        {
            // Linear fade in/out reaching exactly zero at the first and last sample.
            var edge = Math.Min(i, last - i);
            var envelope = edge >= fade ? 1.0 : edge / (double)fade;
            var value = amplitude * envelope * Math.Sin(2 * Math.PI * hz * i / sampleRate);
            pcm[start + i] = (short)Math.Clamp(Math.Round(value), -amplitude, amplitude);
        }
    }

    /// <summary>
    /// Same samples as <see cref="Render"/> wrapped in a canonical 44-byte-header RIFF/WAVE
    /// container (PCM format 1, 1 channel, 16 bits, little-endian), suitable for a
    /// System.Media.SoundPlayer stream on Windows.
    /// </summary>
    public static byte[] RenderWav(ChirpKind kind, int sampleRate = DefaultSampleRate, double volume = DefaultVolume)
    {
        var pcm = Render(kind, sampleRate, volume);
        var dataBytes = pcm.Length * 2;
        var wav = new byte[44 + dataBytes];
        var span = wav.AsSpan();
        "RIFF"u8.CopyTo(span);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[4..], wav.Length - 8);
        "WAVE"u8.CopyTo(span[8..]);
        "fmt "u8.CopyTo(span[12..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[16..], 16);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[20..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[22..], 1);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[24..], sampleRate);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[28..], sampleRate * 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[32..], 2);
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[34..], 16);
        "data"u8.CopyTo(span[36..]);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32LittleEndian(span[40..], dataBytes);
        for (var i = 0; i < pcm.Length; i++)
            System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(span[(44 + i * 2)..], pcm[i]);
        return wav;
    }
}

/// <summary>
/// Decides whether a chirp may play now. A chirp plays only when chirps are enabled in
/// settings, the microphone is not listening (hold-to-talk), and at least
/// <see cref="MinimumInterval"/> has passed since the last chirp that actually played.
/// A refused request does not start or extend the cooldown. Exactly 2 s later is allowed.
/// </summary>
public sealed class ChirpPolicy
{
    public static readonly TimeSpan MinimumInterval = TimeSpan.FromSeconds(2);

    /// <summary>Returns true (and records <paramref name="now"/> as the last play) when the chirp may play.</summary>
    public bool TryPlay(bool chirpsEnabled, bool microphoneListening, DateTimeOffset now)
    {
        if (!chirpsEnabled || microphoneListening)
            return false;
        if (_lastPlayed is { } last && now - last < MinimumInterval)
            return false;
        _lastPlayed = now;
        return true;
    }

    private DateTimeOffset? _lastPlayed;
}
