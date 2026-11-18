namespace Boo.Core;

/// <summary>
/// How much real speech a spoken reply's audio (16-bit little-endian mono PCM) holds. The same measure the live voice
/// check uses (tools/Boo.LiveSmoke/SpokenAudio.cs; the two are pinned equal by ReplyAudioParityTests): the total length
/// of 20 ms windows whose loudness (RMS) is above a quiet floor.
/// </summary>
public static class ReplyAudio
{
    /// <summary>Windows quieter than this RMS (about -36 dBFS) are silence or noise.</summary>
    public const double QuietRms = 500;

    /// <summary>Audio whose loudest sample stays below this is digital silence: the provider-fault case of issue #177.</summary>
    public const int ProviderSilencePeak = 50;

    private const double WindowSeconds = 0.020;

    /// <summary>Seconds of 20 ms windows louder than <see cref="QuietRms"/>.</summary>
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

    /// <summary>The loudest absolute sample (0 to 32768).</summary>
    public static int PeakLevel(ReadOnlySpan<byte> pcm16Mono)
    {
        var peak = 0;
        for (var i = 0; i + 1 < pcm16Mono.Length; i += 2)
        {
            var s = Math.Abs((int)(short)(pcm16Mono[i] | (pcm16Mono[i + 1] << 8)));
            if (s > peak) peak = s;
        }
        return peak;
    }
}
