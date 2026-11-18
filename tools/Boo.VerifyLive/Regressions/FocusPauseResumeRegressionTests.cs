using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.VerifyLive.Regressions;

/// <summary>A fresh Boo with no key and speech off, for the focus life-cycle test (no AI request is made).</summary>
public sealed class FocusLifecycleNoKeyBooFixture : BooAppFixture
{
    public FocusLifecycleNoKeyBooFixture() : base(dir =>
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }) { }
}

/// <summary>
/// The valid focus transitions around issue #70's fix (PR #82): Pause on a RUNNING session really pauses it (status,
/// Paused face, clock stops), Resume really resumes it (Paused face gone, clock runs), End ends it (no Paused face).
/// The rule this test holds: the notch shows the Paused face exactly while the session is paused, never because a
/// session merely exists.
/// </summary>
public sealed class FocusPauseResumeRegressionTests : IClassFixture<FocusLifecycleNoKeyBooFixture>
{
    private static readonly TimeSpan FaceTimeout = TimeSpan.FromSeconds(5);
    private readonly FocusLifecycleNoKeyBooFixture _boo;
    private readonly ITestOutputHelper _output;

    public FocusPauseResumeRegressionTests(FocusLifecycleNoKeyBooFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void PauseResumeEnd_PausedFaceAndClockFollowTheSession()
    {
        _boo.WaitForNotch(TimeSpan.FromSeconds(60));
        Thread.Sleep(1000); // the open hotkey is registered by the notch window
        var panel = _boo.EnsurePanel();
        var notes = new List<string>();
        string Focus() => Name(panel, "FocusStatus");
        string Clock() => Name(panel, "FocusTimer");

        Box(panel, "FocusTaskBox").Text = "Regression: pause, resume, end";
        Box(panel, "FocusDurationBox").Text = "25";
        Button(panel, "FocusStartButton").Invoke();
        Assert.True(BooAppFixture.WaitUntil(() => Focus().StartsWith("Running", StringComparison.Ordinal), FaceTimeout), $"Start: '{Focus()}'");
        MovePointerAway();

        // Pause a RUNNING session: status, face and clock must all say paused.
        Button(panel, "FocusPauseButton").Invoke();
        var paused = BooAppFixture.WaitUntil(() => Focus().StartsWith("Paused", StringComparison.Ordinal), FaceTimeout);
        var pausedFace = BooAppFixture.WaitUntil(() => Face() == "Paused", FaceTimeout);
        var clockAtPause = Clock();
        Thread.Sleep(2500);
        var clockAfterWait = Clock();
        notes.Add($"pause: status='{Focus()}' face='{Face()}' clock {clockAtPause} -> {clockAfterWait}");
        _boo.CaptureScreen("regression-focus-paused");

        // Resume: the Paused face must go away and the clock must run again.
        Button(panel, "FocusResumeButton").Invoke();
        var running = BooAppFixture.WaitUntil(() => Focus().StartsWith("Running", StringComparison.Ordinal), FaceTimeout);
        var leftPaused = BooAppFixture.WaitUntil(() => Face() is { Length: > 0 } f && f != "Paused", FaceTimeout);
        var clockAtResume = Clock();
        var clockMoved = BooAppFixture.WaitUntil(() => Clock() != clockAtResume, TimeSpan.FromSeconds(4));
        notes.Add($"resume: status='{Focus()}' face='{Face()}' clockMoved={clockMoved}");

        // End: the session is over; the Paused face must not come back.
        Button(panel, "FocusEndButton").Invoke();
        var ended = BooAppFixture.WaitUntil(() => Focus().StartsWith("Ended", StringComparison.Ordinal), FaceTimeout);
        var rested = BooAppFixture.WaitUntil(() => Face() == "Hidden", TimeSpan.FromSeconds(10));
        notes.Add($"end: status='{Focus()}' face='{Face()}' rested={rested}");
        _boo.CaptureScreen("regression-focus-pause-resume-end");
        _boo.WriteObservation("regression-focus-pause-resume-end", string.Join("\n", notes));
        _output.WriteLine(string.Join("\n", notes));

        Assert.True(paused, $"Pause on a running session did not say Paused: '{Focus()}'.");
        Assert.True(pausedFace, "Pause on a running session did not show the Paused face.");
        Assert.Equal(clockAtPause, clockAfterWait);
        Assert.True(running, $"Resume did not say Running: '{Focus()}'.");
        Assert.True(leftPaused, "After Resume the notch still shows the Paused face.");
        Assert.True(clockMoved, "After Resume the focus clock did not run.");
        Assert.True(ended, $"End did not end the session: '{Focus()}'.");
        Assert.True(rested, $"After End the notch did not come back to rest; face '{Face()}'.");
    }

    private void MovePointerAway()
    {
        var rect = _boo.WaitForNotch(TimeSpan.FromSeconds(10)).BoundingRectangle;
        Mouse.MoveTo(new System.Drawing.Point(rect.Left + rect.Width / 2, rect.Top + 400));
    }

    private string Face()
    {
        try { return _boo.FindNotch()?.Properties.ItemStatus.ValueOrDefault ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private AutomationElement Element(Window panel, string id) =>
        _boo.WaitFor(() => panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(30), $"element '{id}'");

    private FlaUI.Core.AutomationElements.Button Button(Window panel, string id) => Element(panel, id).AsButton();

    private FlaUI.Core.AutomationElements.TextBox Box(Window panel, string id) => Element(panel, id).AsTextBox();

    private static string Name(Window panel, string id) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(id)) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
