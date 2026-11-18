using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Boo.QaSuite.Journeys;

/// <summary>j16 field branch: one real proposed edit, approved Undo, then no second Undo.</summary>
public sealed class FieldUndoEdgeTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public FieldUndoEdgeTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void Q16F_ApprovedFieldUndo_RestoresOriginal_SecondUndoDoesNothing()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var executable = Environment.GetEnvironmentVariable("BOO_TYPE_TARGET_EXE");
        Assert.True(!string.IsNullOrWhiteSpace(executable) && File.Exists(executable), "SETUP: published field target is required.");
        using var process = Process.Start(new ProcessStartInfo(executable!, "present") { UseShellExecute = false })
            ?? throw new InvalidOperationException("Could not launch the field target.");
        var log = new List<string>();
        var clock = Stopwatch.StartNew();
        Exception? primary = null;
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        try
        {
            Assert.True(BooAppFixture.WaitUntil(() => { process.Refresh(); return process.MainWindowHandle != IntPtr.Zero; }, TimeSpan.FromSeconds(15)));
            var handle = process.MainWindowHandle;
            var target = _boo.Automation.FromHandle(handle).AsWindow();
            Assert.Equal(process.Id, target.Properties.ProcessId.Value);
            Assert.Equal("Boo type target (present)", target.Properties.Name.Value);
            FlaUI.Core.AutomationElements.TextBox Field(string id) => (target.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing target field: " + id)).AsTextBox();
            var original = "ORIGINAL_" + Guid.NewGuid().ToString("N");
            var marker = "EDITED_" + Guid.NewGuid().ToString("N");
            var sentinel = "DECOY_" + Guid.NewGuid().ToString("N");
            Field("FieldA").Text = original;
            Field("FieldB").Text = sentinel;
            void Values(string expected)
            {
                Assert.Equal(expected, Field("FieldA").Text);
                Assert.Equal(sentinel, Field("FieldB").Text);
            }
            Values(original);
            Record($"target pid={process.Id} hwnd={handle}; original={original}; edited={marker}; decoy={sentinel}");
            var panel = QaUi.OpenPanelOnceReady(_boo, Record);
            AutomationElement Find(string id) => panel.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing Boo element: " + id);
            string Status() => Find("StatusText").Properties.Name.Value;
            bool Visible(string id)
            {
                Assert.False(panel.IsOffscreen, "Panel closure cannot prove absence.");
                var element = panel.FindFirstDescendant(cf => cf.ByAutomationId(id));
                return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0;
            }
            string CardValue(string name) => (panel.FindFirstDescendant(cf => cf.ByName(name).And(cf.ByControlType(ControlType.Edit)))
                ?? throw new InvalidOperationException("Missing review field: " + name)).AsTextBox().Text;
            void TargetReady(string boundary)
            {
                Assert.False(process.HasExited);
                process.Refresh();
                Assert.Equal(handle, process.MainWindowHandle);
                Assert.Equal(process.Id, target.Properties.ProcessId.Value);
                target.SetForeground();
                Assert.True(BooAppFixture.WaitUntil(() => GetForegroundWindow() == handle, TimeSpan.FromSeconds(5)), "SETUP: target not foreground at " + boundary);
                var bounds = Field("FieldA").BoundingRectangle;
                Assert.True(bounds.Width > 0 && bounds.Height > 0);
                var point = new NativePoint { X = bounds.Left + bounds.Width / 2, Y = bounds.Top + bounds.Height / 2 };
                Assert.Equal(handle, GetAncestor(WindowFromPoint(point), 2));
                Record(boundary + " target identity/foreground/top verified");
            }
            var display = Find("DisplayBox").AsComboBox();
            Assert.NotEmpty(display.Items);
            display.Select(0);
            Find("SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => Status().StartsWith("Settings saved", StringComparison.Ordinal), TimeSpan.FromSeconds(10)));
            void Send(string text)
            {
                Find("InputBox").AsTextBox().Text = text;
                TargetReady("before Send");
                Record("Send: " + text);
                Find("SendButton").AsButton().Invoke();
            }
            void Review(string title, string text, string expected)
            {
                Assert.True(BooAppFixture.WaitUntil(() => Visible("ActionPreview"), TimeSpan.FromSeconds(120)), "No actionable field review from the single request.");
                Assert.Equal(title, Find("ActionPreviewText").Properties.Name.Value);
                Assert.Equal($"{process.ProcessName} window (process {process.Id})", CardValue("Resolved destination"));
                Assert.Equal("FieldA", CardValue("Automation ID"));
                Assert.Equal(text, CardValue("Text"));
                Assert.False(Visible("ActionBlockedReason"));
                Assert.True(Visible("ApproveButton") && Find("ApproveButton").IsEnabled);
                Values(expected);
                Record($"review={title}; destination pid={process.Id}; FieldA={expected}; FieldB={sentinel}");
            }
            void Approve()
            {
                TargetReady("before Approve");
                Find("ApproveButton").AsButton().Invoke();
            }
            Send($"task replace the text in Field A (automationId FieldA) of Boo type target (present) with exactly {marker}. Propose only that field edit for approval.");
            Review("Type text", marker, original);
            _boo.CaptureScreen("qa-q16f-edit-review");
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => Field("FieldA").Text == marker, TimeSpan.FromSeconds(10)));
            Values(marker);
            Record("approved edit exact readback=" + marker);
            _boo.CaptureScreen("qa-q16f-edited");
            Send("undo");
            Review("Undo: Type text", original, marker);
            _boo.CaptureScreen("qa-q16f-undo-review");
            Approve();
            Assert.True(BooAppFixture.WaitUntil(() => Field("FieldA").Text == original, TimeSpan.FromSeconds(10)));
            Values(original);
            Record("approved Undo exact readback=" + original);
            _boo.CaptureScreen("qa-q16f-restored");
            Send("undo");
            const string nothing = "Nothing to undo: Boo has no recorded edit.";
            Assert.True(BooAppFixture.WaitUntil(() => Status() == nothing, TimeSpan.FromSeconds(10)));
            var rows = Find("TranscriptList").FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(e => e.Properties.Name.Value).ToArray();
            Assert.Equal("Boo: " + nothing, rows.Last());
            var watch = Stopwatch.StartNew();
            do
            {
                Assert.Equal(nothing, Status());
                Assert.False(Visible("ActionPreview"));
                Assert.False(Visible("ApproveButton"));
                Values(original);
                Thread.Sleep(40);
            } while (watch.Elapsed < TimeSpan.FromSeconds(3));
            Record("second Undo: exact acknowledgment, no card/Approve, both fields unchanged for three seconds");
            _boo.CaptureScreen("qa-q16f-second-undo");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-q16f-FAILED"); }
            catch (Exception capture) { Record("capture failure: " + capture.Message); }
            throw;
        }
        finally
        {
            try { _boo.WriteObservation("qa-q16f-field-undo", string.Join("\n", log)); }
            catch (Exception) when (primary is not null) { }
            try { if (!process.HasExited) process.Kill(); }
            catch (InvalidOperationException) { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X; public int Y; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr handle, uint flags);
}
