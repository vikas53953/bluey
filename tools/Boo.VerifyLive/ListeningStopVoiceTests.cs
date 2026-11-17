using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using FlaUI.UIA3;

namespace Boo.VerifyLive;

// Composition makes environment restoration cover launch failures as well as successful launches.
public sealed class ListeningStopVoiceFixture : IDisposable
{
    public BooAppFixture Boo { get; }
    private static readonly string[] Variables = ["BOO_CAPTURE_SCOPE", "BOO_QA_VOICE_INPUT",
        "BOO_QA_VOICE_EVIDENCE", "BOO_QA_VOICE_TRANSCRIBE_MODEL", HostedVoiceOptions.ToolTurnVariable,
        "BOO_QA_CAPTURE_FAULT"];

    public ListeningStopVoiceFixture()
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
            throw new InvalidOperationException("J12C requires GitHub-hosted Windows; no local run.");
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();
        var folder = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(folder)) throw new InvalidOperationException("J12C evidence folder required.");
        Directory.CreateDirectory(folder);
        File.WriteAllLines(Path.Combine(folder, "j12c-hosts.txt"), accepted);
        var key = Environment.GetEnvironmentVariable("BOO_LIVE_OPENAI_KEY");
        if (string.IsNullOrWhiteSpace(key) || AiProviders.CredentialRejectReason(AiProvider.OpenAI, key) is not null)
            throw new InvalidOperationException("Native voice key missing or invalid (value withheld).");
        var model = Environment.GetEnvironmentVariable("BOO_QA_VOICE_MODEL") ?? "gpt-realtime-2.1-mini";
        var transcription = Environment.GetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL") ?? "gpt-4o-mini-transcribe";
        if (!AiProviders.IsValidModelId(model) || !AiProviders.IsValidModelId(transcription))
            throw new InvalidOperationException("Invalid voice model identifier.");
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_TRANSCRIBE_MODEL", transcription);
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_INPUT", Environment.GetEnvironmentVariable("BOO_QA_EYE_HOLD_INPUT") ?? throw new InvalidOperationException("Short eye-hold WAV required."));
        Environment.SetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE", Path.Combine(folder, "j12c-device.jsonl"));
        Environment.SetEnvironmentVariable(HostedVoiceOptions.ToolTurnVariable, null);
        Environment.SetEnvironmentVariable("BOO_QA_CAPTURE_FAULT", null);
        if (File.Exists(Path.Combine(folder, "j12c-device.jsonl")))
            throw new InvalidOperationException("J12C requires a fresh evidence folder.");
        new CredentialStore(Path.Combine(data, AiProviders.CredentialFileName(AiProvider.OpenAI))).Save(key);
        new SettingsStore(Path.Combine(data, "settings.json")).Save(new AppSettings(SpeechEnabled: true,
            ChirpsEnabled: false, Provider: new ProviderSettings(RealtimeModel: model)));
        File.WriteAllText(Path.Combine(folder, "j12c-models.txt"), $"model={model}\ntranscription={transcription}\n" +
            "One capture; existing product hosted-audio plain-response cap: one response, 128 output tokens.\n" +
            "Provider attempt count UNVERIFIED: device counters do not count websocket requests.\n" +
            "Synthetic hosted input/output devices; physical microphone/speakers UNVERIFIED.\n");
    }
}

public sealed class ListeningStopVoiceTests : IClassFixture<ListeningStopVoiceFixture>
{
    private readonly BooAppFixture _boo;
    private readonly List<string> _actions = new();
    public ListeningStopVoiceTests(ListeningStopVoiceFixture fixture) => _boo = fixture.Boo;
    private void Record(string text)
    {
        _actions.Add($"{DateTimeOffset.UtcNow:O} {text}");
        _boo.WriteObservation("j12c-actions", string.Join("\n", _actions));
    }

