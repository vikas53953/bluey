using System.Diagnostics;
using System.IO;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.Explorer;

/// <summary>
/// No key saved: notch states, rapid hover / hotkey, wrong-order clicks, focus edges, settings, and a question with no
/// key. ZERO OpenAI requests (no key = Boo never builds a request; MissingCredentialMessage is thrown before any HTTP).
/// </summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.Explorer")]
public sealed class NoKeyExplorerTests : ExplorerSteps<BooAppFixture>, IClassFixture<NoKeyFixture>
{
    public NoKeyExplorerTests(NoKeyFixture boo, ITestOutputHelper output) : base(boo, output) { }

    [Fact, Step(1)]
    public void X01_Startup_NotchRestsHidden_NoPanel()
    {
        Explore("x01-startup", faces =>
        {
            Boo.WaitForNotch(UiTimeout);
            var hidden = SettlesHidden(TimeSpan.FromSeconds(8));
            Boo.WriteObservation("x01-startup", $"face='{NotchFace()}' panelOpen={Boo.FindPanel() is not null}");
            Assert.True(hidden, $"The notch did not rest Hidden after start; face '{NotchFace()}'.");
            Assert.Null(Boo.FindPanel());
        });
    }

    [Fact, Step(2)]
    public void X02_RapidHotkey_NeverTwoPanels_AlwaysOpenAfterLastPress()
    {
        Explore("x02-rapid-hotkey", faces =>
        {
            var rng = Rng("x02");
            var gaps = new List<int>();
            for (var i = 0; i < 16; i++)
            {
                BooAppFixture.PressOpenShortcut();
                var gap = rng.Next(20, 400);
                gaps.Add(gap);
                Thread.Sleep(gap);
            }
            Thread.Sleep(1500);
            var openAfterBurst = Boo.FindPanel() is not null;
            // Ctrl+Alt+B is "open Boo" (App.OpenPanel never hides): after any burst the panel must be open.
            var opened = BooAppFixture.WaitUntil(() => Boo.FindPanel() is not null, TimeSpan.FromSeconds(5));
            Boo.CaptureScreen("x02-after-burst");
            Boo.WriteObservation("x02-rapid-hotkey", $"gapsMs={string.Join(",", gaps)} panelOpenAfterBurst={openAfterBurst} face='{NotchFace()}'");
            Assert.True(opened, "After 16 quick Ctrl+Alt+B presses the panel is not open.");
        });
    }

    [Fact, Step(3)]
    public void X03_RapidHover_NotchAlwaysSettlesHidden()
    {
        Explore("x03-rapid-hover", faces =>
        {
            ClosePanel();
            var rng = Rng("x03");
            var hover = NotchHoverPoint();
            var away = AwayPoint();
            var plan = new List<string>();
            for (var i = 0; i < 24; i++)
            {
                Mouse.MoveTo(hover);
                var on = rng.Next(20, 700);
                Thread.Sleep(on);
                Mouse.MoveTo(away);
                var off = rng.Next(20, 1300); // crosses the 240 ms reveal, the 900 ms delay and the 160 ms retract
                Thread.Sleep(off);
                plan.Add($"{on}/{off}");
            }
            var settled = SettlesHidden(TimeSpan.FromSeconds(4));
            var unknown = faces.Faces.Where(f => !FaceSampler.KnownFaces.Contains(f)).Distinct().ToList();
            Boo.WriteObservation("x03-rapid-hover", $"onOffMs={string.Join(",", plan)}\nfinalFace='{NotchFace()}' settled={settled}\nunknownFaces={string.Join(",", unknown)}");
            Assert.True(settled, $"After 24 random hover in/out moves the notch did not settle Hidden within 4 s; face '{NotchFace()}'.");
            Assert.Empty(unknown);
            Assert.DoesNotContain(faces.Faces, f => f is "Thinking" or "Responding" or "Capture" or "Listening");
        });
    }

