using System.Drawing;
using System.Text.Json;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.Ui.Tests;

/// <summary>
/// Native smoke run against the published Boo.exe, in story order:
/// 1. Ctrl+Alt+B opens the companion window (and reopens it after Close).
/// 2. Notch colours: save a valid pair, reject a low-contrast pair, reset to defaults.
/// 3. "Start Boo with Windows" writes and removes HKCU\...\Run\Boo.
/// 4. Notch screenshot (hover reveal) saved as CI evidence.
/// Checks read live UI Automation state and the files/registry Boo writes, never Boo's own claims alone.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class BooSmokeTests : IClassFixture<BooAppFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly BooAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public BooSmokeTests(BooAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact, Step(1)]
    public void OpenShortcut_CtrlAltB_OpensCompanionWindow()
    {
        Evidence("01-hotkey", () =>
        {
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(30));
            _output.WriteLine($"Notch window found: '{BooAppFixture.SafeName(notch)}' at {notch.BoundingRectangle}");
            _boo.CaptureScreen("01-before-hotkey");

            Assert.Null(_boo.FindPanel()); // Boo starts with only the notch; the panel is hidden.

            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(UiTimeout);
            var title = BooAppFixture.SafeName(panel);
            _output.WriteLine($"Panel opened by Ctrl+Alt+B: '{title}'");
            Assert.Contains("[QA: capturable]", title, StringComparison.Ordinal); // BOO_QA_CAPTURABLE=1 reached the app.
            _boo.CaptureScreen("01-after-hotkey");

            // Close hides the panel; the shortcut must bring it back.
            Find(panel, "CloseButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout), "Close did not hide the companion window.");

            BooAppFixture.PressOpenShortcut();
            _boo.WaitForPanel(UiTimeout);
        });
    }

    [Fact, Step(2)]
    public void Appearance_SaveRejectReset_PersistsTruthfully()
    {
        Evidence("02-colours", () =>
        {
            var panel = _boo.EnsurePanel();

            // Valid pair (contrast ~10:1) is saved and reflected back.
            SetText(panel, "BodyColorBox", "#123456");
            SetText(panel, "EyeColorBox", "#FFEECC");
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => AppearanceIs("#123456", "#FFEECC"), UiTimeout),
                $"appearance.json did not become #123456/#FFEECC. Content: {ReadOrMissing(_boo.AppearancePath)}");
            Assert.Equal("#123456", Find(panel, "BodyColorBox").AsTextBox().Text, ignoreCase: true);
            Assert.Equal("#FFEECC", Find(panel, "EyeColorBox").AsTextBox().Text, ignoreCase: true);
            Assert.StartsWith("Settings saved", StatusText(panel), StringComparison.Ordinal);
            Assert.Null(TryFind(panel, "AppearanceValidationText"));
            _boo.CaptureScreen("02-saved-custom");
            CaptureNotch("02-notch-custom-colours");

            // Low-contrast pair is rejected, shown to the user, and the saved file is untouched.
            var savedBytes = File.ReadAllBytes(_boo.AppearancePath);
            SetText(panel, "BodyColorBox", "#777777");
            SetText(panel, "EyeColorBox", "#888888");
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            var validation = _boo.WaitFor(() => TryFind(panel, "AppearanceValidationText"), UiTimeout, "the appearance validation message");
            Assert.StartsWith("Appearance was not saved", BooAppFixture.SafeName(validation), StringComparison.Ordinal);
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel).StartsWith("Appearance was not saved", StringComparison.Ordinal), UiTimeout),
                $"Status did not report the rejection. Status: '{StatusText(panel)}'");
            Assert.Equal(savedBytes, File.ReadAllBytes(_boo.AppearancePath));
            _boo.CaptureScreen("02-rejected-low-contrast");

            // Reset restores black body / white eyes on disk and in the boxes, and clears the error.
            Find(panel, "ResetAppearanceButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => AppearanceIs("#000000", "#FFFFFF"), UiTimeout),
                $"appearance.json did not reset to #000000/#FFFFFF. Content: {ReadOrMissing(_boo.AppearancePath)}");
            Assert.Equal("#000000", Find(panel, "BodyColorBox").AsTextBox().Text, ignoreCase: true);
            Assert.Equal("#FFFFFF", Find(panel, "EyeColorBox").AsTextBox().Text, ignoreCase: true);
            Assert.Equal("Appearance reset to black with white eyes.", StatusText(panel));
            Assert.True(
                BooAppFixture.WaitUntil(() => TryFind(panel, "AppearanceValidationText") is null, UiTimeout),
                "The validation message stayed visible after a successful reset.");
            _boo.CaptureScreen("02-reset-defaults");
        });
    }

    [Fact, Step(3)]
    public void StartWithWindows_Checkbox_WritesAndRemovesRunValue()
    {
        Evidence("03-start-with-windows", () =>
        {
            var panel = _boo.EnsurePanel();
            var box = Find(panel, "StartWithWindowsBox").AsCheckBox();
            var expected = "\"" + _boo.ExePath + "\" --background";

            Assert.False(box.IsChecked, "Start with Windows must default to off on fresh settings.");
            var before = _boo.ReadRunValue();
            _output.WriteLine($"Run value before: {before ?? "<absent>"}");

            box.IsChecked = true;
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => string.Equals(_boo.ReadRunValue(), expected, StringComparison.OrdinalIgnoreCase), UiTimeout),
                $"Run value was '{_boo.ReadRunValue() ?? "<absent>"}', expected '{expected}'.");
            Assert.True(SettingsStartWithWindows() is true, $"settings.json did not record StartWithWindows=true: {ReadOrMissing(_boo.SettingsPath)}");
            _output.WriteLine($"Run value after ON: {_boo.ReadRunValue()}");

            // Saving again with it ON is idempotent.
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Thread.Sleep(500);
            Assert.Equal(expected, _boo.ReadRunValue(), ignoreCase: true);

            box.IsChecked = false;
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => _boo.ReadRunValue() is null, UiTimeout),
                $"Run value still present after turning it off: '{_boo.ReadRunValue()}'.");
            Assert.True(SettingsStartWithWindows() is false, $"settings.json did not record StartWithWindows=false: {ReadOrMissing(_boo.SettingsPath)}");
        });
    }

    [Fact, Step(4)]
    public void Notch_Screenshot_IsCapturedForEvidence()
    {
        Evidence("04-notch", () =>
        {
            // Hide the panel so the notch is photographed on its own over the desktop.
            if (_boo.FindPanel() is { } panel)
            {
                Find(panel, "CloseButton").AsButton().Invoke();
                BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout);
            }

            var path = CaptureNotch("04-notch-default");
            Assert.True(new FileInfo(path).Length > 0, "Notch screenshot is empty.");
        });
    }

    /// <summary>
    /// Hovers the top-center strip to reveal the notch and saves a crop around it. The hover/reveal
    /// geometry is recorded as an observation, not asserted (hover acceptance is a separate QA item).
    /// </summary>
    private string CaptureNotch(string name)
    {
        var notch = _boo.WaitForNotch(UiTimeout);
        var collapsed = notch.BoundingRectangle;
        var hoverPoint = new Point(collapsed.Left + collapsed.Width / 2, collapsed.Top + Math.Max(1, Math.Min(3, collapsed.Height / 2)));
        Mouse.MoveTo(hoverPoint);
        Thread.Sleep(900);

        var revealed = _boo.FindNotch()?.BoundingRectangle ?? collapsed;
        var centerX = revealed.Left + revealed.Width / 2;
        var width = Math.Max(revealed.Width + 80, 360);
        var height = Math.Max(revealed.Height + 40, 140);
        var crop = new Rectangle(Math.Max(0, centerX - width / 2), Math.Max(0, revealed.Top), width, height);
        var path = _boo.CaptureRectangle(name, crop);
        _boo.CaptureScreen(name + "-fullscreen");

        var observation = $"collapsed={collapsed} revealed={revealed} hover={hoverPoint} crop={crop} at {DateTime.UtcNow:O}";
        _boo.WriteObservation(name, observation);
        _output.WriteLine($"{name}: {observation}");

        Mouse.MoveTo(new Point(crop.Left + crop.Width / 2, crop.Bottom + 300)); // let it retract
        Thread.Sleep(400);
        return path;
    }

    private void Evidence(string step, Action body)
    {
        try
        {
            body();
        }
        catch (Exception)
        {
            var shot = _boo.CaptureScreen(step + "-FAILED");
            _output.WriteLine($"Failure screenshot: {shot}");
            throw;
        }
    }

    private static AutomationElement? TryFind(Window panel, string automationId) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private AutomationElement Find(Window panel, string automationId) =>
        _boo.WaitFor(() => TryFind(panel, automationId), UiTimeout, $"element '{automationId}'");

    private void SetText(Window panel, string automationId, string value)
    {
        var box = Find(panel, automationId).AsTextBox();
        box.Text = value;
        Assert.True(
            BooAppFixture.WaitUntil(() => string.Equals(box.Text, value, StringComparison.OrdinalIgnoreCase), UiTimeout),
            $"Could not set {automationId} to {value}; it reads '{box.Text}'.");
    }

    private static string StatusText(Window panel) =>
        TryFind(panel, "StatusText") is { } status ? BooAppFixture.SafeName(status) : string.Empty;

    private bool AppearanceIs(string body, string eyes)
    {
        if (!File.Exists(_boo.AppearancePath))
        {
            return false;
        }
        using var json = JsonDocument.Parse(BooAppFixture.ReadShared(_boo.AppearancePath));
        return string.Equals(Property(json.RootElement, "BodyColor"), body, StringComparison.OrdinalIgnoreCase)
            && string.Equals(Property(json.RootElement, "EyeColor"), eyes, StringComparison.OrdinalIgnoreCase);
    }

    private bool? SettingsStartWithWindows()
    {
        if (!File.Exists(_boo.SettingsPath))
        {
            return null;
        }
        using var json = JsonDocument.Parse(BooAppFixture.ReadShared(_boo.SettingsPath));
        foreach (var property in json.RootElement.EnumerateObject())
        {
            if (string.Equals(property.Name, "StartWithWindows", StringComparison.OrdinalIgnoreCase)
                && property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                return property.Value.GetBoolean();
            }
        }
        return null;
    }

    private static string? Property(JsonElement root, string name)
    {
        foreach (var property in root.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
            {
                return property.Value.GetString();
            }
        }
        return null;
    }

    private static string ReadOrMissing(string path) => File.Exists(path) ? BooAppFixture.ReadShared(path) : "<missing>";
}
