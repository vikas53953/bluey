using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.LiveSmoke;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using Xunit.Abstractions;

namespace Boo.VerifyLive;

/// <summary>
/// The verify-boo skill's LIVE journeys: one published Boo.exe, one real AI provider, driven the way a user drives it.
/// Steps run in order on the same app (see .claude/skills/verify-boo/SKILL.md and docs/FEATURE_MAP.md):
/// 0 notch wake by hotkey  1 notch wake by hover  2 typed chat with a real reply  3 screen explain, pointer + ring on a
/// known target window  4 research with source links  5 permissioned Apply then Undo of a file  6 a 25-minute focus
/// session on the existing Developer Mode clock (4x).
/// Every step writes screenshots (jN-*.png) and observations (jN-*.txt) under BOO_UI_ARTIFACTS and asserts on live UI
/// Automation state, pixels or files, never on Boo's own claim alone. A step that cannot prove its feature fails; it
/// never skips.
/// </summary>
[TestCaseOrderer("Boo.Demo.Tests.StepOrderer", "Boo.VerifyLive")]
public sealed class LiveJourneyTests : IClassFixture<LiveAppFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AnswerTimeout = TimeSpan.FromSeconds(150);
    private static readonly TimeSpan RevealTimeout = TimeSpan.FromSeconds(1.5);
    private static readonly TimeSpan RetractTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan RestTimeout = TimeSpan.FromSeconds(8);
    private const string FileMarker = "BOO-LIVE-VERIFY-OK";
    private const string FileName = "boo-live-verify.txt";
    private const string FocusTask = "Write the Q4 firewall migration plan";
    private const int FocusMinutes = 25;

    // One class instance per test, so state shared across steps is static.
    private static TargetWindow? s_target;
    private static string? s_taskFolder;

    private readonly LiveAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public LiveJourneyTests(LiveAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact, Step(0)]
    public void J0_NotchWakes_OnHotkey()
    {
        Evidence("j0-hotkey", () =>
        {
            // Fail closed if anything could answer instead of the real provider (the demo-ci mock uses a hosts entry).
            var resolved = Dns.GetHostAddresses(ProviderHost(LiveAppFixture.Config.TextProvider));
            _boo.WriteObservation("j0-provider-host", $"{ProviderHost(LiveAppFixture.Config.TextProvider)} -> {string.Join(", ", resolved.Select(a => a.ToString()))}");
            Assert.DoesNotContain(resolved, IPAddress.IsLoopback);

            var notch = _boo.WaitForNotch(UiTimeout);
            Assert.Null(_boo.FindPanel()); // Boo starts with only the notch (no screenshot: a resting notch draws nothing).

            BooAppFixture.PressOpenShortcut();
            var panel = _boo.WaitForPanel(TimeSpan.FromSeconds(15));
            _boo.CaptureScreen("j0-after-hotkey");
            _boo.WriteObservation("j0-hotkey", $"notch='{BooAppFixture.SafeName(notch)}' at {notch.BoundingRectangle}; panel='{BooAppFixture.SafeName(panel)}' opened by Ctrl+Alt+B");
            Assert.StartsWith(BooAppFixture.PanelTitlePrefix, BooAppFixture.SafeName(panel), StringComparison.Ordinal);
        });
    }

    [Fact, Step(1)]
    public void J1_NotchWakes_OnHover()
    {
        Evidence("j1-hover", () =>
        {
            // Recorder only (evidence, no new assertion): every face the notch shows during this step, about every 25 ms.
            using var faces = FaceTimeline.Start(_boo, "j1-hover");
            // The open panel keeps the face awake; hide it so the notch can rest.
            if (_boo.FindPanel() is { } panel)
            {
                Find(panel, "CloseButton").AsButton().Invoke();
                Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout), "Close did not hide the panel.");
            }

            var rect = _boo.WaitForNotch(UiTimeout).BoundingRectangle;
            var hover = new Point(rect.Left + rect.Width / 2, rect.Top + 2);
            var away = new Point(hover.X, hover.Y + 300);
            Mouse.MoveTo(away);
            Assert.True(BooAppFixture.WaitUntil(() => NotchState() == "Hidden", RestTimeout),
                $"The notch did not rest in 'Hidden' before hovering. ItemStatus: '{NotchState()}'.");

            faces.Mark("mouse moved onto the hover strip");
            Mouse.MoveTo(hover);
            var clock = Stopwatch.StartNew();
            var revealed = BooAppFixture.WaitUntil(() => NotchState() is { Length: > 0 } s && s != "Hidden", RevealTimeout);
            var revealMs = clock.ElapsedMilliseconds;
            var state = NotchState();
            _boo.CaptureScreen("j1-hover-revealed");

            faces.Mark("mouse moved away");
            Mouse.MoveTo(away);
            clock.Restart();
            var retracted = BooAppFixture.WaitUntil(() => NotchState() == "Hidden", RetractTimeout);
            _boo.WriteObservation("j1-hover", $"notch={rect} hover={hover} revealed={revealed} state='{state}' revealMs={revealMs} retracted={retracted} retractMs={clock.ElapsedMilliseconds}\n" +
                $"timelineFaces={string.Join(">", faces.FacesSeen())} (j1-hover-face-timeline.txt)");
            Assert.True(revealed, $"Hovering {hover} did not wake the notch within {RevealTimeout.TotalSeconds}s. ItemStatus: '{state}'.");
            Assert.True(retracted, $"The notch did not retract within {RetractTimeout.TotalSeconds}s. ItemStatus: '{NotchState()}'.");
        });
    }

    [Fact, Step(2)]
    public void J2_TypedChat_GetsARealAnswer()
    {
        Evidence("j2-chat", () =>
        {
            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);
            var before = BooRows(panel).Count;
            // Recorder only (evidence, no new assertion): the faces of the chat turn (Thinking, Responding, Capture, ...).
            using var faces = FaceTimeline.Start(_boo, "j2-chat");
            var sentAt = faces.NowMs;
            faces.Mark("question sent");
            Ask(panel, "What is the capital of France? Answer with just the city name.");
            var reply = WaitForNewBooRow(panel, before);
            faces.Mark("answer row seen");
            _boo.CaptureScreen("j2-chat-answer");
            var turnFaces = faces.FacesSeen(sentAt);
            _boo.WriteObservation("j2-chat", $"provider={AiProviders.DisplayName(LiveAppFixture.Config.TextProvider)} reply='{Shorten(reply, 400)}' status='{StatusText(panel)}'\n" +
                $"facesDuringTurn={string.Join(">", turnFaces)} (j2-chat-face-timeline.txt)");
            _output.WriteLine($"j2 faces during the chat turn: {string.Join(" > ", turnFaces)}");
            Assert.False(reply.StartsWith("QA-", StringComparison.Ordinal), $"The answer came from the QA mock: '{reply}'");
            Assert.Contains("Paris", reply, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact, Step(3)]
    public void J3_ScreenExplain_PointerAndRingLandOnTheTarget()
    {
        Evidence("j3-explain", () =>
        {
            // Boo skips the pointer on purpose when Windows "Animation effects" are off (AnnotationWindow.StartSequence,
            // reduced motion). DpiSetup turns them on before Boo launches; check they are still on now.
            _boo.WriteObservation("j3-animations", $"{DpiSetup.BeforeLaunch} onNow={WindowsAnimations.IsOn()}");
            Assert.True(WindowsAnimations.IsOn(), "TEST SETUP: Windows animation effects are off, so Boo would not show its pointer by design. Nothing about Boo was checked.");

            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);
            s_target ??= TargetWindow.Show(TargetBounds());
            var targetRect = s_target.ButtonScreenRectangle();
            var windowRect = s_target.ScreenRectangle();
            var before = BooRows(panel).Count;
            SetText(panel, "InputBox", "Explain the window titled 'Boo live target' and point at its big 'Send report' button.");

            // Capture snapshots its target during Send. Establish the external window first;
            // bringing it forward after Send cannot change the already selected capture target.
            BringTargetForward("j3 before send");
            Assert.True(s_target.IsForeground(), "TEST SETUP (j3): target must be foreground before Send.");
            AssertTargetOnTop(s_target, "j3 before send");
            var explainClock = Stopwatch.StartNew(); // Boo gives up on OpenAI after 45 s (HttpClient timeout); record how long it took
            Find(panel, "SendButton").AsButton().Invoke();
            Find(panel, "CloseButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout), "TEST SETUP: Boo's panel did not hide.");
            BringTargetForward("j3 after panel close");
            AssertTargetOnTop(s_target, "j3");

            // Screenshots are of Boo's own windows only (PR #18), so the marks are read from Boo's explanation overlay
            // itself ("Boo screen explanation"), rendered from its own content in memory: it holds only Boo's marks, never
            // the desktop, so nothing else on screen can fake a ring or a pointer. Its window rectangle maps its pixels
            // to screen positions. Ring = AnnotationWindow.MarkColor (243,181,155); pointer = AnnotationWindow.PointerColor
            // (0x42,0x54,0xD6), shown at its landing point for the whole 10 s hold. Poll: the answer takes a few seconds.
            var search = Inflate(windowRect, 160);
            var centre = Point.Empty;
            var pixels = 0;
            OverlayRender? overlay = null;
            var found = BooAppFixture.WaitUntil(() =>
            {
                overlay?.Dispose();
                overlay = OverlayRender.Capture(_boo);
                return overlay is not null && overlay.Find(search, ColourProbe.Ring, 12, out centre, out pixels);
            }, TimeSpan.FromSeconds(90));
            var marksAfterSeconds = found ? explainClock.Elapsed.TotalSeconds : -1;
            var pointer = Point.Empty;
            var pointerPixels = 0;
            var pointerFound = false;
            if (found)
            {
                Thread.Sleep(900); // let the ring finish drawing, then judge the settled marks
                overlay?.Dispose();
                overlay = OverlayRender.Capture(_boo);
                found = overlay is not null && overlay.Find(search, ColourProbe.Ring, 12, out centre, out pixels);
                pointerFound = overlay is not null && overlay.Find(search, ColourProbe.Pointer, 15, out pointer, out pointerPixels);
                _boo.CaptureScreen("j3-explain-ring"); // evidence: Boo's windows (including the overlay with its marks)
            }
            var onWindow = Inflate(windowRect, 20).Contains(centre);
            // Boo's ring is an ellipse inscribed in the mark's box (AnnotationWindow: Ellipse Width/Height = bounds), so a
            // ring around the BUTTON passes through the middle of the button's left, right and bottom edges. A ring around
            // anything bigger (the whole window, for instance) misses those points by ~100 px. The top edge is left out:
            // the mark's label (also ring-coloured) sits just above the button. A centroid check alone could not tell.
            var mid = new Point(targetRect.Left + targetRect.Width / 2, targetRect.Top + targetRect.Height / 2);
            var edges = new[]
            {
                ("left", new Point(targetRect.Left, mid.Y)),
                ("right", new Point(targetRect.Right, mid.Y)),
                ("bottom", new Point(mid.X, targetRect.Bottom)),
            };
            var edgeHits = edges.Select(e => (e.Item1, overlay?.Count(AroundPoint(e.Item2, 30), ColourProbe.Ring) ?? 0)).ToList();
            var ringOnButton = found && edgeHits.All(h => h.Item2 >= 6);
            var pointerOnButton = Inflate(targetRect, 20).Contains(pointer);
            var overlayRect = overlay?.ScreenRect.ToString() ?? "<no overlay>";
            overlay?.Dispose();

            BooAppFixture.PressOpenShortcut();
            panel = _boo.WaitForPanel(UiTimeout);
            var detail = _boo.WaitFor(() => NonEmpty(NameOfId(panel, "AnnotationDetail")), AnswerTimeout, "the screen-explanation outcome line");
            var reply = BooRows(panel).Skip(before).LastOrDefault() ?? string.Empty;
            _boo.CaptureScreen("j3-explain-panel");
            _boo.WriteObservation("j3-explain",
                $"detail='{detail}' marksAfterSeconds={marksAfterSeconds:0.0} overlay={overlayRect} ringFound={found} ringPixels={pixels} ringCentre={centre} button={targetRect} window={windowRect} " +
                $"ringOnButton={ringOnButton} ringPixelsAtButtonEdges={string.Join(",", edgeHits.Select(h => $"{h.Item1}:{h.Item2}"))} onWindow={onWindow} " +
                $"pointerFound={pointerFound} pointerPixels={pointerPixels} pointerCentre={pointer} pointerOnButton={pointerOnButton}\n" +
                $"reply='{Shorten(reply, 600)}'\nstatus='{StatusText(panel)}'");

            Assert.StartsWith("Screen explanation is shown", detail, StringComparison.Ordinal);
            Assert.True(found, $"No ring-coloured pixels in Boo's explanation overlay near the target window {windowRect} (search {search}). See the j3-explain-ring.boo-*.png overlay render.");
            Assert.True(ringOnButton, $"The ring does not run through the 'Send report' button's left, right and bottom edges ({string.Join(", ", edgeHits.Select(h => $"{h.Item1}={h.Item2} px"))}; button {targetRect}), so it is not around the button. See the j3-explain-ring.boo-*.png overlay render.");
            Assert.True(pointerFound, $"No pointer-coloured pixels in Boo's explanation overlay with the ring (search {search}). See the j3-explain-ring.boo-*.png overlay render.");
            Assert.True(pointerOnButton, $"The pointer {pointer} did not land on the 'Send report' button {targetRect} (20 px margin).");
        });
    }

    [Fact, Step(4)]
    public void J4_Research_ShowsRealSourceLinks()
    {
        Evidence("j4-research", () =>
        {
            var panel = _boo.EnsurePanel();
            Ask(panel, "research the latest stable .NET release and its release date, with sources");
            List<string> links;
            try
            {
                links = _boo.WaitFor(() => SourceLinkNames(panel) is { Count: >= 1 } found ? found : null, AnswerTimeout, "at least one source link");
            }
            catch (TimeoutException)
            {
                _boo.WriteObservation("j4-research-timeout", $"sourceStatus='{NameOfId(panel, "SourceStatusText")}' status='{StatusText(panel)}' rows='{string.Join(" || ", BooRows(panel))}'");
                throw;
            }
            Thread.Sleep(1500); // let the remaining links render
            links = SourceLinkNames(panel);
            var sourceStatus = NameOfId(panel, "SourceStatusText");
            // Scroll the links into view so the screenshot shows them, not only the UI Automation read.
            try
            {
                panel.FindAllDescendants().LastOrDefault(e => BooAppFixture.SafeName(e).StartsWith("Open source ", StringComparison.Ordinal))
                    ?.Patterns.ScrollItem.PatternOrDefault?.ScrollIntoView();
                Thread.Sleep(500);
            }
            catch (Exception ex)
            {
                _output.WriteLine($"Could not scroll the source links into view: {ex.Message}");
            }
            _boo.CaptureScreen("j4-research-sources");
            _boo.WriteObservation("j4-research", $"links({links.Count})={string.Join(" | ", links)}\nsourceStatus='{sourceStatus}'\nlastReply='{Shorten(BooRows(panel).LastOrDefault(), 600)}'");
            Assert.DoesNotContain("No sources were returned", sourceStatus, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(links, l => l.Contains("QA source", StringComparison.Ordinal) || l.Contains("example.com", StringComparison.OrdinalIgnoreCase));
        });
    }

    [Fact, Step(5)]
    public void J5_ApplyThenUndo_OnlyAfterApproval()
    {
        Evidence("j5-apply-undo", () =>
        {
            s_taskFolder = Path.Combine(Path.GetTempPath(), "boo-verify-task-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(s_taskFolder);
            var file = Path.Combine(s_taskFolder, FileName);
            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);

            // Choose the Task folder the way a user does: Settings > Task folder, Save settings.
            SetText(panel, "FileRootBox", s_taskFolder);
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => StatusText(panel).StartsWith("Settings saved", StringComparison.Ordinal), UiTimeout),
                $"Settings were not saved. Status: '{StatusText(panel)}'");
            Assert.True(BooAppFixture.WaitUntil(() => BooAppFixture.ReadShared(_boo.SettingsPath).Contains(Path.GetFileName(s_taskFolder), StringComparison.OrdinalIgnoreCase), UiTimeout),
                "settings.json did not record the Task folder.");
            Assert.True(File.Exists(Path.Combine(_boo.DataDirectory, AiProviders.CredentialFileName(LiveAppFixture.Config.TextProvider))),
                "Saving settings removed the seeded key file.");

            Ask(panel, $"create a file named {FileName} in the task folder containing exactly the text {FileMarker}");
            _boo.WaitFor(() => Visible(TryFind(panel, "ActionPreview")), AnswerTimeout, "the Review action card");
            var preview = NameOfId(panel, "ActionPreviewText");
            _boo.CaptureScreen("j5-apply-preview");
            Assert.Null(Visible(TryFind(panel, "ActionBlockedReason")));
            Thread.Sleep(3000);
            Assert.False(File.Exists(file), "The file appeared before Approve was pressed.");

            Find(panel, "ApproveButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => File.Exists(file) && BooAppFixture.ReadShared(file).Contains(FileMarker, StringComparison.Ordinal), UiTimeout),
                $"{file} does not contain '{FileMarker}' after Approve. Preview: '{preview}' Status: '{StatusText(panel)}'");
            var written = BooAppFixture.ReadShared(file);
            _boo.CaptureScreen("j5-applied");

            Ask(panel, "undo");
            _boo.WaitFor(() => Visible(TryFind(panel, "ActionPreview")), UiTimeout, "the Undo review card");
            var undoPreview = NameOfId(panel, "ActionPreviewText");
            _boo.CaptureScreen("j5-undo-preview");
            Assert.True(File.Exists(file), "Undo changed the file before Approve was pressed.");
            Find(panel, "ApproveButton").AsButton().Invoke();
            Assert.True(BooAppFixture.WaitUntil(() => !File.Exists(file), UiTimeout),
                $"The created file is still there after the approved Undo. Undo preview: '{undoPreview}' Status: '{StatusText(panel)}'");
            _boo.CaptureScreen("j5-undone");
            _boo.WriteObservation("j5-apply-undo",
                $"taskFolder={s_taskFolder}\npreview='{preview}'\nwritten='{Shorten(written, 200)}'\nundoPreview='{undoPreview}'\nfileGoneAfterUndo=true\nstatus='{StatusText(panel)}'");
        });
    }

    [Fact, Step(6)]
    public void J6_FocusSession_25Minutes_OnTheDeveloperModeClock()
    {
        Evidence("j6-focus", () =>
        {
            // Recorder only (evidence, no new assertion): every face over the whole session, about every 25 ms.
            using var faces = FaceTimeline.Start(_boo, "j6-focus");
            try
            {
                var panel = _boo.EnsurePanel();
                EnsureDisplaySelected(panel);
                s_target ??= TargetWindow.Show(TargetBounds());

                // Developer Mode is Boo's existing test clock: 4x focus time, a check every 10 real seconds. A 25-minute
                // session therefore takes 375 real seconds. A 25-minute session on the real-time clock stays a manual check.
                var dev = Find(panel, "DeveloperModeBox").AsCheckBox();
                dev.IsChecked = true;
                SetText(panel, "FocusTaskBox", FocusTask);
                SetText(panel, "FocusDurationBox", FocusMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var clock = Stopwatch.StartNew();
                faces.Mark("focus session started");
                Find(panel, "FocusStartButton").AsButton().Invoke();
                Assert.True(BooAppFixture.WaitUntil(() => NameOfId(panel, "FocusStatus").StartsWith("Running", StringComparison.Ordinal), UiTimeout),
                    $"Focus did not start: '{NameOfId(panel, "FocusStatus")}'");
                _boo.CaptureScreen("j6-focus-running");
                Find(panel, "CloseButton").AsButton().Invoke();
                Assert.True(BooAppFixture.WaitUntil(() => _boo.FindPanel() is null, UiTimeout), "TEST SETUP: Boo's panel did not hide.");

                // The user now works in the on-task window: keep it in front for the whole session (re-raised whenever
                // something else takes the foreground). Screenshots are of Boo's windows only, so "in front" is proven by
                // asking Windows which window is at the button, and counted on every re-check.
                BringTargetForward("j6");
                AssertTargetOnTop(s_target, "j6 start");
                var lastRaise = Stopwatch.StartNew();
                var onTopChecks = 0;
                var onTopHits = 0;

                var states = new List<string>();
                var nudges = 0;
                var sawHappy = BooAppFixture.WaitUntil(() =>
                {
                    var notch = _boo.FindNotch();
                    if (notch is null) return false;
                    var state = ItemStatus(notch);
                    if (state.Length > 0 && (states.Count == 0 || states[^1] != state)) states.Add(state);
                    if (notch.FindFirstDescendant(cf => cf.ByAutomationId("NudgeBackButton")) is { } back && Visible(back) is not null)
                    {
                        nudges++;
                        if (nudges <= 3) _boo.CaptureScreen($"j6-nudge-{nudges}");
                        back.AsButton().Invoke();
                    }
                    if (lastRaise.Elapsed > TimeSpan.FromSeconds(5) && s_target is { } t && !t.IsForeground())
                    {
                        t.BringToFront(_boo.Automation);
                        lastRaise.Restart();
                    }
                    if (s_target is { } top && clock.Elapsed > TimeSpan.FromSeconds(30 * (onTopChecks + 1)))
                    {
                        onTopChecks++;
                        if (top.IsTopAt(top.ButtonScreenRectangle())) onTopHits++;
                    }
                    return state == "Happy";
                }, TimeSpan.FromMinutes(9));
                var realSeconds = clock.Elapsed.TotalSeconds;
                faces.Mark(sawHappy ? "happy face seen by the test" : "test stopped waiting for the happy face");
                _boo.CaptureScreen("j6-focus-happy");

                // The session is over: close the target so the final screenshot shows Boo's status and history row.
                s_target?.Dispose();
                s_target = null;
                BooAppFixture.PressOpenShortcut();
                panel = _boo.WaitForPanel(UiTimeout);
                var complete = BooAppFixture.WaitUntil(() => NameOfId(panel, "FocusStatus").StartsWith("Session complete", StringComparison.Ordinal), UiTimeout);
                // History rows read "<task>" then "25 min · 84% focused · Completed" (FocusTimeline.ToHistoryRow). The
                // ItemsControl has no UIA peer of its own, so look for the row texts anywhere in the window.
                string? historyRow = null;
                BooAppFixture.WaitUntil(() => (historyRow = panel.FindAllDescendants().Select(BooAppFixture.SafeName)
                    .FirstOrDefault(s => s.StartsWith($"{FocusMinutes} min", StringComparison.Ordinal) && s.Contains("Completed", StringComparison.Ordinal))) is not null, UiTimeout);
                var history = historyRow ?? "<no completed 25 min row>";
                _boo.CaptureScreen("j6-focus-complete");
                _boo.WriteObservation("j6-focus",
                    $"task='{FocusTask}' minutes={FocusMinutes} developerMode=true realSeconds={realSeconds:0} sawHappy={sawHappy} nudges={nudges} " +
                    $"targetOnTopChecks={onTopHits}/{onTopChecks}\n" +
                    $"states={string.Join(">", states)}\nfocusStatus='{NameOfId(panel, "FocusStatus")}'\nhistory='{Shorten(history, 600)}'");

                // The focus estimate below is only meaningful if the on-task window really was what Boo saw.
                Assert.True(onTopChecks > 0 && onTopHits * 2 > onTopChecks,
                    $"TEST SETUP: the on-task window was in front for only {onTopHits} of {onTopChecks} checks, so the focus estimate proves nothing.");
                Assert.True(sawHappy, $"The notch never showed the happy face. States: {string.Join(">", states)}");
                Assert.True(complete, $"The session did not complete: '{NameOfId(panel, "FocusStatus")}'");
                // 25 focus-minutes at 4x is 375 real seconds; allow for check round trips and UI polling.
                Assert.InRange(realSeconds, 330, 540);
                Assert.True(historyRow is not null, "Session history shows no completed 25 min row.");
                // The screen showed only the on-task window ("Q4 firewall migration plan") the whole time, so Boo's real
                // focus checks must have judged at least part of the session on task.
                var score = System.Text.RegularExpressions.Regex.Match(history, @"(\d+)% focused");
                Assert.True(score.Success, $"Session history has no focus estimate: '{history}'");
                Assert.True(int.Parse(score.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) > 0,
                    $"Boo judged a session spent entirely in the on-task window as '{score.Value}' and nudged {nudges} times (target in front {onTopHits}/{onTopChecks} checks). See j6-focus.txt and the j6-nudge-*.boo-*.png screenshots.");
            }
            finally
            {
                s_target?.Dispose();
                s_target = null;
                if (s_taskFolder is not null)
                {
                    try { Directory.Delete(s_taskFolder, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
                }
            }
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string ProviderHost(AiProvider provider) => provider switch
    {
        AiProvider.Anthropic => "api.anthropic.com",
        AiProvider.Gemini => "generativelanguage.googleapis.com",
        _ => "api.openai.com",
    };

    private string NotchState() => _boo.FindNotch() is { } notch ? ItemStatus(notch) : string.Empty;

    private static string ItemStatus(AutomationElement element)
    {
        try { return element.Properties.ItemStatus.ValueOrDefault ?? string.Empty; }
        catch (Exception) { return string.Empty; }
    }

    private void Ask(Window panel, string text)
    {
        SetText(panel, "InputBox", text);
        Find(panel, "SendButton").AsButton().Invoke();
        _output.WriteLine($"Sent: {text}");
    }

    private string WaitForNewBooRow(Window panel, int before)
    {
        var rows = _boo.WaitFor(() => BooRows(panel) is { } r && r.Count > before ? r : null, AnswerTimeout,
            "a new 'Boo: ...' answer row");
        return rows[^1];
    }

    /// <summary>Text of every transcript row named "Boo: ...", oldest first.</summary>
    private static List<string> BooRows(Window panel)
    {
        var list = TryFind(panel, "TranscriptList");
        if (list is null) return [];
        return list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem))
            .Select(BooAppFixture.SafeName)
            .Where(n => n.StartsWith("Boo: ", StringComparison.Ordinal))
            .Select(n => n["Boo: ".Length..])
            .ToList();
    }

    /// <summary>Boo names each source link "Open source &lt;title&gt;" (MainWindow.RenderSources).</summary>
    private static List<string> SourceLinkNames(Window panel) =>
        panel.FindAllDescendants()
            .Select(BooAppFixture.SafeName)
            .Where(n => n.StartsWith("Open source ", StringComparison.Ordinal))
            .Distinct()
            .ToList();

    private void EnsureDisplaySelected(Window panel)
    {
        var combo = TryFind(panel, "DisplayBox")?.AsComboBox();
        if (combo is null) return;
        try
        {
            if (combo.SelectedItem is null && combo.Items.Length > 0)
            {
                combo.Select(0);
                Thread.Sleep(500);
            }
        }
        catch (Exception ex)
        {
            _output.WriteLine($"DisplayBox selection skipped: {ex.Message}");
        }
    }

    /// <summary>The on-task target window: middle of the primary screen, below the notch.</summary>
    private static Rectangle TargetBounds()
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var width = Math.Min(760, screen.Width / 2);
        var height = Math.Min(520, screen.Height / 2);
        return new Rectangle(screen.Left + (screen.Width - width) / 2, screen.Top + screen.Height / 4, width, height);
    }

    private void BringTargetForward(string step)
    {
        s_target!.BringToFront(_boo.Automation);
        Thread.Sleep(700);
        _output.WriteLine($"{step}: target foreground={s_target!.IsForeground()}");
    }

    /// <summary>
    /// Fails with a setup message (not a verdict on Boo) when the target's button is not the window on top at that spot,
    /// so a covered target can never pass or fail a check about what Boo saw or drew. Windows answers which window is at
    /// the point (WindowFromPoint skips click-through windows such as Boo's explanation overlay); no screen pixels are read.
    /// </summary>
    private void AssertTargetOnTop(TargetWindow target, string step)
    {
        var onTop = BooAppFixture.WaitUntil(() => target.IsTopAt(target.ButtonScreenRectangle()), TimeSpan.FromSeconds(5));
        _output.WriteLine($"{step}: target on top at its button = {onTop}");
        Assert.True(onTop, $"TEST SETUP ({step}): the target window is not the top window at its button, so Boo would not see it. Nothing about Boo was checked.");
    }

    private static Rectangle AroundPoint(Point p, int half) => new(p.X - half, p.Y - half, 2 * half, 2 * half);

    private static Rectangle Inflate(Rectangle r, int by) => new(r.Left - by, r.Top - by, r.Width + 2 * by, r.Height + 2 * by);

    private static string? NonEmpty(string text) => text.Length > 0 ? text : null;

    private static string Shorten(string? text, int max) =>
        text is null ? "<none>" : text.Length <= max ? text : text[..max] + "...";

    private static AutomationElement? Visible(AutomationElement? element)
    {
        try { return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0 ? element : null; }
        catch (Exception) { return null; }
    }

    private void Evidence(string step, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            _boo.CaptureScreen(step + "-FAILED");
            // Logs before guesses: record what Boo itself said, so a failure explains itself even without screenshots.
            var said = "<panel not readable>";
            try
            {
                var panel = _boo.FindPanel() ?? _boo.EnsurePanel();
                said = $"status='{StatusText(panel)}'\nannotationDetail='{NameOfId(panel, "AnnotationDetail")}'\n" +
                       $"focusStatus='{NameOfId(panel, "FocusStatus")}'\nlastBooRows='{string.Join(" || ", BooRows(panel).TakeLast(2).Select(r => Shorten(r, 300)))}'";
                _boo.CaptureScreen(step + "-FAILED-panel");
            }
            catch (Exception)
            {
                // Best effort only; the original failure is what matters.
            }
            _boo.WriteObservation(step + "-FAILED", ex + "\n----- what Boo showed -----\n" + said);
            throw;
        }
    }

    private static AutomationElement? TryFind(Window panel, string automationId) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId));

    private AutomationElement Find(Window panel, string automationId) =>
        _boo.WaitFor(() => TryFind(panel, automationId), UiTimeout, $"element '{automationId}'");

    private void SetText(Window panel, string automationId, string value)
    {
        var box = Find(panel, automationId).AsTextBox();
        box.Text = value;
        Assert.True(BooAppFixture.WaitUntil(() => string.Equals(box.Text, value, StringComparison.OrdinalIgnoreCase), UiTimeout),
            $"Could not set {automationId} to {value}; it reads '{box.Text}'.");
    }

    private static string StatusText(Window panel) => NameOfId(panel, "StatusText");

    private static string NameOfId(Window panel, string automationId) =>
        TryFind(panel, automationId) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}

