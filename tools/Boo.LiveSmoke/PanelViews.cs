using FlaUI.Core.AutomationElements;

namespace Boo.Demo.Tests;

/// <summary>
/// Round 3 (22 Nov 2026): the companion window is a hanging panel with three views (Home, Chat, Settings) and only
/// the open view's controls are in the UI Automation control view. The live drivers in tools/ look for a control in
/// the open view first and, when it is not there, open the other views through their tab buttons and look again.
/// </summary>
public static class PanelViews
{
    /// <summary>The tab buttons of the three views, in the order the driver tries them.</summary>
    private static readonly string[] ViewTabButtons = { "ChatTabButton", "HomeTabButton", "SettingsTabButton" };

    /// <summary>
    /// The control with this AutomationId in whichever view holds it; that view stays open. Null when no view holds
    /// it, with the view that was open put back, so "the element is not there" keeps its meaning (a collapsed
    /// validation message, a cleared Review action).
    /// </summary>
    public static AutomationElement? FindInView(Window panel, string automationId)
    {
        if (panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } here)
        {
            return here;
        }

        var openView = OpenViewTab(panel);
        foreach (var tab in ViewTabButtons)
        {
            if (string.Equals(tab, openView, StringComparison.Ordinal) || !ShowViewTab(panel, tab))
            {
                continue;
            }
            var deadline = DateTime.UtcNow + TimeSpan.FromMilliseconds(400);
            do
            {
                if (panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } found)
                {
                    return found;
                }
                Thread.Sleep(40);
            }
            while (DateTime.UtcNow < deadline);
        }

        if (openView is not null)
        {
            ShowViewTab(panel, openView);
        }
        return null;
    }

    /// <summary>Opens a view by its tab button ("HomeTabButton", "ChatTabButton", "SettingsTabButton"); false when the button is missing.</summary>
    public static bool ShowViewTab(Window panel, string tabButtonId)
    {
        try
        {
            if (panel.FindFirstDescendant(cf => cf.ByAutomationId(tabButtonId)) is not { } button)
            {
                return false;
            }
            button.AsButton().Invoke();
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>The open view's name ("Home", "Chat", "Settings") from the window's HelpText; empty when unreadable.</summary>
    public static string OpenView(Window panel)
    {
        try
        {
            return panel.Properties.HelpText.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string? OpenViewTab(Window panel) => OpenView(panel) switch
    {
        "Home" => "HomeTabButton",
        "Chat" => "ChatTabButton",
        "Settings" => "SettingsTabButton",
        _ => null,
    };
}
