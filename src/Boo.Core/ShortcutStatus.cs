namespace Boo.Core;

/// <summary>
/// Truthful view of which global shortcuts Boo actually owns. Another app can hold Ctrl+Alt+B or Ctrl+Alt+F12
/// (RegisterHotKey fails) and Windows can refuse the hold-to-talk keyboard hook; every failure is folded into one
/// short, actionable message so a later status line cannot hide an earlier one.
/// </summary>
public sealed record ShortcutStatus(bool EmergencyStop, bool OpenPanel, bool HoldToTalk)
{
    public const string OpenGesture = "Ctrl+Alt+B";
    public const string EmergencyStopGesture = "Ctrl+Alt+F12";
    public const string HoldToTalkGesture = "Ctrl+Alt+Space";

    /// <summary>Before registration has been attempted: no failure is reported and no shortcut is advertised.</summary>
    public static ShortcutStatus Pending { get; } = new(false, false, false) { IsPending = true };

    public bool IsPending { get; private init; }

    public bool AllAvailable => !IsPending && EmergencyStop && OpenPanel && HoldToTalk;

    public IReadOnlyList<string> UnavailableShortcuts
    {
        get
        {
            if (IsPending)
            {
                return Array.Empty<string>();
            }

            var list = new List<string>(3);
            if (!OpenPanel) list.Add(OpenGesture);
            if (!EmergencyStop) list.Add(EmergencyStopGesture);
            if (!HoldToTalk) list.Add(HoldToTalkGesture);
            return list;
        }
    }

    /// <summary>One combined sentence for the panel status / settings; null when everything works.</summary>
    public string? PanelMessage
    {
        get
        {
            if (IsPending)
            {
                return null;
            }

            var parts = new List<string>(2);
            if (!OpenPanel && !EmergencyStop)
            {
                parts.Add($"{OpenGesture} and {EmergencyStopGesture} are used by other apps. Open or stop Boo from the tray icon; right-clicking Boo's eyes also stops.");
            }
            else if (!OpenPanel)
            {
                parts.Add($"{OpenGesture} is used by another app. Open Boo from the tray icon.");
            }
            else if (!EmergencyStop)
            {
                parts.Add($"{EmergencyStopGesture} is used by another app. Stop Boo from the tray icon or by right-clicking Boo's eyes.");
            }

            if (!HoldToTalk)
            {
                parts.Add($"{HoldToTalkGesture} is unavailable. Use Talk or hold Boo's eyes to speak.");
            }

            return parts.Count == 0 ? null : string.Join(" ", parts);
        }
    }

    /// <summary>A notch-sized hint (the notch has room for a few words only); null when everything works.</summary>
    public string? NotchMessage
    {
        get
        {
            if (IsPending)
            {
                return null;
            }

            var globalFailures = (OpenPanel ? 0 : 1) + (EmergencyStop ? 0 : 1);
            var total = globalFailures + (HoldToTalk ? 0 : 1);
            if (total == 0)
            {
                return null;
            }

            var noun = total > 1 ? "Shortcuts busy" : "Shortcut busy";
            return globalFailures > 0 ? $"{noun}: use tray" : $"{noun}: use Talk";
        }
    }

    public string TrayShowLabel => !IsPending && OpenPanel ? $"Show Boo ({OpenGesture})" : "Show Boo";

    public string TrayStopLabel => !IsPending && EmergencyStop ? $"Stop ({EmergencyStopGesture})" : "Stop";

    public ShortcutStatus WithGlobalHotkeys(bool emergencyStop, bool openPanel) =>
        IsPending
            ? new ShortcutStatus(emergencyStop, openPanel, HoldToTalk: true)
            : this with { EmergencyStop = emergencyStop, OpenPanel = openPanel };

    public ShortcutStatus WithHoldToTalk(bool holdToTalk) =>
        IsPending
            ? new ShortcutStatus(EmergencyStop: true, OpenPanel: true, holdToTalk)
            : this with { HoldToTalk = holdToTalk };
}