/// <summary>
/// Makes this test process per-monitor DPI aware before any window exists, so UI Automation rectangles, screenshots
/// and the target window all use the same physical pixels at any display scaling.
/// </summary>
internal static class DpiSetup
{
    [ModuleInitializer]
    internal static void Initialize()
    {
        SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

        // Boo reads Windows "Animation effects" when it starts and skips its pointer when they are off (reduced
        // motion, by design). GitHub's Windows VMs have them off; most users have them on. Turn them on before the
        // fixture launches Boo (session only, never saved to the profile) and put the original value back on exit.
        // Only for a real live run (the workflow and Invoke-VerifyBoo.ps1 set BOO_LIVE_TEXT_PROVIDER), never when a test
        // explorer merely loads this assembly to list its tests.
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveConfig.ProviderVariable)))
        {
            BeforeLaunch = $"not changed: {LiveConfig.ProviderVariable} is not set (not a live run); on={WindowsAnimations.IsOn()}";
            return;
        }
        var animations = WindowsAnimations.EnsureOn();
        BeforeLaunch = $"animationEffectsBeforeLaunch={animations.WasOn} onAtLaunch={WindowsAnimations.IsOn()}";
        // A killed run skips this; nothing is saved to the profile, so signing out restores the user's own value.
        AppDomain.CurrentDomain.ProcessExit += (_, _) =>
        {
            if (!animations.Restore()) Console.Error.WriteLine("verify-boo: could not restore Windows animation effects to off.");
        };
    }

    /// <summary>What the animation setting was before Boo launched, for the j3 observation.</summary>
    internal static string BeforeLaunch { get; private set; } = "<module initializer did not run>";

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