    [Fact]
    public void J12C_StopWhileChordHeld_DoesNotRestartOnRelease()
    {
        var clock = Stopwatch.StartNew();
        long stopAt = -1;
        try
        {
            using var exe = File.OpenRead(_boo.ExePath);
            _boo.WriteObservation("j12c-identity", $"exe={_boo.ExePath}\npid={_boo.App.ProcessId}\n" +
                $"sha256={Convert.ToHexString(SHA256.HashData(exe))}\ncommit={Environment.GetEnvironmentVariable("GITHUB_SHA")}\n");
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(60));
            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(TimeSpan.FromSeconds(8));
            using var faces = new FaceRecorder(_boo, panel.Properties.NativeWindowHandle.Value);
            Assert.True(BooAppFixture.WaitUntil(() => faces.Count >= 2, TimeSpan.FromSeconds(2)));
            var mark = faces.Mark;
            Capture("j12c-before-hold");
            var before = Rows(panel);
            _boo.WriteObservation("j12c-transcript-before", string.Join("\n", before));
            try
            {
                Keyboard.Press(VirtualKeyShort.CONTROL);
                Keyboard.Press(VirtualKeyShort.ALT);
                Keyboard.Press(VirtualKeyShort.SPACE);
                Record($"physical Ctrl+Alt+Space down clockMs={clock.ElapsedMilliseconds}");
                Assert.True(BooAppFixture.WaitUntil(() =>
                {
                    NoApproval(panel);
                    return notch.Properties.ItemStatus.Value == "Listening"
                        && faces.SeenSince("Listening", mark) && Counter("inputBytes") > 0;
                }, TimeSpan.FromSeconds(20)), "Actual Listening and positive native hosted input required.");
                Capture("j12c-listening");
                Assert.Equal("Listening", notch.Properties.ItemStatus.Value);
                var input = Counter("inputBytes");
                var starts = Counter("captureStarts");
                Assert.Equal(1L, starts);
                Assert.True(input > 0 && input < Counter("expectedInputBytes"),
                    "SETUP: require active, unfinished input at Stop; no retry.");
                Assert.InRange(Counter("expectedInputBytes"), 4800L, 120000L);
                Assert.Equal(0L, Counter("nonzeroBytes"));
                var rendered = Counter("renderedBytes");
                var atStop = Rows(panel);
                Assert.Equal(before, atStop);
                // Timestamp precedes physical dispatch; modifiers and Space stay held.
                stopAt = clock.ElapsedMilliseconds;
                Record($"one physical F12 down; actionClockMs={stopAt}; deadlineMs={stopAt + 1000}; inputBytes={input}; captureStarts={starts}; renderedBytes={rendered}");
                Keyboard.Press(VirtualKeyShort.F12);
                Keyboard.Release(VirtualKeyShort.F12);
                var acknowledged = false;
                var cleared = false;
                do
                {
                    NoApproval(panel);
                    // Existing product acknowledgement: NotchWindow.ShowStoppedBriefly -> HelpText.
                    acknowledged |= notch.Properties.HelpText.Value.Contains("Stopped", StringComparison.Ordinal);
                    cleared = notch.Properties.ItemStatus.Value != "Listening";
                    if (acknowledged && cleared) break;
                    Thread.Sleep(40);
                } while (clock.ElapsedMilliseconds < stopAt + 1000);
                Assert.True(clock.ElapsedMilliseconds <= stopAt + 1000 && acknowledged && cleared,
                    "Stop acknowledgement and actual Listening clear must share the action's 1s deadline.");
                // Capture may advance before Stop is processed; freeze at its acknowledged boundary.
                input = Counter("inputBytes");
                Assert.True(input > 0 && input < Counter("expectedInputBytes"),
                    "Stop must cancel unfinished capture, not coincide with natural WAV exhaustion.");
                Stable();
                Record($"ack and Listening cleared clockMs={clock.ElapsedMilliseconds}");
                try
                {
                    Watch("Space still held");
                    Capture("j12c-stopped-held");
                }
                finally { ReleaseAll(); }

                void Stable()
                {
                    Record($"device snapshot clockMs={clock.ElapsedMilliseconds}; inputBytes={Counter("inputBytes")}; captureStarts={Counter("captureStarts")}; renderedBytes={Counter("renderedBytes")}; nonzeroBytes={Counter("nonzeroBytes")}");
                    Assert.Equal(input, Counter("inputBytes")); // No invented late-frame allowance.
                    Assert.Equal(starts, Counter("captureStarts"));
                    Assert.Equal(rendered, Counter("renderedBytes"));
                    Assert.Equal(0L, Counter("nonzeroBytes"));
                    Assert.Equal(atStop, Rows(panel));
                }
                void Watch(string phase)
                {
                    var until = clock.ElapsedMilliseconds + 3000;
                    Record($"watch {phase} startMs={clock.ElapsedMilliseconds}; untilMs={until}");
                    do
                    {
                        NoApproval(panel);
                        var face = notch.Properties.ItemStatus.Value;
                        Assert.NotEqual("Listening", face);
                        Assert.NotEqual("Responding", face);
                        Stable();
                        Thread.Sleep(40);
                    } while (clock.ElapsedMilliseconds < until);
                    Record($"watch {phase} completeMs={clock.ElapsedMilliseconds}");
                }
                Record($"physical chord released clockMs={clock.ElapsedMilliseconds}");
                Watch("after chord release");
                Capture("j12c-after-release");
                faces.AssertHealthySince(mark);
                _boo.WriteObservation("j12c-transcript-after", string.Join("\n", Rows(panel)));
                _boo.WriteObservation("j12c-result", "Sampled UI/device assertions passed. Provider request count UNVERIFIED: no request receipt. " +
                    "No second request inferred from stable capture/input/playback/transcript and no renewed Listening. " +
                    "Existing hosted product ceiling: one response / 128 output tokens; Stop may yield zero responses. " +
                    "Physical microphone/speakers UNVERIFIED. Full journey UNVERIFIED until hosted evidence is inspected.");
            }
            finally { ReleaseAll(); }
        }
        catch (Exception ex)
        {
            Record($"RED once; no retry; stopAt={stopAt}; {ex}");
            Capture("j12c-FAILED");
            throw;
        }
    }

    private static void ReleaseAll()
    {
        try { Keyboard.Release(VirtualKeyShort.F12); }
        finally
        {
            try { Keyboard.Release(VirtualKeyShort.SPACE); }
            finally
            {
                try { Keyboard.Release(VirtualKeyShort.ALT); }
                finally { Keyboard.Release(VirtualKeyShort.CONTROL); }
            }
        }
    }

    private static string[] Rows(Window panel) => panel.FindFirstDescendant(cf => cf.ByAutomationId("TranscriptList"))!
        .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(e => e.Properties.Name.Value).ToArray();
    private static void NoApproval(Window panel) => Assert.DoesNotContain(
        panel.FindAllDescendants(cf => cf.ByName("Review action")), e => !e.IsOffscreen);
    private long Counter(string property)
    {
        var path = Path.Combine(_boo.ArtifactsDirectory, "j12c-device.jsonl");
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
        private readonly IntPtr _panelHandle;
        public long Mark => _clock.ElapsedMilliseconds;
        public int Count { get { lock (_gate) return _reads.Count; } }
        public FaceRecorder(BooAppFixture boo, IntPtr panelHandle)
        {
            _boo = boo;
            _panelHandle = panelHandle;
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
                    NoApproval(automation.FromHandle(_panelHandle).AsWindow());
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
        { lock (_gate) _boo.WriteObservation("j12c-faces", string.Join("\n", _reads.Select(r => $"[{r.Start},{r.End}] {r.State}"))); }
        public void Dispose()
        {
            _stop = true;
            Assert.True(_thread.Join(TimeSpan.FromSeconds(2)), "Face recorder did not stop.");
            Save();
        }
    }
}
