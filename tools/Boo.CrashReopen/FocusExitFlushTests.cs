using System.Diagnostics;
using System.Drawing;
using Boo.Core;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.CrashReopen;

/// <summary>A fresh data folder, speech off, no key. Its Boo is quit from the tray by the test.</summary>
public sealed class ExitFlushFixture : BooAppFixture
{
    public ExitFlushFixture() : base(FocusAckDriver.Seed) { }
}

/// <summary>
/// PR #170 mutation M04: quitting Boo must not lose a save that is still queued. The disk write of a Pause is held by a
/// lock, Boo is quit from its tray menu (its only graceful exit) while that save waits, and the lock is released well
/// inside the documented 10-second exit-flush limit. After Boo has exited, sessions.db in the same data folder must hold
/// the Pause. Without the flush at exit, Boo exits before the write and the stored session stays Running.
/// </summary>
public sealed class QuitWithAPendingSaveTests : IClassFixture<ExitFlushFixture>
{
    private readonly ExitFlushFixture _app;
    private readonly FocusAckDriver _boo;

    public QuitWithAPendingSaveTests(ExitFlushFixture app, ITestOutputHelper output)
    {
        _app = app;
        _boo = new FocusAckDriver(app, output);
    }

    [Fact]
    public void QuitWhileThePauseSaveIsHeld_TheSaveLandsBeforeBooExits()
    {
        var task = "exit-flush " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.Start(task);
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after Start");
            Assert.Equal(FocusStatus.Running, _boo.Stored(task)?.Status);
            var quit = OpenTrayQuit();
            var hold = _boo.HoldWrites();
            try
            {
                _boo.Press("FocusPauseButton");
                _boo.WaitFace("Paused");
                Assert.Equal(FocusStatus.Running, _boo.Stored(task)?.Status);
                _boo.Record("tray Quit invoke (the Pause save is still held)");
                quit.Invoke();
                Thread.Sleep(2000);
                Assert.False(_app.App.HasExited, "Boo exited while its Pause save was still waiting on the disk.");
            }
            finally
            {
                hold.Dispose();
            }
            Assert.True(BooAppFixture.WaitUntil(() => _app.App.HasExited, TimeSpan.FromSeconds(30)), "Boo did not exit after Quit.");
            var stored = _boo.Stored(task);
            _boo.Record($"after exit: stored={stored?.Status.ToString() ?? "<none>"}");
            Assert.Equal(FocusStatus.Paused, stored?.Status);
        }
        finally
        {
            _boo.Flush("exit-flush");
        }
    }

    /// <summary>
    /// Opens Boo's tray menu the way a person does (the overflow arrow when the icon is hidden, then a right-click on the
    /// "Boo companion" icon) and returns its Quit item without invoking it.
    /// </summary>
    private FlaUI.Core.AutomationElements.MenuItem OpenTrayQuit()
    {
        var automation = _app.Automation;
        var desktop = automation.GetDesktop();
        var explorer = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process) explorer.Add(process.Id);
        }
        Assert.NotEmpty(explorer);
        static bool Visible(AutomationElement e) => !e.IsOffscreen && e.BoundingRectangle.Width > 0 && e.BoundingRectangle.Height > 0;
        AutomationElement[] Shell(Func<AutomationElement, bool> match) => desktop.FindAllChildren()
            .Where(root => explorer.Contains(root.Properties.ProcessId.ValueOrDefault))
            .SelectMany(root => root.FindAllDescendants().Prepend(root))
            .Where(match).ToArray();
        static bool Named(AutomationElement e, string name) => e.Properties.Name.TryGetValue(out var actual) && actual == name;

        AutomationElement[] Icons() => Shell(e => Named(e, "Boo companion") && Visible(e));
        var icons = Icons();
        if (icons.Length == 0)
        {
            var overflow = Assert.Single(Shell(e => e.Properties.ControlType.ValueOrDefault == ControlType.Button
                && Named(e, "Show Hidden Icons") && Visible(e)));
            var box = overflow.BoundingRectangle;
            Mouse.Click(new Point(box.Left + box.Width / 2, box.Top + box.Height / 2), MouseButton.Left);
            Assert.True(BooAppFixture.WaitUntil(() => (icons = Icons()).Length > 0, TimeSpan.FromSeconds(3)), "The Boo tray icon did not appear.");
        }
        var bounds = Assert.Single(icons).BoundingRectangle;
        // The centre of the icon is Explorer's picture; the button's left padding is the button itself.
        Mouse.RightClick(new Point(bounds.Left + Math.Max(1, bounds.Width / 8), bounds.Top + bounds.Height / 2));
        AutomationElement? quit = null;
        Assert.True(BooAppFixture.WaitUntil(() =>
        {
            quit = desktop.FindAllDescendants(cf => cf.ByProcessId(_app.App.ProcessId).And(cf.ByControlType(ControlType.Menu)))
                .Where(Visible)
                .SelectMany(menu => menu.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
                .SingleOrDefault(item => Named(item, "Quit") && Visible(item));
            return quit is not null;
        }, TimeSpan.FromSeconds(5)), "Boo's tray menu did not show a Quit item.");
        _boo.Capture("exit-flush-tray-menu");
        _boo.Record("tray menu open with Quit");
        return quit!.AsMenuItem();
    }
}