/// <summary>
/// Windows "Animation effects" (SPI_GET/SETCLIENTAREAANIMATION, which WPF reads as SystemParameters.ClientAreaAnimation).
/// Turned on for the current session only (no SPIF_UPDATEINIFILE, so nothing is written to the user's profile) and
/// restored on Dispose.
/// </summary>
internal sealed class WindowsAnimations : IDisposable
{
    private const uint GetClientAreaAnimation = 0x1042;
    private const uint SetClientAreaAnimation = 0x1043;
    private const uint SendChange = 0x02;

    private WindowsAnimations(bool wasOn) => WasOn = wasOn;

    public bool WasOn { get; }

    public static bool IsOn()
    {
        var on = false;
        return SystemParametersInfo(GetClientAreaAnimation, 0, ref on, 0) && on;
    }

    public static WindowsAnimations EnsureOn()
    {
        var wasOn = IsOn();
        if (!wasOn) Set(true);
        return new WindowsAnimations(wasOn);
    }

    public void Dispose() => Restore();

    /// <summary>Puts back the value seen before <see cref="EnsureOn"/>; false only when Windows refused.</summary>
    public bool Restore() => WasOn || Set(false);

    private static bool Set(bool on) => SystemParametersInfo(SetClientAreaAnimation, 0, new IntPtr(on ? 1 : 0), SendChange);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, ref bool value, uint winIni);

    [DllImport("user32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint param, IntPtr value, uint winIni);
}

/// <summary>
/// A plain external window with one big, labelled button: a target whose position the test knows, so the pointer and
/// ring can be checked against it. It is owned by this test process, so closing it never touches the user's apps.
/// </summary>
internal sealed class TargetWindow : IDisposable
{
    // Green, far from both Boo colours (pointer blue 66,84,214 and ring peach 243,181,155), so neither can be mistaken for it.
    public static readonly Color ButtonColor = Color.FromArgb(0, 128, 64);

    private readonly System.Windows.Forms.Form _form;
    private readonly System.Windows.Forms.Button _button;
    private readonly Thread _thread;

    private TargetWindow(Rectangle bounds)
    {
        using var ready = new ManualResetEventSlim();
        System.Windows.Forms.Form? form = null;
        System.Windows.Forms.Button? button = null;
        _thread = new Thread(() =>
        {
            form = new System.Windows.Forms.Form
            {
                Text = "Boo live target",
                StartPosition = System.Windows.Forms.FormStartPosition.Manual,
                Bounds = bounds,
                BackColor = Color.White,
                TopMost = true,
            };
            var label = new System.Windows.Forms.Label
            {
                Text = "Q4 firewall migration plan\r\n1. Inventory current rules\r\n2. Map rules to the new zones\r\n3. Schedule the cutover window",
                Font = new Font("Segoe UI", 16),
                ForeColor = Color.Black,
                Dock = System.Windows.Forms.DockStyle.Top,
                Height = bounds.Height / 2,
                Padding = new System.Windows.Forms.Padding(16),
            };
            button = new System.Windows.Forms.Button
            {
                Text = "Send report",
                Font = new Font("Segoe UI", 20, FontStyle.Bold),
                BackColor = ButtonColor,
                FlatStyle = System.Windows.Forms.FlatStyle.Flat,
                ForeColor = Color.White,
                Width = bounds.Width / 2,
                Height = bounds.Height / 4,
                Left = bounds.Width / 4,
                Top = bounds.Height / 2 + 10,
            };
            form.Controls.Add(button);
            form.Controls.Add(label);
            form.Shown += (_, _) => ready.Set();
            System.Windows.Forms.Application.Run(form);
        });
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.IsBackground = true;
        _thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(20)))
        {
            throw new TimeoutException("The target window did not appear.");
        }
        _form = form!;
        _button = button!;
        Handle = (IntPtr)_form.Invoke(() => _form.Handle);
    }

    public IntPtr Handle { get; }

    public bool IsForeground() => GetForegroundWindow() == Handle;

    /// <summary>True when the top-level window at the centre of <paramref name="area"/> (screen pixels) is this window.</summary>
    public bool IsTopAt(Rectangle area)
    {
        var hit = WindowFromPoint(new NativePoint { X = area.Left + area.Width / 2, Y = area.Top + area.Height / 2 });
        return hit != IntPtr.Zero && GetAncestor(hit, 2 /* GA_ROOT */) == Handle;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr WindowFromPoint(NativePoint point);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);

    /// <summary>Topmost and foreground, the way a user clicking into it would leave it.</summary>
    public void BringToFront(FlaUI.UIA3.UIA3Automation automation)
    {
        SetWindowPos(Handle, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040); // HWND_TOPMOST; NOSIZE | NOMOVE | SHOWWINDOW
        try
        {
            automation.FromHandle(Handle).AsWindow().SetForeground();
        }
        catch (Exception)
        {
            // Topmost alone still keeps it in view; AssertTargetOnTop checks the result.
        }
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr insertAfter, int x, int y, int cx, int cy, uint flags);

    public static TargetWindow Show(Rectangle bounds) => new(bounds);

    public Rectangle ScreenRectangle() => (Rectangle)_form.Invoke(() => _form.Bounds);

    public Rectangle ButtonScreenRectangle() => (Rectangle)_form.Invoke(() => _button.RectangleToScreen(_button.ClientRectangle));

    public void Dispose()
    {
        try
        {
            _form.Invoke(_form.Close);
        }
        catch (Exception)
        {
            // Already closed.
        }
        _thread.Join(TimeSpan.FromSeconds(5));
    }
}

