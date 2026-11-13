using System.Collections.Concurrent;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Providers;
using Xunit.Abstractions;

namespace Boo.LiveSmoke;

/// <summary>
/// One real OpenAI voice round trip through Boo's own voice path: Boo's <see cref="RealtimeClient"/> in push-to-talk mode
/// (the same session Boo's hold-to-talk uses) receives a synthetic spoken question (a checked-in WAV made offline by
/// Windows' speech synthesizer, never a microphone), commits it as a user turn, and must get back:
/// (1) OpenAI's transcript of the question saying "two plus three" (speech-to-text worked on the real audio),
/// (2) a spoken answer (real audio bytes) whose transcript says "five" (the model understood and answered aloud),
/// and no error or disconnect. Microphone capture and speaker playback are not exercised (a GitHub VM has neither);
/// everything between them is.
///
/// Refuses to run anywhere but a GitHub-hosted runner (RUNNER_ENVIRONMENT=github-hosted) or a throwaway VM the local
/// verify-boo runner declared with -ThrowawayVm (BOO_THROWAWAY_VM=1), never a personal PC, and runs the same anti-mock
/// guard as the typed round trip, so the answer must come from the real api.openai.com. Voice is always OpenAI: it
/// uses BOO_LIVE_OPENAI_KEY whatever text provider the run chose.
/// Models are set per run: BOO_LIVE_REALTIME_MODEL (default: the cheapest realtime model) and
/// BOO_LIVE_TRANSCRIBE_MODEL (default: Boo's own, the cheapest speech-to-text model).
/// </summary>
public sealed class LiveVoiceRoundTripTests
{
    public const string RealtimeModelVariable = "BOO_LIVE_REALTIME_MODEL";
    public const string TranscribeModelVariable = "BOO_LIVE_TRANSCRIBE_MODEL";
    public const string DefaultRealtimeModel = "gpt-realtime-2.1-mini";
    public const string QuestionFile = "Voice/what-is-two-plus-three.wav";
    public const string ThrowawayVmVariable = "BOO_THROWAWAY_VM";

    private const int SampleRate = 24000;                  // Boo's session: audio/pcm, rate 24000, 16-bit mono
    private const int BytesPerSecond = SampleRate * 2;
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(90);
    private static readonly Regex HeardQuestion = new(@"\b(two|2)\s*(plus|\+|and)\s*(three|3)\b", RegexOptions.IgnoreCase);
    private static readonly Regex SaidAnswer = new(@"\b(five|5)\b", RegexOptions.IgnoreCase);

    private readonly ITestOutputHelper _output;

    public LiveVoiceRoundTripTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public async Task SpokenQuestion_GetsASpokenAnswerFromRealOpenAiVoice()
    {
        if (!BooAppFixture.OnGitHubHostedRunner() && Environment.GetEnvironmentVariable(ThrowawayVmVariable) != "1")
        {
            throw new InvalidOperationException(
                "LIVE VOICE FAILED CLOSED: the live voice round trip runs only on a GitHub-hosted runner " +
                "(RUNNER_ENVIRONMENT=github-hosted) or a throwaway VM (verify-boo -ThrowawayVm), never on a personal PC.");
        }
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();

        var key = Environment.GetEnvironmentVariable(LiveConfig.KeyVariable(AiProvider.OpenAI));
        if (string.IsNullOrWhiteSpace(key))
        {
            throw new InvalidOperationException(
                $"LIVE VOICE FAILED CLOSED: the repository secret {LiveConfig.KeyVariable(AiProvider.OpenAI)} is missing or " +
                "empty (Boo's voice is always OpenAI). This run is not a pass.");
        }
        if (AiProviders.CredentialRejectReason(AiProvider.OpenAI, key) is { } reason)
        {
            throw new InvalidOperationException($"LIVE VOICE FAILED CLOSED: the OpenAI key is not usable: {reason}");
        }
        var realtimeModel = ModelFrom(RealtimeModelVariable, DefaultRealtimeModel);
        var transcribeModel = ModelFrom(TranscribeModelVariable, RealtimeProtocol.DefaultTranscriptionModel);

        var pcm = ReadPcm24kMono16(Path.Combine(AppContext.BaseDirectory, QuestionFile));
        var events = new ConcurrentQueue<RealtimeEvent>();
        var settings = new ProviderSettings(TextProvider: AiProvider.OpenAI) with { RealtimeModel = realtimeModel };
        await using var client = new RealtimeClient(() => key.Trim(), settings, pushToTalk: true, transcriptionModel: transcribeModel);
        client.Received += events.Enqueue;

        using var deadline = new CancellationTokenSource(TurnTimeout);
        var started = DateTime.UtcNow;
        await client.ConnectAsync(deadline.Token);
        // Hold-to-talk sends microphone chunks as they arrive; send the question the same way, 100 ms at a time.
        for (var offset = 0; offset < pcm.Length; offset += BytesPerSecond / 10)
        {
            await client.SendAudioAsync(pcm[offset..Math.Min(pcm.Length, offset + BytesPerSecond / 10)], deadline.Token);
        }
        await client.CommitAudioTurnAsync(deadline.Token);

        // Done when the answer is complete AND the question's transcript arrived (they can come in either order), or
        // as soon as anything fails. Then a short grace period, so an error sent right after the answer is still seen.
        while (!deadline.IsCancellationRequested &&
               !(events.Any(e => e.Kind == "response.done") &&
                 events.Any(e => e.Kind is "conversation.item.input_audio_transcription.completed"
                     or "conversation.item.input_audio_transcription.failed")) &&
               !events.Any(e => e.Kind is "error" or "disconnect" or "conversation.item.input_audio_transcription.failed"))
        {
            await Task.Delay(100, CancellationToken.None);
        }
        await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
        var seconds = (DateTime.UtcNow - started).TotalSeconds;

        var heard = events.LastOrDefault(e => e.Kind == "conversation.item.input_audio_transcription.completed")?.Text ?? string.Empty;
        var answer = events.LastOrDefault(e => e.Kind == "response.output_audio_transcript.done")?.Text
                     ?? string.Concat(events.Where(e => e.Kind == "response.output_audio_transcript.delta").Select(e => e.Text));
        var answerAudio = events.Where(e => e.Kind == "response.output_audio.delta" && e.Audio is not null).SelectMany(e => e.Audio!).ToArray();
        var problems = events.Where(e => e.Kind is "error" or "disconnect").Select(e => $"{e.Kind}: {e.Text}").ToList();
        if (events.Any(e => e.Kind == "conversation.item.input_audio_transcription.failed"))
            problems.Add("speech-to-text of the question failed (conversation.item.input_audio_transcription.failed)");
        // Boo's session offers tools (screen_help, focus); a plain question must be answered aloud, not with a tool call.
        foreach (var call in events.Where(e => e.Kind == "response.function_call_arguments.done"))
            problems.Add($"the model called a tool instead of answering aloud (call {call.ItemId})");
        var done = events.LastOrDefault(e => e.Kind == "response.done");
        if (done?.Text is { } status) problems.Add($"response.done: {status}");
        var questionSeconds = (double)pcm.Length / BytesPerSecond;
        var answerSeconds = (double)answerAudio.Length / BytesPerSecond;

        WriteEvidence(accepted, realtimeModel, transcribeModel, questionSeconds, answerSeconds, seconds, heard, answer, answerAudio, problems);
        _output.WriteLine($"voice: heard='{heard}' answer='{answer}' answerAudio={answerSeconds:0.0}s in {seconds:0.0}s");

        Assert.True(problems.Count == 0, "OpenAI voice reported a problem: " + string.Join("; ", problems));
        Assert.True(done is not null, $"No complete voice answer within {TurnTimeout.TotalSeconds:0}s.");
        Assert.True(HeardQuestion.IsMatch(heard),
            $"OpenAI's transcript of the spoken question should say 'two plus three' but was '{heard}' " +
            "(bad or silent audio, or speech-to-text failed).");
        Assert.True(answerAudio.Length >= BytesPerSecond / 2,
            $"The answer should be spoken (at least 0.5 s of audio) but only {answerSeconds:0.00} s of audio arrived.");
        Assert.True(SaidAnswer.IsMatch(answer), $"The spoken answer should say 'five' but its transcript was '{answer}'.");
    }

