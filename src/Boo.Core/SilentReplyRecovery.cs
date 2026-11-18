namespace Boo.Core;

/// <summary>
/// Issue #177: a spoken (realtime) reply whose audio holds no speech while its transcript has words. OpenAI sometimes
/// sends such a reply (digital silence, loudest sample 5 of 32767, with the transcript "Five"); Boo then played silence
/// and the user heard nothing. This watches one reply at a time and, when the reply is complete, says whether Boo must
/// speak the transcript itself.
/// "No speech" means no 20 ms window of the whole reply is louder than <see cref="ReplyAudio.QuietRms"/>: the speech is
/// measured as the chunks arrive (windows and samples carried across chunk boundaries), over every chunk, so real speech
/// anywhere in the reply prevents a recovery. This is a loudness rule, not a judgement of what a person can hear: a reply
/// with any window above the floor is left alone. It decides at most once per reply, never for a muted reply or an empty
/// transcript, and does no I/O; the app performs the recovery.
/// </summary>
public sealed class SilentReplyRecovery
{
    /// <summary>The longest transcript Boo will read aloud in a recovery (a spoken reply is short; this bounds the cost).</summary>
    public const int MaxRecoveryCharacters = 600;

    /// <summary>The most transcript text kept from partial (delta) updates; anything beyond can never be read aloud anyway.</summary>
    public const int MaxTranscriptCharacters = 4 * MaxRecoveryCharacters;

    private const double WindowSeconds = 0.020;

    private readonly int _sampleRate;
    private readonly int _windowBytes;
    private readonly byte[] _carry;
    private int _carryLength;
    private long _bytes;
    private int _loudWindows;
    private int _peak;
    private int _chunks;
    private string _transcript = string.Empty;
    private bool _decided;

    public SilentReplyRecovery(int sampleRate = 24000)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        _sampleRate = sampleRate;
        _windowBytes = (int)(sampleRate * WindowSeconds) * 2;
        _carry = new byte[_windowBytes];
    }

    /// <summary>Starts watching a new reply (response.created).</summary>
    public void Begin()
    {
        _carryLength = 0;
        _bytes = 0;
        _loudWindows = 0;
        _peak = 0;
        _chunks = 0;
        _transcript = string.Empty;
        _decided = false;
    }

    /// <summary>One chunk of the reply's audio, exactly as it arrived (response.output_audio.delta). Every chunk counts.</summary>
    public void Audio(byte[] chunk)
    {
        ArgumentNullException.ThrowIfNull(chunk);
        _chunks++;
        _bytes += chunk.Length;
        foreach (var b in chunk)
        {
            _carry[_carryLength++] = b;
            if ((_carryLength & 1) == 0)
            {
                var sample = Math.Abs((int)(short)(_carry[_carryLength - 2] | (_carry[_carryLength - 1] << 8)));
                if (sample > _peak) _peak = sample;
            }
            if (_carryLength == _windowBytes)
            {
                if (ReplyAudio.SpeechSeconds(_carry, _sampleRate) > 0) _loudWindows++;
                _carryLength = 0;
            }
        }
    }

    /// <summary>The reply's spoken transcript so far or in full (response.output_audio_transcript.delta / .done).</summary>
    public void Transcript(string? text, bool done)
    {
        var next = done ? text ?? string.Empty : _transcript + (text ?? string.Empty);
        _transcript = next.Length <= MaxTranscriptCharacters ? next : next[..MaxTranscriptCharacters];
    }

    /// <summary>
    /// Called once the reply is complete (response.done): null when nothing needs doing, otherwise what Boo must read
    /// aloud and why. <paramref name="sampleRate"/> must be the rate this watcher was made for.
    /// </summary>
    public SilentReplyDecision? Complete(bool speechMuted, int sampleRate)
    {
        if (sampleRate != _sampleRate) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (_decided)
        {
            return null;
        }
        _decided = true;
        var text = _transcript.Trim();
        if (speechMuted || text.Length == 0 || _loudWindows > 0)
        {
            return null;
        }
        return new SilentReplyDecision(
            text.Length <= MaxRecoveryCharacters ? text : text[..MaxRecoveryCharacters],
            _peak < ReplyAudio.ProviderSilencePeak,
            _peak,
            _bytes / 2.0 / _sampleRate,
            _chunks);
    }
}

/// <summary>What a recovery reads aloud, and the measured audio that made it necessary (lengths and levels only).</summary>
public sealed record SilentReplyDecision(string Text, bool ProviderSilence, int Peak, double AudioSeconds, int Chunks);
