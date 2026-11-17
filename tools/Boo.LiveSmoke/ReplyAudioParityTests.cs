using System.IO;
using Boo.Core;

namespace Boo.LiveSmoke;

/// <summary>
/// Issue #177: Boo's product check of a spoken reply (Boo.Core.ReplyAudio, used to recover a silent reply) and the live
/// voice test's acceptance measure (SpokenAudio) must be the same measure: the same quiet floor, the same provider-silence
/// peak, and the same speech seconds and peak on every real answer recorded from OpenAI. If one changes alone, this fails.
/// </summary>
public sealed class ReplyAudioParityTests
{
    private const int Rate = 24000;

    [Fact]
    public void The_product_and_the_live_check_share_their_thresholds()
    {
        Assert.Equal(SpokenAudio.QuietRms, ReplyAudio.QuietRms);
        Assert.Equal(SpokenAudio.ProviderSilencePeak, ReplyAudio.ProviderSilencePeak);
    }

    [Theory]
    [InlineData("answer-five-real-speech.wav")]
    [InlineData("answer-silent-from-provider.wav")]
    [InlineData("answer-silent-from-provider-177.wav")]
    [InlineData("answer-soft-five-177a.wav")]
    public void The_product_and_the_live_check_measure_every_recorded_answer_alike(string fixture)
    {
        var pcm = LiveVoiceRoundTripTests.ReadPcm24kMono16(Path.Combine(AppContext.BaseDirectory, "Voice", fixture));
        Assert.Equal(SpokenAudio.SpeechSeconds(pcm, Rate), ReplyAudio.SpeechSeconds(pcm, Rate), 9);
        Assert.Equal(SpokenAudio.PeakLevel(pcm), ReplyAudio.PeakLevel(pcm));
    }
}
