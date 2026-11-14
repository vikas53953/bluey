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
using System.Drawing;

[assembly: CollectionBehavior(DisableTestParallelization = true)]
namespace Boo.NativeVoice;

public sealed class NativeVoiceFixture : BooAppFixture
{
    public NativeVoiceFixture() : base(Seed) { }
    private static void Seed(string data) => NativeVoiceSeed.Apply(data, "audio-device.jsonl");
}

/// <summary>
/// The same real-key, real-provider seed, with Boo.Core.CaptureFaultPolicy switched on (BOO_QA_CAPTURE_FAULT=1): every
/// per-request screen capture fails before a frame is taken, so an explicit task reaches the fail-closed guard with no
/// observation. The variable is set in the seed (BooAppFixture copies the process environment after it) and cleared
/// right after launch so no other fixture inherits it.
/// </summary>
public sealed class FailedCaptureVoiceFixture : BooAppFixture
{
    public const string Variable = "BOO_QA_CAPTURE_FAULT";
    public FailedCaptureVoiceFixture() : base(data =>
    {
        NativeVoiceSeed.Apply(data, "audio-device-fail-closed.jsonl");
        Environment.SetEnvironmentVariable(Variable, "1");
        Environment.SetEnvironmentVariable(HostedVoiceOptions.ToolTurnVariable, "1");
    })
    {
        Environment.SetEnvironmentVariable(Variable, null);
        Environment.SetEnvironmentVariable(HostedVoiceOptions.ToolTurnVariable, null);
    }
}

internal static class NativeVoiceSeed
{
    /// <param name="evidenceFile">Per-fixture device evidence file under BOO_UI_ARTIFACTS (each Boo appends its own counters).</param>
    public static void Apply(string data, string evidenceFile)
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
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE", Path.Combine(folder, evidenceFile));
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
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(30));
            var rect = notch.BoundingRectangle;
            Mouse.MoveTo(new Point(rect.Left + rect.Width / 2, rect.Top + 2));
            Assert.True(BooAppFixture.WaitUntil(() =>
                notch.FindFirstDescendant(cf => cf.ByAutomationId("OpenButton")) is { IsOffscreen: false } button
                && button.BoundingRectangle.Height > 0, TimeSpan.FromSeconds(10)), "Notch eyes did not reveal.");
            var eyes = notch.FindFirstDescendant(cf => cf.ByAutomationId("OpenButton"))!.BoundingRectangle;
            Mouse.MoveTo(new Point(eyes.Left + eyes.Width / 2, eyes.Top + Math.Min(20, eyes.Height / 2)));
            Mouse.Down(MouseButton.Left);
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
                Mouse.Up(MouseButton.Left);
            }
            var answered = BooAppFixture.WaitUntil(() =>
            {
                Observe(panel, statuses);
                using var doc = Device();
                var currentRows = Rows(panel);
                return statuses.LastOrDefault()?.StartsWith("Reply complete.", StringComparison.Ordinal) == true
                    && currentRows.Any(s => s.StartsWith("Boo: ") && Five(s))
                    && currentRows.Any(HeardQuestion)
                    && doc is not null && doc.RootElement.GetProperty("renderedBytes").GetInt64() >= 4800
                    && doc.RootElement.GetProperty("nonzeroBytes").GetInt64() > 0;
            }, TimeSpan.FromSeconds(45));
            var rows = Rows(panel);
            _boo.CaptureScreen("native-reply");
            _boo.WriteObservation("native-voice", $"answered={answered}\nrows={string.Join(" | ", rows)}\nstatuses={string.Join(" > ", statuses)}\nmodel={Environment.GetEnvironmentVariable("BOO_QA_VOICE_MODEL")}\ntranscription={Environment.GetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL")}\nOne response, 128 output-token ceiling; virtual devices, physical audio UNVERIFIED.");
            Assert.True(answered, "App did not show the question, completed five reply and consumed non-silent audio; inspect native-voice.txt.");
            Assert.Contains(rows, HeardQuestion);
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
        catch (Exception ex)
        {
            _boo.WriteObservation("native-failure", $"error={ex.Message}\nstatuses={string.Join(" > ", statuses)}");
            _boo.CaptureScreen("native-FAILED");
            throw;
        }
    }
    private static bool HeardQuestion(string s) => s.StartsWith("You: ", StringComparison.Ordinal)
        && Regex.IsMatch(s, @"two|2", RegexOptions.IgnoreCase)
        && Regex.IsMatch(s, @"three|3", RegexOptions.IgnoreCase);
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
