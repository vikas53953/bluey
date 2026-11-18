using System.IO;
using System.Text;
using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Issue #177: replays real answer audio captured from OpenAI through Boo's per-reply check, chunked the way it arrived
/// on the wire. A reply whose audio holds no speech while its transcript has words must be read aloud by Boo; a real
/// spoken answer, even a soft one, must be left alone. Fixtures are the hosted runs' own audio, kept in
/// tools/Boo.LiveSmoke/Voice and read by path (no test project changes).
/// </summary>
public sealed class SilentReplyRecoveryTests
{
    private const int Rate = 24000;
    private const int WireChunk = 19200;   // OpenAI sent 0.4 s per audio delta in the #177 runs

    private static SilentReplyDecision? Replay(string fixture, string transcript, bool muted = false, bool transcriptDone = true)
    {
        var recovery = new SilentReplyRecovery();
        recovery.Begin();
        var pcm = Fixture(fixture);
        for (var at = 0; at < pcm.Length; at += WireChunk)
        {
            recovery.Audio(pcm[at..Math.Min(pcm.Length, at + WireChunk)]);
        }
        recovery.Transcript(transcript, transcriptDone);
        return recovery.Complete(muted, Rate);
    }

    [Fact]
    public void Silent_provider_audio_with_a_transcript_is_read_aloud()
    {
        // live-ai-smoke run 44022596617 (lane/face 1bafb77): 4.05 s of audio, loudest sample 5, transcript "Five".
        var decision = Replay("answer-silent-from-provider-177.wav", "Five");

        Assert.NotNull(decision);
        Assert.Equal("Five", decision.Text);
        Assert.True(decision.ProviderSilence);
        Assert.Equal(5, decision.Peak);
        Assert.Equal(11, decision.Chunks);
        Assert.Equal(4.05, decision.AudioSeconds, 2);
    }

    [Fact]
    public void Inaudible_noise_with_a_transcript_is_read_aloud_too()
    {
        // live-sentinel run 43576162645 (#118): 1.55 s of a flat noise floor, loudest sample 335, no speech, transcript "5".
        var decision = Replay("answer-silent-from-provider.wav", "5");

        Assert.NotNull(decision);
        Assert.Equal("5", decision.Text);
        Assert.False(decision.ProviderSilence);   // not digital silence, but still no speech at all
    }

    [Theory]
    [InlineData("answer-five-real-speech.wav")]   // a normal spoken "five"
    [InlineData("answer-soft-five-177a.wav")]     // #177 (a): a soft but audible "five" (0.12 s above the floor, peak 13662)
    public void A_real_spoken_answer_is_left_alone_even_when_soft(string fixture)
    {
        Assert.Null(Replay(fixture, "5"));
    }

    [Fact]
    public void Nothing_is_read_when_speech_is_muted_or_the_transcript_is_empty()
    {
        Assert.Null(Replay("answer-silent-from-provider-177.wav", "Five", muted: true));
        Assert.Null(Replay("answer-silent-from-provider-177.wav", ""));
        Assert.Null(Replay("answer-silent-from-provider-177.wav", "   "));
    }

    [Fact]
    public void A_reply_is_recovered_at_most_once_and_the_spoken_text_is_bounded()
    {
        var recovery = new SilentReplyRecovery();
        recovery.Begin();
        recovery.Audio(new byte[Rate * 2]);
        recovery.Transcript(new string('a', SilentReplyRecovery.MaxRecoveryCharacters + 50), done: true);
        var first = recovery.Complete(speechMuted: false, Rate);
        Assert.NotNull(first);
        Assert.Equal(SilentReplyRecovery.MaxRecoveryCharacters, first.Text.Length);
        Assert.Null(recovery.Complete(speechMuted: false, Rate));

        recovery.Begin();   // the next reply is judged afresh
        recovery.Audio(new byte[Rate * 2]);
        recovery.Transcript("Hello", done: true);
        Assert.NotNull(recovery.Complete(speechMuted: false, Rate));
    }

    [Fact]
    public void Transcript_deltas_add_up_when_no_final_transcript_arrives()
    {
        var recovery = new SilentReplyRecovery();
        recovery.Begin();
        recovery.Audio(new byte[Rate * 2]);
        recovery.Transcript("The answer ", done: false);
        recovery.Transcript("is five.", done: false);
        Assert.Equal("The answer is five.", recovery.Complete(speechMuted: false, Rate)?.Text);
    }

    private static byte[] Fixture(string name)
    {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot(), "tools", "Boo.LiveSmoke", "Voice", name));
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            if (id == "data")
            {
                return bytes[(at + 8)..(at + 8 + size - size % 2)];
            }
            at += 8 + size + size % 2;
        }
        throw new InvalidDataException(name + " has no data chunk");
    }

    private static string RepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boo.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new InvalidOperationException("Boo.sln not found above " + AppContext.BaseDirectory);
    }
}
