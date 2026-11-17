using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Providers;
using Xunit.Abstractions;

namespace Boo.LiveSmoke;

/// <summary>
/// MEASUREMENT ONLY (issue #179, throwaway branch, never merged): four spoken questions, each in a fresh realtime session
/// through Boo's own voice client, on the model set by BOO_LIVE_REALTIME_MODEL (gpt-realtime-2.1 for this series: the model
/// users get). Per turn it records what was heard, the answer, whether it is right, how much speech the answer holds, the
/// server's session.updated echo and response.done usage, and a cost computed from that usage at ASSUMED upper-bound prices
/// (OpenAI's pricing page refuses automated reads). It stops itself when this run's cost passes RunCapUsd.
/// It always passes unless the session itself fails: wrong or silent answers are what it measures, not failures.
/// </summary>
public sealed class LiveVoiceMeasureTests
{
    private const int SampleRate = 24000;
    private const double RunCapUsd = 0.25;
    // Assumed upper-bound prices, US$ per 1M tokens (above the published gpt-realtime list prices known to the build lane).
    private const double AudioIn = 40, AudioOut = 80, TextIn = 5, TextOut = 20;

    private static readonly (string File, Regex Right)[] Questions =
    [
        ("measure-two-plus-three-sentence.wav", new(@"\b(five|5)\b", RegexOptions.IgnoreCase)),
        ("measure-seven-times-six.wav", new(@"\b(forty[- ]two|42)\b", RegexOptions.IgnoreCase)),
        ("measure-capital-of-france.wav", new(@"\bparis\b", RegexOptions.IgnoreCase)),
        ("measure-days-in-a-week.wav", new(@"\b(seven|7)\b", RegexOptions.IgnoreCase)),
    ];

    private readonly ITestOutputHelper _output;