/// <summary>Boo's annotation colours: Ring = AnnotationWindow.MarkColor; Pointer = AnnotationWindow.PointerColor.</summary>
internal static class ColourProbe
{
    public static readonly Color Ring = Color.FromArgb(243, 181, 155);
    public static readonly Color Pointer = Color.FromArgb(0x42, 0x54, 0xD6);

    public static bool Near(Color c, Color target) =>
        Math.Abs(c.R - target.R) <= 18 && Math.Abs(c.G - target.G) <= 18 && Math.Abs(c.B - target.B) <= 18;
}

/// <summary>
/// Boo's explanation overlay ("Boo screen explanation", AnnotationWindow) rendered from its OWN content with PrintWindow,
/// in memory only (never saved; the evidence screenshots come from BooAppFixture.CaptureScreen). Same method as PR #18's
/// Boo-window renderer, so it holds only Boo's marks, never the desktop: nothing else on screen can fake a ring or a
/// pointer. <see cref="ScreenRect"/> (physical pixels; this process is per-monitor DPI aware) maps pixels to the screen.
/// </summary>
internal sealed class OverlayRender : IDisposable
{
    public const string OverlayTitle = "Boo screen explanation";

    private readonly Bitmap _bitmap;

    private OverlayRender(Bitmap bitmap, Rectangle screenRect)
    {
        _bitmap = bitmap;
        ScreenRect = screenRect;
    }

