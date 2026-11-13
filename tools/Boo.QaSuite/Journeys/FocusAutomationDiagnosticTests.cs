using System.Diagnostics;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Boo.QaSuite.Journeys;

/// <summary>Issue #67 diagnostic. Ten fixed no-key cycles; no retry or suppression of UIA failures.</summary>
public sealed class FocusAutomationDiagnosticTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public FocusAutomationDiagnosticTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q67_CompactWindow_FocusStartPauseEnd_TenConsecutiveCyclesWithoutUiaErrors()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner(), "Diagnostic runs only on GitHub's own VM.");
        var panel = QaUi.OpenPanel(_boo);
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var phase = "setup";
        void Record(string value) => log.Add($"{clock.ElapsedMilliseconds} [{phase}] {value}");
        AutomationElement Find(string id)
        {
            Record("FindFirst begin " + id);
            var element = panel.FindFirstDescendant(cf => cf.ByAutomationId(id));
            Assert.NotNull(element);
            Record("FindFirst end " + id);
            return element;
        }
        void WaitForStatus(Func<string, bool> matches, string expected)
        {
            var last = string.Empty;
            var matched = BooAppFixture.WaitUntil(() =>
            {
                var readStart = clock.ElapsedMilliseconds;
                last = Find("FocusStatus").Properties.Name.Value;
                Record($"FocusStatus='{last}' readMs={clock.ElapsedMilliseconds - readStart}");
                return matches(last);
            }, TimeSpan.FromSeconds(5));
            Assert.True(matched, $"Expected {expected}; last '{last}'.");
        }
        try
        {
            var window = panel.Patterns.Window.Pattern;
            window.SetWindowVisualState(WindowVisualState.Normal);
            Assert.True(BooAppFixture.WaitUntil(() => window.WindowVisualState.Value == WindowVisualState.Normal, TimeSpan.FromSeconds(5)));
            var transform = panel.Patterns.Transform.Pattern;
            Assert.True(transform.CanResize.Value);
            transform.Resize(600, 400); // match the frozen test's preceding small-resize request; WPF clamps to minimum
            Thread.Sleep(700); // same fixed layout-settle interval as the preceding frozen resize step
            window.SetWindowVisualState(WindowVisualState.Maximized);
            Assert.True(BooAppFixture.WaitUntil(() => window.WindowVisualState.Value == WindowVisualState.Maximized, TimeSpan.FromSeconds(5)));
            window.SetWindowVisualState(WindowVisualState.Normal);
            Assert.True(BooAppFixture.WaitUntil(() => window.WindowVisualState.Value == WindowVisualState.Normal, TimeSpan.FromSeconds(5)));
            Record("compact/restored bounds=" + panel.BoundingRectangle);
            Find("CloseButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, TimeSpan.FromSeconds(5)));
            panel = QaUi.OpenPanel(_boo);
            Record("panel reopened after compact/maximize/restore setup");
            for (var cycle = 1; cycle <= 10; cycle++)
            {
                phase = $"cycle {cycle}: prepare";
                Find("FocusTaskBox").AsTextBox().Text = "UIA diagnostic cycle " + cycle;
                Find("FocusDurationBox").AsTextBox().Text = "5";
                phase = $"cycle {cycle}: Start/Running";
                Find("FocusStartButton").AsButton().Invoke();
                WaitForStatus(s => s.StartsWith("Running", StringComparison.Ordinal), "Running after Start");
                phase = $"cycle {cycle}: Ctrl+Alt+S/Paused";
                Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);
                WaitForStatus(s => s == "Paused by Stop; Resume is explicit.", "Paused by Stop");
                phase = $"cycle {cycle}: End";
                Find("FocusEndButton").AsButton().Invoke();
                WaitForStatus(s => s.StartsWith("Ended", StringComparison.Ordinal), "Ended");
                Assert.False(_boo.App.HasExited);
                _boo.CaptureScreen("qa-q67-cycle-" + cycle);
                Record("cycle completed without UIA exception");
            }
        }
        catch (Exception ex)
        {
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-q67-FAILED"); }
            catch (Exception captureError) { Record("failure capture error: " + captureError); }
            throw;
        }
        finally
        {
            _boo.WriteObservation("qa-q67-focus-uia-diagnostic", string.Join("\n", log));
        }
    }
}