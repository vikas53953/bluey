using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.Definitions;

namespace Boo.QaSuite.Journeys;

/// <summary>j10a only. A physical tap opens the panel; hold/capture-loss remain voice-owned.</summary>
public sealed class EyeTapTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public EyeTapTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q10_PhysicalEyeTapUnder200Ms_OpensPanelWithoutListening()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner(), "Native journeys run only on GitHub's own VM.");
        var log = new List<string>();
        var clock = Stopwatch.StartNew();
        Exception? primary = null;
        var held = false;
        var events = new ConcurrentQueue<(long At, string Face)>();
        Window? Panel() => _boo.App.GetAllTopLevelWindows(_boo.Automation)
            .FirstOrDefault(w => w.Properties.Name.Value.StartsWith(BooAppFixture.PanelTitlePrefix, StringComparison.Ordinal));
        void Record(string value) => log.Add($"{clock.ElapsedMilliseconds} {value}");
        try
        {
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            Assert.Null(Panel());
            using var faceEvents = notch.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, value) => events.Enqueue((clock.ElapsedMilliseconds, value as string ?? "<invalid>")),
                notch.Automation.PropertyLibrary.Element.ItemStatus);
            var bounds = notch.BoundingRectangle;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 300);
            Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.ItemStatus.Value == "Hidden", TimeSpan.FromSeconds(5)));
            using var recorder = new NotchStateRecorder(_boo, "eye-tap");
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 2);
            Assert.True(BooAppFixture.WaitUntil(() => notch.Properties.ItemStatus.Value == "Hover", TimeSpan.FromSeconds(5)));
            Assert.True(BooAppFixture.WaitUntil(() => events.Any(e => e.Face == "Hover"), TimeSpan.FromSeconds(5)),
                "SETUP: registered ItemStatus events did not observe the known Hover transition.");
            Thread.Sleep(300); // finish the shipped 240 ms reveal before locating the button
            var eyes = notch.FindFirstDescendant(cf => cf.ByName("Open Boo companion"));
            Assert.NotNull(eyes);
            var hit = eyes.BoundingRectangle;
            Assert.True(hit.Width > 0 && hit.Height > 0 && !eyes.IsOffscreen);
            Mouse.Position = new Point(hit.Left + hit.Width / 2, hit.Top + hit.Height / 2);
            Thread.Sleep(120); // establish healthy pre-action recorder coverage
            var observationStart = recorder.Mark();
            var eventStart = events.Count;
            var down = clock.ElapsedMilliseconds;
            Record("physical left-down begin");
            held = true;
            Mouse.Down(MouseButton.Left);
            Thread.Sleep(40);
            Mouse.Up(MouseButton.Left);
            held = false;
            var up = clock.ElapsedMilliseconds;
            Record($"physical left-up complete; conservative press bound=[{down},{up}], duration={up - down}ms");
            Assert.InRange(up - down, 1L, 199L);
            Assert.True(BooAppFixture.WaitUntil(() => Panel() is not null, TimeSpan.FromSeconds(5)),
                "One physical eye tap did not open the panel; no shortcut or tap retry was attempted.");
            Thread.Sleep(500);
            var health = recorder.ObservationFailureSince(observationStart);
            Record("observation health: " + (health ?? "healthy within 100 ms"));
            Assert.Null(health);
            Assert.False(recorder.HasSeenSince("Listening", observationStart), "Tap entered Listening.");
            // Produce a known post-action transition as an event-delivery health check.
            var eventBarrier = events.Count;
            Mouse.Position = new Point(bounds.Left + bounds.Width / 2, bounds.Top + 300);
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(eventBarrier).Any(e => e.Face == "Idle"), TimeSpan.FromSeconds(5)),
                "ItemStatus event stream did not deliver the post-tap Idle transition.");
            Thread.Sleep(500); // bounded observation window for queued property-change callbacks
            var actionEvents = events.Skip(eventStart).ToArray();
            Assert.DoesNotContain(actionEvents, e => e.Face == "<invalid>");
            Assert.DoesNotContain(actionEvents, e => e.Face == "Listening");
            Record("panel open; no Listening in action-local events or healthy polling observations");
            _boo.CaptureScreen("qa-j10a-eye-tap");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j10a-FAILED"); }
            catch (Exception captureError) { Record("capture error: " + captureError); }
            throw;
        }
        finally
        {
            try { if (held) Mouse.Up(MouseButton.Left); }
            catch (Exception releaseError) when (primary is not null) { Record("release error: " + releaseError); }
            log.AddRange(events.Select(e => $"{e.At} ItemStatus event {e.Face}"));
            try { _boo.WriteObservation("qa-j10a-eye-tap", string.Join("\n", log)); }
            catch (Exception evidenceError) when (primary is not null) { Console.Error.WriteLine("Additional evidence failure: " + evidenceError); }
        }
    }
}
