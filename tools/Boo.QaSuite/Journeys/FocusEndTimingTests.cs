using System.Collections.Concurrent;
using System.Drawing;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Boo.QaSuite.Journeys;

/// <summary>j13d only: bracket native Happy timing without treating UIA delivery as producer time.</summary>
public sealed class FocusEndTimingTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public FocusEndTimingTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q13H_EndFromPaused_HappyIntervalContainsNominalDurationThenRetreats()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var log = new List<string>();
        var events = new ConcurrentQueue<(long At, string Face)>();
        var cleanup = new List<Action>();
        Exception? primary = null;
        try
        {
            var panel = QaUi.OpenPanel(_boo);
            AutomationElement Find(string id) => panel.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing required element: " + id);
            void Press(string id) => Find(id).AsButton().Invoke();
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            var recorder = new NotchStateRecorder(_boo, "focus-end-timing");
            cleanup.Add(recorder.Dispose);
            var subscription = notch.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, value) => events.Enqueue((recorder.Mark(), value as string ?? "<invalid>")),
                notch.Automation.PropertyLibrary.Element.ItemStatus);
            cleanup.Insert(0, subscription.Dispose);
            var bounds = notch.BoundingRectangle;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 300);
            var task = "j13 end " + Guid.NewGuid().ToString("N");
            Find("DeveloperModeBox").AsCheckBox().IsChecked = true;
            Find("FocusTaskBox").AsTextBox().Text = task;
            Find("FocusDurationBox").AsTextBox().Text = "25";
            cleanup.Insert(0, () => Press("FocusEndButton"));
            Press("FocusStartButton");
            Assert.True(BooAppFixture.WaitUntil(() => Find("FocusStatus").Properties.Name.Value.StartsWith("Running", StringComparison.Ordinal), TimeSpan.FromSeconds(3)));
            Press("FocusPauseButton");
            Assert.True(BooAppFixture.WaitUntil(() => Find("FocusStatus").Properties.Name.Value.StartsWith("Paused", StringComparison.Ordinal)
                && notch.Properties.ItemStatus.Value == "Paused" && recorder.HasSeen("Paused")
                && events.Any(e => e.Face == "Paused"), TimeSpan.FromSeconds(3)));
            var store = new SessionStore(Path.Combine(_boo.DataDirectory, "sessions.db"));
            var session = Assert.Single(store.List(), s => s.Task == task);
            Assert.Equal(FocusStatus.Paused, session.Status);
            _boo.CaptureScreen("qa-j13h-paused");
            Thread.Sleep(150);
            var mark = recorder.Mark();
            var eventStart = events.Count;
            var before = recorder.ReadIntervals;
            Assert.Equal("Paused", before[^1].State);
            var invocationStart = recorder.Mark();
            Press("FocusEndButton");
            var invocationEnd = recorder.Mark();
            log.Add($"End invocation [{invocationStart},{invocationEnd}] session={session.Id}");
            Assert.True(BooAppFixture.WaitUntil(() => recorder.HasSeenSince("Happy", mark), TimeSpan.FromSeconds(2)));
            Assert.Equal("Happy", notch.Properties.ItemStatus.Value);
            _boo.CaptureScreen("qa-j13h-happy");
            Assert.True(BooAppFixture.WaitUntil(() => recorder.HasSeenSince("Idle", mark), TimeSpan.FromSeconds(4)));
            Assert.Equal("Idle", notch.Properties.ItemStatus.Value);
            _boo.CaptureScreen("qa-j13h-idle");
            Assert.True(BooAppFixture.WaitUntil(() => recorder.HasSeenSince("Hidden", mark), TimeSpan.FromSeconds(3)));
            Assert.Equal("Hidden", notch.Properties.ItemStatus.Value);
            var health = recorder.ObservationFailureSince(mark);
            log.Add("observation health=" + (health ?? "healthy within 100 ms"));
            Assert.Null(health);
            var reads = recorder.ReadIntervals;
            var firstHappy = Array.FindIndex(reads, r => r.Start > mark && r.State == "Happy");
            var firstIdle = Array.FindIndex(reads, r => r.Start > mark && r.State == "Idle");
            Assert.True(firstHappy > 0 && firstIdle > firstHappy);
            var lastPaused = reads.Take(firstHappy).Last(r => r.State == "Paused");
            var happyStart = reads[firstHappy];
            var lastHappy = reads.Take(firstIdle).Last(r => r.State == "Happy");
            var idleStart = reads[firstIdle];
            long hLo = lastPaused.Start, hHi = happyStart.End, iLo = lastHappy.Start, iHi = idleStart.End;
            log.Add($"Happy entry=[{hLo},{hHi}] exit=[{iLo},{iHi}] duration=[{iLo - hHi},{iHi - hLo}] nominal=2500ms; compatibility only");
            Assert.InRange(hHi - hLo, 0L, 100L);
            Assert.InRange(iHi - iLo, 0L, 100L);
            Assert.True(iLo - hHi <= 2500 && iHi - hLo >= 2500, "Observed Happy duration interval excludes nominal 2500 ms.");
            var actionReads = reads.Where(r => r.Start > mark).ToArray();
            static string[] ConsecutiveStates(IEnumerable<string> values)
            {
                var states = new List<string>();
                foreach (var value in values)
                    if (states.Count == 0 || states[^1] != value) states.Add(value);
                return states.ToArray();
            }
            var readSequence = ConsecutiveStates(new[] { "Paused" }.Concat(actionReads.Select(r => r.State)));
            log.Add("deduplicated direct reads=" + string.Join(" -> ", readSequence));
            Assert.Equal(new[] { "Paused", "Happy", "Idle", "Hidden" }, readSequence);
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(eventStart).Any(e => e.Face == "Hidden"), TimeSpan.FromSeconds(1)));
            var sequence = events.Skip(eventStart).Select(e => e.Face).ToArray();
            var eventSequence = ConsecutiveStates(sequence);
            log.Add("deduplicated event deliveries=" + string.Join(" -> ", eventSequence));
            Assert.Equal(new[] { "Happy", "Idle", "Hidden" }, eventSequence);
            Assert.True(Find("FocusStatus").Properties.Name.Value.StartsWith("Ended", StringComparison.Ordinal));
            var ended = Assert.Single(store.List(), s => s.Id == session.Id);
            Assert.Equal(FocusStatus.Ended, ended.Status);
            Assert.Equal(task, ended.Task);
            var history = string.Join(" || ", Find("HistoryList").FindAllDescendants().Select(e => e.Properties.Name.Value));
            Assert.Contains(task, history, StringComparison.Ordinal);
            log.Add($"ended session={ended.Id} status={ended.Status}; history={history}");
            _boo.CaptureScreen("qa-j13h-hidden-ended");
            cleanup.RemoveAt(0); // session already ended; no second End invocation
        }
        catch (Exception ex)
        {
            primary = ex;
            log.Add("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j13h-FAILED"); }
            catch (Exception evidenceError) { log.Add("capture error: " + evidenceError); }
            throw;
        }
        finally
        {
            var secondary = new List<Exception>();
            foreach (var action in cleanup)
            {
                try { action(); }
                catch (Exception ex) { log.Add("cleanup error: " + ex); secondary.Add(ex); }
            }
            log.AddRange(events.Select(e => $"{e.At} ItemStatus event delivery {e.Face}"));
            try { _boo.WriteObservation("qa-j13h-end-timing", string.Join("\n", log)); }
            catch (Exception ex) { Console.Error.WriteLine(ex); secondary.Add(ex); }
            if (primary is null && secondary.Count > 0) throw new AggregateException(secondary);
        }
    }
}