    [Fact, Step(4)]
    public void X04_WrongOrderClicks_PanelStaysSane()
    {
        Explore("x04-wrong-order", faces =>
        {
            var panel = OpenPanel();
            var notes = new List<string>();

            // Focus controls before any session exists.
            foreach (var id in new[] { "FocusPauseButton", "FocusResumeButton", "FocusEndButton" })
            {
                var b = TryFind(panel, id);
                var enabled = IsEnabled(b);
                if (enabled && Visible(b) is not null) b!.AsButton().Invoke();
                Thread.Sleep(300);
                notes.Add($"{id}: enabled={enabled} -> focusStatus='{NameOfId(panel, "FocusStatus")}' face='{NotchFace()}'");
            }

            // Approve / Cancel with no proposal: must be unreachable.
            foreach (var id in new[] { "ApproveButton", "CancelButton" })
            {
                var b = TryFind(panel, id);
                notes.Add($"{id}: visible={Visible(b) is not null} enabled={IsEnabled(b)}");
                Assert.True(Visible(b) is null || !IsEnabled(b), $"{id} is clickable with no proposed action.");
            }

            // Send with nothing typed (twice), then Stop with nothing running.
            Type(panel, "   ");
            Find(panel, "SendButton").AsButton().Invoke();
            Find(panel, "SendButton").AsButton().Invoke();
            Thread.Sleep(300);
            var emptyStatus = Status(panel);
            notes.Add($"empty send x2 -> status='{emptyStatus}' rows={Rows(panel).Count}");
            Assert.Equal("Type a question first.", emptyStatus);
            Assert.Empty(Rows(panel));

            if (TryFind(panel, "StopButton") is { } stop && IsEnabled(stop)) stop.AsButton().Invoke();
            PressStop();
            Thread.Sleep(600);
            notes.Add($"Stop x2 with nothing running -> status='{Status(panel)}' face='{NotchFace()}'");

            // Close, then reopen: a half-typed question must survive (or the loss is recorded).
            Type(panel, "draft question kept?");
            ClosePanel();
            panel = OpenPanel();
            var kept = Find(panel, "InputBox").AsTextBox().Text;
            notes.Add($"draft after close+reopen='{kept}'");
            Boo.CaptureScreen("x04-after-wrong-order");
            var settled = SettlesHidden(TimeSpan.FromSeconds(6));
            notes.Add($"settledHidden={settled} face='{NotchFace()}'");
            Boo.WriteObservation("x04-wrong-order", string.Join("\n", notes));
            Assert.True(settled, $"The notch stayed '{NotchFace()}' after Stop with nothing running.");
            Type(panel, string.Empty);
        });
    }

    [Fact, Step(5)]
    public void X05_AskWithNoKey_PlainMessage_NoStuckThinking()
    {
        Explore("x05-no-key-ask", faces =>
        {
            var panel = OpenPanel();
            SelectDisplay(panel);
            var clock = Stopwatch.StartNew();
            Type(panel, "What is the capital of France?");
            Find(panel, "SendButton").AsButton().Invoke();
            var done = BooAppFixture.WaitUntil(() => Status(panel) is { Length: > 0 } s && !LooksBusy(s), TimeSpan.FromSeconds(30));
            var status = Status(panel);
            var ms = clock.ElapsedMilliseconds;
            var settled = SettlesHidden(TimeSpan.FromSeconds(10));
            Boo.CaptureScreen("x05-no-key");
            Boo.WriteObservation("x05-no-key-ask", $"status='{status}' afterMs={ms} settled={settled} face='{NotchFace()}' rows='{string.Join(" || ", Rows(panel))}'");
            Assert.True(done, $"With no key Boo still looks busy after 30 s: '{status}'.");
            Assert.Contains("key", status, StringComparison.OrdinalIgnoreCase);
            Assert.True(settled, $"The notch stayed '{NotchFace()}' after a failed no-key question.");
            Assert.True(IsEnabled(TryFind(panel, "InputBox")), "The question box is disabled after the failure.");
        });
    }

    [Fact, Step(6)]
    public void X06_Focus_WrongOrderAndRestart_StatesStayConsistent()
    {
        Explore("x06-focus", faces =>
        {
            var panel = OpenPanel();
            var notes = new List<string>();
            void Click(string id, string label)
            {
                var b = Find(panel, id);
                var enabled = IsEnabled(b);
                if (enabled) b.AsButton().Invoke();
                Thread.Sleep(700);
                notes.Add($"{label}: enabled={enabled} focusStatus='{NameOfId(panel, "FocusStatus")}' face='{NotchFace()}'");
            }

            Find(panel, "FocusTaskBox").AsTextBox().Text = string.Empty;
            Click("FocusStartButton", "start with no task");
            Assert.Equal("Name the focus task first.", NameOfId(panel, "FocusStatus"));

            Find(panel, "FocusTaskBox").AsTextBox().Text = "Explorer focus task";
            Find(panel, "FocusDurationBox").AsTextBox().Text = "0";
            Click("FocusStartButton", "start with 0 minutes");
            Find(panel, "FocusDurationBox").AsTextBox().Text = "25";
            Click("FocusStartButton", "start");
            Click("FocusPauseButton", "pause");
            var pausedFace = BooAppFixture.WaitUntil(() => NotchFace() == "Paused", TimeSpan.FromSeconds(3));
            Click("FocusPauseButton", "pause again");
            Click("FocusResumeButton", "resume");
            Click("FocusResumeButton", "resume again");
            Click("FocusStartButton", "start while running (expect today: old session ends quietly, §5-F9)");
            Click("FocusPauseButton", "pause");
            Click("FocusEndButton", "end from paused");
            var happy = BooAppFixture.WaitUntil(() => NotchFace() == "Happy", TimeSpan.FromSeconds(3));
            Click("FocusResumeButton", "resume after end");
            Click("FocusPauseButton", "pause after end");
            Click("FocusEndButton", "end after end");
            Boo.CaptureScreen("x06-focus-end");
            var settled = SettlesHidden(TimeSpan.FromSeconds(8));
            notes.Add($"pausedFaceSeen={pausedFace} happySeen={happy} settled={settled}");
            Boo.WriteObservation("x06-focus", string.Join("\n", notes));
            Assert.True(pausedFace, "Pausing a focus session did not show the Paused face.");
            Assert.True(happy, "Ending a focus session did not show the Happy face.");
            Assert.True(settled, $"After the session ended the notch stayed '{NotchFace()}'.");
            Assert.DoesNotContain("Paused", NameOfId(panel, "FocusStatus"), StringComparison.Ordinal);
        });
    }

