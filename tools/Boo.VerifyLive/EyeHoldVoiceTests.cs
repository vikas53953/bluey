using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.UIA3;

namespace Boo.VerifyLive;

// Composition makes environment restoration cover launch failures as well as successful launches.
public sealed class EyeHoldVoiceFixture : IDisposable
{
    public BooAppFixture Boo { get; }
    private static readonly string[] Variables = ["BOO_CAPTURE_SCOPE", "BOO_QA_VOICE_INPUT",
        "BOO_QA_VOICE_EVIDENCE", "BOO_QA_VOICE_TRANSCRIBE_MODEL", HostedVoiceOptions.ToolTurnVariable,
        "BOO_QA_CAPTURE_FAULT"];

    public EyeHoldVoiceFixture()
    {
        var previous = Variables.ToDictionary(v => v, Environment.GetEnvironmentVariable);
        try { Boo = new SeededApp(); }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    public void Dispose() => Boo.Dispose();

    private sealed class SeededApp : BooAppFixture
    {
        public SeededApp() : base(Seed) { }
    }

    // Bounded copy of #93's internal NativeVoiceSeed.Apply, which is not linked into this assembly.
    private static void Seed(string data)
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true"
            || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "github-hosted")
            throw new InvalidOperationException("J10B requires GitHub-hosted Windows; no local run.");
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();
        var folder = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("J10B evidence folder required.");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "j10b-hosts.txt"), accepted);
        var key = Environment.GetEnvironmentVariable("BOO_LIVE_OPENAI_KEY");
        if (string.IsNullOrWhiteSpace(key) || AiProviders.CredentialRejectReason(AiProvider.OpenAI, key) is not null)
            throw new InvalidOperationException("Native voice key missing or invalid (value withheld).");
        var model = Environment.GetEnvironmentVariable("BOO_QA_VOICE_MODEL") ?? "gpt-realtime-2.1-mini";
        var transcription = Environment.GetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL") ?? "gpt-4o-mini-transcribe";
        if (!AiProviders.IsValidModelId(model) || !AiProviders.IsValidModelId(transcription))
            throw new InvalidOperationException("Invalid voice model identifier.");
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL", transcription);
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_INPUT", Environment.GetEnvironmentVariable("BOO_QA_EYE_HOLD_INPUT") ?? throw new InvalidOperationException("Short eye-hold WAV required."));
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE", Path.Combine(folder, "j10b-device.jsonl"));
        Environment.SetEnvironmentVariable(HostedVoiceOptions.ToolTurnVariable, null);
        Environment.SetEnvironmentVariable("BOO_QA_CAPTURE_FAULT", null);
        if (File.Exists(Path.Combine(folder, "j10b-device.jsonl")))
            throw new InvalidOperationException("J10B requires a fresh evidence folder.");
        new CredentialStore(Path.Combine(data, AiProviders.CredentialFileName(AiProvider.OpenAI))).Save(key);
        new SettingsStore(Path.Combine(data, "settings.json")).Save(new AppSettings(SpeechEnabled: true,
            ChirpsEnabled: false, Provider: new ProviderSettings(RealtimeModel: model)));
        File.WriteAllText(Path.Combine(folder, "j10b-models.txt"), $"model={model}\ntranscription={transcription}\n" +
            "One capture; existing product hosted-audio plain-response cap: one response, 128 output tokens.\n" +
            "Provider attempt count UNVERIFIED: device counters do not count websocket requests.\n" +
            "Synthetic hosted input/output devices; physical microphone/speakers UNVERIFIED.\n");
    }
}

public sealed class EyeHoldVoiceTests : IClassFixture<EyeHoldVoiceFixture>
{
    private readonly BooAppFixture _boo;
    private readonly List<string> _actions = new();
    public EyeHoldVoiceTests(EyeHoldVoiceFixture fixture) => _boo = fixture.Boo;
    private void Record(string text)
    {
        _actions.Add($"{DateTimeOffset.UtcNow:O} {text}");
        _boo.WriteObservation("j10b-actions", string.Join("\n", _actions));
    }

