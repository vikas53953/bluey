namespace Boo.Core;

/// <summary>
/// Issue #177: a spoken (realtime) reply whose audio holds no speech while its transcript has words. OpenAI sometimes
/// sends such a reply (digital silence, loudest sample 5 of 32767, with the transcript "Five"); Boo then played silence
/// and the user heard nothing. This watches one reply at a time (its audio chunks and its spoken transcript) and, when
/// the reply is complete, says whether Boo must speak the transcript itself. It decides at most once per reply and
/// never for a muted reply or an empty transcript. It does no I/O; the app performs the recovery.
/// </summary>
public sealed class SilentReplyRecovery
{
    /// <summary>The longest transcript Boo will read aloud in a recovery (a spoken reply is short; this bounds the cost).</summary>
    public const int MaxRecoveryCharacters = 600;

    /// <summary>The most answer audio kept for the check (24 kHz 16-bit mono: about 60 s).</summary>
    public const int MaxAudioBytes = 60 * 48000;

    private readonly List<byte> _audio = new();
    private int _chunks;
    private string _transcript = string.Empty;
    private bool _decided;

    /// <summary>Starts watching a new reply (response.created).</summary>
    public void Begin()
    {
        _audio.Clear();
        _chunks = 0;
        _transcript = string.Empty;
        _decided = false;
    }

    /// <summary>One chunk of the reply's audio, exactly as it arrived (response.output_audio.delta).</summary>
    public void Audio(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _chunks++;
        var room = MaxAudioBytes - _audio.Count;
        if (room > 0)
        {
            _audio.AddRange(chunk.Length <= room ? chunk : chunk[..room]);
        }
    }

    /// <summary>The reply's spoken transcript so far or in full (response.output_audio_transcript.delta / .done).</summary>
    public void Transcript(string? text, bool done)
    {
        if (done)
        {
            _transcript = text ?? string.Empty;
        }
        else
        {
            _transcript += text ?? string.Empty;
        }
    }

    /// <summary>
    /// Called once the reply is complete (response.done): null when nothing needs doing, otherwise what Boo must read
    /// aloud and why.
    /// </summary>
    public SilentReplyDecision? Complete(bool speechMuted, int sampleRate)
    {
        if (_decided)
        {
            return null;
        }
        _decided = true;
        var text = _transcript.Trim();
        if (speechMuted || text.Length == 0)
        {
            return null;
        }
        var audio = _audio.ToArray();
        if (ReplyAudio.SpeechSeconds(audio, sampleRate) > 0)
        {
            return null;
        }
        var peak = ReplyAudio.PeakLevel(audio);
        return new SilentReplyDecision(
            text.Length <= MaxRecoveryCharacters ? text : text[..MaxRecoveryCharacters],
            peak < ReplyAudio.ProviderSilencePeak,
            peak,
            audio.Length / 2.0 / sampleRate,
            _chunks);
    }
}

/// <summary>What a recovery reads aloud, and the measured audio that made it necessary (lengths and levels only).</summary>
public sealed record SilentReplyDecision(string Text, bool ProviderSilence, int Peak, double AudioSeconds, int Chunks);