    /// <summary>A model id from the environment, or the default; a malformed id fails closed.</summary>
    private static string ModelFrom(string variable, string fallback)
    {
        var value = Environment.GetEnvironmentVariable(variable)?.Trim();
        if (string.IsNullOrEmpty(value)) return fallback;
        if (!AiProviders.IsValidModelId(value))
            throw new InvalidOperationException($"LIVE VOICE FAILED CLOSED: {variable} '{value}' is not a valid model id.");
        return value;
    }

    /// <summary>The PCM samples of a WAV that must be exactly 24 kHz, 16-bit, mono (what Boo's session expects).</summary>
    public static byte[] ReadPcm24kMono16(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 12 || Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            throw new InvalidDataException($"{path} is not a WAV file.");
        int? channels = null, rate = null, bits = null;
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var id = Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            var body = at + 8;
            if (size < 0 || body + size > bytes.Length) break;
            if (id == "fmt ")
            {
                channels = BitConverter.ToInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                if (channels != 1 || rate != SampleRate || bits != 16)
                    throw new InvalidDataException($"{path} must be {SampleRate} Hz, 16-bit, mono (is {rate} Hz, {bits}-bit, {channels} channel(s)).");
                return bytes[body..(body + size - size % 2)];
            }
            at = body + size + size % 2;
        }
        throw new InvalidDataException($"{path} has no audio data.");
    }

    /// <summary>
    /// Rough cost of this run (US$), from OpenAI's published prices (13 Nov 2026) and its audio-token rates. Assumptions,
    /// stated in the evidence: the user's audio is about 10 tokens/s, the model's audio about 20 tokens/s, and the session
    /// instructions plus tool schemas are about 2,000 text input tokens. Prices: realtime mini audio $10 / $20 per 1M
    /// input/output tokens, its text $0.60 per 1M input tokens; speech-to-text $0.003 per minute.
    /// </summary>
    public static double EstimatedCostUsd(double questionSeconds, double answerSeconds) =>
        questionSeconds * 10 * 10.0 / 1_000_000
        + answerSeconds * 20 * 20.0 / 1_000_000
        + 2_000 * 0.60 / 1_000_000
        + questionSeconds / 60 * 0.003;

    private static void WriteEvidence(IReadOnlyList<string> accepted, string realtimeModel, string transcribeModel,
        double questionSeconds, double answerSeconds, double seconds, string heard, string answer, byte[] answerAudio,
        IReadOnlyList<string> problems)
    {
        var folder = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) return;
        Directory.CreateDirectory(folder);
        var cost = EstimatedCostUsd(questionSeconds, answerSeconds);
        File.WriteAllText(Path.Combine(folder, "live-voice-01-round-trip.txt"), string.Join('\n', new[]
        {
            "anti-mock guard ran before the voice session: no proxy, every AI host public",
        }.Concat(accepted).Concat(new[]
        {
            string.Create(CultureInfo.InvariantCulture, $"realtimeModel={realtimeModel} transcribeModel={transcribeModel}"),
            string.Create(CultureInfo.InvariantCulture, $"question audio={questionSeconds:0.0}s answer audio={answerSeconds:0.0}s turn took={seconds:0.0}s"),
            $"heard (OpenAI speech-to-text of the synthetic question)='{heard}'",
            $"answer (transcript of Boo's spoken reply)='{answer}'",
            $"problems='{string.Join("; ", problems)}'",
            string.Create(CultureInfo.InvariantCulture, $"estimated cost of this voice turn: about ${cost:0.0000} (see LiveVoiceRoundTripTests.EstimatedCostUsd for the assumptions)"),
        })) + "\n");
        if (answerAudio.Length > 0) File.WriteAllBytes(Path.Combine(folder, "live-voice-01-answer.wav"), Wav(answerAudio));
    }

    /// <summary>The answer's PCM in a WAV container, so a reviewer can listen to what Boo said.</summary>
    private static byte[] Wav(byte[] pcm)
    {
        using var stream = new MemoryStream();
        using (var w = new BinaryWriter(stream, Encoding.ASCII, leaveOpen: true))
        {
            w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
            w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(SampleRate); w.Write(BytesPerSecond); w.Write((short)2); w.Write((short)16);
            w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
        }
        return stream.ToArray();
    }
}
