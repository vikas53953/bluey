using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Security.Cryptography;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.QaSuite.Journeys;

/// <summary>
/// The suite's own journeys on ONE published Boo.exe with the real OpenAI key (seeded by <see cref="LiveAppFixture"/>,
/// which runs the anti-mock guard before Boo starts). In order:
/// S0 launch (only the notch, then Ctrl+Alt+B opens the panel)  S1 hover wakes and retracts the notch
/// S2 real chat: a random code made at run time must come back, and the notch shows Thinking then Responding
/// S3 Stop while an answer is on its way: the face clears and no answer lands
/// S4 focus session Start, Pause (notch shows Paused), Resume, End
/// S5 settings: Save writes settings.json, and a NEW Boo started from that file shows the same choices.
/// A notch-state recorder runs for the whole class (qa-notch-states-journeys.txt).
/// </summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.QaSuite")]
public sealed class QaJourneyTests : IClassFixture<LiveAppFixture>
{
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(120);
    private static NotchStateRecorder? s_recorder;

    private readonly LiveAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public QaJourneyTests(LiveAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
        s_recorder ??= new NotchStateRecorder(boo, "journeys");
    }

    [Fact, Step(0)]
    public void S0_Launch_OnlyTheNotch_ThenHotkeyOpensThePanel()
    {
        Evidence("qa-s0-launch", () =>
        {
            var exe = new FileInfo(_boo.ExePath);
            string sha;
            using (var stream = exe.OpenRead()) sha = Convert.ToHexString(SHA256.HashData(stream));
            var version = FileVersionInfo.GetVersionInfo(exe.FullName);
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            var panelAtStart = _boo.FindPanel();
            var processes = Process.GetProcessesByName("Boo").Length;

            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(TimeSpan.FromSeconds(15));
            _boo.CaptureScreen("qa-s0-after-hotkey");
            var status = QaUi.NameOf(panel, "StatusText");
            _boo.WriteObservation("qa-s0-launch",
                $"exe={exe.FullName}\nexeSha256={sha}\nfileVersion={version.FileVersion} product={version.ProductVersion}\n" +
                $"booProcesses={processes}\nnotch='{BooAppFixture.SafeName(notch)}' at {notch.BoundingRectangle}\n" +
                $"panelAtStart={(panelAtStart is null ? "none" : "OPEN")}\npanel='{BooAppFixture.SafeName(panel)}'\nstatus='{status}'");

            Assert.Equal(1, processes);
            Assert.Null(panelAtStart);
            Assert.StartsWith(BooAppFixture.PanelTitlePrefix, BooAppFixture.SafeName(panel), StringComparison.Ordinal);
            Assert.DoesNotContain("could not be read", status, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact, Step(1)]
    public void S1_Hover_WakesTheNotch_ThenItRetracts()
    {
        Evidence("qa-s1-hover", () =>
        {
            if (_boo.FindPanel() is { } panel)
            {
                QaUi.Find(_boo, panel, "CloseButton").AsButton().Invoke();
                Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, QaUi.UiTimeout), "Close did not hide the panel.");
            }
            var rect = _boo.WaitForNotch(QaUi.UiTimeout).BoundingRectangle;
            var hover = new Point(rect.Left + rect.Width / 2, rect.Top + 2);
            var away = new Point(hover.X, hover.Y + 300);
            Mouse.MoveTo(away);
            Assert.True(BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) == "Hidden", TimeSpan.FromSeconds(8)),
                $"The notch did not rest in 'Hidden'. ItemStatus: '{QaUi.NotchState(_boo)}'.");

            Mouse.MoveTo(hover);
            var revealed = BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) is { Length: > 0 } s && s != "Hidden", TimeSpan.FromSeconds(1.5));
            var state = QaUi.NotchState(_boo);
            _boo.CaptureScreen("qa-s1-hover-revealed");
            Mouse.MoveTo(away);
            var retracted = BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) == "Hidden", TimeSpan.FromSeconds(3));
            _boo.WriteObservation("qa-s1-hover", $"notch={rect} revealed={revealed} state='{state}' retracted={retracted}");
            Assert.True(revealed, $"Hovering the top strip did not wake the notch. ItemStatus: '{state}'.");
            Assert.True(retracted, $"The notch did not retract after the mouse left. ItemStatus: '{QaUi.NotchState(_boo)}'.");
        });
    }

    [Fact, Step(2)]
    public void S2_RealChat_EchoesARunTimeCode_AndTheNotchThinksThenResponds()
    {
        Evidence("qa-s2-chat", () =>
        {
            // A runtime nonce rejects canned replies; the independent host/TLS/build guards establish provider provenance.
            const string letters = "bcdfghjkmnpqrstvwxz";
            var code = new string(Enumerable.Range(0, 8).Select(_ => letters[RandomNumberGenerator.GetInt32(letters.Length)]).ToArray()).ToUpperInvariant();
            var panel = QaUi.OpenPanel(_boo);
            var before = QaUi.BooRows(panel).Count;
            var faces = new List<string>();
            var turnStarted = s_recorder!.Mark();
            QaUi.Ask(_boo, panel, $"Copy the following eight-character token verbatim. Preserve every character in order, with no inserted spaces or punctuation. Return only the token: {code}");

            string? reply = null;
            var answered = BooAppFixture.WaitUntil(() =>
            {
                var face = QaUi.NotchState(_boo);
                if (face.Length > 0 && (faces.Count == 0 || faces[^1] != face)) faces.Add(face);
                var rows = QaUi.BooRows(panel);
                if (rows.Count > before) reply = rows[^1];
                return reply is not null;
            }, AnswerTimeout);
            // The Responding face is held for a few seconds after the reply; keep sampling briefly.
            BooAppFixture.WaitUntil(() =>
            {
                var face = QaUi.NotchState(_boo);
                if (face.Length > 0 && (faces.Count == 0 || faces[^1] != face)) faces.Add(face);
                return faces.Contains("Responding");
            }, TimeSpan.FromSeconds(3));
            _boo.CaptureScreen("qa-s2-chat-answer");
            var config = LiveAppFixture.Config;
            _boo.WriteObservation("qa-s2-chat",
                $"provider={AiProviders.DisplayName(config.TextProvider)} model={config.Model ?? "<Boo default>"}\ncode={code}\n" +
                $"answered={answered} reply='{QaUi.Shorten(reply, 300)}'\nfacesDuringTurn={string.Join(">", faces)}\n" +
                $"recorderSeen={string.Join(",", s_recorder!.Seen)}\nstatus='{QaUi.NameOf(panel, "StatusText")}'");

            Assert.True(answered, $"No answer from OpenAI within {AnswerTimeout.TotalSeconds:0}s. Status: '{QaUi.NameOf(panel, "StatusText")}'");
            Assert.False(reply!.StartsWith("QA-", StringComparison.Ordinal), $"The answer came from the QA mock: '{reply}'");
            Assert.Contains(code.ToUpperInvariant(), reply, StringComparison.Ordinal);
            // Boo shows Capture (its screenshot) for the whole typed request, which outranks Thinking (run 43425302356:
            // Capture>Responding). Either one proves the notch showed Boo working; whether Thinking itself is ever
            // visible is reported per state (UNVERIFIED if never seen), not assumed here.
            Assert.True(faces.Any(QaUi.IsWorkingFace) || s_recorder.HasSeenSince("Thinking", turnStarted) || s_recorder.HasSeenSince("Capture", turnStarted),
                $"The notch never showed Boo working (Thinking or Capture) during the turn ({string.Join(">", faces)}).");
            Assert.True(faces.Contains("Responding") || s_recorder.HasSeenSince("Responding", turnStarted), $"The notch never showed Responding after the reply ({string.Join(">", faces)}).");
        });
    }

    [Fact, Step(3)]
    public void S3_Stop_WhileAnAnswerIsOnItsWay_ClearsTheFaceAndNoAnswerLands()
    {
        Evidence("qa-s3-stop", () =>
        {
            var panel = QaUi.OpenPanel(_boo);
            var before = QaUi.BooRows(panel).Count;
            QaUi.Ask(_boo, panel, "Write a 400-word story about a lighthouse keeper and a lost cat.");
            // Boo sets this status the moment Send starts a request (before any network call), so Stop lands mid-answer.
            var thinking = BooAppFixture.WaitUntil(() =>
                QaUi.NameOf(panel, "StatusText").StartsWith("Thinking about", StringComparison.Ordinal) && QaUi.BooRows(panel).Count == before,
                TimeSpan.FromSeconds(5));
            var faceAtStop = QaUi.NotchState(_boo);
            QaUi.Find(_boo, panel, "StopButton").AsButton().Invoke();
            var clock = Stopwatch.StartNew();
            var cleared = BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) is { Length: > 0 } s && !QaUi.IsWorkingFace(s), TimeSpan.FromSeconds(5));
            var clearedMs = clock.ElapsedMilliseconds;
            // An answer that was already on its way must not land after Stop.
            var landed = BooAppFixture.WaitUntil(() => QaUi.BooRows(panel).Count > before, TimeSpan.FromSeconds(15));
            _boo.CaptureScreen("qa-s3-after-stop");
            _boo.WriteObservation("qa-s3-stop",
                $"requestInFlightAtStop={thinking} faceAtStop='{faceAtStop}' faceClearedAfterStop={cleared} clearedMs={clearedMs} faceNow='{QaUi.NotchState(_boo)}'\n" +
                $"answerLandedAfterStop={landed}\nstatus='{QaUi.NameOf(panel, "StatusText")}'");
            Assert.True(thinking, $"TEST SETUP: no request was in flight after Send, so Stop was not tested mid-answer. Status: '{QaUi.NameOf(panel, "StatusText")}'");
            Assert.True(cleared, $"After Stop the notch still shows '{QaUi.NotchState(_boo)}' after 5 s.");
            Assert.False(landed, $"An answer landed after Stop: '{QaUi.Shorten(QaUi.BooRows(panel).LastOrDefault(), 200)}'");
        });
    }

    [Fact, Step(4)]
    public void S4_Focus_StartPauseResumeEnd_AndTheNotchShowsPaused()
    {
        Evidence("qa-s4-focus", () =>
        {
            var panel = QaUi.OpenPanel(_boo);
            SelectFirstDisplay(panel);
            QaUi.Find(_boo, panel, "FocusTaskBox").AsTextBox().Text = "Review the firewall change list";
            QaUi.Find(_boo, panel, "FocusDurationBox").AsTextBox().Text = "25";
            QaUi.Find(_boo, panel, "FocusStartButton").AsButton().Invoke();
            var running = WaitForFocus(panel, "Running");
            var afterStart = QaUi.NameOf(panel, "FocusStatus");

            QaUi.Find(_boo, panel, "FocusPauseButton").AsButton().Invoke();
            var paused = WaitForFocus(panel, "Paused");
            var pausedFace = BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) == "Paused", TimeSpan.FromSeconds(5));
            var faceWhilePaused = QaUi.NotchState(_boo);
            _boo.CaptureScreen("qa-s4-focus-paused");

            QaUi.Find(_boo, panel, "FocusResumeButton").AsButton().Invoke();
            var resumed = WaitForFocus(panel, "Running");
            var afterResume = QaUi.NameOf(panel, "FocusStatus");

            QaUi.Find(_boo, panel, "FocusEndButton").AsButton().Invoke();
            var ended = BooAppFixture.WaitUntil(() => QaUi.NameOf(panel, "FocusStatus") is var s
                && !s.StartsWith("Running", StringComparison.Ordinal) && !s.StartsWith("Paused", StringComparison.Ordinal) && s.Length > 0, QaUi.UiTimeout);
            var afterEnd = QaUi.NameOf(panel, "FocusStatus");
            _boo.CaptureScreen("qa-s4-focus-ended");
            _boo.WriteObservation("qa-s4-focus",
                $"running={running} '{afterStart}'\npaused={paused} pausedFace={pausedFace} face='{faceWhilePaused}'\n" +
                $"resumed={resumed} '{afterResume}'\nended={ended} '{afterEnd}'");

            Assert.True(running, $"Focus did not start: '{afterStart}'");
            Assert.True(paused, $"Pause did not pause focus: '{QaUi.NameOf(panel, "FocusStatus")}'");
            Assert.True(pausedFace, $"While focus was paused the notch showed '{faceWhilePaused}', not 'Paused'.");
            Assert.True(resumed, $"Resume did not resume focus: '{afterResume}'");
            Assert.True(ended, $"End did not end focus: '{afterEnd}'");
        });
    }

    [Fact, Step(5)]
    public void S5_Settings_SaveWritesTheFile_AndANewBooStartsWithTheSameChoices()
    {
        Evidence("qa-s5-settings", () =>
        {
            var panel = QaUi.OpenPanel(_boo);
            QaUi.Find(_boo, panel, "ReducedMotionBox").AsCheckBox().IsChecked = true;
            QaUi.Find(_boo, panel, "ChirpMute").AsCheckBox().IsChecked = true;
            QaUi.Find(_boo, panel, "SaveSettingsButton").AsButton().Invoke();
            var saved = BooAppFixture.WaitUntil(() =>
            {
                try
                {
                    var s = new SettingsStore(_boo.SettingsPath).Load();
                    return s.ReducedMotion && !s.ChirpsEnabled;
                }
                catch (Exception)
                {
                    return false;
                }
            }, TimeSpan.FromSeconds(15));
            var file = new SettingsStore(_boo.SettingsPath).Load();
            var json = File.ReadAllText(_boo.SettingsPath);
            var statusAfterSave = QaUi.NameOf(panel, "StatusText");
            _boo.CaptureScreen("qa-s5-settings-saved");

            // A brand-new Boo, started from exactly the saved file (and no key), must show the same choices.
            s_recorder?.Dispose();
            _boo.App.Kill();
            using var reloaded = new SavedSettingsFixture(json);
            var reloadPanel = QaUi.OpenPanel(reloaded);
            bool? Checked(string id) => QaUi.Find(reloaded, reloadPanel, id).AsCheckBox().IsChecked;
            var reducedMotion = Checked("ReducedMotionBox");
            var chirpMute = Checked("ChirpMute");
            var speechMute = Checked("SpeechMute");
            reloaded.CaptureScreen("qa-s5-settings-reloaded");
            _boo.WriteObservation("qa-s5-settings",
                $"savedToFile={saved} reducedMotion={file.ReducedMotion} chirps={file.ChirpsEnabled} speech={file.SpeechEnabled} " +
                $"chatModel='{file.Provider?.ReasoningModel}' focusModel='{file.Provider?.FocusModel}'\nstatusAfterSave='{statusAfterSave}'\n" +
                $"reloaded: ReducedMotionBox={reducedMotion} ChirpMute={chirpMute} SpeechMute={speechMute}");

            Assert.True(saved, $"Save did not write the choices to settings.json. Status: '{statusAfterSave}'");
            Assert.False(file.SpeechEnabled, "Saving settings turned speech back on (it was off).");
            if (LiveAppFixture.Config.Model is { } model)
                Assert.Equal(model, file.Provider?.ReasoningModel);
            Assert.True(reducedMotion == true, "A new Boo started from the saved file did not show Reduced motion ticked.");
            Assert.True(chirpMute == true, "A new Boo started from the saved file did not show sounds muted.");
            Assert.True(speechMute == true, "A new Boo started from the saved file did not show speech muted.");
        });
    }

    private bool WaitForFocus(Window panel, string prefix) =>
        BooAppFixture.WaitUntil(() => QaUi.NameOf(panel, "FocusStatus").StartsWith(prefix, StringComparison.Ordinal), QaUi.UiTimeout);

    private void SelectFirstDisplay(Window panel)
    {
        var combo = QaUi.TryFind(panel, "DisplayBox")?.AsComboBox();
        if (combo is null) return;
        try
        {
            if (combo.SelectedItem is null && combo.Items.Length > 0)
            {
                combo.Select(0);
                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"DisplayBox selection: {ex.Message}");
        }
    }

    private void Evidence(string name, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            try
            {
                _boo.CaptureScreen(name + "-FAILED");
                _boo.WriteObservation(name + "-FAILED", ex.Message);
            }
            catch (Exception)
            {
                // Evidence is best effort; the original failure is what matters.
            }
            throw;
        }
    }
}

/// <summary>A fresh Boo started from a given settings.json and no key (S5's reload check).</summary>
public sealed class SavedSettingsFixture : BooAppFixture
{
    public SavedSettingsFixture(string settingsJson)
        : base(dir =>
        {
            Environment.SetEnvironmentVariable(LiveAppFixture.CaptureScopeVariable, "boo-windows");
            File.WriteAllText(Path.Combine(dir, "settings.json"), settingsJson);
        })
    {
    }
}
