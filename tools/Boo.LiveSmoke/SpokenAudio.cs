namespace Boo.LiveSmoke;

/// <summary>
/// Measures how much real speech a 16-bit mono PCM clip holds: the total length of 20 ms windows whose loudness (RMS)
/// is above a quiet floor. The live voice test uses it to prove the answer was spoken, instead of the clip's total
/// length: a one-word answer ("five") carries about 0.25 s of speech whatever the trailing silence, so a length floor
/// measured silence, failed a correct 0.45 s answer, and would have passed half a second of pure silence.
/// </summary>
public static class SpokenAudio
{
    /// <summary>Windows quieter than this RMS (16-bit samples, about -36 dBFS) count as silence or noise.</summary>
    public const double QuietRms = 500;

    /// <summary>The least speech a spoken answer must hold. Real one-word answers measured 0.26-0.28 s.</summary>
    public const double MinimumSpeechSeconds = 0.15;

    private const double WindowSeconds = 0.020;

    /// <summary>Seconds of 20 ms windows louder than <see cref="QuietRms"/> in little-endian 16-bit mono PCM.</summary>
    public static double SpeechSeconds(ReadOnlySpan<byte> pcm16Mono, int sampleRate)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        var samplesPerWindow = (int)(sampleRate * WindowSeconds);
        var samples = pcm16Mono.Length / 2;
        var loudWindows = 0;
        for (var start = 0; start + samplesPerWindow <= samples; start += samplesPerWindow)
        {
            double sumSquares = 0;
            for (var i = start; i < start + samplesPerWindow; i++)
            {
                double s = (short)(pcm16Mono[2 * i] | (pcm16Mono[2 * i + 1] << 8));
                sumSquares += s * s;
            }
            if (Math.Sqrt(sumSquares / samplesPerWindow) > QuietRms) loudWindows++;
        }
        return loudWindows * WindowSeconds;
    }

    /// <summary>
    /// The live voice test's acceptance rule, kept here so it is tested offline (SpokenAudioTests): the answer counts as
    /// spoken only when it holds at least <see cref="MinimumSpeechSeconds"/> of speech.
    /// </summary>
    public static bool IsSpokenAnswer(ReadOnlySpan<byte> pcm16Mono, int sampleRate) =>
        SpeechSeconds(pcm16Mono, sampleRate) >= MinimumSpeechSeconds;

    /// <summary>
    /// Proves the measure itself before a live result is trusted (fails closed): one second of silence must hold no
    /// speech (the old length rule accepted it), and 0.3 s of a clearly audible tone must count as speech.
    /// Returns null when both hold, or the reason it does not.
    /// </summary>
    public static string? SelfCheck(int sampleRate)
    {
        var silence = new byte[sampleRate * 2];
        if (SpeechSeconds(silence, sampleRate) > 0)
            return "a second of silence was counted as speech";

        var toneSamples = (int)(sampleRate * 0.3);
        var tone = new byte[toneSamples * 2];
        for (var i = 0; i < toneSamples; i++)
        {
            var s = (short)(4000 * Math.Sin(2 * Math.PI * 220 * i / sampleRate));
            tone[2 * i] = (byte)(s & 0xFF);
            tone[2 * i + 1] = (byte)((s >> 8) & 0xFF);
        }
        var toneSpeech = SpeechSeconds(tone, sampleRate);
        if (toneSpeech < MinimumSpeechSeconds)
            return $"0.3 s of an audible tone measured only {toneSpeech:0.00} s of speech";
        return null;
    }
}
