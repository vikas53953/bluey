using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using Xunit.Abstractions;

namespace Boo.VerifyLive.Regressions;

/// <summary>A fresh Boo with no key and speech off: each question still adds its "You" row, and costs nothing.</summary>
public sealed class TranscriptOrderNoKeyBooFixture : BooAppFixture
{
    public TranscriptOrderNoKeyBooFixture() : base(dir =>
    {
        Environment.SetEnvironmentVariable("BOO_CAPTURE_SCOPE", "boo-windows");
        new SettingsStore(Path.Combine(dir, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: new ProviderSettings(TextProvider: AiProvider.OpenAI)));
    }) { }
}

/// <summary>
/// The Conversation list around issue #73's fix (PR #84): rows appear in the order they were asked, and after every new
/// row the list shows the NEWEST row, with the oldest scrolled out of view once the list overflows. Read through UI
/// Automation (what screen readers use): order from the list's items, visibility from each item's IsOffscreen.
/// </summary>
public sealed class TranscriptOrderAndScrollRegressionTests : IClassFixture<TranscriptOrderNoKeyBooFixture>
{
    private const int Questions = 12;
    private readonly TranscriptOrderNoKeyBooFixture _boo;
    private readonly ITestOutputHelper _output;

    public TranscriptOrderAndScrollRegressionTests(TranscriptOrderNoKeyBooFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact]
    public void DistinctQuestions_AppearInOrder_AndTheNewestRowIsAlwaysInView()
    {
        _boo.WaitForNotch(TimeSpan.FromSeconds(60));
        Thread.Sleep(1000); // the open hotkey is registered by the notch window
        var panel = _boo.EnsurePanel();
        var asked = new List<string>();
        var newestHidden = new List<string>();

        for (var i = 1; i <= Questions; i++)
        {
            var question = $"Order check {i:00}";
            asked.Add("You: " + question);
            Element(panel, "InputBox").AsTextBox().Text = question;
            Element(panel, "SendButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => YouRows(panel).Count == i, TimeSpan.FromSeconds(15)),
                $"Question {i} did not add its row (rows: {YouRows(panel).Count}).");
            Thread.Sleep(300); // let the list scroll after the new row is laid out
            var newest = YouItems(panel).FirstOrDefault(e => BooAppFixture.SafeName(e) == "You: " + question);
            if (newest is null || newest.IsOffscreen)
            {
                newestHidden.Add(question);
            }
        }

        var rows = YouRows(panel);
        var items = YouItems(panel);
        var oldestOffscreen = items.Count > 0 && items[0].IsOffscreen;
        _boo.CaptureScreen("regression-transcript-order-scroll");
        _boo.WriteObservation("regression-transcript-order-scroll",
            $"rows='{string.Join(" || ", rows)}'\nnewestNotInView='{string.Join(", ", newestHidden)}' oldestOffscreen={oldestOffscreen}");
        _output.WriteLine(string.Join(" || ", rows));

        Assert.Equal(asked, rows);
        Assert.Empty(newestHidden);
        Assert.True(oldestOffscreen, "After 12 rows the oldest row is still in view, so the list never had to scroll; the check proved nothing.");
    }

    private List<AutomationElement> YouItems(Window panel) =>
        Element(panel, "TranscriptList")
            .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .Where(e => BooAppFixture.SafeName(e).StartsWith("You: ", StringComparison.Ordinal))
            .ToList();

    private List<string> YouRows(Window panel) => YouItems(panel).Select(BooAppFixture.SafeName).ToList();

    private AutomationElement Element(Window panel, string id) =>
        _boo.WaitFor(() => panel.FindFirstDescendant(cf => cf.ByAutomationId(id)), TimeSpan.FromSeconds(30), $"element '{id}'");
}
