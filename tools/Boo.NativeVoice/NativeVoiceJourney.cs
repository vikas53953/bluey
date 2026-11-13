using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Storage;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Boo.NativeVoice;

public sealed class NativeVoiceFixture : BooAppFixture
{
    public NativeVoiceFixture() : base(Seed) { }
    private static void Seed(string data)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("Native voice E2E requires GitHub-hosted Windows; no local run.");
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();
        var folder = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS")!;
        File.WriteAllLines(Path.Combine(folder, "hosts.txt"), accepted);
        var key = Environment.GetEnvironmentVariable("BOO_LIVE_OPENAI_KEY");
        if (string.IsNullOrWhiteSpace(key) || AiProviders.CredentialRejectReason(AiProvider.OpenAI, key) is not null)
            throw new InvalidOperationException("Native voice key missing or invalid (value withheld).");
        var model = Environment.GetEnvironmentVariable("BOO_QA_VOICE_MODEL") ?? "gpt-realtime-2.1-mini";
        var transcription = Environment.GetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL") ?? "gpt-4o-mini-transcribe";
        if (!AiProviders.IsValidModelId(model) || !AiProviders.IsValidModelId(transcription))
            throw new InvalidOperationException("Invalid voice model identifier.");
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL", transcription);
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_INPUT", Path.Combine(AppContext.BaseDirectory, "question.wav"));
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE", Path.Combine(folder, "audio-device.jsonl"));
        new CredentialStore(Path.Combine(data, AiProviders.CredentialFileName(AiProvider.OpenAI))).Save(key);
        new SettingsStore(Path.Combine(data, "settings.json")).Save(new AppSettings(SpeechEnabled: true,
            ChirpsEnabled: false, Provider: new ProviderSettings(RealtimeModel: model)));
    }
}

public sealed class NativeVoiceJourney : IClassFixture<NativeVoiceFixture>
{
    private readonly NativeVoiceFixture _boo;
    public NativeVoiceJourney(NativeVoiceFixture boo) => _boo = boo;

    [Fact]
    public void PublishedApp_HoldToTalk_RendersRealSpokenReply()
    {
        var statuses = new List<string>();
        try
        {
            _boo.WaitForNotch(TimeSpan.FromSeconds(30));
            var panel = _boo.EnsurePanel();
            panel.Focus();
            Keyboard.Press(VirtualKeyShort.CONTROL);
            Keyboard.Press(VirtualKeyShort.ALT);
            Keyboard.Press(VirtualKeyShort.SPACE);
            try
            {
                Assert.True(BooAppFixture.WaitUntil(() =>
                {
                    Observe(panel, statuses);
                    using var doc = Device();
                    return doc is not null && doc.RootElement.GetProperty("inputBytes").GetInt64()
                        == doc.RootElement.GetProperty("expectedInputBytes").GetInt64();
                }, TimeSpan.FromSeconds(20)), "Synthetic WAV was not fully captured before release.");
                _boo.CaptureScreen("native-listening");
            }
            finally
            {
                Keyboard.Release(VirtualKeyShort.SPACE);
                Keyboard.Release(VirtualKeyShort.ALT);
                Keyboard.Release(VirtualKeyShort.CONTROL);
            }
            var answered = BooAppFixture.WaitUntil(() =>
            {
                Observe(panel, statuses);
                using var doc = Device();
                return statuses.LastOrDefault()?.StartsWith("Reply complete.", StringComparison.Ordinal) == true
                    && Rows(panel).Any(s => s.StartsWith("Boo: ") && Five(s))
                    && doc is not null && doc.RootElement.GetProperty("renderedBytes").GetInt64() >= 4800
                    && doc.RootElement.GetProperty("nonzeroBytes").GetInt64() > 0;
            }, TimeSpan.FromSeconds(45));
            var rows = Rows(panel);
            _boo.CaptureScreen("native-reply");
            _boo.WriteObservation("native-voice", $"answered={answered}\nrows={string.Join(" | ", rows)}\nstatuses={string.Join(" > ", statuses)}\nmodel={Environment.GetEnvironmentVariable("BOO_QA_VOICE_MODEL")}\ntranscription={Environment.GetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL")}\nOne response, 128 output-token ceiling; virtual devices, physical audio UNVERIFIED.");
            Assert.True(answered, "App did not show five and consume non-silent reply audio; inspect native-voice.txt.");
            Assert.Contains(rows, s => !s.StartsWith("Boo: ") && Regex.IsMatch(s, @"two|2", RegexOptions.IgnoreCase)
                && Regex.IsMatch(s, @"three|3", RegexOptions.IgnoreCase));
            using var device = Device();
            Assert.NotNull(device);
            Assert.Equal(device.RootElement.GetProperty("expectedInputBytes").GetInt64(), device.RootElement.GetProperty("inputBytes").GetInt64());
            Assert.Equal(1, device.RootElement.GetProperty("captureStarts").GetInt32());
            // A red proof is valid only after the normal path above succeeded. Provider failure cannot impersonate it.
            if (Environment.GetEnvironmentVariable("BOO_QA_VOICE_RED_PROOF") == "true")
            {
                _boo.WriteObservation("red-proof-ready", "Normal five answer, full input and consumed audio proven; now deliberately require seven.");
                Assert.Contains(rows, s => s.StartsWith("Boo: ") && Regex.IsMatch(s, @"\b(seven|7)\b", RegexOptions.IgnoreCase));
            }
        }
        catch
        {
            _boo.CaptureScreen("native-FAILED");
            throw;
        }
    }
    private static bool Five(string s) => Regex.IsMatch(s, @"\b(five|5)\b", RegexOptions.IgnoreCase);
    private static string[] Rows(Window panel) => panel.FindFirstDescendant(cf => cf.ByAutomationId("TranscriptList"))!
        .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(BooAppFixture.SafeName).ToArray();
    private static void Observe(Window panel, List<string> statuses)
    {
        var s = BooAppFixture.SafeName(panel.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))!);
        if (statuses.Count == 0 || statuses[^1] != s) statuses.Add(s);
    }
    private static JsonDocument? Device()
    {
        var path = Environment.GetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE")!;
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var lines = reader.ReadToEnd();
        var complete = lines.LastIndexOf('\n');
        if (complete < 0) return null;
        var line = lines[..complete].Split('\n')[^1];
        return JsonDocument.Parse(line);
    }
}
