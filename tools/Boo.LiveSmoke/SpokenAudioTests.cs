using System.IO;

namespace Boo.LiveSmoke;

/// <summary>
/// Offline checks of the live voice test's "was the answer spoken?" rule (<see cref="SpokenAudio"/>). No network, no key,
/// no paid call: they run first in the same live-smoke job and in any mutation run. Each check pins one boundary that a
/// Codex mutation report (PR #48, head 147d051) showed was untested:
/// M2 a window exactly at the quiet floor must not count; M3 quiet noise must not count; M4 too little speech must be
/// rejected; M5 the last complete 20 ms window must be counted.
/// </summary>
public sealed class SpokenAudioTests
{
    private const int Rate = 24000;          // Boo's realtime audio: 24 kHz, 16-bit mono
    private const int Window = Rate / 50;    // 20 ms = 480 samples

    /// <summary>16-bit little-endian PCM holding <paramref name="samples"/> samples of the constant value <paramref name="level"/>.</summary>
    private static byte[] Constant(int samples, short level)
    {
        var pcm = new byte[samples * 2];
        for (var i = 0; i < samples; i++)
        {
            pcm[2 * i] = (byte)(level & 0xFF);
            pcm[2 * i + 1] = (byte)((level >> 8) & 0xFF);
        }
        return pcm;
    }

    /// <summary>Loud (RMS 4000) audio for exactly <paramref name="windows"/> 20 ms windows, then silence.</summary>
    private static byte[] LoudThenSilent(int windows, int silentWindows)
    {
        var loud = Constant(windows * Window, 4000);
        var pcm = new byte[loud.Length + silentWindows * Window * 2];
        loud.CopyTo(pcm, 0);
        return pcm;
    }

    [Fact]
    public void A_window_exactly_at_the_quiet_floor_is_not_speech_and_one_just_above_is()
    {
        // A constant level L has RMS exactly L, so 499 / 500 / 501 sit just below, on and just above the floor (500).
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(Constant(Window, 499), Rate), 6);
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(Constant(Window, 500), Rate), 6);   // M2: ">=" would count this
        Assert.Equal(0.020, SpokenAudio.SpeechSeconds(Constant(Window, 501), Rate), 6);
    }

    [Fact]
    public void Quiet_noise_below_the_floor_is_not_speech()
    {
        // M3: with the floor removed, any non-zero sample would count. One second of faint hiss and of loudish hum
        // still under the floor must both measure zero; all-zero silence alone cannot tell.
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(Constant(Rate, 1), Rate), 6);
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(Constant(Rate, 499), Rate), 6);
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(Constant(Rate, -499), Rate), 6);
    }

    [Fact]
    public void An_answer_is_spoken_only_with_enough_speech()
    {
        // M4: the live test's own acceptance rule. Silence, 0.14 s of speech: rejected; 0.16 s: accepted.
        Assert.False(SpokenAudio.IsSpokenAnswer(new byte[Rate * 2], Rate));
        Assert.False(SpokenAudio.IsSpokenAnswer(LoudThenSilent(windows: 7, silentWindows: 30), Rate));   // 0.14 s
        Assert.True(SpokenAudio.IsSpokenAnswer(LoudThenSilent(windows: 8, silentWindows: 30), Rate));    // 0.16 s
    }

    [Fact]
    public void Every_complete_window_is_counted_including_the_last_and_a_partial_one_is_not()
    {
        // M5: 479 samples are less than one window; 480 is exactly one; 481 is one plus a scrap; 960 is exactly two.
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(Constant(Window - 1, 4000), Rate), 6);
        Assert.Equal(0.020, SpokenAudio.SpeechSeconds(Constant(Window, 4000), Rate), 6);
        Assert.Equal(0.020, SpokenAudio.SpeechSeconds(Constant(Window + 1, 4000), Rate), 6);
        Assert.Equal(0.040, SpokenAudio.SpeechSeconds(Constant(2 * Window, 4000), Rate), 6);
    }

    [Fact]
    public void The_built_in_self_check_passes_on_the_real_measure()
    {
        Assert.Null(SpokenAudio.SelfCheck(Rate));
    }

    private static byte[] Fixture(string name) =>
        LiveVoiceRoundTripTests.ReadPcm24kMono16(Path.Combine(AppContext.BaseDirectory, "Voice", name));

    [Fact]
    public void A_real_spoken_answer_from_OpenAI_passes()
    {
        // OpenAI's spoken "five" from live-ai-smoke run 43575903395: 0.6 s long, loudest sample about 11,900.
        var pcm = Fixture("answer-five-real-speech.wav");
        Assert.True(SpokenAudio.PeakLevel(pcm) > 5000, "the fixture is real speech");
        Assert.True(SpokenAudio.SpeechSeconds(pcm, Rate) >= 0.20, "a spoken one-word answer holds about a quarter second of speech");
        Assert.True(SpokenAudio.IsSpokenAnswer(pcm, Rate));
    }

    [Fact]
    public void A_silent_answer_from_OpenAI_fails_even_when_long_and_with_a_transcript()
    {
        // live-sentinel run 43576162645: the decoded answer audio was 1.55 s, its transcript said "5", and the audio is a
        // flat noise floor (loudest sample 335 of 32767, RMS 20 to 30): no speech at all. The old 0.5 s length rule passed
        // such an answer; this rule must reject it, and so must pure digital silence of the same length.
        var pcm = Fixture("answer-silent-from-provider.wav");
        Assert.True(pcm.Length >= Rate * 2, "the fixture is longer than one second");
        Assert.True(SpokenAudio.PeakLevel(pcm) < 500, "the fixture never gets loud");
        Assert.Equal(0.0, SpokenAudio.SpeechSeconds(pcm, Rate), 6);
        Assert.False(SpokenAudio.IsSpokenAnswer(pcm, Rate));
        Assert.False(SpokenAudio.IsSpokenAnswer(new byte[pcm.Length], Rate));
    }

    /// <summary>Little-endian 16-bit PCM of exactly these samples.</summary>
    private static byte[] Samples(params short[] values)
    {
        var pcm = new byte[values.Length * 2];
        for (var i = 0; i < values.Length; i++)
        {
            pcm[2 * i] = (byte)(values[i] & 0xFF);
            pcm[2 * i + 1] = (byte)((values[i] >> 8) & 0xFF);
        }
        return pcm;
    }

    [Fact]
    public void The_peak_level_counts_negative_samples_by_their_size()
    {
        // Codex mutation M6 on PR #118: a peak that ignores the sign reports quiet for loud negative swings.
        Assert.Equal(32768, SpokenAudio.PeakLevel(Samples(-32768)));
        Assert.Equal(12345, SpokenAudio.PeakLevel(Samples(-12345)));
        Assert.Equal(12345, SpokenAudio.PeakLevel(Samples(12345)));
        Assert.Equal(32768, SpokenAudio.PeakLevel(Samples(-32768, 1)));
        Assert.Equal(0, SpokenAudio.PeakLevel(Array.Empty<byte>()));
        Assert.Equal(0, SpokenAudio.PeakLevel(new byte[480]));
        Assert.Equal(7, SpokenAudio.PeakLevel(new byte[] { 0x07, 0x00, 0xFF }));
    }
}
