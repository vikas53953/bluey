using System.Diagnostics;
using System.IO;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Boo.QaSuite.Journeys;

/// <summary>j15d. One real-model proposal, two separate user approvals and exact file readbacks.</summary>
public sealed class SequentialApprovalTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public SequentialApprovalTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void QD15_TwoFileSteps_RequireSeparateApprovalsAndShowNextStep()
    {
        var panel = QaUi.OpenPanel(_boo);
        var folder = Path.Combine(_boo.DataDirectory, "sequential-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var first = Path.Combine(folder, "first-" + Guid.NewGuid().ToString("N") + ".txt");
        var second = Path.Combine(folder, "second-" + Guid.NewGuid().ToString("N") + ".txt");
        var markerA = "FIRST_" + Guid.NewGuid().ToString("N");
        var markerB = "SECOND_" + Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        bool Visible(string id)
        {
            var element = panel.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0;
        }
        void Review(string prefix, string path)
        {
            Assert.True(BooAppFixture.WaitUntil(() => Visible("ActionPreview")
                && QaUi.NameOf(panel, "ActionPreviewText").StartsWith(prefix, StringComparison.Ordinal), TimeSpan.FromSeconds(120)),
                $"No {prefix} card. Status: {QaUi.NameOf(panel, "StatusText")}");
            var destination = panel.FindFirstDescendant(cf => cf.ByName("Resolved destination").And(cf.ByControlType(ControlType.Edit)))?.AsTextBox().Text;
            Assert.False(string.IsNullOrWhiteSpace(destination));
            Assert.True(string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination!), StringComparison.OrdinalIgnoreCase),
                $"Proposal destination '{destination}' differs from expected '{path}'.");
            Record($"card='{QaUi.NameOf(panel, "ActionPreviewText")}' destination='{destination}'");
        }
        void Approve() => QaUi.Find(_boo, panel, "ApproveButton").AsButton().Invoke();
        Exception? primary = null;
        try
        {
            QaUi.Find(_boo, panel, "FileRootBox").AsTextBox().Text = folder;
            QaUi.Find(_boo, panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => QaUi.NameOf(panel, "StatusText").StartsWith("Settings saved", StringComparison.Ordinal), TimeSpan.FromSeconds(10)));
            QaUi.Ask(_boo, panel, $"create two files in the selected task folder in this order: first {Path.GetFileName(first)} containing exactly {markerA}, then {Path.GetFileName(second)} containing exactly {markerB}. Propose both file steps for separate approvals.");
            Review("Write file", first);
            Assert.False(File.Exists(first));
            Assert.False(File.Exists(second));
            _boo.CaptureScreen("qa-j15d-first-review");
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => File.Exists(first), TimeSpan.FromSeconds(10)));
            Assert.Equal(markerA, BooAppFixture.ReadShared(first).Trim());
            Review("Next step:", second);
            Assert.False(File.Exists(second), "Second step ran without its own approval.");
            Record("first file verified; second absent before separate approval");
            _boo.CaptureScreen("qa-j15d-next-step");
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => File.Exists(second), TimeSpan.FromSeconds(10)));
            Assert.Equal(markerB, BooAppFixture.ReadShared(second).Trim());
            Assert.Equal(markerA, BooAppFixture.ReadShared(first).Trim());
            Assert.True(BooAppFixture.WaitUntil(() => !Visible("ActionPreview"), TimeSpan.FromSeconds(5)));
            Record("both exact contents verified; final card removed");
            _boo.CaptureScreen("qa-j15d-both-verified");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j15d-FAILED"); }
            catch (Exception captureError) { Record("capture error: " + captureError); }
            throw;
        }
        finally
        {
            try { _boo.WriteObservation("qa-j15d-sequential-approval", string.Join("\n", log)); }
            catch (Exception evidenceError) when (primary is not null) { Console.Error.WriteLine("Additional evidence failure: " + evidenceError); }
        }
    }
}