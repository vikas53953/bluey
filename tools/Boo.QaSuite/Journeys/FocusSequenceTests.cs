using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Boo.QaSuite.Journeys;

/// <summary>Partial j13: native UI sequence and real classifications; CLR end-event delivery remains unobservable.</summary>
public sealed class FocusSequenceTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public FocusSequenceTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void Q13S_FullUiSequence_WithPeriodicChecks_EventDeliveryUnverified()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var events = new ConcurrentQueue<(long At, string Face)>();
        var secondary = new List<Exception>();
        Exception? primary = null;
        Action? endCleanup = null;
        IDisposable? subscription = null;
        using var recorder = new NotchStateRecorder(_boo, "focus-sequence");
        var pid = _boo.App.ProcessId;
        var data = _boo.DataDirectory;
        void Record(string text) => log.Add($"{recorder.Mark()} {text}");
        void Bound()
        {
            Assert.True(clock.Elapsed < TimeSpan.FromMinutes(6), "Six-minute overall sequence cap exceeded.");
            Assert.Equal(pid, _boo.App.ProcessId);
            Assert.Equal(data, _boo.DataDirectory);
        }
        void Wait(Func<bool> predicate, int milliseconds, string reason)
        {
            Assert.True(milliseconds > 0, reason + ": action budget exhausted");
            var until = clock.ElapsedMilliseconds + milliseconds;
            while (true)
            {
                Bound();
                var matched = predicate();
                Bound();
                Assert.True(clock.ElapsedMilliseconds <= until, reason + ": read exceeded deadline");
                if (matched) return;
                Assert.True(clock.ElapsedMilliseconds < until, reason);
                Thread.Sleep(20);
            }
        }
        void Watch(int milliseconds, Action assertion)
        {
            var until = clock.ElapsedMilliseconds + milliseconds;
            do { Bound(); assertion(); Thread.Sleep(20); } while (clock.ElapsedMilliseconds < until);
            Bound();
        }
        void Health(long mark)
        {
            // Allow a second read for very fast invocations, without excluding the action boundary.
            Watch(100, () => { });
            var health = recorder.ObservationFailureSince(mark);
            Record($"health window [{mark},{recorder.Mark()}]: {health ?? "healthy within 100 ms"}");
            Assert.Null(health);
        }
        void Capture(string name, bool enforceCap = true)
        {
            if (enforceCap) Bound();
            _boo.CaptureScreen(name);
            var manifest = File.ReadAllText(Path.Combine(_boo.ArtifactsDirectory, name + ".capture.txt"));
            var images = Regex.Matches(manifest, @"(?m)^boo-(\d+):").Cast<Match>().ToArray();
            Assert.NotEmpty(images);
            foreach (var image in images)
            {
                using var bitmap = new Bitmap(Path.Combine(_boo.ArtifactsDirectory, name + ".boo-" + image.Groups[1].Value + ".png"));
                Assert.True(bitmap.Width > 0 && bitmap.Height > 0);
            }
            if (enforceCap) Bound();
        }
        static string[] Dedup(IEnumerable<string> values)
        {
            var result = new List<string>();
            foreach (var value in values)
                if (result.Count == 0 || result[^1] != value) result.Add(value);
            return result.ToArray();
        }
        try
        {
            Record($"identity pid={pid}; isolated data={data}");
            var panel = QaUi.OpenPanelOnceReady(_boo, Record);
            Bound();
            AutomationElement Find(string id) => panel.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing required element: " + id);
            var buttons = new[] { "FocusEndButton", "FocusPauseButton", "FocusResumeButton", "FocusStartButton" }.ToDictionary(id => id, id => Find(id).AsButton());
            void Press(string id)
            {
                Bound();
                // QA_TIMED_INVOKE_BEGIN
                buttons[id].Invoke();
                // QA_TIMED_INVOKE_END
                Bound();
            }
            string Status() => Find("FocusStatus").Properties.Name.Value;
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            string Face() { Assert.Equal(pid, notch.Properties.ProcessId.Value); return notch.Properties.ItemStatus.Value; }
            subscription = notch.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, value) => events.Enqueue((recorder.Mark(), value as string ?? "<invalid>")),
                notch.Automation.PropertyLibrary.Element.ItemStatus);
            var display = Find("DisplayBox").AsComboBox();
            Assert.NotEmpty(display.Items);
            display.Select(0);
            Find("ChirpMute").AsCheckBox().IsChecked = true;
            Assert.True(Find("ChirpMute").AsCheckBox().IsChecked);
            Assert.True(Find("SpeechMute").AsCheckBox().IsChecked);
            Find("DeveloperModeBox").AsCheckBox().IsChecked = true;
            Assert.True(Find("DeveloperModeBox").AsCheckBox().IsChecked);
            Find("FocusDurationBox").AsTextBox().Text = "25";
            var bounds = notch.BoundingRectangle;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 300);
            var store = new SessionStore(Path.Combine(data, "sessions.db"));
            FocusSnapshot Session(Guid id) => Assert.Single(store.List(), s => s.Id == id);
            string History() => string.Join(" || ", Find("HistoryList").FindAllDescendants().Select(e => e.Properties.Name.Value));
            void CheckHistory(params string[] tasks)
            {
                var history = History();
                foreach (var task in tasks) Assert.Contains(task, history, StringComparison.Ordinal);
                Record("UI history=" + history);
            }
            Guid Start(string task, bool replacement = false)
            {
                Find("FocusTaskBox").AsTextBox().Text = task;
                var mark = recorder.Mark();
                Record("Start invocation task=" + task);
                Press("FocusStartButton");
                Wait(() => Status().StartsWith("Running", StringComparison.Ordinal) && Face() == "Hidden", 3000 - (int)(recorder.Mark() - mark), "Start missing Running/Hidden");
                FocusSnapshot? saved = null;
                Wait(() => (saved = store.List().SingleOrDefault(s => s.Task == task)) is not null,
                    3000 - (int)(recorder.Mark() - mark), "Start snapshot not persisted within action budget");
                Assert.NotNull(saved);
                if (replacement)
                {
                    var remaining = Math.Max(0, 3000 - (int)(recorder.Mark() - mark));
                    Watch(remaining, () => Assert.NotEqual("Happy", Face()));
                    Assert.False(recorder.HasSeenSince("Happy", mark));
                    Assert.DoesNotContain(events.Where(e => e.At >= mark), e => e.Face == "Happy");
                }
                Health(mark);
                Assert.Equal(FocusStatus.Running, saved.Status);
                Record("Start session=" + saved.Id);
                return saved.Id;
            }
            void Pause(Guid id, bool pin)
            {
                var mark = recorder.Mark();
                Press("FocusPauseButton");
                Wait(() => Status().StartsWith("Paused", StringComparison.Ordinal) && Face() == "Paused", 2000 - (int)(recorder.Mark() - mark), "Pause missing within 2 s");
                Wait(() => Session(id).Status == FocusStatus.Paused,
                    2000 - (int)(recorder.Mark() - mark), "Paused snapshot not persisted within action budget");
                Wait(() => events.Any(e => e.At >= mark && e.Face == "Paused"), 1000, "Pause event missing");
                if (pin) Watch(1700, () => Assert.Equal("Paused", Face()));
                Health(mark);
            }
            int Completed(Guid id)
            {
                var checks = Session(id).Checks;
                Assert.DoesNotContain(checks, c => c.Activity == FocusActivity.Unavailable);
                var completed = checks.Where(c => c.Activity != FocusActivity.Paused).ToArray();
                foreach (var check in completed)
                {
                    Assert.NotNull(check.Probabilities);
                    Assert.NotEmpty(check.Probabilities!);
                    Assert.All(check.Probabilities!, p => Assert.True(double.IsFinite(p.Value) && p.Value >= 0 && p.Value <= 1));
                    Assert.InRange(check.Probabilities!.Values.Sum(), .99, 1.01);
                }
                return completed.Length;
            }
            int Captures(long mark) => Dedup(recorder.ReadIntervals.Where(r => r.Start > mark).Select(r => r.State)).Count(s => s == "Capture");
            var taskA = "Review Boo focus controls for QA " + Guid.NewGuid().ToString("N");
            var initial = recorder.Mark();
            endCleanup = () => buttons["FocusEndButton"].Invoke();
            var a = Start(taskA);
            Wait(() => Completed(a) >= 2 && Captures(initial) >= 2, 150000, "Two persisted real classifications and Capture transitions missing; no retry");
            Health(initial);
            Record($"A completed classifications={Completed(a)}; actual provider attempt count UNVERIFIED; no hard call cap claimed");
            Capture("qa-q13s-two-checks");
            Pause(a, true);
            var resumeMark = recorder.Mark();
            var resumeEvents = events.Count;
            var beforeResume = Completed(a);
            Press("FocusResumeButton");
            Wait(() => Status() == "Running after explicit Resume.", 2000 - (int)(recorder.Mark() - resumeMark), "Explicit Resume status missing");
            Wait(() => Face() == "Hidden" && recorder.HasSeenSince("Idle", resumeMark)
                && events.Skip(resumeEvents).Any(e => e.Face == "Hidden"), 3000 - (int)(recorder.Mark() - resumeMark), "Resume Idle/Hidden missing");
            Watch(Math.Max(0, 3000 - (int)(recorder.Mark() - resumeMark)), () => Assert.NotEqual("Capture", Face()));
            Health(resumeMark);
            Assert.False(recorder.HasSeenSince("Capture", resumeMark));
            Assert.Equal(new[] { "Paused", "Idle", "Hidden" }, Dedup(new[] { "Paused" }.Concat(recorder.ReadIntervals.Where(r => r.Start > resumeMark).Select(r => r.State))));
            Assert.Equal(new[] { "Idle", "Hidden" }, Dedup(events.Skip(resumeEvents).Select(e => e.Face)));
            var thirdMark = recorder.Mark();
            Wait(() => Completed(a) > beforeResume && Captures(thirdMark) >= 1, 90000, "Post-Resume completed classification/Capture missing; no retry");
            Health(thirdMark);
            Assert.Equal(a, Session(a).Id);
            Pause(a, false);
            Capture("qa-q13s-second-pause");
            var endMark = recorder.Mark();
            var endEvents = events.Count;
            Assert.Equal("Paused", Face());
            Press("FocusEndButton");
            Wait(() => recorder.HasSeenSince("Happy", endMark) && Face() == "Happy", 2000 - (int)(recorder.Mark() - endMark), "End Happy missing");
            Wait(() => recorder.HasSeenSince("Idle", endMark) && Face() == "Idle", 4000, "End Idle missing");
            Wait(() => recorder.HasSeenSince("Hidden", endMark) && Face() == "Hidden", 3000, "End Hidden missing");
            Wait(() => events.Skip(endEvents).Any(e => e.Face == "Hidden"), 1000, "End Hidden event missing");
            Health(endMark);
            var reads = recorder.ReadIntervals;
            var firstHappy = Array.FindIndex(reads, r => r.Start > endMark && r.State == "Happy");
            var firstIdle = Array.FindIndex(reads, r => r.Start > endMark && r.State == "Idle");
            Assert.True(firstHappy > 0 && firstIdle > firstHappy);
            var lastPaused = reads.Take(firstHappy).Last(r => r.State == "Paused");
            var lastHappy = reads.Take(firstIdle).Last(r => r.State == "Happy");
            long hLo = lastPaused.Start, hHi = reads[firstHappy].End, iLo = lastHappy.Start, iHi = reads[firstIdle].End;
            Record($"Happy entry=[{hLo},{hHi}] exit=[{iLo},{iHi}] duration=[{iLo-hHi},{iHi-hLo}]; nominal 2500 ms compatibility only");
            Assert.InRange(hHi - hLo, 0L, 100L);
            Assert.InRange(iHi - iLo, 0L, 100L);
            Assert.True(iLo - hHi <= 2500 && iHi - hLo >= 2500);
            Assert.Equal(new[] { "Paused", "Happy", "Idle", "Hidden" }, Dedup(new[] { "Paused" }.Concat(reads.Where(r => r.Start > endMark).Select(r => r.State))));
            Assert.Equal(new[] { "Happy", "Idle", "Hidden" }, Dedup(events.Skip(endEvents).Select(e => e.Face)));
            Assert.StartsWith("Ended", Status(), StringComparison.Ordinal);
            Assert.Equal(FocusStatus.Ended, Session(a).Status);
            CheckHistory(taskA);
            Capture("qa-q13s-a-ended");
            var taskB = "Review Boo pause controls for QA " + Guid.NewGuid().ToString("N");
            var taskC = "Review Boo replacement controls for QA " + Guid.NewGuid().ToString("N");
            var b = Start(taskB);
            var c = Start(taskC, true);
            Assert.NotEqual(a, b); Assert.NotEqual(a, c); Assert.NotEqual(b, c);
            Assert.Equal(FocusStatus.Ended, Session(a).Status);
            Assert.Equal(FocusStatus.Ended, Session(b).Status);
            Assert.Equal(FocusStatus.Running, Session(c).Status);
            Assert.Equal(3, store.List().Count);
            CheckHistory(taskA, taskB, taskC);
            Capture("qa-q13s-replacement-history");
            Bound();
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { Capture("qa-q13s-FAILED", false); }
            catch (Exception evidenceError) { secondary.Add(evidenceError); Record("capture error: " + evidenceError); }
            throw;
        }
        finally
        {
            Record("UNVERIFIED — MainWindow.FocusSessionEnded presence/count/absence. Expected 1 explicit A End, 0 replacement B; actual unobservable through existing out-of-process mechanisms.");
            try { _boo.WriteObservation("qa-q13s-event-delivery-receipt", log[^1]); }
            catch (Exception ex) { secondary.Add(ex); }
            Record("Cleanup End begins AFTER final event receipt; cleanup Happy excluded from assertions.");
            try { endCleanup?.Invoke(); }
            catch (Exception ex) { secondary.Add(ex); Record("cleanup End error: " + ex); }
            try { subscription?.Dispose(); }
            catch (Exception ex) { secondary.Add(ex); }
            log.AddRange(events.Select(e => $"{e.At} ItemStatus event delivery {e.Face}"));
            log.AddRange(secondary.Select(e => "secondary error: " + e));
            try { _boo.WriteObservation("qa-q13s-focus-sequence", string.Join("\n", log)); }
            catch (Exception ex) { secondary.Add(ex); Console.Error.WriteLine(ex); }
            if (primary is null && secondary.Count > 0) throw new AggregateException(secondary);
        }
    }
}
