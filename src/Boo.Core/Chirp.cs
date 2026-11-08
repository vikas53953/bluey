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
    public static short[] Render(ChirpKind kind, int sampleRate = DefaultSampleRate, double volume = DefaultVolume) =>
        throw new NotImplementedException();

    /// <summary>
    /// Same samples as <see cref="Render"/> wrapped in a canonical 44-byte-header RIFF/WAVE
    /// container (PCM format 1, 1 channel, 16 bits, little-endian), suitable for a
    /// System.Media.SoundPlayer stream on Windows.
    /// </summary>
    public static byte[] RenderWav(ChirpKind kind, int sampleRate = DefaultSampleRate, double volume = DefaultVolume) =>
        throw new NotImplementedException();
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
    public bool TryPlay(bool chirpsEnabled, bool microphoneListening, DateTimeOffset now) =>
        throw new NotImplementedException();
}
