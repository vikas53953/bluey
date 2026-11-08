using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// U8 section A: Ctrl+Alt+S (Riley's "pause everything" gesture) joins Ctrl+Alt+F12 in ShortcutStatus.
/// Existing 3-argument behaviour must not change; PauseAll defaults to true.
/// </summary>
public sealed class PauseAllShortcutTests
{
    private const string OpenTaken = "Ctrl+Alt+B is used by another app. Open Boo from the tray icon.";
    private const string StopTaken = "Ctrl+Alt+F12 is used by another app. Stop Boo from the tray icon or by right-clicking Boo's eyes.";
    private const string BothTaken = "Ctrl+Alt+B and Ctrl+Alt+F12 are used by other apps. Open or stop Boo from the tray icon; right-clicking Boo's eyes also stops.";
    private const string TalkUnavailable = "Ctrl+Alt+Space is unavailable. Use Talk or hold Boo's eyes to speak.";
    private const string PauseTakenStopWorks = "Ctrl+Alt+S is used by another app; Ctrl+Alt+F12 still stops Boo.";
    private const string PauseTaken = "Ctrl+Alt+S is used by another app.";

    /// <summary>Every 3-argument combination with today's exact PanelMessage / NotchMessage.</summary>
    public static IEnumerable<object?[]> ThreeArgumentCases() => new[]
    {
        new object?[] { true, true, true, null, null },
        new object?[] { true, false, true, OpenTaken, "Shortcut busy: use tray" },
        new object?[] { false, true, true, StopTaken, "Shortcut busy: use tray" },
        new object?[] { false, false, true, BothTaken, "Shortcuts busy: use tray" },
        new object?[] { true, true, false, TalkUnavailable, "Shortcut busy: use Talk" },
        new object?[] { true, false, false, OpenTaken + " " + TalkUnavailable, "Shortcuts busy: use tray" },
        new object?[] { false, true, false, StopTaken + " " + TalkUnavailable, "Shortcuts busy: use tray" },
        new object?[] { false, false, false, BothTaken + " " + TalkUnavailable, "Shortcuts busy: use tray" },
    };

    [Fact]
    public void PauseAllGestureIsCtrlAltS()
    {
        Assert.Equal("Ctrl+Alt+S", ShortcutStatus.PauseAllGesture);
    }

    [Fact]
    public void ThreeArgumentConstructionDefaultsPauseAllToTrue()
    {
        var status = new ShortcutStatus(true, true, true);

        Assert.True(status.PauseAll);
        Assert.Equal(new ShortcutStatus(true, true, true, true), status);
        Assert.True(status.AllAvailable);
    }

    [Theory]
    [MemberData(nameof(ThreeArgumentCases))]
    public void ThreeArgumentBehaviourIsUnchanged(bool emergencyStop, bool openPanel, bool holdToTalk, string? panel, string? notch)
    {
        var status = new ShortcutStatus(emergencyStop, openPanel, holdToTalk);

        Assert.Equal(panel, status.PanelMessage);
        Assert.Equal(notch, status.NotchMessage);
        Assert.Equal(emergencyStop && openPanel && holdToTalk, status.AllAvailable);
        Assert.DoesNotContain(ShortcutStatus.PauseAllGesture, status.UnavailableShortcuts);
    }

    [Theory]
    [MemberData(nameof(ThreeArgumentCases))]
    public void PanelAndNotchMessagesAreByteIdenticalWhenPauseAllIsTrue(bool emergencyStop, bool openPanel, bool holdToTalk, string? panel, string? notch)
    {
        var status = new ShortcutStatus(emergencyStop, openPanel, holdToTalk, PauseAll: true);

        Assert.Equal(panel, status.PanelMessage);
        Assert.Equal(notch, status.NotchMessage);
        Assert.Equal(new ShortcutStatus(emergencyStop, openPanel, holdToTalk).UnavailableShortcuts, status.UnavailableShortcuts);
    }

    [Fact]
    public void PauseAllFailureIsListedAfterTheExistingEntries()
    {
        Assert.Equal(new[] { "Ctrl+Alt+S" }, new ShortcutStatus(true, true, true, false).UnavailableShortcuts);
        Assert.Equal(
            new[] { "Ctrl+Alt+B", "Ctrl+Alt+F12", "Ctrl+Alt+Space", "Ctrl+Alt+S" },
            new ShortcutStatus(false, false, false, false).UnavailableShortcuts);
        Assert.Equal(
            new[] { "Ctrl+Alt+F12", "Ctrl+Alt+S" },
            new ShortcutStatus(false, true, true, false).UnavailableShortcuts);
    }

    [Fact]
    public void AllAvailableRequiresPauseAll()
    {
        Assert.False(new ShortcutStatus(true, true, true, false).AllAvailable);
        Assert.True(new ShortcutStatus(true, true, true, true).AllAvailable);
    }

