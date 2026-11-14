using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Boo.QaSuite.Journeys;

/// <summary>j13 Pause/Resume only; periodic checks and immediate Resume persistence are not claimed.</summary>
public sealed class FocusResumeTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public FocusResumeTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q13R_PauseResume_OrdersFacesAndRetainsSessionAtPauseCheckpoint()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var events = new ConcurrentQueue<(long At, string Face)>();
        Exception? primary = null;
        var cleanup = new List<Action>();
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        try
        {
            var panel = QaUi.OpenPanel(_boo);
            AutomationElement Find(string id) => panel.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing required element: " + id);
            void Press(string id) => Find(id).AsButton().Invoke();
            string Status() => Find("FocusStatus").Properties.Name.Value;
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            var subscription = notch.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, value) => events.Enqueue((clock.ElapsedMilliseconds, value as string ?? "<invalid>")),
                notch.Automation.PropertyLibrary.Element.ItemStatus);
            cleanup.Add(subscription.Dispose);
            var recorder = new NotchStateRecorder(_boo, "focus-resume");
            cleanup.Add(recorder.Dispose);
            var bounds = notch.BoundingRectangle;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 300);
            Find("DeveloperModeBox").AsCheckBox().IsChecked = true;
            Assert.True(Find("DeveloperModeBox").AsCheckBox().IsChecked);
            var task = "j13 resume " + Guid.NewGuid().ToString("N");
            Find("FocusTaskBox").AsTextBox().Text = task;
            Find("FocusDurationBox").AsTextBox().Text = "25";
            cleanup.Insert(0, () => Press("FocusEndButton"));
            var starting = clock.ElapsedMilliseconds;
            Record("Start begin");
            Press("FocusStartButton");
            Assert.True(BooAppFixture.WaitUntil(() => Status().StartsWith("Running", StringComparison.Ordinal)
                && notch.Properties.ItemStatus.Value == "Hidden", TimeSpan.FromSeconds(3)), "Running/Hidden baseline missing.");
            var store = new SessionStore(Path.Combine(_boo.DataDirectory, "sessions.db"));
            var session = Assert.Single(store.List(), s => s.Task == task);
            Assert.Equal(FocusStatus.Running, session.Status);
            Thread.Sleep(150);
            var mark = recorder.Mark();
            var eventStart = events.Count;
            Record("Pause begin session=" + session.Id);
            Press("FocusPauseButton");
            Assert.True(BooAppFixture.WaitUntil(() => Status().StartsWith("Paused", StringComparison.Ordinal)
                && notch.Properties.ItemStatus.Value == "Paused", TimeSpan.FromSeconds(2)));
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(eventStart).Any(e => e.Face == "Paused"), TimeSpan.FromSeconds(1)),
                "Known Pause event was not delivered.");
            Assert.Equal(FocusStatus.Paused, Assert.Single(store.List(), s => s.Id == session.Id).Status);
            var pin = Stopwatch.StartNew();
            while (pin.Elapsed < TimeSpan.FromMilliseconds(1700))
            {
                Assert.Equal("Paused", notch.Properties.ItemStatus.Value);
                Thread.Sleep(40);
            }
            Assert.True(clock.ElapsedMilliseconds - starting < 9000, "Setup exceeded pre-capture window; no retry.");
            var resumeEvents = events.Count;
            var resumed = clock.ElapsedMilliseconds;
            Record("Resume begin");
            Press("FocusResumeButton");
            Assert.True(BooAppFixture.WaitUntil(() => Status() == "Running after explicit Resume.", TimeSpan.FromSeconds(2)));
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(resumeEvents).Any(e => e.Face == "Hidden"), TimeSpan.FromSeconds(3)),
                "Resume did not deliver Hidden within bounded pre-capture window.");
            Thread.Sleep(150);
            var health = recorder.ObservationFailureSince(mark);
            Record("observation health=" + (health ?? "healthy within 100 ms"));
            Assert.Null(health);
            Assert.False(recorder.HasSeenSince("Capture", mark), "Unexpected scheduled capture during bounded Pause/Resume observation.");
            Assert.DoesNotContain(events.Skip(eventStart), e => e.Face == "Capture");
            var sequence = events.Skip(resumeEvents).ToArray();
            var idle = Array.FindIndex(sequence, e => e.Face == "Idle");
            var hidden = Array.FindIndex(sequence, e => e.Face == "Hidden");
            Assert.True(idle >= 0 && hidden > idle, "Expected ordered action-local Idle then Hidden after Resume.");
            Assert.True(resumed <= sequence[idle].At && sequence[idle].At < sequence[hidden].At
                && sequence[hidden].At <= resumed + 3000, "Idle/Hidden events missed the action-bound three-second Resume window.");
            Record($"Resume sequence: invocation={resumed} Idle={sequence[idle].At} Hidden={sequence[hidden].At}");
            Assert.DoesNotContain(sequence, e => e.Face is not ("Idle" or "Hidden"));
            Assert.Equal("Hidden", notch.Properties.ItemStatus.Value);
            Assert.False(panel.IsOffscreen);
            Assert.True(clock.ElapsedMilliseconds - resumed < 9000, "Observation exceeded pre-capture window; no retry.");
            Record("Running panel; ordered Paused->Idle->Hidden. Immediate Resume persistence UNVERIFIED.");
            _boo.CaptureScreen("qa-j13-resumed");
            Record("second Pause checkpoint begin");
            Press("FocusPauseButton");
            Assert.True(BooAppFixture.WaitUntil(() => Status().StartsWith("Paused", StringComparison.Ordinal), TimeSpan.FromSeconds(2)));
            var saved = Assert.Single(store.List(), s => s.Id == session.Id);
            Assert.Equal(FocusStatus.Paused, saved.Status);
            Assert.Equal(task, saved.Task);
            var history = string.Join(" || ", Find("HistoryList").FindAllDescendants().Select(e => e.Properties.Name.Value));
            Assert.Contains(task, history, StringComparison.Ordinal);
            Record($"checkpoint session={saved.Id} status={saved.Status}; history={history}");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j13-resume-FAILED"); }
            catch (Exception evidenceError) { Record("capture error: " + evidenceError); }
            throw;
        }
        finally
        {
            var secondary = new List<Exception>();
            foreach (var action in cleanup)
            {
                try { action(); }
                catch (Exception ex) { Record("cleanup error: " + ex); secondary.Add(ex); }
            }
            log.AddRange(events.Select(e => $"{e.At} ItemStatus event {e.Face}"));
            try { _boo.WriteObservation("qa-j13-pause-resume", string.Join("\n", log)); }
            catch (Exception ex) { Console.Error.WriteLine(ex); secondary.Add(ex); }
            if (primary is null && secondary.Count > 0) throw new AggregateException(secondary);
        }
    }
}