/// <summary>
/// PR #170 mutation M05: the saved acknowledgment may only appear once the history list already shows what was saved.
/// The Pause write is held so the acknowledgment is pending; after release the test watches the status and, at the first
/// sample that shows the saved "Paused." text, reads the history row (status first, history second, so a row read after an
/// acknowledgment that came first must already be current).
/// </summary>
public sealed class AcknowledgmentFollowsHistoryTests : IClassFixture<AckOrderFixture>
{
    private readonly AckOrderFixture _app;
    private readonly FocusAckDriver _boo;

    public AcknowledgmentFollowsHistoryTests(AckOrderFixture app, ITestOutputHelper output)
    {
        _app = app;
        _boo = new FocusAckDriver(app, output);
    }

    [Fact]
    public void FirstSavedPauseAcknowledgment_FindsTheHistoryRowAlreadyPaused()
    {
        var task = "ack-history " + Guid.NewGuid().ToString("N")[..8];
        try
        {
            _boo.Start(task);
            _boo.WaitStatus(s => s.StartsWith("Running;", StringComparison.Ordinal), "Running after Start");
            Assert.True(BooAppFixture.WaitUntil(() => Row(task)?.EndsWith("Running", StringComparison.Ordinal) == true, TimeSpan.FromSeconds(10)),
                $"the history row did not show Running: '{Row(task)}'");
            using (_boo.HoldWrites())
            {
                _boo.Press("FocusPauseButton");
                _boo.WaitFace("Paused");
                Thread.Sleep(500);
                Assert.False(_boo.Status().StartsWith("Paused.", StringComparison.Ordinal), "the Pause was acknowledged while its save was held");
            }
            var watch = Stopwatch.StartNew();
            string? rowAtAcknowledgment = null;
            while (watch.Elapsed < TimeSpan.FromSeconds(30))
            {
                if (_boo.Status().StartsWith("Paused.", StringComparison.Ordinal))
                {
                    rowAtAcknowledgment = Row(task);
                    break;
                }
            }
            _boo.Record($"history row at the first saved acknowledgment: '{rowAtAcknowledgment}'");
            Assert.NotNull(rowAtAcknowledgment);
            Assert.EndsWith("Paused", rowAtAcknowledgment, StringComparison.Ordinal);
        }
        finally
        {
            _boo.Flush("ack-history");
        }
    }

    /// <summary>The detail line ("25 min · ... · Paused") shown under the task's title in the history list, or null.</summary>
    private string? Row(string task)
    {
        var names = _boo.PanelElement("HistoryList")?.FindAllDescendants()
            .Select(e => e.Properties.Name.ValueOrDefault ?? string.Empty).ToList() ?? new List<string>();
        var title = names.IndexOf(task);
        return title < 0 ? null : names.Skip(title + 1).FirstOrDefault(name => name != task);
    }
}
