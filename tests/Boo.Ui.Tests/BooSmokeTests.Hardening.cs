using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Boo.Ui.Tests;

/// <summary>
/// Smoke steps 5+ on the same live Boo as steps 1-4 (Boo is single-instance). CI has no OpenAI key,
/// so these cover the honest offline paths:
/// 5. Ask and Talk with no key settle on a plain message; the microphone never turns on.
/// 6. Focus session Start / Pause / Resume / End: status text, clock, sessions.db and the history caption.
/// 7. Ctrl+Alt+F12 (emergency stop) pauses a running focus session ("Paused by Stop").
/// 8. The panel survives a below-minimum resize and maximise/restore, and still closes/reopens.
/// </summary>
public sealed partial class BooSmokeTests
{
    private const string EmptyQuestionStatus = "Type a question first.";

    [Fact, Step(5)]
    public void AskAndTalk_WithoutApiKey_FailHonestlyAndMicrophoneStaysOff()
    {
        Evidence("05-no-key", () =>
        {
            var panel = _boo.EnsurePanel();

            // An empty Send is refused with a known message; that also gives a clean status to wait from.
            SetText(panel, "InputBox", string.Empty);
            ResetStatusWithEmptySend(panel);

            // Typed question with no OpenAI key configured: Boo must settle on an honest message, not hang or crash.
            const string question = "What can you help me with right now?";
            SetText(panel, "InputBox", question);
            Find(panel, "SendButton").AsButton().Invoke();
            var settledAsk = WaitForSettledNoKeyStatus(panel, "Ask");
            _output.WriteLine($"Ask without key -> status '{settledAsk}'");
            Assert.False(_boo.App.HasExited, "Boo exited after a typed question with no key.");
            Assert.True(
                BooAppFixture.WaitUntil(() => string.IsNullOrEmpty(Find(panel, "InputBox").AsTextBox().Text), UiTimeout),
                "The question was not taken from the input box.");
            AssertMicrophoneOff(panel);
            _boo.WriteObservation(
                "05-ask-no-key",
                $"status='{settledAsk}' listening='{NameOfId(panel, "ListeningStatus")}' transcriptHasQuestion={HasDescendantNamed(panel, question)}");
            _boo.CaptureScreen("05-ask-no-key");

            // Talk with no key: the microphone must not start and the panel must say why, in plain words.
            ResetStatusWithEmptySend(panel);
            Find(panel, "TalkButton").AsButton().Invoke();
            var settledTalk = WaitForSettledNoKeyStatus(panel, "Talk");
            _output.WriteLine($"Talk without key -> status '{settledTalk}', listening '{NameOfId(panel, "ListeningStatus")}'");

            // Give a late (wrong) microphone start a moment to show itself, then check it never did.
            Thread.Sleep(1000);
            AssertMicrophoneOff(panel);
            Assert.False(_boo.App.HasExited, "Boo exited after Talk with no key.");
            _boo.WriteObservation(
                "05-talk-no-key",
                $"status='{StatusText(panel)}' listening='{NameOfId(panel, "ListeningStatus")}'");
            _boo.CaptureScreen("05-talk-no-key");
        });
    }

    [Fact, Step(6)]
    public void FocusSession_StartPauseResumeEnd_UpdatesStatusAndHistory()
    {
        Evidence("06-focus", () =>
        {
            var panel = _boo.EnsurePanel();
            const string task = "UI smoke focus task six";

            // Developer Mode stays off on purpose: its 4x clock finishes the 1-minute minimum session in ~15 s,
            // which would race the Pause/Resume checks below. 5 real minutes leaves plenty of room.
            SetText(panel, "FocusTaskBox", task);
            SetText(panel, "FocusDurationBox", "5");
            Find(panel, "FocusStartButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Running", StringComparison.Ordinal), "Running after Start");
            AssertFocusClockMoves(panel);
            _boo.CaptureScreen("06-running");

            Find(panel, "FocusPauseButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Paused", StringComparison.Ordinal), "Paused after Pause");
            AssertFocusClockFrozen(panel);
            Assert.True(
                BooAppFixture.WaitUntil(() => DatabaseContains(task) && DatabaseContains("Paused"), UiTimeout),
                "sessions.db does not contain the paused session.");
            _boo.CaptureScreen("06-paused");

            Find(panel, "FocusResumeButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Running", StringComparison.Ordinal), "Running after Resume");
            AssertFocusClockMoves(panel);
            _boo.CaptureScreen("06-resumed");

