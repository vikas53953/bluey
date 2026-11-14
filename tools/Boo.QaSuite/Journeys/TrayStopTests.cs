using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;

namespace Boo.QaSuite.Journeys;

/// <summary>j12f only: invoke Stop from Boo's actual Explorer notification-area menu.</summary>
public sealed class TrayStopTests : IClassFixture<NoKeyFixture>
{
    private readonly NoKeyFixture _boo;
    public TrayStopTests(NoKeyFixture boo) => _boo = boo;

    [Fact]
    public void Q12F_TrayStop_PausesRunningFocusAndKeepsItPinned()
    {
        Assert.True(BooAppFixture.OnGitHubHostedRunner());
        var clock = Stopwatch.StartNew();
        var log = new List<string>();
        var events = new ConcurrentQueue<(long At, string Face)>();
        var cleanup = new List<Action>();
        Exception? primary = null;
        void Record(string text) => log.Add($"{clock.ElapsedMilliseconds} {text}");
        bool Visible(AutomationElement e) => !e.IsOffscreen && e.BoundingRectangle.Width > 0 && e.BoundingRectangle.Height > 0;
        try
        {
            Window? Panel() => _boo.App.GetAllTopLevelWindows(_boo.Automation)
                .SingleOrDefault(w => w.Properties.Name.Value.StartsWith(BooAppFixture.PanelTitlePrefix, StringComparison.Ordinal));
            Assert.Null(Panel());
            Record("one Ctrl+Alt+B open press");
            BooAppFixture.PressOpenShortcut();
            Window? panel = null;
            Assert.True(BooAppFixture.WaitUntil(() => (panel = Panel()) is not null, TimeSpan.FromSeconds(8)));
            AutomationElement Find(string id) => panel!.FindFirstDescendant(cf => cf.ByAutomationId(id))
                ?? throw new InvalidOperationException("Missing required element: " + id);
            void Press(string id) => Find(id).AsButton().Invoke();
            var task = "j12f " + Guid.NewGuid().ToString("N");
            Find("FocusTaskBox").AsTextBox().Text = task;
            Find("FocusDurationBox").AsTextBox().Text = "25";
            var desktop = _boo.Automation.GetDesktop();
            var explorerIds = new HashSet<int>();
            foreach (var process in Process.GetProcessesByName("explorer"))
            {
                using (process) explorerIds.Add(process.Id);
            }
            Assert.NotEmpty(explorerIds);
            var walker = _boo.Automation.TreeWalkerFactory.GetControlViewWalker();
            bool ExplorerAncestry(AutomationElement e)
            {
                AutomationElement? current = e;
                for (var depth = 0; current is not null && depth < 32; depth++, current = walker.GetParent(current))
                    if (explorerIds.Contains(current.Properties.ProcessId.Value)) return true;
                return false;
            }
            AutomationElement[] ShellElements(Func<AutomationElement, bool> predicate)
            {
                var results = new Dictionary<string, AutomationElement>();
                foreach (var root in desktop.FindAllChildren().Where(e => explorerIds.Contains(e.Properties.ProcessId.Value)))
                    foreach (var e in root.FindAllDescendants().Prepend(root).Where(predicate))
                        results.TryAdd(string.Join(",", e.Properties.RuntimeId.Value), e);
                return results.Values.ToArray();
            }
            bool NotificationContainer(AutomationElement e)
            {
                var type = e.Properties.ControlType.Value;
                if (type is not (ControlType.Pane or ControlType.ToolBar or ControlType.Window)) return false;
                var name = e.Properties.Name.Value;
                return name.Contains("notification area", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("notification overflow", StringComparison.OrdinalIgnoreCase)
                    || name.Contains("system tray", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Hidden icons", StringComparison.OrdinalIgnoreCase);
            }
            AutomationElement[] Areas()
            {
                var areas = ShellElements(NotificationContainer);
                Assert.NotEmpty(areas);
                foreach (var area in areas)
                {
                    Assert.True(ExplorerAncestry(area));
                    Record($"notification container name={area.Properties.Name.Value} type={area.Properties.ControlType.Value} pid={area.Properties.ProcessId.Value} runtimeId={string.Join(",", area.Properties.RuntimeId.Value)} bounds={area.BoundingRectangle}");
                }
                return areas;
            }
            AutomationElement[] InAreas(Func<AutomationElement, bool> predicate)
            {
                var results = new Dictionary<string, AutomationElement>();
                foreach (var area in Areas())
                    foreach (var e in area.FindAllDescendants().Where(predicate))
                        results.TryAdd(string.Join(",", e.Properties.RuntimeId.Value), e);
                return results.Values.ToArray();
            }
            AutomationElement? AncestorArea(AutomationElement e)
            {
                AutomationElement? current = walker.GetParent(e);
                for (var depth = 0; current is not null && depth < 32; depth++, current = walker.GetParent(current))
                {
                    Record($"ancestry name={current.Properties.Name.Value} type={current.Properties.ControlType.Value} pid={current.Properties.ProcessId.Value} runtimeId={string.Join(",", current.Properties.RuntimeId.Value)}");
                    if (NotificationContainer(current) && ExplorerAncestry(current)) return current;
                }
                return null;
            }
            AutomationElement[] Icons() => InAreas(e => e.Properties.Name.Value == "Boo companion" && Visible(e));
            var icons = Icons();
            if (icons.Length == 0)
            {
                var overflow = Assert.Single(InAreas(e => Visible(e) && e.Properties.ControlType.Value == ControlType.Button
                    && e.Properties.Name.Value.Contains("hidden", StringComparison.OrdinalIgnoreCase)
                    && e.Properties.Name.Value.Contains("icon", StringComparison.OrdinalIgnoreCase)));
                Assert.True(ExplorerAncestry(overflow));
                var box = overflow.BoundingRectangle;
                Record($"overflow name={overflow.Properties.Name.Value} pid={overflow.Properties.ProcessId.Value} bounds={box}; one physical open click");
                _boo.CaptureRectangle("qa-j12f-overflow-control", box);
                Mouse.Click(new Point(box.Left + box.Width / 2, box.Top + box.Height / 2), MouseButton.Left);
                Assert.True(BooAppFixture.WaitUntil(() => (icons = Icons()).Length > 0, TimeSpan.FromSeconds(3)),
                    "Hosted Explorer did not expose Boo tray icon; no fallback Stop path used.");
            }
            var icon = Assert.Single(icons);
            Assert.True(ExplorerAncestry(icon));
            var area = AncestorArea(icon);
            Assert.NotNull(area);
            var iconIdentity = icon.Properties.RuntimeId.Value;
            var areaIdentity = area.Properties.RuntimeId.Value;
            Record($"discovered icon name={icon.Properties.Name.Value} pid={icon.Properties.ProcessId.Value} type={icon.Properties.ControlType.Value} bounds={icon.BoundingRectangle}");
            var notch = _boo.WaitForNotch(QaUi.UiTimeout);
            var subscription = notch.RegisterPropertyChangedEvent(TreeScope.Element,
                (_, _, value) => events.Enqueue((clock.ElapsedMilliseconds, value as string ?? "<invalid>")),
                notch.Automation.PropertyLibrary.Element.ItemStatus);
            cleanup.Add(subscription.Dispose);
            var recorder = new NotchStateRecorder(_boo, "tray-stop");
            cleanup.Add(recorder.Dispose);
            cleanup.Insert(0, () => Press("FocusEndButton"));
            var running = clock.ElapsedMilliseconds;
            Press("FocusStartButton");
            Assert.True(BooAppFixture.WaitUntil(() => Find("FocusStatus").Properties.Name.Value.StartsWith("Running", StringComparison.Ordinal), TimeSpan.FromSeconds(3)));
            var store = new SessionStore(Path.Combine(_boo.DataDirectory, "sessions.db"));
            var before = Assert.Single(store.List(), s => s.Task == task);
            Assert.Equal(FocusStatus.Running, before.Status);
            Assert.True(Visible(icon));
            Assert.Equal("Boo companion", icon.Properties.Name.Value);
            Assert.True(ExplorerAncestry(icon));
            var bounds = icon.BoundingRectangle;
            var point = new Point(bounds.Left + bounds.Width / 2, bounds.Top + bounds.Height / 2);
            Mouse.Position = point;
            var hit = _boo.Automation.FromPoint(point);
            Assert.True(ExplorerAncestry(hit));
            Assert.Equal(iconIdentity, hit.Properties.RuntimeId.Value);
            Assert.Equal("Boo companion", hit.Properties.Name.Value);
            var hitArea = AncestorArea(hit);
            Assert.NotNull(hitArea);
            Assert.Equal(areaIdentity, hitArea.Properties.RuntimeId.Value);
            Record($"revalidated icon hit={hit.Properties.Name.Value} pid={hit.Properties.ProcessId.Value} point={point.X},{point.Y}; one right-click");
            Mouse.RightClick(point);
            AutomationElement? menu = null;
            AutomationElement? stop = null;
            Assert.True(BooAppFixture.WaitUntil(() =>
            {
                var menus = desktop.FindAllDescendants(cf => cf.ByProcessId(_boo.App.ProcessId).And(cf.ByControlType(ControlType.Menu))).Where(Visible).ToArray();
                var matches = menus.SelectMany(m => m.FindAllDescendants(cf => cf.ByControlType(ControlType.MenuItem)))
                    .Where(e => Visible(e) && e.Properties.Name.Value is "Stop (Ctrl+Alt+F12)" or "Stop").ToArray();
                if (matches.Length != 1) return false;
                var selectedStop = matches[0];
                stop = selectedStop;
                menu = Assert.Single(menus, m => m.FindAllDescendants().Any(e => e.Properties.RuntimeId.Value.SequenceEqual(selectedStop.Properties.RuntimeId.Value)));
                return true;
            }, TimeSpan.FromSeconds(3)), "Boo-process tray Stop menu not uniquely exposed; no shortcut/panel fallback.");
            Assert.NotNull(menu);
            Assert.NotNull(stop);
            Assert.Equal(_boo.App.ProcessId, menu.Properties.ProcessId.Value);
            Assert.True(stop.IsEnabled && stop.Patterns.Invoke.IsSupported);
            Record($"menu pid={menu.Properties.ProcessId.Value} name={menu.Properties.Name.Value} bounds={menu.BoundingRectangle}; item={stop.Properties.Name.Value} InvokeSupported=true");
            _boo.CaptureRectangle("qa-j12f-tray-menu", menu.BoundingRectangle);
            Assert.True(clock.ElapsedMilliseconds - running < 9000, "Running-to-Stop setup exceeded bounded window; no retry.");
            Thread.Sleep(150);
            var mark = recorder.Mark();
            var eventStart = events.Count;
            Record("single tray Stop Invoke begin");
            stop.AsMenuItem().Invoke();
            Record("single tray Stop Invoke complete");
            Assert.True(BooAppFixture.WaitUntil(() => Find("FocusStatus").Properties.Name.Value == "Paused by Stop; Resume is explicit."
                && notch.Properties.ItemStatus.Value == "Paused", TimeSpan.FromSeconds(3)));
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(eventStart).Any(e => e.Face == "Paused"), TimeSpan.FromSeconds(1)));
            var watch = Stopwatch.StartNew();
            while (watch.Elapsed < TimeSpan.FromSeconds(3))
            {
                Assert.Equal("Paused", notch.Properties.ItemStatus.Value);
                Assert.Equal("Paused by Stop; Resume is explicit.", Find("FocusStatus").Properties.Name.Value);
                Thread.Sleep(40);
            }
            var health = recorder.ObservationFailureSince(mark);
            Record("health=" + (health ?? "healthy within 100 ms"));
            Assert.Null(health);
            Assert.DoesNotContain(events.Skip(eventStart), e => e.Face != "Paused");
            var after = Assert.Single(store.List(), s => s.Id == before.Id);
            Assert.Equal(FocusStatus.Paused, after.Status);
            Assert.Equal(task, after.Task);
            Record($"session={after.Id} task={after.Task} status={after.Status}; pinned3sec");
            _boo.CaptureScreen("qa-j12f-paused");
            var barrier = events.Count;
            Press("FocusEndButton");
            cleanup.RemoveAt(0);
            Assert.True(BooAppFixture.WaitUntil(() => events.Skip(barrier).Any(e => e.Face == "Happy"), TimeSpan.FromSeconds(3)), "Missing post-window event delivery.");
        }
        catch (Exception ex)
        {
            primary = ex;
            Record("FAILED: " + ex);
            try { _boo.CaptureScreen("qa-j12f-FAILED"); }
            catch (Exception evidenceError) { Record("capture error: " + evidenceError); }
            throw;
        }
        finally
        {
            var secondary = new List<Exception>();
            foreach (var action in cleanup)
            {
                try { action(); }
                catch (Exception ex) { Record("cleanup error: " + ex); secondary.Add(ex); }
            }
            log.AddRange(events.Select(e => $"{e.At} ItemStatus event {e.Face}"));
            try { _boo.WriteObservation("qa-j12f-tray-stop", string.Join("\n", log)); }
            catch (Exception ex) { Console.Error.WriteLine(ex); secondary.Add(ex); }
            if (primary is null && secondary.Count > 0) throw new AggregateException(secondary);
        }
    }
}