    [Fact, Step(7)]
    public void X07_Settings_BadInputRejected_GoodInputSaved_SurvivesKillAndRelaunch()
    {
        Explore("x07-settings", faces =>
        {
            var panel = OpenPanel();
            var notes = new List<string>();
            var before = BooAppFixture.ReadShared(Boo.SettingsPath);

            Find(panel, "AnthropicModelBox").AsTextBox().Text = "bad model id!";
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Thread.Sleep(600);
            notes.Add($"bad model -> status='{Status(panel)}'");
            Assert.StartsWith("Settings were not saved", Status(panel), StringComparison.Ordinal);
            Assert.Equal(before, BooAppFixture.ReadShared(Boo.SettingsPath));
            Find(panel, "AnthropicModelBox").AsTextBox().Text = string.Empty;

            var colourBefore = File.Exists(Boo.AppearancePath) ? BooAppFixture.ReadShared(Boo.AppearancePath) : "<none>";
            Find(panel, "BodyColorBox").AsTextBox().Text = "not-a-colour";
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Thread.Sleep(600);
            notes.Add($"bad colour -> status='{Status(panel)}' validation='{NameOfId(panel, "AppearanceValidationText")}'");
            Assert.Equal(colourBefore, File.Exists(Boo.AppearancePath) ? BooAppFixture.ReadShared(Boo.AppearancePath) : "<none>");
            Assert.Equal(before, BooAppFixture.ReadShared(Boo.SettingsPath));
            if (TryFind(panel, "ResetAppearanceButton") is { } reset && IsEnabled(reset)) reset.AsButton().Invoke();
            Thread.Sleep(400);

            // A good change: chirps off. Save twice quickly (double click).
            var chirp = Find(panel, "ChirpMute").AsCheckBox();
            chirp.IsChecked = true;
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => System.Text.RegularExpressions.Regex.IsMatch(BooAppFixture.ReadShared(Boo.SettingsPath),
                    "\"ChirpsEnabled\"\\s*:\\s*false", System.Text.RegularExpressions.RegexOptions.IgnoreCase), UiTimeout),
                "settings.json did not record chirps off. It reads: " + Shorten(BooAppFixture.ReadShared(Boo.SettingsPath), 500));
            var saved = BooAppFixture.ReadShared(Boo.SettingsPath);
            notes.Add($"good save -> status='{Status(panel)}'");

            // Kill Boo the hard way right after the save, relaunch on the same data, and read the setting back.
            var dataCopy = Path.Combine(Path.GetTempPath(), "boo-explorer-x07-" + Guid.NewGuid().ToString("N"));
            KilledOnPurpose = true;
            Boo.App.Kill();
            BooAppFixture.WaitUntil(() => Boo.App.HasExited, TimeSpan.FromSeconds(10));
            CopyDir(Boo.DataDirectory, dataCopy);
            using var again = new RelaunchFixture(dataCopy);
            var relaunchedPanel = OpenPanel(again);
            Thread.Sleep(1500);
            var relaunchProblems = HealthProblems(again);
            notes.Add("relaunch health: " + string.Join("; ", relaunchProblems.DefaultIfEmpty("ok")));
            Assert.Empty(relaunchProblems);
            var chirpAfter = relaunchedPanel.FindFirstDescendant(cf => cf.ByAutomationId("ChirpMute"))?.AsCheckBox().IsChecked;
            var savedAfter = BooAppFixture.ReadShared(again.SettingsPath);
            again.CaptureScreen("x07-after-relaunch");
            notes.Add($"after kill+relaunch: chirpMuteChecked={chirpAfter} settingsUnchanged={savedAfter == saved} crashLogs={Directory.Exists(Path.Combine(again.DataDirectory, "logs"))}");
            Boo.WriteObservation("x07-settings", string.Join("\n", notes));
            Assert.True(chirpAfter == true, "After a relaunch the saved 'chirps off' setting is not shown.");
            Assert.Equal(saved, savedAfter);
            Assert.False(again.App.HasExited, "Boo exited right after the relaunch.");
        });
    }

    internal static void CopyDir(string from, string to)
    {
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(to, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
