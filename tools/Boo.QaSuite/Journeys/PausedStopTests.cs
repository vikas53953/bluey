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
using FlaUI.Core.WindowsAPI;

namespace Boo.QaSuite.Journeys;

/// <summary>j12g only: a single Stop chord preserves an already-paused focus session.</summary>
public sealed class PausedStopTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public PausedStopTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q12G_StopWhilePaused_PreservesSessionStatusAndPinnedFace()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var events = new ConcurrentQueue<(long At, string Face)>();
        Exception? primary = null;
        Action? unsubscribe = null;
        Action? end = null;
        NotchStateRecorder? recorder = null;
        void Record(string value) => log.Add($"{clock.ElapsedMilliseconds} {value}");
        try
        {
            var panel = QaUi.OpenPanel(_boo);
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            var subscription = notch.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, value) => events.Enqueue((clock.ElapsedMilliseconds, value as string ?? "<invalid>")),
                notch.Automation.PropertyLibrary.Element.ItemStatus);
            unsubscribe = subscription.Dispose;
            var bounds = notch.BoundingRectangle;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 300);
            recorder = new NotchStateRecorder(_boo, "paused-stop");
            void Press(string id) => QaUi.Find(_boo, panel, id).AsButton().Invoke();
            string Status() => QaUi.NameOf(panel, "FocusStatus");
            var task = "j12g " + Guid.NewGuid().ToString("N");
            var store = new SessionStore(Path.Combine(_boo.DataDirectory, "sessions.db"));
            QaUi.Find(_boo, panel, "FocusTaskBox").AsTextBox().Text = task;
            QaUi.Find(_boo, panel, "FocusDurationBox").AsTextBox().Text = "25";
            end = () => Press("FocusEndButton");
            Press("FocusStartButton");
            Assert.True(BooAppFixture.WaitUntil(() => Status().StartsWith("Running", StringComparison.Ordinal), TimeSpan.FromSeconds(5)));
            Press("FocusPauseButton");
            Assert.True(BooAppFixture.WaitUntil(() => Status().StartsWith("Paused", StringComparison.Ordinal)
                && notch.Properties.ItemStatus.Value == "Paused", TimeSpan.FromSeconds(5)));
            Assert.True(BooAppFixture.WaitUntil(() => events.Any(e => e.Face == "Paused"), TimeSpan.FromSeconds(5)),
                "SETUP: ItemStatus events did not deliver known Pause transition.");
            var before = Assert.Single(store.List(), s => s.Task == task);
            Assert.Equal(FocusStatus.Paused, before.Status);
            var statusBefore = Status();
            Record($"before session={before.Id} status={before.Status} panel={statusBefore}");
            Thread.Sleep(150); // establish pre-action recorder coverage
            var mark = recorder.Mark();
            var eventStart = events.Count;
            Record("single Ctrl+Alt+F12 begin");
            try
            {
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.F12);
            }
            catch (Exception ex)
            {
                primary = ex;
                throw;
            }
            finally
            {
                foreach (var key in new[] { VirtualKeyShort.F12, VirtualKeyShort.ALT, VirtualKeyShort.CONTROL })
                {
                    try { Keyboard.Release(key); }
                    catch (Exception ex) when (primary is not null) { Record("modifier release error: " + ex); }
                }
            }
            Record("single Ctrl+Alt+F12 complete");
            Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.HelpText.Value.Contains("Stopped", StringComparison.Ordinal),
                TimeSpan.FromSeconds(1)), "One Stop chord did not acknowledge Stopped.");
            Record("Stopped acknowledgment=" + notch.Properties.HelpText.Value);
            // Exceeds the 1.6 s acknowledgment and 240+900+160 ms reveal/retract timeline.
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                Assert.Equal("Paused", notch.Properties.ItemStatus.Value);
                Assert.Equal(statusBefore, Status());
                Thread.Sleep(40);
            }
            var health = recorder.ObservationFailureSince(mark);
            Record("action observation health=" + (health ?? "healthy within 100 ms"));
            Assert.Null(health);
            Assert.False(recorder.HasSeenSince("Hidden", mark));
            var after = Assert.Single(store.List(), s => s.Id == before.Id);
            Assert.Equal(FocusStatus.Paused, after.Status);
            Assert.Equal(task, after.Task);
            Record($"after session={after.Id} status={after.Status} panel={Status()}");
            _boo.CaptureScreen("qa-j12g-paused-stop");
            // A known post-window transition proves callback delivery remains active.
            var barrier = events.Count;
            var endAction = clock.ElapsedMilliseconds;
            var actionEvents = events.Skip(eventStart).Take(barrier - eventStart).ToArray();
            Record($"End action boundary={endAction} events=[{eventStart},{barrier})");
            Assert.DoesNotContain(actionEvents, e => e.Face != "Paused");
            Press("FocusEndButton");
            end = null;
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(barrier).Any(e => e.Face == "Happy"), TimeSpan.FromSeconds(5)),
                "ItemStatus stream did not deliver post-window End/Happy transition.");
            Thread.Sleep(500); // retain delayed callbacks before checking the action interval
            // Validate the pre-End interval again after callback drain; Happy never truncates it.
            Assert.DoesNotContain(events.Skip(eventStart).Where(e => e.At < endAction), e => e.Face != "Paused");
            Record("action interval retains Paused; post-window Happy delivered");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j12g-FAILED"); }
            catch (Exception evidenceError) { Record("capture error: " + evidenceError); }
            throw;
        }
        finally
        {
            var secondary = new List<Exception>();
            foreach (var cleanup in new Action?[] { end, unsubscribe, recorder is null ? null : recorder.Dispose })
            {
                try { cleanup?.Invoke(); }
                catch (Exception ex) { Record("cleanup error: " + ex); secondary.Add(ex); }
            }
            log.AddRange(events.Select(e => $"{e.At} ItemStatus event {e.Face}"));
            try { _boo.WriteObservation("qa-j12g-paused-stop", string.Join("\n", log)); }
            catch (Exception ex) { secondary.Add(ex); Console.Error.WriteLine("Evidence error: " + ex); }
            if (primary is null && secondary.Count > 0) throw new AggregateException(secondary);
        }
    }
}
