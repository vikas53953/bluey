using System.Diagnostics;
using System.IO;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Boo.QaSuite.Journeys;

/// <summary>j15 a-c only. Four bounded real chat requests across the three cases; no retries.</summary>
public sealed class ApprovalEdgeTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public ApprovalEdgeTests(LiveAppFixture boo) => _boo = boo;

    [Theory]
    [InlineData("cancel")]
    [InlineData("escape")]
    [InlineData("replacement")]
    public void Q15_CancelEscapeOrReplacement_CannotApplyAnOldProposal(string branch)
    {
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var panel = QaUi.OpenPanel(_boo);
        var folder = Path.Combine(_boo.DataDirectory, "approval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var filename = "proposal-" + Guid.NewGuid().ToString("N") + ".txt";
        var path = Path.Combine(folder, filename);
        void Record(string value) => log.Add($"{clock.ElapsedMilliseconds} {value}");
        bool Visible(string id)
        {
            // Read against the retained live panel directly: a failed UIA call fails the test,
            // rather than turning a read failure into evidence that a card disappeared.
            var element = panel.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0;
        }
        void CheckAbsent()
        {
            Assert.False(File.Exists(path), "An unapproved proposal changed the file system.");
            Assert.False(Visible("ActionPreview"), "Old Review action card is still visible.");
            Assert.False(Visible("ApproveButton"), "Old approval action remains available.");
            Assert.False(panel.IsOffscreen, "Panel closed instead of dismissing the action card.");
        }
        try
        {
            QaUi.Find(_boo, panel, "FileRootBox").AsTextBox().Text = folder;
            QaUi.Find(_boo, panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => QaUi.NameOf(panel, "StatusText").StartsWith("Settings saved", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)), "Task-folder settings did not save.");
            Assert.False(File.Exists(path));
            Record("send new uniquely named write proposal; file absent");
            QaUi.Ask(_boo, panel, $"create a file named {filename} in the task folder containing exactly the text APPROVAL_EDGE");
            Assert.True(BooAppFixture.WaitUntil(() => Visible("ActionPreview"), TimeSpan.FromSeconds(120)),
                $"No pending write card. Status: {QaUi.NameOf(panel, "StatusText")}");
            var title = QaUi.NameOf(panel, "ActionPreviewText");
            Assert.Contains("Write file", title, StringComparison.Ordinal);
            var destination = panel.FindFirstDescendant(cf => cf.ByName("Resolved destination"))?.AsTextBox().Text;
            Assert.False(string.IsNullOrWhiteSpace(destination), "Write proposal has no resolved destination.");
            Assert.True(string.Equals(Path.GetFullPath(path), Path.GetFullPath(destination!), StringComparison.OrdinalIgnoreCase),
                $"Write proposal targets '{destination}', expected '{path}'.");
            Assert.False(File.Exists(path), "File exists before any approval.");
            Record($"pending card title='{title}' destination='{destination}'; visible=true; file absent");
            _boo.CaptureScreen("qa-q15-" + branch + "-pending");

            if (branch == "cancel")
            {
                Record("invoke Cancel proposed action");
                QaUi.Find(_boo, panel, "CancelButton").AsButton().Invoke();
            }
            else if (branch == "escape")
            {
                panel.Focus();
                Record("send physical Escape key");
                Keyboard.Type(VirtualKeyShort.ESCAPE);
            }
            else
            {
                var before = QaUi.BooRows(panel).Count;
                var token = Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
                Record("submit replacement question before approval; expected token=" + token);
                QaUi.Ask(_boo, panel, $"Copy this token verbatim, with no spaces or punctuation. Return only the token: {token}");
                Assert.True(BooAppFixture.WaitUntil(() => !Visible("ActionPreview"), TimeSpan.FromSeconds(5)),
                    "New request did not invalidate the old card.");
                CheckAbsent();
                Assert.True(BooAppFixture.WaitUntil(() => QaUi.BooRows(panel).Count > before, TimeSpan.FromSeconds(120)),
                    "Replacement question received no answer.");
                var reply = QaUi.BooRows(panel)[^1];
                Record("replacement reply=" + reply);
                Assert.Equal(token, reply.Trim());
            }
            Assert.True(BooAppFixture.WaitUntil(() => !Visible("ActionPreview"), TimeSpan.FromSeconds(5)),
                "Pending proposal was not dismissed.");
            var until = clock.ElapsedMilliseconds + 3000;
            do
            {
                CheckAbsent();
                Record("card=false approval=false fileAbsent=true");
                Thread.Sleep(50);
            } while (clock.ElapsedMilliseconds < until);
            _boo.CaptureScreen("qa-q15-" + branch + "-dismissed");
        }
        catch (Exception ex)
        {
            Record("FAILED: " + ex);
            _boo.CaptureScreen("qa-q15-" + branch + "-FAILED");
            throw;
        }
        finally
        {
            Record("final fileExists=" + File.Exists(path));
            Record("transcript=" + string.Join(" || ", QaUi.BooRows(panel)));
            _boo.WriteObservation("qa-q15-" + branch, string.Join("\n", log));
        }
    }
}