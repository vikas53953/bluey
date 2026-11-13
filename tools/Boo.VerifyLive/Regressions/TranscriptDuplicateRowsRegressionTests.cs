using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace Boo.VerifyLive.Regressions;

/// <summary>A fresh Boo with no key and speech off: a question with no key still adds its "You" row, and costs nothing.</summary>
public sealed class TranscriptNoKeyBooFixture : BooAppFixture
{
    public TranscriptNoKeyBooFixture() : base(dir =>
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }) { }
}

/// <summary>
/// Regression for the app explorer's finding (issue linked in docs/FEATURE_MAP.md row X8): when the
/// same line appears twice in Conversation (the same question asked twice, or the same short answer twice), the screen
/// shows both rows but UI Automation, which screen readers use, exposes only ONE of them. Found by run
/// https://github.com/vikas53953/bluey/actions/runs/43469967627 (b01-wrong-key.txt reads one "You" row; the panel image
/// b01-wrong-key.boo-1.png shows two). The transcript rows are value-equal records, and WPF keys an item's automation
/// peer by the item. The rule this test holds: every row a user can see is a row UI Automation reports, in order.
/// </summary>
public sealed class TranscriptDuplicateRowsRegressionTests : IClassFixture<TranscriptNoKeyBooFixture>
{
    private const string Question = "Regression: the same question twice";
    private readonly TranscriptNoKeyBooFixture _boo;
    private readonly ITestOutputHelper _output;

    public TranscriptDuplicateRowsRegressionTests(TranscriptNoKeyBooFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void SameQuestionTwice_ScreenReadersSeeBothRows()
    {
        _boo.WaitForNotch(TimeSpan.FromSeconds(60));
        Thread.Sleep(1000); // the open hotkey is registered by the notch window
        var panel = _boo.EnsurePanel();

        for (var i = 0; i < 2; i++)
        {
            Element(panel, "InputBox").AsTextBox().Text = Question;
            Element(panel, "SendButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Status(panel).Contains("key", StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(15)),
                $"TEST SETUP: ask #{i + 1} did not finish with the no-key message (status '{Status(panel)}').");
            Thread.Sleep(500);
        }

        var rows = Element(panel, "TranscriptList")
            .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .Select(BooAppFixture.SafeName)
            .ToList();
        var youRows = rows.Count(r => r == "You: " + Question);
        _boo.CaptureScreen("regression-transcript-duplicates");
        _boo.WriteObservation("regression-transcript-duplicates", $"uiaRows={rows.Count} youRows={youRows} rows='{string.Join(" || ", rows)}'");
        _output.WriteLine($"rows: {string.Join(" || ", rows)}");

        Assert.Equal(2, youRows);
    }

    private AutomationElement Element(Window panel, string id) =>
        _boo.WaitFor(() => panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(30), $"element '{id}'");

    private static string Status(Window panel) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId("StatusText")) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
