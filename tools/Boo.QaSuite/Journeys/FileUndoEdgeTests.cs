using System.Diagnostics;
using System.IO;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;

namespace Boo.QaSuite.Journeys;

/// <summary>j16 file branch only. One real creation request; Undo is driven through Boo's approval UI.</summary>
public sealed class FileUndoEdgeTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public FileUndoEdgeTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void Q16_SecondApprovedFileUndo_RecreatesTheOriginalContents()
    {
        var panel = QaUi.OpenPanel(_boo);
        var folder = Path.Combine(_boo.DataDirectory, "second-undo-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var filename = "undo-" + Guid.NewGuid().ToString("N") + ".txt";
        var path = Path.Combine(folder, filename);
        var marker = "SECOND_UNDO_" + Guid.NewGuid().ToString("N");
        var clock = Stopwatch.StartNew();
        var observations = new List<string>();
        void Log(string text) => observations.Add($"{clock.ElapsedMilliseconds} {text}");
        bool CardVisible()
        {
            var element = panel.FindFirstDescendant(cf => cf.ByAutomationId("ActionPreview"));
            return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0;
        }
        void Review(string stage)
        {
            Assert.True(BooAppFixture.WaitUntil(CardVisible, TimeSpan.FromSeconds(120)),
                $"Missing {stage} review card. Status: {QaUi.NameOf(panel, "StatusText")}");
            var title = QaUi.NameOf(panel, "ActionPreviewText");
            Assert.Contains(stage == "first-undo" ? "Delete file" : "Write file", title, StringComparison.Ordinal);
            var destination = panel.FindFirstDescendant(cf => cf.ByName("Resolved destination"))?.AsTextBox().Text;
            Assert.False(string.IsNullOrWhiteSpace(destination), "Review card has no resolved destination.");
            Assert.True(string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination!), StringComparison.OrdinalIgnoreCase),
                $"Review card targets '{destination}', expected '{path}'.");
            Log($"{stage} card='{title}' destination='{destination}' fileExists={File.Exists(path)}");
            _boo.CaptureScreen("qa-q16-" + stage + "-review");
        }
        void Approve() => QaUi.Find(_boo, panel, "ApproveButton").AsButton().Invoke();
        try
        {
            QaUi.Find(_boo, panel, "FileRootBox").AsTextBox().Text = folder;
            QaUi.Find(_boo, panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => QaUi.NameOf(panel, "StatusText").StartsWith("Settings saved", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)));
            Assert.False(File.Exists(path));
            QaUi.Ask(_boo, panel, $"create a file named {filename} in the task folder containing exactly the text {marker}");
            Review("create");
            Assert.False(File.Exists(path), "Creation happened without approval.");
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10)), "Approved creation did not write a file.");
            var original = BooAppFixture.ReadShared(path);
            Assert.Equal(marker, original.Trim());
            Log("approved creation contents=" + original);
            _boo.CaptureScreen("qa-q16-created");

            QaUi.Ask(_boo, panel, "undo");
            Review("first-undo");
            Assert.Equal(original, BooAppFixture.ReadShared(path));
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => !File.Exists(path), TimeSpan.FromSeconds(10)), "First approved Undo did not remove the file.");
            Log("first approved Undo: file absent");
            _boo.CaptureScreen("qa-q16-first-undone");

            QaUi.Ask(_boo, panel, "undo");
            Review("second-undo");
            Assert.False(File.Exists(path), "Second Undo recreated the file before approval.");
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => File.Exists(path), TimeSpan.FromSeconds(10)), "Second approved Undo did not recreate the file.");
            var restored = BooAppFixture.ReadShared(path);
            Assert.Equal(original, restored);
            Log("second approved Undo restored exact contents=" + restored);
            _boo.CaptureScreen("qa-q16-second-undone");
        }
        catch (Exception ex)
        {
            Log("FAILED: " + ex);
            _boo.CaptureScreen("qa-q16-FAILED");
            throw;
        }
        finally
        {
            Log("final fileExists=" + File.Exists(path));
            Log("transcript=" + string.Join(" || ", QaUi.BooRows(panel)));
            _boo.WriteObservation("qa-q16-file-second-undo", string.Join("\n", observations));
        }
    }
}