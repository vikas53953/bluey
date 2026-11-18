using System.Diagnostics;
using System.Drawing;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Boo.CrashReopen;

/// <summary>Opens Boo's notification-area (tray) menu on the hosted desktop the way a person does.</summary>
public static class TrayMenu
{
    /// <summary>
    /// Opens the menu (the overflow arrow when the icon is hidden, then a right-click on the "Boo companion" icon) and
    /// returns the one visible Boo menu item whose name matches <paramref name="item"/>, without invoking it.
    /// </summary>
    public static FlaUI.Core.AutomationElements.MenuItem Open(BooAppFixture app, Func<string, bool> item, string what)
    {
        var desktop = app.Automation.GetDesktop();
        var explorer = new HashSet<int>();
        foreach (var process in Process.GetProcessesByName("explorer"))
        {
            using (process) explorer.Add(process.Id);
        }
        Assert.NotEmpty(explorer);
        AutomationElement[] Shell(Func<AutomationElement, bool> match) => desktop.FindAllChildren()
            .Where(root => explorer.Contains(root.Properties.ProcessId.ValueOrDefault))
            .SelectMany(root => root.FindAllDescendants().Prepend(root))
            .Where(match).ToArray();

        AutomationElement[] Icons() => Shell(e => Named(e, n => n == "Boo companion") && Visible(e));
        var icons = Icons();
        if (icons.Length == 0)
        {
            var overflow = Assert.Single(Shell(e => e.Properties.ControlType.ValueOrDefault == ControlType.Button
                && Named(e, n => n == "Show Hidden Icons") && Visible(e)));
            var box = overflow.BoundingRectangle;
            Mouse.Click(new Point(box.Left + box.Width / 2, box.Top + box.Height / 2), MouseButton.Left);
            Assert.True(BooAppFixture.WaitUntil(() => (icons = Icons()).Length > 0, TimeSpan.FromSeconds(3)), "The Boo tray icon did not appear.");
        }
        var bounds = Assert.Single(icons).BoundingRectangle;
        // The centre of the icon is Explorer's picture; the button's left padding is the button itself.
        Mouse.RightClick(new Point(bounds.Left + Math.Max(1, bounds.Width / 8), bounds.Top + bounds.Height / 2));
        AutomationElement? found = null;
        Assert.True(BooAppFixture.WaitUntil(() =>
        {
            found = desktop.FindAllDescendants(cf => cf.ByProcessId(app.App.ProcessId).And(cf.ByControlType(ControlType.Menu)))
                .Where(Visible)
                .SelectMany(menu => menu.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
                .SingleOrDefault(e => Named(e, item) && Visible(e));
            return found is not null;
        }, TimeSpan.FromSeconds(5)), $"Boo's tray menu did not show {what}.");
        return found!.AsMenuItem();
    }

    private static bool Visible(AutomationElement e) => !e.IsOffscreen && e.BoundingRectangle.Width > 0 && e.BoundingRectangle.Height > 0;

    private static bool Named(AutomationElement e, Func<string, bool> match) => e.Properties.Name.TryGetValue(out var actual) && match(actual);
}
