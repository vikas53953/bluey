using System.Text.Json;
using FlaUI.Core.AutomationElements;
using Xunit.Abstractions;

namespace Boo.Ui.Tests;

/// <summary>
/// Launches its own Boo with a BOO_DATA_DIR pre-seeded with corrupt settings.json and appearance.json.
/// Test parallelisation is off for the assembly (OrderedSteps.cs + xunit.runner.json), so this class's fixture
/// is created only after another class's fixture has been disposed, and Boo (single-instance) never runs twice.
/// </summary>
public sealed class CorruptDataFixture : BooAppFixture
{
    public const string CorruptSettings = "{ \"SpeechEnabled\": tru";
    public const string CorruptAppearance = "{\"BodyColor\": 42, \"EyeColor\": [";

    public CorruptDataFixture()
        : base(directory =>
        {
            File.WriteAllText(Path.Combine(directory, "settings.json"), CorruptSettings);
            File.WriteAllText(Path.Combine(directory, "appearance.json"), CorruptAppearance);
        })
    {
    }
}

public sealed class CorruptDataStartupTests : IClassFixture<CorruptDataFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(15);

    private readonly CorruptDataFixture _boo;
    private readonly ITestOutputHelper _output;

    public CorruptDataStartupTests(CorruptDataFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void CorruptSettingsAndAppearance_BooStartsWithDefaults_AndSaveRepairsFiles()
    {
        try
        {
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(30));
            _output.WriteLine($"Notch found with corrupt data: '{BooAppFixture.SafeName(notch)}'");
            Assert.False(_boo.App.HasExited, "Boo exited at startup with corrupt settings/appearance files.");

            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(UiTimeout);
            _boo.CaptureScreen("09-corrupt-start-panel");

            // Defaults are shown: black body / white eyes, Start with Windows off.
            Assert.Equal("#000000", Find(panel, "BodyColorBox").AsTextBox().Text, ignoreCase: true);
            Assert.Equal("#FFFFFF", Find(panel, "EyeColorBox").AsTextBox().Text, ignoreCase: true);
            Assert.False(Find(panel, "StartWithWindowsBox").AsCheckBox().IsChecked, "Start with Windows must fall back to off.");

            // The corrupt appearance is reported to the user rather than silently ignored.
            var validation = _boo.WaitFor(() => TryFind(panel, "AppearanceValidationText"), UiTimeout, "the corrupt-appearance message");
            var validationText = BooAppFixture.SafeName(validation);
            Assert.False(string.IsNullOrWhiteSpace(validationText), "The corrupt-appearance message is empty.");
            Assert.DoesNotContain("Exception", validationText, StringComparison.Ordinal);
            _boo.WriteObservation("09-corrupt-start", $"status='{StatusText(panel)}' appearanceMessage='{validationText}'");

            // Boo did not overwrite the user's files just by starting.
            Assert.Equal(CorruptDataFixture.CorruptSettings, BooAppFixture.ReadShared(_boo.SettingsPath));
            Assert.Equal(CorruptDataFixture.CorruptAppearance, BooAppFixture.ReadShared(_boo.AppearancePath));

            // An explicit Save replaces both with valid files.
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => IsValidJsonObject(_boo.SettingsPath) && IsValidJsonObject(_boo.AppearancePath), UiTimeout),
                $"Save did not repair the files. settings: {ReadOrMissing(_boo.SettingsPath)} appearance: {ReadOrMissing(_boo.AppearancePath)}");
            Assert.True(
                BooAppFixture.WaitUntil(() => TryFind(panel, "AppearanceValidationText") is null, UiTimeout),
                "The corrupt-appearance message stayed after a successful save.");
            Assert.False(_boo.App.HasExited, "Boo exited after saving over corrupt files.");
            _boo.CaptureScreen("09-corrupt-start-repaired");
        }
        catch (Exception)
        {
            var shot = _boo.CaptureScreen("09-corrupt-start-FAILED");
            _output.WriteLine($"Failure screenshot: {shot}");
            throw;
        }
    }

    private static AutomationElement? TryFind(Window panel, string automationId) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private AutomationElement Find(Window panel, string automationId) =>
        _boo.WaitFor(() => TryFind(panel, automationId), UiTimeout, $"element '{automationId}'");

    private static string StatusText(Window panel) =>
        TryFind(panel, "StatusText") is { } status ? BooAppFixture.SafeName(status) : string.Empty;

    private static bool IsValidJsonObject(string path)
    {
        if (!File.Exists(path))
        {
            return false;
        }
        using var json = JsonDocument.Parse(BooAppFixture.ReadShared(path));
        return json.RootElement.ValueKind == JsonValueKind.Object;
    }

    private static string ReadOrMissing(string path) => File.Exists(path) ? BooAppFixture.ReadShared(path) : "<missing>";
}