    [Fact]
    public void PauseAllFailureAloneSaysF12StillStops()
    {
        var status = new ShortcutStatus(EmergencyStop: true, OpenPanel: true, HoldToTalk: true, PauseAll: false);

        Assert.Equal(PauseTakenStopWorks, status.PanelMessage);
    }

    [Fact]
    public void PauseAllFailureWithEmergencyStopFailureUsesShortSentenceAfterExistingOne()
    {
        var status = new ShortcutStatus(EmergencyStop: false, OpenPanel: true, HoldToTalk: true, PauseAll: false);

        Assert.Equal(StopTaken + " " + PauseTaken, status.PanelMessage);
    }

    [Fact]
    public void PauseAllSentenceIsAppendedAfterEverySentence()
    {
        Assert.Equal(
            BothTaken + " " + TalkUnavailable + " " + PauseTaken,
            new ShortcutStatus(false, false, false, false).PanelMessage);
        Assert.Equal(
            OpenTaken + " " + TalkUnavailable + " " + PauseTakenStopWorks,
            new ShortcutStatus(true, false, false, false).PanelMessage);
        Assert.Equal(
            TalkUnavailable + " " + PauseTakenStopWorks,
            new ShortcutStatus(true, true, false, false).PanelMessage);
    }

    [Fact]
    public void NotchMessageIsNullWhenOnlyPauseAllFails()
    {
        Assert.Null(new ShortcutStatus(true, true, true, false).NotchMessage);
    }

    [Fact]
    public void NotchMessageIgnoresPauseAllOtherwise()
    {
        Assert.Equal("Shortcut busy: use tray", new ShortcutStatus(false, true, true, false).NotchMessage);
        Assert.Equal("Shortcut busy: use Talk", new ShortcutStatus(true, true, false, false).NotchMessage);
        Assert.Equal("Shortcuts busy: use tray", new ShortcutStatus(false, false, false, false).NotchMessage);
    }

    [Fact]
    public void TrayStopLabelIsUnchanged()
    {
        Assert.Equal("Stop (Ctrl+Alt+F12)", new ShortcutStatus(true, true, true, false).TrayStopLabel);
        Assert.Equal("Stop (Ctrl+Alt+F12)", new ShortcutStatus(true, true, true, true).TrayStopLabel);
        Assert.Equal("Stop", new ShortcutStatus(false, true, true, true).TrayStopLabel);
        Assert.Equal("Stop", ShortcutStatus.Pending.TrayStopLabel);
    }

    [Fact]
    public void PendingReportsNoPauseAllFailure()
    {
        var pending = ShortcutStatus.Pending;

        Assert.Empty(pending.UnavailableShortcuts);
        Assert.Null(pending.PanelMessage);
        Assert.Null(pending.NotchMessage);
        Assert.False(pending.AllAvailable);
    }

    [Fact]
    public void WithPauseAllFromPendingAssumesOtherShortcutsWork()
    {
        var failed = ShortcutStatus.Pending.WithPauseAll(false);
        Assert.Equal(new ShortcutStatus(true, true, true, false), failed);
        Assert.False(failed.IsPending);
        Assert.Equal(new[] { "Ctrl+Alt+S" }, failed.UnavailableShortcuts);

        var ok = ShortcutStatus.Pending.WithPauseAll(true);
        Assert.Equal(new ShortcutStatus(true, true, true, true), ok);
        Assert.False(ok.IsPending);
        Assert.True(ok.AllAvailable);
    }

    [Fact]
    public void WithPauseAllChangesOnlyPauseAll()
    {
        var status = new ShortcutStatus(EmergencyStop: false, OpenPanel: true, HoldToTalk: false).WithPauseAll(false);

        Assert.False(status.EmergencyStop);
        Assert.True(status.OpenPanel);
        Assert.False(status.HoldToTalk);
        Assert.False(status.PauseAll);

        status = status.WithPauseAll(true);
        Assert.Equal(new ShortcutStatus(false, true, false, true), status);
    }

    [Fact]
    public void OtherWithersPreservePauseAll()
    {
        var status = new ShortcutStatus(true, true, true, false);

        var afterHotkeys = status.WithGlobalHotkeys(emergencyStop: false, openPanel: true);
        Assert.False(afterHotkeys.PauseAll);
        Assert.False(afterHotkeys.EmergencyStop);

        var afterHold = status.WithHoldToTalk(false);
        Assert.False(afterHold.PauseAll);
        Assert.False(afterHold.HoldToTalk);

        var fromPending = ShortcutStatus.Pending.WithPauseAll(false).WithHoldToTalk(true).WithGlobalHotkeys(true, true);
        Assert.False(fromPending.PauseAll);
    }

    [Fact]
    public void OtherWithersFromPendingStartWithPauseAllTrue()
    {
        Assert.True(ShortcutStatus.Pending.WithGlobalHotkeys(emergencyStop: true, openPanel: true).PauseAll);
        Assert.True(ShortcutStatus.Pending.WithHoldToTalk(false).PauseAll);
        Assert.True(ShortcutStatus.Pending.WithGlobalHotkeys(true, true).AllAvailable);
    }
}
