using System.Globalization;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Providers;
using Xunit.Abstractions;

namespace Boo.LiveSmoke;

/// <summary>
/// Issue #177, live half: the speech Boo plays when a spoken reply came back silent comes from OpenAI's text-to-speech
/// through Boo's own <see cref="SpeechClient"/>. This makes that exact request once, with Boo's default voice, and
/// checks the answer is real 24 kHz 16-bit mono speech (at least the same 0.15 s of speech above the quiet floor the
/// voice check requires). OpenAI cannot be made to send a silent reply on demand, so the replay of its real silent
/// reply is proven offline (SilentReplyRecoveryTests, SilentReplyWiringTests) and the recovery speech is proven here.
/// One request, a few hundredths of a cent; hosted runner or throwaway VM only, after the anti-mock guard.
/// </summary>
public sealed class LiveSpeechRecoveryTests
{
    public const string SpokenText = "The answer is five.";
    private const int Rate = 24000;
    private readonly ITestOutputHelper _output;

    public LiveSpeechRecoveryTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task RecoverySpeech_FromRealOpenAi_IsAudible24kMonoSpeech()
    {
        if (!BooAppFixture.OnGitHubHostedRunner() && Environment.GetEnvironmentVariable(LiveVoiceRoundTripTests.ThrowawayVmVariable) != "1")
        {
            throw new InvalidOperationException(
                "LIVE SPEECH FAILED CLOSED: runs only on a GitHub-hosted runner or a throwaway VM, never on a personal PC.");
        }
        RealAiHostGuard.EnsureRealProviderHosts();
        var key = Environment.GetEnvironmentVariable(LiveConfig.KeyVariable(AiProvider.OpenAI));
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"LIVE SPEECH FAILED CLOSED: the repository secret {LiveConfig.KeyVariable(AiProvider.OpenAI)} is missing or empty. This run is not a pass.");
        }
        var voice = new ProviderSettings().Voice;
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        var client = new SpeechClient(http, () => key.Trim(), voice);

        var audio = await client.SpeakAsync(SpokenText, CancellationToken.None);

        var seconds = audio.Length / 2.0 / Rate;
        var speech = ReplyAudio.SpeechSeconds(audio, Rate);
        var peak = ReplyAudio.PeakLevel(audio);
        var note = string.Create(CultureInfo.InvariantCulture,
            $"model={SpeechClient.DefaultModel} voice={voice} text='{SpokenText}' bytes={audio.Length} seconds={seconds:0.00} speech={speech:0.00} peak={peak}");
        _output.WriteLine("speech recovery: " + note);
        var evidence = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllText(Path.Combine(evidence, "live-speech-recovery.txt"), note + Environment.NewLine);
            await File.WriteAllBytesAsync(Path.Combine(evidence, "live-speech-recovery.pcm"), audio);
        }

        Assert.InRange(seconds, 0.5, 10);   // "The answer is five." at 24 kHz 16-bit mono, not some other rate or a container
        Assert.True(speech >= 0.15, $"The recovery speech should be audible but held only {speech:0.00} s of speech ({note}).");
    }
}
