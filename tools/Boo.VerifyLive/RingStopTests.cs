using System.Diagnostics;
using System.Drawing;
using System.IO;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;

namespace Boo.VerifyLive;

/// <summary>j12h only: Stop must remove a proven ring and pointer before natural expiry.</summary>
public sealed class RingStopTests : IClassFixture<LiveAppFixture>
{
    private readonly LiveAppFixture _boo;
    public RingStopTests(LiveAppFixture boo) => _boo = boo;

    [Fact]
    public void Q12H_StopClosesRingAndPointer()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        Window[] Overlays() => _boo.App.GetAllTopLevelWindows(_boo.Automation)
            .Where(w => w.Properties.Name.Value == OverlayRender.OverlayTitle).ToArray();
        try
        {
            Assert.True(WindowsAnimations.IsOn(), "SETUP: animations required for pointer proof.");
            _boo.WaitForNotch(TimeSpan.FromSeconds(60));
            Assert.Null(_boo.FindPanel());
            Record("notch ready; one Ctrl+Alt+B open action");
            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(TimeSpan.FromSeconds(8));
            AutomationElement Find(string id) => panel.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing " + id);
            var display = Find("DisplayBox").AsComboBox();
            if (display.SelectedItem is null) display.Select(0);
            var screen = System.Windows.Forms.Screen.PrimaryScreen!.Bounds;
            using var target = TargetWindow.Show(new Rectangle(screen.Width / 4, screen.Height / 4,
                Math.Min(760, screen.Width / 2), Math.Min(520, screen.Height / 2)));
            var button = target.ButtonScreenRectangle();
            target.BringToFront(_boo.Automation);
            Assert.True(BooAppFixture.WaitUntil(() => target.IsForeground() && target.IsTopAt(button), TimeSpan.FromSeconds(5)));
            Find("InputBox").AsTextBox().Text = "Explain the window titled 'Boo live target' and point at its big 'Send report' button.";
            Assert.Empty(Overlays());
            var lastAbsent = clock.ElapsedMilliseconds;
            Assert.True(target.IsForeground() && target.IsTopAt(button), "SETUP: target lost foreground/top before Send.");
            Find("SendButton").AsButton().Invoke();
            Find("CloseButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, TimeSpan.FromSeconds(60)));
            target.BringToFront(_boo.Automation);
            Assert.True(target.IsForeground() && target.IsTopAt(button));
            var deadline = clock.ElapsedMilliseconds + 90_000; // existing J3 ring search budget
            Window[] overlays;
            do
            {
                var readStart = clock.ElapsedMilliseconds;
                overlays = Overlays(); // errors propagate; failed enumeration never means absence
                Assert.InRange(clock.ElapsedMilliseconds - readStart, 0L, 100L);
                if (overlays.Length > 0) break;
                lastAbsent = readStart; // conservative lower bound for subsequent creation
                Thread.Sleep(40);
            } while (clock.ElapsedMilliseconds < deadline);
            var overlayWindow = Assert.Single(overlays);
            var hwnd = overlayWindow.Properties.NativeWindowHandle.Value;
            var pid = overlayWindow.Properties.ProcessId.Value;
            Assert.NotEqual(IntPtr.Zero, hwnd);
            Record($"overlay hwnd={hwnd} pid={pid} creation after={lastAbsent}");
            Assert.True(BooAppFixture.WaitUntil(() =>
            {
                using var candidate = OverlayRender.Capture(_boo);
                return candidate is not null && candidate.Find(Rectangle.Inflate(target.ScreenRectangle(), 160, 160),
                    ColourProbe.Ring, 12, out _, out _);
            }, TimeSpan.FromSeconds(90)), "No real ring before Stop precondition.");
            Thread.Sleep(900); // existing J3 settle, no changed oracle
            using (var render = OverlayRender.Capture(_boo))
            {
                Assert.NotNull(render); // render failures cannot pass the positive visual precondition
                var search = Rectangle.Inflate(target.ScreenRectangle(), 160, 160);
                Assert.True(render.Find(search, ColourProbe.Ring, 12, out _, out var ringPixels));
                Assert.True(render.Find(search, ColourProbe.Pointer, 15, out var pointer, out var pointerPixels));
                Assert.True(Rectangle.Inflate(button, 20, 20).Contains(pointer));
                var mid = new Point(button.Left + button.Width / 2, button.Top + button.Height / 2);
                foreach (var edge in new[] { new Point(button.Left, mid.Y), new Point(button.Right, mid.Y), new Point(mid.X, button.Bottom) })
                    Assert.True(render.Count(new Rectangle(edge.X - 30, edge.Y - 30, 60, 60), ColourProbe.Ring) >= 6);
                Record($"ring pixels={ringPixels}; pointer pixels={pointerPixels}; pointer={pointer}");
            }
            _boo.CaptureScreen("qa-j12h-before-stop");
            Assert.Equal(hwnd, Assert.Single(Overlays()).Properties.NativeWindowHandle.Value);
            // AnnotationWindow's minimum hold is 10,000 ms. Include capture/render/dispatch time;
            // the whole Stop deadline must end at least 2 s before even the earliest expiry.
            var sent = clock.ElapsedMilliseconds;
            Assert.True(sent + 1000 < lastAbsent + 8000, "SETUP: evidence consumed natural-expiry exclusion margin.");
            Record($"one Stop chord start={sent}; shared deadline={sent + 1000}; earliest creation={lastAbsent}");
            try { Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.F12); }
            finally
            {
                Keyboard.Release(VirtualKeyShort.F12);
                Keyboard.Release(VirtualKeyShort.ALT);
                Keyboard.Release(VirtualKeyShort.CONTROL);
            }
            var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(1));
            var acknowledged = false;
            var gone = false;
            do
            {
                acknowledged |= notch.Properties.HelpText.Value.Contains("Stopped", StringComparison.Ordinal);
                gone = Overlays().Length == 0;
                if (acknowledged && gone) break;
                Thread.Sleep(40);
            } while (clock.ElapsedMilliseconds < sent + 1000);
            Assert.True(clock.ElapsedMilliseconds <= sent + 1000 && acknowledged && gone,
                "Stop acknowledgment and overlay removal must both meet the same 1 s deadline.");
            Record("acknowledged and all explanation windows absent");
            var watchUntil = clock.ElapsedMilliseconds + 3000;
            do { Assert.Empty(Overlays()); Thread.Sleep(40); } while (clock.ElapsedMilliseconds < watchUntil);
            _boo.CaptureScreen("qa-j12h-after-stop");
            Assert.Empty(Overlays());
            var manifest = File.ReadAllText(Path.Combine(_boo.ArtifactsDirectory, "qa-j12h-after-stop.capture.txt"));
            Assert.DoesNotContain(OverlayRender.OverlayTitle, manifest, StringComparison.Ordinal);
            Record("three-second watch complete; next capture manifest excludes explanation overlay");
        }
        catch (Exception ex)
        {
            Record("FAILED: " + ex);
            _boo.CaptureScreen("qa-j12h-FAILED");
            throw;
        }
        finally { _boo.WriteObservation("qa-j12h-ring-stop", string.Join("\n", log)); }
    }
}