    public LiveVoiceMeasureTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task Measure_FourQuestions_OnTheConfiguredRealtimeModel()
    {
        if (!BooAppFixture.OnGitHubHostedRunner())
            throw new InvalidOperationException("MEASUREMENT runs only on a GitHub-hosted runner.");
        RealAiHostGuard.EnsureRealProviderHosts();
        var key = Environment.GetEnvironmentVariable(LiveConfig.KeyVariable(AiProvider.OpenAI));
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidOperationException("MEASUREMENT: no OpenAI key.");
        var model = Environment.GetEnvironmentVariable(LiveVoiceRoundTripTests.RealtimeModelVariable)?.Trim();
        if (string.IsNullOrEmpty(model)) throw new InvalidOperationException("MEASUREMENT: BOO_LIVE_REALTIME_MODEL must be set.");
        var folder = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS") ?? Path.GetTempPath();
        Directory.CreateDirectory(folder);
        var spent = 0.0;
        for (var q = 0; q < Questions.Length; q++)
        {
            if (spent > RunCapUsd)
            {
                _output.WriteLine($"MEASURE stop: run cost {spent:0.0000} passed the cap {RunCapUsd}");
                break;
            }
            var (file, right) = Questions[q];
            var pcm = LiveVoiceRoundTripTests.ReadPcm24kMono16(Path.Combine(AppContext.BaseDirectory, "Voice", file));
            var events = new ConcurrentQueue<RealtimeEvent>();
            var raw = new ConcurrentQueue<string>();
            var settings = new ProviderSettings(TextProvider: AiProvider.OpenAI) with { RealtimeModel = model };
            await using (var client = LiveVoiceRoundTripTests.CreateClient(() => key.Trim(), settings, RealtimeProtocol.DefaultTranscriptionModel))
            {
                client.Received += events.Enqueue;
                client.RawFrameObserver = raw.Enqueue;
                using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                await client.ConnectAsync(deadline.Token);
                for (var at = 0; at < pcm.Length; at += SampleRate / 5)
                    await client.SendAudioAsync(pcm[at..Math.Min(pcm.Length, at + SampleRate / 5)], deadline.Token);
                await client.CommitAudioTurnAsync(deadline.Token);
                while (!deadline.IsCancellationRequested
                       && !(events.Any(e => e.Kind == "response.done")
                            && events.Any(e => e.Kind is "conversation.item.input_audio_transcription.completed" or "conversation.item.input_audio_transcription.failed"))
                       && !events.Any(e => e.Kind is "error" or "disconnect"))
                    await Task.Delay(100, CancellationToken.None);
                await Task.Delay(1000, CancellationToken.None);
            }
            var heard = events.LastOrDefault(e => e.Kind == "conversation.item.input_audio_transcription.completed")?.Text ?? string.Empty;
            var answer = events.LastOrDefault(e => e.Kind == "response.output_audio_transcript.done")?.Text
                         ?? string.Concat(events.Where(e => e.Kind == "response.output_audio_transcript.delta").Select(e => e.Text));
            var audio = events.Where(e => e.Kind == "response.output_audio.delta" && e.Audio is not null).SelectMany(e => e.Audio!).ToArray();
            var speech = SpokenAudio.SpeechSeconds(audio, SampleRate);
            var peak = SpokenAudio.PeakLevel(audio);
            var sessionEcho = raw.FirstOrDefault(m => m.Contains("\"type\":\"session.updated\"", StringComparison.Ordinal)) ?? "";
            var done = raw.LastOrDefault(m => m.Contains("\"type\":\"response.done\"", StringComparison.Ordinal)) ?? "";
            var (usage, cost) = Usage(done);
            cost += pcm.Length / 2.0 / SampleRate / 60 * 0.003;   // speech-to-text of the question (gpt-4o-mini-transcribe), assumed
            spent += cost;
            var problems = string.Join("; ", events.Where(e => e.Kind is "error" or "disconnect").Select(e => e.Kind + ":" + e.Text));
            var record = new
            {
                turn = q + 1, question = file, model, heard, answer,
                correct = right.IsMatch(answer),
                silent = speech == 0,
                speechSeconds = Math.Round(speech, 2), audioSeconds = Math.Round(audio.Length / 2.0 / SampleRate, 2), peak,
                usage, costUsd = Math.Round(cost, 6), problems,
                sessionTemperature = Temperature(sessionEcho),
            };
            var line = JsonSerializer.Serialize(record);
            _output.WriteLine("MEASURE " + line);
            File.AppendAllText(Path.Combine(folder, "measure-turns.jsonl"), line + "\n");
            if (q == 0) File.WriteAllText(Path.Combine(folder, "measure-session-updated.json"), sessionEcho + "\n");
            File.WriteAllText(Path.Combine(folder, $"measure-response-done-{q + 1}.json"), done + "\n");
        }
        File.WriteAllText(Path.Combine(folder, "measure-cost.txt"), spent.ToString("0.000000", CultureInfo.InvariantCulture) + "\n");
        _output.WriteLine($"MEASURE run cost {spent:0.000000} USD (assumed prices)");
    }

    private static string Temperature(string sessionEcho)
    {
        if (sessionEcho.Length == 0) return "<no echo>";
        using var doc = JsonDocument.Parse(sessionEcho);
        return doc.RootElement.TryGetProperty("session", out var s) && s.TryGetProperty("temperature", out var t)
            ? t.GetRawText()
            : "<not in the server's session echo>";
    }

    private static (object Usage, double Cost) Usage(string done)
    {
        if (done.Length == 0) return (new { missing = true }, 0);
        using var doc = JsonDocument.Parse(done);
        if (!doc.RootElement.TryGetProperty("response", out var r) || !r.TryGetProperty("usage", out var u)) return (new { missing = true }, 0);
        static int Get(JsonElement e, string a, string b) =>
            e.TryGetProperty(a, out var d) && d.TryGetProperty(b, out var v) && v.TryGetInt32(out var n) ? n : 0;
        var textIn = Get(u, "input_token_details", "text_tokens");
        var audioIn = Get(u, "input_token_details", "audio_tokens");
        var textOut = Get(u, "output_token_details", "text_tokens");
        var audioOut = Get(u, "output_token_details", "audio_tokens");
        var cost = (textIn * TextIn + audioIn * AudioIn + textOut * TextOut + audioOut * AudioOut) / 1_000_000.0;
        return (new { textIn, audioIn, textOut, audioOut, raw = u.GetRawText() }, cost);
    }
}
