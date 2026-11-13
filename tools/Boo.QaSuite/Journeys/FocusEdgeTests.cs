using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;

namespace Boo.QaSuite.Journeys;

/// <summary>Additional j13 edges. No key: these lifecycle actions require no AI response.</summary>
public sealed class FocusEdgeTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public FocusEdgeTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q13_EndFromPaused_AndReplaceRunningSession_KeepHistoryAndFacesHonest()
    {
        using var recorder = new NotchStateRecorder(_boo, "focus-edges");
        var panel = QaUi.OpenPanel(_boo);
        var store = new SessionStore(Path.Combine(_boo.DataDirectory, "sessions.db"));
        void Press(string id) => QaUi.Find(_boo, panel, id).AsButton().Invoke();
        void Status(string prefix) => Assert.True(BooAppFixture.WaitUntil(
            () => QaUi.NameOf(panel, "FocusStatus").StartsWith(prefix, StringComparison.Ordinal),
            TimeSpan.FromSeconds(5)), $"Expected {prefix}; got {QaUi.NameOf(panel, "FocusStatus")}");
        void Start(string task)
        {
            QaUi.Find(_boo, panel, "FocusTaskBox").AsTextBox().Text = task;
            QaUi.Find(_boo, panel, "FocusDurationBox").AsTextBox().Text = "25";
            Press("FocusStartButton");
            Status("Running");
        }
        try
        {
            QaUi.Find(_boo, panel, "DeveloperModeBox").AsCheckBox().IsChecked = true;
            Start("j13 explicitly ended");
            Press("FocusPauseButton");
            Status("Paused");
            Assert.True(BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) == "Paused", TimeSpan.FromSeconds(5)));
            var ending = recorder.Mark();
            Press("FocusEndButton");
            Assert.True(BooAppFixture.WaitUntil(() => recorder.HasSeenSince("Happy", ending), TimeSpan.FromSeconds(5)),
                "Ending a paused session did not show Happy.");
            Assert.Equal(FocusStatus.Ended, Assert.Single(store.List()).Status);
            _boo.CaptureScreen("qa-q13-ended-from-paused");
            Assert.True(BooAppFixture.WaitUntil(() => QaUi.NotchState(_boo) != "Happy", TimeSpan.FromSeconds(8)));

            Start("j13 replaced session");
            var replaced = Assert.Single(store.List().Where(s => s.Task == "j13 replaced session"));
            var observationStart = recorder.MarkObservationWindow();
            var replacing = recorder.Mark();
            Start("j13 replacement session");
            var happy = BooAppFixture.WaitUntil(() => recorder.HasSeenSince("Happy", replacing), TimeSpan.FromSeconds(3));
            var sessions = store.List();
            var health = recorder.ObservationFailureSince(observationStart);
            _boo.WriteObservation("qa-q13-observation-health", health ?? "Healthy: action boundaries, read durations and observation gaps within 100 ms.");
            Assert.Null(health);
            Assert.False(happy, "Replacing a running session emitted the completed-session Happy face.");
            Assert.Equal(3, sessions.Count);
            Assert.Equal(FocusStatus.Ended, Assert.Single(sessions.Where(s => s.Id == replaced.Id)).Status);
            Assert.Equal(FocusStatus.Running, Assert.Single(sessions.Where(s => s.Task == "j13 replacement session")).Status);
            var history = QaUi.Find(_boo, panel, "HistoryList");
            var names = string.Join("\n", history.FindAllDescendants().Select(BooAppFixture.SafeName));
            Assert.Contains("j13 replaced session", names, StringComparison.Ordinal);
            Assert.Contains("j13 replacement session", names, StringComparison.Ordinal);
            _boo.WriteObservation("qa-q13-history", string.Join("\n", sessions.Select(s => $"{s.Id} {s.Task}: {s.Status}"))
                + $"\nHappyAfterReplacement={happy}\nUI history:\n{names}");
            _boo.CaptureScreen("qa-q13-replacement-history");
        }
        catch (Exception ex)
        {
            _boo.CaptureScreen("qa-q13-FAILED");
            _boo.WriteObservation("qa-q13-FAILED", ex.ToString());
            throw;
        }
        finally
        {
            Press("FocusEndButton");
        }
    }
}