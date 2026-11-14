using System.IO;

namespace Boo.LiveSmoke;

/// <summary>
/// Offline checks of the answer-audio decode path (no key, no network): real OpenAI audio cut into realtime messages
/// the way the server sends them must come back byte for byte through Boo's own parser, and every way of losing audio
/// in transit (a message the parser drops, a sample split across messages, audio under an event name Boo does not
/// read) must be reported, so a decode bug can never pass as a "silent answer" and a silent answer never as speech.
/// </summary>
public sealed class AnswerAudioFramesTests
{
    private const int Rate = 24000;

    private static byte[] Fixture(string name) =>
        LiveVoiceRoundTripTests.ReadPcm24kMono16(Path.Combine(AppContext.BaseDirectory, "Voice", name));

    private static string Message(string type, string base64, int contentIndex = 0) =>
        $"{{\"type\":\"{type}\",\"item_id\":\"item_1\",\"content_index\":{contentIndex},\"delta\":\"{base64}\"}}";

    /// <summary>The PCM cut into chunks of the given byte sizes (the rest in the last), each base64-encoded on its own.</summary>
    private static List<string> PerMessage(byte[] pcm, params int[] sizes)
    {
        var messages = new List<string>();
        var at = 0;
        foreach (var size in sizes)
        {
            var n = Math.Min(size, pcm.Length - at);
            messages.Add(Message(AnswerAudioFrames.AudioDelta, Convert.ToBase64String(pcm, at, n)));
            at += n;
        }
        if (at < pcm.Length)
            messages.Add(Message(AnswerAudioFrames.AudioDelta, Convert.ToBase64String(pcm, at, pcm.Length - at)));
        return messages;
    }

    [Fact]
    public void Real_speech_sent_in_even_chunks_comes_back_exactly_and_is_spoken()
    {
        var speech = Fixture("answer-five-real-speech.wav");
        var report = AnswerAudioFrames.Assemble(PerMessage(speech, 4800, 4800, 9600));
        Assert.Empty(report.Problems);
        Assert.Equal(speech, report.Pcm);
        Assert.True(SpokenAudio.IsSpokenAnswer(report.Pcm, Rate));
    }

    [Fact]
    public void A_sample_split_across_two_messages_is_reported()
    {
        // 4801 bytes: the last 16-bit sample of the first message is completed by the next one. The bytes still join
        // correctly, but a decoder that turns each message into samples on its own would lose or shift audio.
        var speech = Fixture("answer-five-real-speech.wav");
        var report = AnswerAudioFrames.Assemble(PerMessage(speech, 4801, 4799));
        Assert.Equal(speech, report.Pcm);
        Assert.Contains(report.Problems, p => p.Contains("split across messages", StringComparison.Ordinal));
    }

    [Fact]
    public void One_base64_string_cut_mid_way_is_reported_as_dropped_audio()
    {
        // Text cut at a place that is not a multiple of 4 characters is not valid base64 on its own; Boo's parser drops
        // such a message. That must be caught as a decode failure, never measured as a quieter answer.
        var speech = Fixture("answer-five-real-speech.wav");
        var text = Convert.ToBase64String(speech);
        var messages = new List<string>
        {
            Message(AnswerAudioFrames.AudioDelta, text[..1001]),
            Message(AnswerAudioFrames.AudioDelta, text[1001..]),
        };
        var report = AnswerAudioFrames.Assemble(messages);
        Assert.Contains(report.Problems, p => p.Contains("DROPPED", StringComparison.Ordinal));
        Assert.NotEqual(speech.Length, report.Pcm.Length);
    }

    [Fact]
    public void Audio_under_an_event_name_Boo_does_not_read_is_reported()
    {
        var speech = Fixture("answer-five-real-speech.wav");
        var report = AnswerAudioFrames.Assemble(new[] { Message("response.audio.delta", Convert.ToBase64String(speech)) });
        Assert.Contains(report.Problems, p => p.Contains("response.audio.delta", StringComparison.Ordinal));
        Assert.Empty(report.Pcm);
    }

    [Fact]
    public void A_silent_answer_decodes_cleanly_and_is_still_not_spoken()
    {
        // The sentinel's real silent answer: nothing is lost in decoding, the audio itself holds no speech.
        var silent = Fixture("answer-silent-from-provider.wav");
        var report = AnswerAudioFrames.Assemble(PerMessage(silent, 4800, 9600, 19200));
        Assert.Empty(report.Problems);
        Assert.Equal(silent, report.Pcm);
        Assert.False(SpokenAudio.IsSpokenAnswer(report.Pcm, Rate));
    }

    [Fact]
    public void The_session_output_format_is_recorded()
    {
        var updated = "{\"type\":\"session.updated\",\"session\":{\"audio\":{\"output\":{\"format\":{\"type\":\"audio/pcm\",\"rate\":24000}}}}}";
        var report = AnswerAudioFrames.Assemble(new[] { updated });
        Assert.Contains(report.Lines, l => l.Contains("\"rate\":24000", StringComparison.Ordinal));
    }
}
