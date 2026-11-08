using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ShortcutStatusTests
{
    [Fact]
    public void AllRegisteredHasNoMessage()
    {
        var status = new ShortcutStatus(EmergencyStop: true, OpenPanel: true, HoldToTalk: true);

        Assert.True(status.AllAvailable);
        Assert.Null(status.PanelMessage);
        Assert.Null(status.NotchMessage);
        Assert.Empty(status.UnavailableShortcuts);
    }

    [Fact]
    public void OpenShortcutTakenPointsToTray()
    {
        var status = new ShortcutStatus(EmergencyStop: true, OpenPanel: false, HoldToTalk: true);

        Assert.False(status.AllAvailable);
        Assert.Equal("Ctrl+Alt+B is used by another app. Open Boo from the tray icon.", status.PanelMessage);
        Assert.Equal("Shortcut busy: use tray", status.NotchMessage);
        Assert.Equal(new[] { ShortcutStatus.OpenGesture }, status.UnavailableShortcuts);
    }

    [Fact]
    public void EmergencyStopTakenPointsToTrayStopAndRightClick()
    {
        var status = new ShortcutStatus(EmergencyStop: false, OpenPanel: true, HoldToTalk: true);

        Assert.Equal(
            "Ctrl+Alt+F12 is used by another app. Stop Boo from the tray icon or by right-clicking Boo's eyes.",
            status.PanelMessage);
        Assert.Equal("Shortcut busy: use tray", status.NotchMessage);
    }

    [Fact]
    public void BothGlobalShortcutsTakenAreReportedTogether()
    {
        var status = new ShortcutStatus(EmergencyStop: false, OpenPanel: false, HoldToTalk: true);

        Assert.Equal(
            "Ctrl+Alt+B and Ctrl+Alt+F12 are used by other apps. Open or stop Boo from the tray icon; right-clicking Boo's eyes also stops.",
            status.PanelMessage);
        Assert.Equal("Shortcuts busy: use tray", status.NotchMessage);
        Assert.Equal(2, status.UnavailableShortcuts.Count);
    }

    [Fact]
    public void HoldToTalkFailureSuggestsAlternatives()
    {
        var status = new ShortcutStatus(EmergencyStop: true, OpenPanel: true, HoldToTalk: false);

        Assert.Equal("Ctrl+Alt+Space is unavailable. Use Talk or hold Boo's eyes to speak.", status.PanelMessage);
        Assert.Equal("Shortcut busy: use Talk", status.NotchMessage);
    }

    [Fact]
    public void EverythingFailingCombinesIntoOneMessage()
    {
        var status = new ShortcutStatus(EmergencyStop: false, OpenPanel: false, HoldToTalk: false);

        Assert.Equal(
            "Ctrl+Alt+B and Ctrl+Alt+F12 are used by other apps. Open or stop Boo from the tray icon; right-clicking Boo's eyes also stops. " +
            "Ctrl+Alt+Space is unavailable. Use Talk or hold Boo's eyes to speak.",
            status.PanelMessage);
        Assert.Equal("Shortcuts busy: use tray", status.NotchMessage);
        Assert.Equal(3, status.UnavailableShortcuts.Count);
    }

    [Fact]
    public void TrayMenuLabelOnlyAdvertisesWorkingShortcut()
    {
        Assert.Equal("Show Boo (Ctrl+Alt+B)", new ShortcutStatus(true, true, true).TrayShowLabel);
        Assert.Equal("Show Boo", new ShortcutStatus(true, false, true).TrayShowLabel);
        Assert.Equal("Stop (Ctrl+Alt+F12)", new ShortcutStatus(true, true, true).TrayStopLabel);
        Assert.Equal("Stop", new ShortcutStatus(false, true, true).TrayStopLabel);
    }

    [Fact]
    public void PendingStatusDoesNotClaimShortcuts()
    {
        var status = ShortcutStatus.Pending;

        Assert.Null(status.PanelMessage);
        Assert.Null(status.NotchMessage);
        Assert.Equal("Show Boo", status.TrayShowLabel);
        Assert.Equal("Stop", status.TrayStopLabel);
    }

    [Fact]
    public void WithersUpdateOnlyTheirShortcuts()
    {
        var status = new ShortcutStatus(true, true, true).WithHoldToTalk(false);
        Assert.False(status.HoldToTalk);
        Assert.True(status.OpenPanel);

        status = status.WithGlobalHotkeys(emergencyStop: false, openPanel: true);
        Assert.False(status.EmergencyStop);
        Assert.True(status.OpenPanel);
        Assert.False(status.HoldToTalk);
    }
}
