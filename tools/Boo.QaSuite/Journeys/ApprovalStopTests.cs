using System.Diagnostics;
using System.Drawing;
using System.IO;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Boo.QaSuite.Journeys;

/// <summary>j12e: one physical notch right-click cancels an actionable, unapproved file proposal.</summary>
public sealed class ApprovalStopTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public ApprovalStopTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void Q12E_NotchRightClick_ClearsPendingProposalAndAcknowledgesStop()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var log = new List<string>();
        var clock = Stopwatch.StartNew();
        Exception? primary = null;
        Window? panel = null;
        var held = false;
        var path = string.Empty;
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        AutomationElement Find(string id) => panel!.FindFirstDescendant(cf => cf.ByAutomationId(id))
            ?? throw new InvalidOperationException("Missing required element: " + id);
        bool Visible(string id)
        {
            Assert.False(panel!.IsOffscreen, "Panel closure cannot establish card dismissal.");
            var element = panel.FindFirstDescendant(cf => cf.ByAutomationId(id));
            return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0;
        }
        void Transcript() => Record("transcript=" + string.Join(" || ", Find("TranscriptList")
            .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(e => e.Properties.Name.Value)));
        try
        {
            panel = QaUi.OpenPanel(_boo);
            var folder = Path.Combine(_boo.DataDirectory, "stop-approval-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            var filename = "pending-" + Guid.NewGuid().ToString("N") + ".txt";
            path = Path.Combine(folder, filename);
            Find("FileRootBox").AsTextBox().Text = folder;
            Find("SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Find("StatusText").Properties.Name.Value.StartsWith("Settings saved", StringComparison.Ordinal),
                TimeSpan.FromSeconds(10)));
            var saved = new SettingsStore(Path.Combine(_boo.DataDirectory, "settings.json")).Load();
            Assert.Equal(Path.GetFullPath(folder), saved.FileRoot);
            Assert.False(File.Exists(path));
            var question = $"create a file named {filename} in the selected task folder containing exactly STOP_APPROVAL. Propose the write for approval.";
            Record("input=" + question + " expected destination=" + path);
            Find("InputBox").AsTextBox().Text = question;
            Find("SendButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Visible("ActionPreview"), TimeSpan.FromSeconds(120)), "No write proposal from single request.");
            var title = Find("ActionPreviewText").Properties.Name.Value;
            Assert.Contains("Write file", title, StringComparison.Ordinal);
            var destination = panel.FindFirstDescendant(cf => cf.ByName("Resolved destination").And(cf.ByControlType(ControlType.Edit)));
            Assert.NotNull(destination);
            var resolved = destination.AsTextBox().Text;
            Assert.Equal(Path.GetFullPath(path), Path.GetFullPath(resolved), ignoreCase: true);
            Assert.True(Visible("ApproveButton") && Find("ApproveButton").IsEnabled, "Proposal must be actionable before Stop.");
            Assert.False(File.Exists(path));
            Record($"pending title={title} destination={resolved} approveEnabled=true fileAbsent=true");
            Transcript();
            _boo.CaptureScreen("qa-j12e-pending");
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            var bounds = notch.BoundingRectangle;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 2);
            Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.ItemStatus.Value == "Hover", TimeSpan.FromSeconds(5)));
            Thread.Sleep(300); // finish the existing 240 ms reveal before locating the hit surface
            var pocket = notch.FindFirstDescendant(cf => cf.ByName("Open Boo companion").And(cf.ByControlType(ControlType.Button)));
            Assert.NotNull(pocket);
            bounds = pocket.BoundingRectangle;
            Assert.True(bounds.Width > 0 && bounds.Height > 0 && !pocket.IsOffscreen);
            var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Mouse.Position = point;
            var hit = _boo.Automation.FromPoint(point);
            Assert.NotNull(hit);
            Assert.Equal(notch.Properties.ProcessId.Value, hit.Properties.ProcessId.Value);
            Assert.Equal("Open Boo companion", hit.Properties.Name.Value);
            Assert.Equal(ControlType.Button, hit.Properties.ControlType.Value);
            Record($"verified pocket hit name={hit.Properties.Name.Value} process={hit.Properties.ProcessId.Value} type={hit.Properties.ControlType.Value}; point={point.X},{point.Y}; pocketBounds={bounds}");
            Record("single right-down begin");
            held = true;
            Mouse.Down(MouseButton.Right);
            Mouse.Up(MouseButton.Right);
            held = false;
            Record("single right-up complete");
            var acknowledged = BooAppFixture.WaitUntil(() => notch.Properties.HelpText.Value.Contains("Stopped", StringComparison.Ordinal), TimeSpan.FromSeconds(1));
            Record("Stopped acknowledgment=" + acknowledged + "; HelpText=" + notch.Properties.HelpText.Value);
            Assert.True(BooAppFixture.WaitUntil(() => !Visible("ActionPreview"), TimeSpan.FromSeconds(5)));
            var watch = Stopwatch.StartNew();
            do
            {
                Assert.False(Visible("ActionPreview"));
                Assert.False(Visible("ApproveButton"));
                Assert.False(File.Exists(path), "Stop allowed an unapproved file effect.");
                Record("card=false approve=false fileAbsent=true");
                Thread.Sleep(50);
            } while (watch.Elapsed < TimeSpan.FromSeconds(3));
            Transcript();
            _boo.CaptureScreen("qa-j12e-dismissed");
            Assert.True(acknowledged, "Notch right-click Stop did not acknowledge Stopped.");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j12e-FAILED"); }
            catch (Exception captureError) { Record("capture error: " + captureError); }
            throw;
        }
        finally
        {
            var secondary = new List<Exception>();
            try { if (held) Mouse.Up(MouseButton.Right); }
            catch (Exception releaseError) { Record("release error: " + releaseError); secondary.Add(releaseError); }
            try { if (panel is not null) Transcript(); }
            catch (Exception readError) { Record("transcript error: " + readError); secondary.Add(readError); }
            Record("final fileExists=" + File.Exists(path));
            try { _boo.WriteObservation("qa-j12e-approval-stop", string.Join("\n", log)); }
            catch (Exception evidenceError) { Console.Error.WriteLine("Evidence error: " + evidenceError); secondary.Add(evidenceError); }
            if (primary is null && secondary.Count > 0) throw new AggregateException(secondary);
        }
    }
}
