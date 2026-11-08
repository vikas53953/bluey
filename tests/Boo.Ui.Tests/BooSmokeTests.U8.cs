using System.Diagnostics;
using System.Drawing;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Boo.Ui.Tests;

/// <summary>
/// U8 smoke steps on the same live Boo:
/// 9. Ctrl+Alt+S (Riley's "pause everything" gesture) pauses a running focus session exactly like Ctrl+Alt+F12.
/// 10. Hover-to-reveal: the notch's UIA ItemStatus (its NotchFaceState name) leaves "Hidden" when the mouse
///     touches the top-centre hover strip and returns to "Hidden" after the mouse leaves.
/// </summary>
public sealed partial class BooSmokeTests
{
    private static readonly TimeSpan RevealTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RetractTimeout = TimeSpan.FromSeconds(3);

    [Fact, Step(9)]
    public void PauseAllHotkey_CtrlAltS_PausesRunningFocusSession()
    {
        Evidence("09-pause-all", () =>
        {
            var panel = _boo.EnsurePanel();
            SetText(panel, "FocusTaskBox", "UI smoke pause-all task nine");
            SetText(panel, "FocusDurationBox", "5");
            Find(panel, "FocusStartButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Running", StringComparison.Ordinal), "Running after Start");
            _boo.CaptureScreen("09-running");

            // Ctrl+Alt+S is Riley's pause-everything gesture (NotchWindow hotkey 'S' = 0x53); it runs the same StopAll.
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.KEY_S);
            WaitForFocusStatus(panel, s => s == "Paused by Stop; Resume is explicit.", "'Paused by Stop' after Ctrl+Alt+S");
            AssertFocusClockFrozen(panel);
            AssertMicrophoneOff(panel);
            Assert.False(_boo.App.HasExited, "Boo exited after Ctrl+Alt+S.");
            _boo.CaptureScreen("09-paused-by-ctrl-alt-s");

            // Leave no running session behind for later steps.
            Find(panel, "FocusEndButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Ended", StringComparison.Ordinal), "Ended after End");
        });
    }

    [Fact, Step(10)]
    public void Notch_HoverStrip_RevealsAndRetracts()
    {
        Evidence("10-hover-reveal", () =>
        {
            // The open panel keeps the face awake; close it so the notch can rest in Hidden.
            if (_boo.FindPanel() is { } panel)
            {
                Find(panel, "CloseButton").AsButton().Invoke();
                BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout);
            }

            var notch = _boo.WaitForNotch(UiTimeout);
            var rect = notch.BoundingRectangle;
            var hoverPoint = new Point(rect.Left + rect.Width / 2, rect.Top + 2);
            var awayPoint = new Point(hoverPoint.X, hoverPoint.Y + 300);

            // Start from a resting notch: mouse well away from the strip.
            Mouse.MoveTo(awayPoint);
            Assert.True(
                BooAppFixture.WaitUntil(() => NotchItemStatus() == "Hidden", RetractTimeout),
                $"The notch did not rest in 'Hidden' before the hover check. ItemStatus: '{NotchItemStatus()}' (expected the NotchFaceState name).");
            _boo.CaptureScreen("10-before-hover");

            Mouse.MoveTo(hoverPoint);
            var clock = Stopwatch.StartNew();
            var revealed = BooAppFixture.WaitUntil(() => NotchItemStatus() is { Length: > 0 } s && s != "Hidden", RevealTimeout);
            var revealMs = clock.ElapsedMilliseconds;
            var revealedState = NotchItemStatus();
            _boo.CaptureScreen("10-hover-revealed");
            Assert.True(
                revealed,
                $"Hovering the top-centre strip at {hoverPoint} (notch {rect}) did not reveal the notch within {RevealTimeout.TotalSeconds}s. ItemStatus: '{revealedState}'.");

            Mouse.MoveTo(awayPoint);
            clock.Restart();
            var retracted = BooAppFixture.WaitUntil(() => NotchItemStatus() == "Hidden", RetractTimeout);
            var retractMs = clock.ElapsedMilliseconds;
            _boo.CaptureScreen("10-after-leave");

            var observation = $"notch={rect} hover={hoverPoint} away={awayPoint} revealedState='{revealedState}' revealMs={revealMs} retractMs={retractMs} retracted={retracted} at {DateTime.UtcNow:O}";
            _boo.WriteObservation("10-hover-reveal", observation);
            _output.WriteLine(observation);

            Assert.True(
                retracted,
                $"The notch did not return to 'Hidden' within {RetractTimeout.TotalSeconds}s after the mouse moved 300 px below the strip. ItemStatus: '{NotchItemStatus()}'.");
            Assert.False(_boo.App.HasExited, "Boo exited during the hover check.");
        });
    }

    /// <summary>The notch window's UIA ItemStatus (the current NotchFaceState name); empty when unavailable.</summary>
    private string NotchItemStatus()
    {
        try
        {
            return _boo.FindNotch() is { } notch ? notch.Properties.ItemStatus.ValueOrDefault ?? string.Empty : string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }
}
