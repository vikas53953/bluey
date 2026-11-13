using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.VerifyLive.Regressions;

/// <summary>A fresh Boo with no key and speech off: focus controls need no AI, so this costs no request.</summary>
public sealed class NoKeyBooFixture : BooAppFixture
{
    public NoKeyBooFixture() : base(dir =>
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }) { }
}

/// <summary>
/// Regression for the app explorer's finding (issue linked in docs/FEATURE_MAP.md row F11): after a focus session has
/// ENDED, pressing Pause still told the user "Paused" and pinned the notch on the Paused face, with no session to
/// resume or end, so nothing but Stop or a new session could clear it. Found by run
/// https://github.com/vikas53953/bluey/actions/runs/43468157115 (x06-focus.txt).
/// The rule this test holds: the Pause button only acts on a RUNNING session (the class of bug: a focus control that
/// acts on a session state it does not apply to); after End it changes nothing.
/// </summary>
public sealed class FocusPauseAfterEndRegressionTests : IClassFixture<NoKeyBooFixture>
{
    private readonly NoKeyBooFixture _boo;
    private readonly ITestOutputHelper _output;

    public FocusPauseAfterEndRegressionTests(NoKeyBooFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void PauseAfterEnd_DoesNotPinThePausedFace_OrClaimPaused()
    {
        _boo.WaitForNotch(TimeSpan.FromSeconds(60));
        Thread.Sleep(1000); // the open hotkey is registered by the notch window
        var panel = _boo.EnsurePanel();
        string Focus() => Name(panel, "FocusStatus");

        Box(panel, "FocusTaskBox").Text = "Regression: pause after end";
        Box(panel, "FocusDurationBox").Text = "25";
        Button(panel, "FocusStartButton").Invoke();
        Assert.True(BooAppFixture.WaitUntil(() => Focus().StartsWith("Running", StringComparison.Ordinal), TimeSpan.FromSeconds(10)), $"Start: '{Focus()}'");

        Button(panel, "FocusEndButton").Invoke();
        Assert.True(BooAppFixture.WaitUntil(() => Focus().StartsWith("Ended", StringComparison.Ordinal), TimeSpan.FromSeconds(10)), $"End: '{Focus()}'");
        var endedText = Focus();

        // The wrong-order click: Pause on an ended session.
        var pause = Button(panel, "FocusPauseButton");
        if (pause.IsEnabled) pause.Invoke();
        Thread.Sleep(800);
        var afterPause = Focus();

        // With the pointer away from the notch and nothing running, the notch must come back to rest.
        var rect = _boo.WaitForNotch(TimeSpan.FromSeconds(10)).BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + 400));
        var rested = BooAppFixture.WaitUntil(() => Face() == "Hidden", TimeSpan.FromSeconds(8));
        _boo.CaptureScreen("regression-focus-pause-after-end");
        _boo.WriteObservation("regression-focus-pause-after-end",
            $"afterEnd='{endedText}' pauseEnabled={pause.IsEnabled} afterPause='{afterPause}' face='{Face()}' rested={rested}");
        _output.WriteLine($"afterPause='{afterPause}' face='{Face()}'");

        Assert.Equal(endedText, afterPause);
        Assert.True(rested, $"After Pause on an ended session the notch stays '{Face()}' with no session running.");
    }

    private string Face()
    {
        try { return _boo.FindNotch()?.Properties.ItemStatus.ValueOrDefault ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private AutomationElement Element(Window panel, string id) =>
        _boo.WaitFor(() => panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(30), $"element '{id}'");

    private FlaUI.Core.AutomationElements.Button Button(Window panel, string id) => Element(panel, id).AsButton();

    private TextBox Box(Window panel, string id) => Element(panel, id).AsTextBox();

    private string Name(Window panel, string id) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