    [Fact]
    public void Q10B_EyeHold_RecordsAndReleasesARealVoiceTurn()
    {
        var clock = Stopwatch.StartNew();
        long down = -1, up = -1;
        try
        {
            using var exe = File.OpenRead(_boo.ExePath);
            _boo.WriteObservation("j10b-identity", $"exe={_boo.ExePath}\npid={_boo.App.ProcessId}\n" +
                $"sha256={Convert.ToHexString(SHA256.HashData(exe))}\ncommit={Environment.GetEnvironmentVariable("GITHUB_SHA")}\n");
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(60));
            Assert.NotEmpty(notch.Properties.ItemStatus.Value);
            Assert.Null(_boo.FindPanel());
            Record("notch ready; one Ctrl+Alt+B open chord");
            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(TimeSpan.FromSeconds(8));
            var rect = notch.BoundingRectangle;
            Mouse.MoveTo(new Point(rect.Left + rect.Width / 2, rect.Top + 2));
            Assert.True(BooAppFixture.WaitUntil(() => notch.FindFirstDescendant(cf => cf.ByAutomationId("OpenButton"))
                is { IsOffscreen: false } e && e.BoundingRectangle.Height > 0, TimeSpan.FromSeconds(10)));
            var eyes = notch.FindFirstDescendant(cf => cf.ByAutomationId("OpenButton"))!.BoundingRectangle;
            Mouse.MoveTo(new Point(eyes.Left + eyes.Width / 2, eyes.Top + Math.Min(20, eyes.Height / 2)));
            using var faces = new FaceRecorder(_boo);
            Assert.True(BooAppFixture.WaitUntil(() => faces.Count >= 2, TimeSpan.FromSeconds(2)));
            var mark = faces.Mark;
            var dataBefore = DataHashes();
            Mouse.Down(MouseButton.Left);
            down = clock.ElapsedMilliseconds;
            try
            {
                Record($"physical left down at {down}ms");
                Assert.True(BooAppFixture.WaitUntil(() =>
                {
                    NoApproval(panel);
                    return notch.Properties.ItemStatus.Value == "Listening" && faces.SeenSince("Listening", mark)
                        && Counter("inputBytes") > 0;
                }, TimeSpan.FromSeconds(20)), "No actual Listening face and positive held input.");
                Capture("j10b-listening");
                // Offline-generated short question must finish before the physical three-second release.
                Assert.True(BooAppFixture.WaitUntil(() =>
                {
                    NoApproval(panel);
                    return clock.ElapsedMilliseconds - down >= 3000
                        && Counter("expectedInputBytes") > 0 && Counter("inputBytes") == Counter("expectedInputBytes");
                },
                    TimeSpan.FromMilliseconds(Math.Max(1, 3500 - (clock.ElapsedMilliseconds - down)))),
                    "Full WAV not captured within the existing 20s input bound.");
                Assert.InRange(clock.ElapsedMilliseconds - down, 3000L, 3500L);
            }
            finally
            {
                Mouse.Up(MouseButton.Left);
                up = clock.ElapsedMilliseconds;
                Record($"physical left up at {up}ms; actual hold={up - down}ms; required hold[3000,3500]ms, full short WAV");
            }
            Assert.InRange(up - down, 3000L, 3500L); // Bound the completed physical release, including input/scheduling delay.
            Assert.True(BooAppFixture.WaitUntil(() =>
            {
                NoApproval(panel);
                if (notch.Properties.ItemStatus.Value == "Responding"
                    && !File.Exists(Path.Combine(_boo.ArtifactsDirectory, "j10b-responding.capture.txt")))
                    Capture("j10b-responding");
                var rows = Rows(panel);
                return panel.FindFirstDescendant(cf => cf.ByAutomationId("StatusText"))!.Properties.Name.Value
                    .StartsWith("Reply complete.", StringComparison.Ordinal)
                    && rows.Any(HeardQuestion) && rows.Any(s => s.StartsWith("Boo: ", StringComparison.Ordinal)
                        && Regex.IsMatch(s, @"\b(five|5)\b", RegexOptions.IgnoreCase))
                    && Counter("renderedBytes") >= 4800 && Counter("nonzeroBytes") > 0;
            }, TimeSpan.FromSeconds(45)), "No real heard 2+3, completed five reply and consumed nonzero audio.");
            faces.AssertHealthySince(mark);
            Assert.True(faces.SeenSince("Listening", mark));
            Assert.True(faces.SeenSince("Responding", mark));
            Capture("j10b-reply");
            Assert.True(File.Exists(Path.Combine(_boo.ArtifactsDirectory, "j10b-responding.capture.txt")));
            Assert.Equal(1, Counter("captureStarts"));
            Assert.True(Counter("expectedInputBytes") > 0);
            Assert.InRange(Counter("expectedInputBytes"), 4800L, 120000L); // 0.1-2.5 seconds at PCM24k/mono16; complete input required.
            Assert.Equal(Counter("expectedInputBytes"), Counter("inputBytes"));
            NoApproval(panel);
            var changed = DataHashes().Where(p => !dataBefore.TryGetValue(p.Key, out var hash) || hash != p.Value).Select(p => p.Key).ToArray();
            _boo.WriteObservation("j10b-negative-side-effects", $"changed data files={string.Join(",", changed)}\n" +
                "No visible approval UI throughout hold/reply observation; no action clicks issued.\n" +
                "External filesystem/process side effects UNVERIFIED; no product action audit log is exposed by this harness.\n");
            Assert.DoesNotContain(changed, p => p.Contains("undo", StringComparison.OrdinalIgnoreCase)
                || p.Contains("action", StringComparison.OrdinalIgnoreCase));
            _boo.WriteObservation("j10b-result", string.Join("\n", Rows(panel)) +
                $"\nholdMs={up - down}\nProvider attempt count UNVERIFIED. Canonical j10 capture-loss UNVERIFIED.\n");
        }
        catch
        {
            Record($"FAIL once; no retry; down={down} up={up}");
            Capture("j10b-FAILED");
            throw;
        }
    }

    private Dictionary<string, string> DataHashes() => Directory.GetFiles(_boo.DataDirectory, "*", SearchOption.AllDirectories)
        .Where(p => !Path.GetFileName(p).Contains("credential", StringComparison.OrdinalIgnoreCase))
        .ToDictionary(p => Path.GetRelativePath(_boo.DataDirectory, p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
    private static string[] Rows(Window panel) => panel.FindFirstDescendant(cf => cf.ByAutomationId("TranscriptList"))!
        .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(e => e.Properties.Name.Value).ToArray();
    private static bool HeardQuestion(string s) => s.StartsWith("You: ", StringComparison.Ordinal)
        && Regex.IsMatch(s, @"\b(two|2)\b", RegexOptions.IgnoreCase)
        && Regex.IsMatch(s, @"\b(three|3)\b", RegexOptions.IgnoreCase);
    private static void NoApproval(Window panel) => Assert.DoesNotContain(panel.FindAllDescendants(), e =>
        !e.IsOffscreen && e.Properties.Name.ValueOrDefault == "Review action");
    private long Counter(string property)
    {
        var path = Path.Combine(_boo.ArtifactsDirectory, "j10b-device.jsonl");
        if (!File.Exists(path)) return 0;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        var content = reader.ReadToEnd();
        var end = content.LastIndexOf('\n');
        if (end < 0) return 0;
        using var doc = JsonDocument.Parse(content[..end].Split('\n')[^1]);
        return doc.RootElement.GetProperty(property).GetInt64();
    }
    private void Capture(string name)
    {
        _boo.CaptureScreen(name);
        var manifest = Path.Combine(_boo.ArtifactsDirectory, name + ".capture.txt");
        Assert.True(File.Exists(manifest));
        Assert.NotEmpty(File.ReadAllText(manifest));
        var images = Directory.GetFiles(_boo.ArtifactsDirectory, name + ".boo-*.png");
        Assert.NotEmpty(images);
        foreach (var path in images)
        {
            using var image = Image.FromFile(path);
            Assert.True(image.Width > 0 && image.Height > 0);
        }
    }

    // Same 40ms sampling / maximum 100ms UIA read and inter-read gap as QaUi.NotchStateRecorder.
    // Read exceptions are retained as failed observations and fail the action-window health assertion.
    private sealed class FaceRecorder : IDisposable
    {
        private readonly BooAppFixture _boo;
        private readonly Thread _thread;
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly object _gate = new();
        private readonly List<(long Start, long End, string State)> _reads = new();
        private volatile bool _stop;
        public long Mark => _clock.ElapsedMilliseconds;
        public int Count { get { lock (_gate) return _reads.Count; } }
        public FaceRecorder(BooAppFixture boo)
        {
            _boo = boo;
            _thread = new Thread(Run) { IsBackground = true };
            _thread.SetApartmentState(ApartmentState.MTA);
            _thread.Start();
        }
        public bool SeenSince(string state, long mark)
        { lock (_gate) return _reads.Any(r => r.Start > mark && r.State == state); }
        private void Run()
        {
            using var automation = new UIA3Automation();
            Window? notch = null;
            while (!_stop)
            {
                var start = Mark;
                var state = string.Empty;
                try
                {
                    notch ??= _boo.App.GetAllTopLevelWindows(automation).Single(w =>
                        w.Properties.Name.Value is BooAppFixture.NotchName or BooAppFixture.NotchTitle);
                    state = notch.Properties.ItemStatus.Value;
                }
                catch (Exception ex) { state = "ERROR:" + ex.GetType().Name; notch = null; }
                lock (_gate) _reads.Add((start, Mark, state));
                Thread.Sleep(40);
            }
        }
        public void AssertHealthySince(long mark)
        {
            var end = Mark;
            (long Start, long End, string State)[] reads;
            lock (_gate) reads = _reads.Where(r => r.Start > mark && r.End <= end).ToArray();
            Save();
            Assert.True(reads.Length >= 2);
            Assert.True(reads[0].Start <= mark + 100 && reads[^1].End >= end - 100);
            for (var i = 0; i < reads.Length; i++)
            {
                Assert.NotEmpty(reads[i].State);
                Assert.False(reads[i].State.StartsWith("ERROR:", StringComparison.Ordinal));
                Assert.True(reads[i].End - reads[i].Start <= 100, "Face UIA read exceeded 100ms.");
                if (i > 0) Assert.True(reads[i].Start - reads[i - 1].End <= 100, "Face observation gap exceeded 100ms.");
            }
        }
        private void Save()
        { lock (_gate) _boo.WriteObservation("j10b-faces", string.Join("\n", _reads.Select(r => $"[{r.Start},{r.End}] {r.State}"))); }
        public void Dispose()
        {
            _stop = true;
            Assert.True(_thread.Join(TimeSpan.FromSeconds(2)), "Face recorder did not stop.");
            Save();
        }
    }
}