            Find(panel, "FocusEndButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Ended", StringComparison.Ordinal), "Ended after End");
            AssertFocusClockFrozen(panel);
            Assert.True(
                BooAppFixture.WaitUntil(() => DatabaseContains("Ended"), UiTimeout),
                "sessions.db does not record the ended session.");

            // The history caption switches from "No saved sessions..." once a session is saved and listed.
            Assert.True(
                BooAppFixture.WaitUntil(
                    () => NameOfId(panel, "HistoryStatus").StartsWith("Periodic observations are estimates", StringComparison.Ordinal),
                    UiTimeout),
                $"History did not update after End. HistoryStatus: '{NameOfId(panel, "HistoryStatus")}'");
            _boo.WriteObservation(
                "06-focus-history",
                $"historyStatus='{NameOfId(panel, "HistoryStatus")}' historyRowFound={HasDescendantNamed(panel, task)} timer='{NameOfId(panel, "FocusTimer")}'");
            _boo.CaptureScreen("06-ended");
            Assert.False(_boo.App.HasExited, "Boo exited during the focus session.");
        });
    }

    [Fact, Step(7)]
    public void EmergencyStopHotkey_PausesRunningFocusSession()
    {
        Evidence("07-emergency-stop", () =>
        {
            var panel = _boo.EnsurePanel();
            SetText(panel, "FocusTaskBox", "UI smoke stop task seven");
            SetText(panel, "FocusDurationBox", "5");
            Find(panel, "FocusStartButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Running", StringComparison.Ordinal), "Running after Start");
            _boo.CaptureScreen("07-running");

            // Ctrl+Alt+F12 is Boo's global emergency stop (NotchWindow EmergencyKey = 0x7B, VK_F12).
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.F12);
            WaitForFocusStatus(panel, s => s == "Paused by Stop; Resume is explicit.", "'Paused by Stop' after Ctrl+Alt+F12");
            AssertFocusClockFrozen(panel);
            AssertMicrophoneOff(panel);
            Assert.False(_boo.App.HasExited, "Boo exited after the emergency stop.");
            _boo.CaptureScreen("07-paused-by-stop");

            // Leave no running session behind for later steps.
            Find(panel, "FocusEndButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Ended", StringComparison.Ordinal), "Ended after End");
        });
    }

    [Fact, Step(8)]
    public void Panel_SurvivesSmallResizeAndMaximizeRestore()
    {
        Evidence("08-resize", () =>
        {
            var panel = _boo.EnsurePanel();
            var window = panel.Patterns.Window.Pattern;

            // Boo shows the panel maximised the first time; restore it so it can be resized.
            window.SetWindowVisualState(WindowVisualState.Normal);
            Assert.True(
                BooAppFixture.WaitUntil(() => window.WindowVisualState.Value == WindowVisualState.Normal, UiTimeout),
                "The companion window did not restore to Normal.");

            var transform = panel.Patterns.Transform.PatternOrDefault;
            if (transform is not null && transform.CanResize.ValueOrDefault)
            {
                transform.Resize(600, 400); // below MinWidth/MinHeight: WPF clamps it; Boo must not throw.
                Thread.Sleep(700);
                _output.WriteLine($"After small resize: {panel.BoundingRectangle}");
            }
            else
            {
                // Recorded, not failed: whether UIA offers Transform on this window is a host detail, not Boo behaviour.
                _boo.WriteObservation("08-resize-skipped", "TransformPattern unavailable or not resizable; small-size resize not exercised.");
            }
            AssertPanelUsable(panel);
            _boo.CaptureScreen("08-small");

            window.SetWindowVisualState(WindowVisualState.Maximized);
            Assert.True(
                BooAppFixture.WaitUntil(() => window.WindowVisualState.Value == WindowVisualState.Maximized, UiTimeout),
                "The companion window did not maximise.");
            AssertPanelUsable(panel);
            _boo.CaptureScreen("08-maximized");

            window.SetWindowVisualState(WindowVisualState.Normal);
            Assert.True(
                BooAppFixture.WaitUntil(() => window.WindowVisualState.Value == WindowVisualState.Normal, UiTimeout),
                "The companion window did not restore after maximise.");
            AssertPanelUsable(panel);
            _boo.CaptureScreen("08-restored");

            // Close and reopen still work after the size changes.
            Find(panel, "CloseButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout),
                "Close did not hide the companion window after resizing.");
            BooAppFixture.PressOpenShortcut();
            AssertPanelUsable(_boo.WaitForPanel(UiTimeout));
            Assert.False(_boo.App.HasExited, "Boo exited during resize/maximise/restore.");
        });
    }

    // ------------------------------------------------------------------ helpers for steps 5+

    private void ResetStatusWithEmptySend(Window panel)
    {
        Find(panel, "SendButton").AsButton().Invoke();
        Assert.True(
            BooAppFixture.WaitUntil(() => StatusText(panel) == EmptyQuestionStatus, UiTimeout),
            $"Empty Send did not ask for a question. Status: '{StatusText(panel)}'");
    }

    /// <summary>
    /// Waits for the status line to leave <see cref="EmptyQuestionStatus"/> and any in-progress text, and land
    /// on an honest no-key outcome: a message that mentions the key/credential, or one of Boo's known plain
    /// "provider unavailable" / "setup failed" messages. Also checks no secret or raw exception text leaked.
    /// </summary>
    private string WaitForSettledNoKeyStatus(Window panel, string action)
    {
        var settled = string.Empty;
        var ok = BooAppFixture.WaitUntil(() => IsSettledNoKeyStatus(settled = StatusText(panel)), TimeSpan.FromSeconds(30));
        Assert.True(
            ok,
            $"{action} without a key did not settle on an honest message within 30s. Status: '{StatusText(panel)}', " +
            $"listening: '{NameOfId(panel, "ListeningStatus")}'");
        Assert.DoesNotContain("sk-", settled, StringComparison.Ordinal);
        Assert.DoesNotContain("Exception", settled, StringComparison.Ordinal);
        Assert.DoesNotContain("   at ", settled, StringComparison.Ordinal);
        return settled;
    }

    private static bool IsSettledNoKeyStatus(string status)
    {
        if (string.IsNullOrWhiteSpace(status)
            || status == EmptyQuestionStatus
            || status.StartsWith("Thinking", StringComparison.Ordinal)
            || status.StartsWith("Sent to the voice conversation", StringComparison.Ordinal)
            || status.StartsWith("Microphone active", StringComparison.Ordinal))
        {
            return false;
        }

        return status.Contains("key", StringComparison.OrdinalIgnoreCase)
            || status.Contains("credential", StringComparison.OrdinalIgnoreCase)
            || status.Contains("provider", StringComparison.OrdinalIgnoreCase)
            || status.Contains("setup failed", StringComparison.OrdinalIgnoreCase)
            || status.Contains("unavailable", StringComparison.OrdinalIgnoreCase)
            || status.Contains("microphone", StringComparison.OrdinalIgnoreCase); // runner has no mic: an honest mic reason also counts
    }

    /// <summary>The microphone is off: the listening line does not claim Boo is listening, and the status does not either.</summary>
    private void AssertMicrophoneOff(Window panel)
    {
        var listening = NameOfId(panel, "ListeningStatus");
        Assert.False(
            listening.StartsWith("Listening", StringComparison.Ordinal),
            $"The microphone looks active: ListeningStatus='{listening}'.");
        Assert.False(
            StatusText(panel).StartsWith("Microphone active", StringComparison.Ordinal),
            $"The status claims the microphone is active: '{StatusText(panel)}'.");
    }

    private void WaitForFocusStatus(Window panel, Func<string, bool> matches, string what)
    {
        Assert.True(
            BooAppFixture.WaitUntil(() => matches(NameOfId(panel, "FocusStatus")), UiTimeout),
            $"Focus status was not {what}. FocusStatus: '{NameOfId(panel, "FocusStatus")}', timer: '{NameOfId(panel, "FocusTimer")}'");
    }

    /// <summary>The mm:ss countdown changes within a few seconds (the 1 s clock is running).</summary>
    private void AssertFocusClockMoves(Window panel)
    {
        var first = NameOfId(panel, "FocusTimer");
        Assert.True(
            BooAppFixture.WaitUntil(() => NameOfId(panel, "FocusTimer") != first, TimeSpan.FromSeconds(6)),
            $"The focus clock did not move from '{first}'.");
    }

    /// <summary>The countdown holds still for 2.5 s (paused or ended sessions do not keep counting).</summary>
    private void AssertFocusClockFrozen(Window panel)
    {
        Thread.Sleep(1200); // let any tick already queued before the pause land
        var first = NameOfId(panel, "FocusTimer");
        Thread.Sleep(2500);
        var second = NameOfId(panel, "FocusTimer");
        Assert.Equal(first, second);
    }

    private void AssertPanelUsable(Window panel)
    {
        Assert.False(_boo.App.HasExited, "Boo is no longer running.");
        Assert.NotNull(_boo.FindPanel());
        foreach (var id in new[] { "InputBox", "SendButton", "TalkButton", "FocusStartButton", "SaveSettingsButton", "StatusText", "CloseButton" })
        {
            Find(panel, id);
        }
    }

    private static string NameOfId(Window panel, string automationId) =>
        TryFind(panel, automationId) is { } element ? BooAppFixture.SafeName(element) : string.Empty;

    private static bool HasDescendantNamed(Window panel, string name)
    {
        try
        {
            return panel.FindFirstDescendant(cf => cf.ByName(name)) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// sessions.db uses SQLite's default rollback journal (no WAL), so a committed Save is in the main file.
    /// TEXT is stored as UTF-8, so a byte search finds the task name and the Status column value.
    /// </summary>
    private bool DatabaseContains(string text)
    {
        var path = Path.Combine(_boo.DataDirectory, "sessions.db");
        if (!File.Exists(path))
        {
            return false;
        }
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.GetBuffer().AsSpan(0, (int)buffer.Length).IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;
    }
}
