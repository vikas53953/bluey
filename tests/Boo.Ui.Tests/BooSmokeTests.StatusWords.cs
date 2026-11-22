using System.Text.RegularExpressions;
using FlaUI.Core.AutomationElements;

namespace Boo.Ui.Tests;

/// <summary>
/// Round 3 status word (Sprint 1 MVP pick 4, 22 Nov 2026), on the same live Boo:
/// 15. While a focus session runs and the panel is closed, the notch shows the time left (mm:ss) beside the eyes, it
///     ticks down, and it is gone as soon as the panel opens again. Written before the implementation; frozen.
/// </summary>
public sealed partial class BooSmokeTests
{
    private static readonly Regex FocusClock = new("^[0-9]{2,}:[0-9]{2}$", RegexOptions.CultureInvariant);

    [Fact, Step(15)]
    public void Notch_ShowsFocusTimeBesideTheEyes_WhilePanelIsClosed()
    {
        Evidence("15-notch-focus-time", () =>
        {
            var panel = _boo.EnsurePanel();
            SetText(panel, "FocusTaskBox", "UI smoke notch clock task fifteen");
            SetText(panel, "FocusDurationBox", "5");
            Find(panel, "FocusStartButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Running", StringComparison.Ordinal), "Running after Start");

            // While the panel is open the notch carries no word (the panel already shows the clock).
            Assert.True(BooAppFixture.WaitUntil(() => NotchStatusWord() is null, UiTimeout),
                $"The notch showed '{NotchStatusWord()}' while the panel was open; the word belongs to the closed-panel notch only.");

            Find(panel, "CloseButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout), "Close did not hide the panel.");

            string? first = null;
            var shown = BooAppFixture.WaitUntil(() => (first = NotchStatusWord()) is { } w && FocusClock.IsMatch(w), UiTimeout);
            _boo.CaptureScreen("15-focus-time-on-notch");
            Assert.True(shown, $"The notch did not show the focus time (mm:ss) beside the eyes within {UiTimeout.TotalSeconds}s; it shows '{first ?? "(nothing)"}'.");
            Assert.NotEqual("Hidden", NotchItemStatus());

            string? second = null;
            var ticked = BooAppFixture.WaitUntil(() => (second = NotchStatusWord()) is { } w && FocusClock.IsMatch(w) && w != first, TimeSpan.FromSeconds(4));
            Assert.True(ticked, $"The notch clock stayed at '{first}' for 4 s; it should tick every second (now '{second}').");
            Assert.True(string.CompareOrdinal(second, first) < 0 || second!.Length < first!.Length,
                $"The notch clock went up from '{first}' to '{second}'; the time left must count down.");

            BooAppFixture.PressOpenShortcut();
            panel = _boo.WaitForPanel(UiTimeout);
            Assert.True(BooAppFixture.WaitUntil(() => NotchStatusWord() is null, UiTimeout),
                $"Opening the panel did not clear the notch word; it still shows '{NotchStatusWord()}'.");
            _boo.CaptureScreen("15-panel-open-no-word");

            // Leave no running session behind for later steps.
            Find(panel, "FocusEndButton").AsButton().Invoke();
            WaitForFocusStatus(panel, s => s.StartsWith("Ended", StringComparison.Ordinal), "Ended after End");
            Assert.True(BooAppFixture.WaitUntil(() => NotchStatusWord() is null, UiTimeout),
                $"The notch still shows '{NotchStatusWord()}' after the session ended.");
        });
    }

    /// <summary>The status word beside the eyes (UIA Name of the NotchStatusWord text), or null when the notch shows none.</summary>
    private string? NotchStatusWord()
    {
        try
        {
            var word = _boo.FindNotch()?.FindFirstDescendant(cf => cf.ByAutomationId("NotchStatusWord"));
            if (word is null || word.IsOffscreen)
            {
                return null;
            }
            var text = word.Name;
            return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
        }
        catch (Exception ex) when (ex is FlaUI.Core.Exceptions.PropertyNotSupportedException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }
}
