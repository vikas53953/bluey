using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Issue #177, review of PR #186 (P2): the per-reply check must judge the WHOLE reply, measured as it arrives. Real speech
/// after any amount of silence prevents a recovery, splitting the audio at any byte (mid-sample, mid-window) gives the
/// same answer as measuring it in one piece, and transcript text from partial updates is bounded.
/// </summary>
public sealed class SilentReplyRecoveryEdgeTests
{
    private const int Rate = 24000;

    /// <summary>16-bit mono PCM: <paramref name="silentSeconds"/> of silence, then <paramref name="loudSeconds"/> at RMS 4000.</summary>
    private static byte[] SilenceThenSpeech(double silentSeconds, double loudSeconds)
    {
        var silent = (int)(Rate * silentSeconds);
        var loud = (int)(Rate * loudSeconds);
        var pcm = new byte[(silent + loud) * 2];
        for (var i = silent; i < silent + loud; i++)
        {
            const short level = 4000;
            pcm[2 * i] = unchecked((byte)level);
            pcm[2 * i + 1] = (byte)(level >> 8);
        }
        return pcm;
    }

    private static SilentReplyDecision? Feed(byte[] pcm, int chunk, string transcript = "Five")
    {
        var recovery = new SilentReplyRecovery();
        recovery.Begin();
        for (var at = 0; at < pcm.Length; at += chunk)
        {
            recovery.Audio(pcm[at..Math.Min(pcm.Length, at + chunk)]);
        }
        recovery.Transcript(transcript, done: true);
        return recovery.Complete(speechMuted: false, Rate);
    }

    [Fact]
    public void Real_speech_after_a_minute_of_silence_prevents_a_recovery()
    {
        Assert.Null(Feed(SilenceThenSpeech(61, 0.3), 19200));
        Assert.NotNull(Feed(SilenceThenSpeech(61, 0), 19200));
    }

    [Fact]
    public void Splitting_the_audio_anywhere_gives_the_same_answer_as_one_piece()
    {
        var justEnough = SilenceThenSpeech(0.5, 0.020);   // exactly one loud 20 ms window
        var tooLittle = SilenceThenSpeech(0.5, 0.019);    // less than a full window: no window is loud
        foreach (var chunk in new[] { 1, 3, 7, 479, 960, 961, 19200, int.MaxValue / 4 })
        {
            Assert.Null(Feed(justEnough, chunk));
            Assert.NotNull(Feed(tooLittle, chunk));
        }
    }

    [Fact]
    public void The_measured_peak_seconds_and_chunks_cover_every_chunk()
    {
        var pcm = SilenceThenSpeech(2, 0);
        pcm[pcm.Length - 2] = 7;   // the loudest sample is in the last chunk
        var decision = Feed(pcm, 1000);
        Assert.NotNull(decision);
        Assert.Equal(7, decision.Peak);
        Assert.Equal(2.0, decision.AudioSeconds, 6);
        Assert.Equal((pcm.Length + 999) / 1000, decision.Chunks);
        Assert.True(decision.ProviderSilence);
    }

    [Fact]
    public void Transcript_deltas_are_bounded()
    {
        var recovery = new SilentReplyRecovery();
        recovery.Begin();
        recovery.Audio(new byte[Rate * 2]);
        for (var i = 0; i < 100; i++)
        {
            recovery.Transcript(new string('a', 100), done: false);
        }
        var decision = recovery.Complete(speechMuted: false, Rate);
        Assert.NotNull(decision);
        Assert.Equal(SilentReplyRecovery.MaxRecoveryCharacters, decision.Text.Length);
    }

    [Fact]
    public void A_different_sample_rate_than_the_watcher_was_made_for_is_refused()
    {
        var recovery = new SilentReplyRecovery();
        recovery.Begin();
        Assert.Throws<ArgumentOutOfRangeException>(() => recovery.Complete(speechMuted: false, 16000));
    }
}
