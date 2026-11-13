using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;

namespace Boo.QaSuite.Journeys;

/// <summary>j7 uses the real clock. Phase labels are inferred from pointer timings, not internal hooks.</summary>
public sealed class HoverEdgeTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public HoverEdgeTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q7_HoverEdges_AllFourBranches_OnTheRealClock()
    {
        Assert.Equal("true", Environment.GetEnvironmentVariable("GITHUB_ACTIONS"));
        Assert.Equal("github-hosted", Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT"));
        bool wasOn = false;
        Assert.True(SystemParametersInfo(0x1042, 0, ref wasOn, 0), "Cannot read Windows animation effects.");
        var trace = new List<string>();
        var clock = Stopwatch.StartNew();
        var samples = new List<(long Start, long At, string Face)>();
        var actionStarts = new Dictionary<long, long>();
        var notch = _boo.WaitForNotch(QaUi.UiTimeout);
        var points = notch.BoundingRectangle;
        var hover = new Point(points.Left + points.Width / 2, points.Top + 2);
        var away = new Point(hover.X, hover.Y + 300);
        void Sample()
        {
            var start = clock.ElapsedMilliseconds;
            var face = notch.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
            var end = clock.ElapsedMilliseconds;
            samples.Add((start, end, face));
            trace.Add($"read [{start},{end}] observed {face}");
        }
        void ObserveUntil(long deadline)
        {
            while (clock.ElapsedMilliseconds < deadline) { Sample(); Thread.Sleep(10); }
            Sample();
        }
        long Move(Point point, string action)
        {
            var begin = clock.ElapsedMilliseconds;
            trace.Add($"{begin} action-begin {action}");
            // MoveTo interpolates at 0.5 pixels/ms in FlaUI 4, missing subsecond branch deadlines.
            // Position sends the real pointer directly; UIA transitions still bound app processing time.
            Mouse.Position = point;
            var at = clock.ElapsedMilliseconds;
            trace.Add($"{at} action-end {action}");
            actionStarts[at] = begin;
            return at;
        }
        (long Lower, long Upper) Transition(long action, string before, string after)
        {
            var begin = actionStarts[action];
            var index = samples.FindIndex(s => s.Start >= begin && s.Face == after);
            Assert.True(index > 0, $"SETUP: no bounded {before}>{after} transition after action [{begin},{action}].");
            var previous = samples[index - 1];
            var current = samples[index];
            Assert.Equal(before, previous.Face);
            var lower = Math.Max(begin, previous.Start);
            var upper = current.At;
            trace.Add($"transition {before}>{after} bounded [{lower},{upper}]");
            Assert.InRange(upper - lower, 0L, 35L);
            return (lower, upper);
        }
        void FreshHidden(string branch)
        {
            Move(away, branch + " setup-away");
            Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.ItemStatus.ValueOrDefault == "Hidden", TimeSpan.FromSeconds(5)),
                "SETUP: fresh Hidden was not established for " + branch);
            Sample();
            Assert.Equal("Hidden", samples[^1].Face);
        }
        void Continuous(long from, long to)
        {
            var window = samples.Where(s => s.At >= from && s.At <= to).ToArray();
            Assert.True(window.Length > 2, "SETUP: insufficient face observations.");
            var times = new[] { from }.Concat(window.Select(s => s.At)).Append(to).ToArray();
            Assert.True(times.Zip(times.Skip(1), (a, b) => b - a).Max() <= 100,
                "SETUP: observation gap exceeded 100 ms; absence of Hidden is not proven.");
            Assert.All(window, s => Assert.False(string.IsNullOrEmpty(s.Face), "SETUP: failed UIA face read."));
        }
        void Ordered(long from, params string[] expected)
        {
            var index = 0;
            foreach (var sample in samples.Where(s => s.At >= from))
                if (index < expected.Length && sample.Face == expected[index]) index++;
            Assert.True(index == expected.Length, "Missing ordered faces: " + string.Join(">", expected));
        }
        try
        {
            if (!wasOn) Assert.True(SetSystemParameters(0x1043, 0, new IntPtr(1), 2), "Cannot enable session animation effects.");
            bool on = false;
            Assert.True(SystemParametersInfo(0x1042, 0, ref on, 0) && on, "SETUP: Windows animation effects remain off.");
            var panel = QaUi.OpenPanel(_boo);
            Assert.False(QaUi.Find(_boo, panel, "ReducedMotionBox").AsCheckBox().IsChecked == true,
                "SETUP: Boo reduced motion is on.");
            Assert.False(new SettingsStore(_boo.SettingsPath).Load().ReducedMotion);
            QaUi.Find(_boo, panel, "CloseButton").AsButton().Invoke();
            using var recorder = new NotchStateRecorder(_boo, "hover-edges");
            FreshHidden("a");
            var a = Move(hover, "a enter");
            ObserveUntil(a + 100);
            var left = Move(away, "a leave during reveal");
            Assert.InRange(left - a, 70L, 150L);
            ObserveUntil(a + 1700);
            Continuous(a, clock.ElapsedMilliseconds);
            Ordered(a, "Hover", "Idle", "Hidden");
            var entryA = Transition(a, "Hidden", "Hover");
            var leaveA = Transition(left, "Hover", "Idle");
            Assert.True(entryA.Upper < actionStarts[left], "SETUP: Hover was not observed before leave.");
            Assert.True(leaveA.Upper < entryA.Lower + 240, "SETUP: leave cannot be placed entirely inside reveal.");
            var hiddenIndex = samples.FindIndex(s => s.Start >= leaveA.Upper && s.Face == "Hidden");
            Assert.True(hiddenIndex > 0);
            var hiddenLower = samples[hiddenIndex - 1].Start;
            var hiddenUpper = samples[hiddenIndex].At;
            Assert.Equal("Idle", samples[hiddenIndex - 1].Face);
            Assert.InRange(hiddenUpper - hiddenLower, 0L, 35L);
            // 240 reveal + 900 delayed countdown + 160 retract; allow one 60 Hz frame (20 ms).
            // This rejects the earlier Hidden that a countdown incorrectly starting on leave would produce.
            Assert.True(hiddenLower >= entryA.Lower + 1300 - 20 && hiddenUpper <= entryA.Upper + 1300 + 20,
                $"Hidden interval [{hiddenLower},{hiddenUpper}] outside delayed-countdown bounds from entry [{entryA.Lower},{entryA.Upper}].");
            trace.Add($"first Hidden bounded [{hiddenLower},{hiddenUpper}]; expected entry + 1300 ms, frame tolerance 20 ms");
            _boo.CaptureScreen("qa-q7-a-retracted");

            foreach (var delay in new[] { 400, 950 })
            {
                FreshHidden("reenter-" + delay);
                var entered = Move(hover, "enter settled reveal");
                ObserveUntil(entered + 400); // finish the 240 ms reveal before leaving
                Assert.Equal("Hover", samples[^1].Face);
                var leave = Move(away, "leave settled reveal");
                ObserveUntil(leave + delay);
                var reenter = Move(hover, "reenter-" + delay);
                Assert.InRange(reenter - leave, (long)delay - 20, (long)delay + 35);
                ObserveUntil(reenter + 200);
                var end = clock.ElapsedMilliseconds;
                Continuous(leave, end);
                Ordered(entered, "Hover", "Idle", "Hover");
                var entryBounds = Transition(entered, "Hidden", "Hover");
                var leaveBounds = Transition(leave, "Hover", "Idle");
                var returnBounds = Transition(reenter, "Idle", "Hover");
                Assert.True(actionStarts[leave] >= entryBounds.Upper + 240,
                    "SETUP: reveal completion is not bounded before leave.");
                var earliestReturn = returnBounds.Lower - leaveBounds.Upper;
                var latestReturn = returnBounds.Upper - leaveBounds.Lower;
                trace.Add($"return relative to leave bounded [{earliestReturn},{latestReturn}]");
                if (delay == 950)
                    Assert.True(earliestReturn >= 900 && latestReturn < 1060,
                        "SETUP: re-entry uncertainty does not fit entirely inside the 900-1060 ms retract interval.");
                else
                    Assert.True(earliestReturn >= 0 && latestReturn < 900,
                        "SETUP: re-entry uncertainty does not fit entirely inside the delay interval.");
                Assert.DoesNotContain(samples.Where(s => s.At >= leave && s.At <= end), s => s.Face == "Hidden");
                _boo.CaptureScreen("qa-q7-reenter-" + delay);
            }

            FreshHidden("d");
            panel = QaUi.OpenPanel(_boo);
            QaUi.Find(_boo, panel, "FocusTaskBox").AsTextBox().Text = "j7 pinned pause";
            QaUi.Find(_boo, panel, "FocusDurationBox").AsTextBox().Text = "25";
            QaUi.Find(_boo, panel, "FocusStartButton").AsButton().Invoke();
            QaUi.Find(_boo, panel, "FocusPauseButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.ItemStatus.ValueOrDefault == "Paused", TimeSpan.FromSeconds(5)));
            QaUi.Find(_boo, panel, "CloseButton").AsButton().Invoke();
            var d = clock.ElapsedMilliseconds;
            Move(hover, "d hover paused");
            ObserveUntil(d + 400);
            var dLeave = Move(away, "d leave paused");
            ObserveUntil(dLeave + 1700); // past normal delay and retract deadline
            Continuous(d, clock.ElapsedMilliseconds);
            Assert.All(samples.Where(s => s.At >= d), s => Assert.Equal("Paused", s.Face));
            _boo.CaptureScreen("qa-q7-d-pinned");
        }
        catch (Exception ex)
        {
            trace.Add("FAILED: " + ex);
            _boo.CaptureScreen("qa-q7-FAILED");
            throw;
        }
        finally
        {
            _boo.WriteObservation("qa-q7-timing", "Phase timing inferred from real pointer actions. Max observation gap 100 ms.\n" + string.Join("\n", trace));
            if (!wasOn) Assert.True(SetSystemParameters(0x1043, 0, IntPtr.Zero, 2), "Failed to restore Windows animation effects.");
        }
    }

    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter, [MarshalAs(UnmanagedType.Bool)] ref bool value, uint flags);
    [DllImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetSystemParameters(uint action, uint parameter, IntPtr value, uint flags);
}