    public Rectangle ScreenRect { get; }

    /// <summary>The visible overlay rendered now, or null when Boo shows none.</summary>
    public static OverlayRender? Capture(BooAppFixture boo)
    {
        try
        {
            foreach (var window in boo.App.GetAllTopLevelWindows(boo.Automation))
            {
                if (BooAppFixture.SafeName(window) != OverlayTitle || window.IsOffscreen) continue;
                var hwnd = window.Properties.NativeWindowHandle.ValueOrDefault;
                if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r) || r.Right <= r.Left || r.Bottom <= r.Top) continue;
                var rect = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                var bitmap = new Bitmap(rect.Width, rect.Height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                var rendered = false;
                using (var graphics = Graphics.FromImage(bitmap))
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        rendered = PrintWindow(hwnd, hdc, 2 /* PW_RENDERFULLCONTENT */);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }
                if (!rendered)
                {
                    bitmap.Dispose();
                    continue;
                }
                return new OverlayRender(bitmap, rect);
            }
        }
        catch (Exception)
        {
            // The overlay may close between the lookup and the render; the caller polls again.
        }
        return null;
    }

    /// <summary>Pixels of <paramref name="colour"/> inside <paramref name="search"/> (screen pixels), and their centre.</summary>
    public bool Find(Rectangle search, Color colour, int minPixels, out Point centre, out int pixels)
    {
        centre = Point.Empty;
        pixels = 0;
        var area = Rectangle.Intersect(search, ScreenRect);
        long sumX = 0, sumY = 0;
        for (var y = area.Top; y < area.Bottom; y++)
        {
            for (var x = area.Left; x < area.Right; x++)
            {
                if (!ColourProbe.Near(_bitmap.GetPixel(x - ScreenRect.Left, y - ScreenRect.Top), colour)) continue;
                sumX += x;
                sumY += y;
                pixels++;
            }
        }
        // A 3 px ring stroke yields hundreds of pixels and the pointer's fill dozens; a handful of hits is noise.
        if (pixels < minPixels) return false;
        centre = new Point((int)(sumX / pixels), (int)(sumY / pixels));
        return true;
    }

    public int Count(Rectangle area, Color colour)
    {
        Find(area, colour, int.MaxValue, out _, out var pixels);
        return pixels;
    }

    public void Dispose() => _bitmap.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left, Top, Right, Bottom;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out NativeRect rect);
}
