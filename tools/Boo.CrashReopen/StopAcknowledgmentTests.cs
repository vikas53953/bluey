using System.Drawing;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.CrashReopen;

/// <summary>A fresh data folder, speech off, no key. The panel is open for the whole test.</summary>
public sealed class StopAckFixture : BooAppFixture
{
    public StopAckFixture() : base(FocusAckDriver.Seed) { }
}

/// <summary>
/// Issue #103, the whole class: every way a person stops Boo shows the same "Stopped" acknowledgment on the notch (its UI
/// Automation HelpText, which Q12E reads). Before the fix only the hotkey did; the notch right-click, the tray menu's Stop
/// and the panel's Stop button went through App.StopAll, whose acknowledgment branch only ran with no panel, and the panel
/// always exists. Each Stop is driven the way a person does it on the published Boo.exe, and each must be acknowledged
/// within 1 second. Between Stops the test waits for the previous "Stopped" to clear, so one flash cannot count twice.
/// </summary>
public sealed class StopAcknowledgmentTests : IClassFixture<StopAckFixture>
{
    private static readonly TimeSpan Acknowledge = TimeSpan.FromSeconds(1);
    private readonly StopAckFixture _app;
    private readonly FocusAckDriver _boo;

    public StopAcknowledgmentTests(StopAckFixture app, ITestOutputHelper output)
    {
        _app = app;
        _boo = new FocusAckDriver(app, output);
    }

    [Fact]
    public void EveryUserStop_ShowsStoppedOnTheNotch()
    {
        try
        {
            _boo.Status();   // opens the panel, as a person would have it open
            var notch = _app.WaitForNotch(TimeSpan.FromSeconds(30));
            var results = new List<string>
            {
                "notch right-click: " + Acknowledged(notch, () => RightClickNotch(notch)),
                "tray Stop: " + Acknowledged(notch, () => TrayMenu.Open(_app, name => name.StartsWith("Stop", StringComparison.Ordinal), "a Stop item").Invoke()),
                "panel Stop button: " + Acknowledged(notch, () => _boo.Press("StopButton")),
            };
            foreach (var result in results) _boo.Record(result);
            Assert.Equal(
                ["notch right-click: Stopped", "tray Stop: Stopped", "panel Stop button: Stopped"],
                results);
        }
        finally
        {
            _boo.Flush("stop-acknowledgment");
        }
    }

    /// <summary>Waits for any earlier "Stopped" to clear, runs <paramref name="stop"/>, then reports "Stopped" or what the notch said instead.</summary>
    private string Acknowledged(AutomationElement notch, Action stop)
    {
        Assert.True(BooAppFixture.WaitUntil(() => !HelpText(notch).Contains("Stopped", StringComparison.Ordinal), TimeSpan.FromSeconds(5)),
            "an earlier Stopped acknowledgment did not clear");
        stop();
        return BooAppFixture.WaitUntil(() => HelpText(notch).Contains("Stopped", StringComparison.Ordinal), Acknowledge)
            ? "Stopped"
            : "no acknowledgment (HelpText='" + HelpText(notch) + "')";
    }

    private static string HelpText(AutomationElement notch) => notch.Properties.HelpText.ValueOrDefault ?? string.Empty;

    /// <summary>Hovers the notch until it reveals its pocket, checks the pocket is what the pointer hits, then right-clicks it.</summary>
    private void RightClickNotch(AutomationElement notch)
    {
        var bounds = notch.BoundingRectangle;
        Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 2);
        Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.ItemStatus.ValueOrDefault == "Hover", TimeSpan.FromSeconds(5)), "the notch did not reveal on hover");
        Thread.Sleep(300);
        var pocket = notch.FindFirstDescendant(cf => cf.ByName("Open Boo companion").And(cf.ByControlType(ControlType.Button)));
        Assert.NotNull(pocket);
        var box = pocket.BoundingRectangle;
        var point = new Point(box.Left + box.Width / 2, box.Top + box.Height / 2);
        Mouse.Position = point;
        Assert.Equal("Open Boo companion", _app.Automation.FromPoint(point).Properties.Name.ValueOrDefault);
        Mouse.Down(MouseButton.Right);
        Mouse.Up(MouseButton.Right);
    }
}
