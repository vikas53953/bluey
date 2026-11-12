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
            Assert.Null(_boo.FindPanel()); // Boo starts with only the notch.
            _boo.CaptureScreen("j0-before-hotkey");

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
            _boo.CaptureScreen("j1-before-hover");

            Mouse.MoveTo(hover);
            var clock = Stopwatch.StartNew();
            var revealed = BooAppFixture.WaitUntil(() => NotchState() is { Length: > 0 } s && s != "Hidden", RevealTimeout);
            var revealMs = clock.ElapsedMilliseconds;
            var state = NotchState();
            var notch = _boo.FindNotch();
            if (notch is not null) _boo.CaptureRectangle("j1-hover-notch", Inflate(notch.BoundingRectangle, 80));
            _boo.CaptureScreen("j1-hover-revealed");

            Mouse.MoveTo(away);
            clock.Restart();
            var retracted = BooAppFixture.WaitUntil(() => NotchState() == "Hidden", RetractTimeout);
            _boo.WriteObservation("j1-hover", $"notch={rect} hover={hover} revealed={revealed} state='{state}' revealMs={revealMs} retracted={retracted} retractMs={clock.ElapsedMilliseconds}");
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
            Ask(panel, "What is the capital of France? Answer with just the city name.");
            var reply = WaitForNewBooRow(panel, before);
            _boo.CaptureScreen("j2-chat-answer");
            _boo.WriteObservation("j2-chat", $"provider={AiProviders.DisplayName(LiveAppFixture.Config.TextProvider)} reply='{Shorten(reply, 400)}' status='{StatusText(panel)}'");
            Assert.False(reply.StartsWith("QA-", StringComparison.Ordinal), $"The answer came from the QA mock: '{reply}'");
            Assert.Contains("Paris", reply, StringComparison.OrdinalIgnoreCase);
        });
    }

    [Fact, Step(3)]
    public void J3_ScreenExplain_PointerAndRingLandOnTheTarget()
    {
        Evidence("j3-explain", () =>
        {
            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);
            s_target ??= TargetWindow.Show(PlaceTargetAwayFrom(panel.BoundingRectangle));
            Thread.Sleep(800);
            var targetRect = s_target.ButtonScreenRectangle();
            var windowRect = s_target.ScreenRectangle();
            var beforePath = _boo.CaptureScreen("j3-before-ask");

            var before = BooRows(panel).Count;
            Ask(panel, "Explain the window titled 'Boo live target' and point at its big 'Send report' button.");
            var detail = _boo.WaitFor(() => NonEmpty(NameOfId(panel, "AnnotationDetail")), AnswerTimeout, "the screen-explanation outcome line");
            Thread.Sleep(1800); // Fly (<=620 ms) + land (120 ms) + ring (620 ms) + margin, inside the 10 s hold.
            var afterPath = _boo.CaptureScreen("j3-explain-ring");
            var reply = BooRows(panel).Skip(before).LastOrDefault() ?? string.Empty;

            // The ring colour is AnnotationWindow.MarkColor (243,181,155). Only pixels that CHANGED between the two shots
            // and match it count, so peach pixels that were already on screen cannot fake a ring.
            var search = Inflate(windowRect, 160);
            var found = RingProbe.FindNewRing(beforePath, afterPath, search, out var centre, out var pixels);
            var onButton = Inflate(targetRect, 60).Contains(centre);
            var onWindow = Inflate(windowRect, 20).Contains(centre);
            _boo.WriteObservation("j3-explain",
                $"detail='{detail}' ringFound={found} ringPixels={pixels} ringCentre={centre} button={targetRect} window={windowRect} " +
                $"onButton={onButton} onWindow={onWindow}\nreply='{Shorten(reply, 600)}'\nstatus='{StatusText(panel)}'");

            Assert.StartsWith("Screen explanation is shown", detail, StringComparison.Ordinal);
            Assert.True(found, $"No new ring-coloured pixels near the target window {windowRect} (search {search}). See j3-explain-ring.png.");
            Assert.True(onWindow, $"The ring centre {centre} is not on the target window {windowRect}.");
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
            try
            {
                var panel = _boo.EnsurePanel();
                EnsureDisplaySelected(panel);
                s_target ??= TargetWindow.Show(PlaceTargetAwayFrom(panel.BoundingRectangle));

                // Developer Mode is Boo's existing test clock: 4x focus time, a check every 10 real seconds. A 25-minute
                // session therefore takes 375 real seconds. A 25-minute session on the real-time clock stays a manual check.
                var dev = Find(panel, "DeveloperModeBox").AsCheckBox();
                dev.IsChecked = true;
                SetText(panel, "FocusTaskBox", FocusTask);
                SetText(panel, "FocusDurationBox", FocusMinutes.ToString(System.Globalization.CultureInfo.InvariantCulture));
                var clock = Stopwatch.StartNew();
                Find(panel, "FocusStartButton").AsButton().Invoke();
                Assert.True(BooAppFixture.WaitUntil(() => NameOfId(panel, "FocusStatus").StartsWith("Running", StringComparison.Ordinal), UiTimeout),
                    $"Focus did not start: '{NameOfId(panel, "FocusStatus")}'");
                _boo.CaptureScreen("j6-focus-running");
                Find(panel, "CloseButton").AsButton().Invoke();

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
                        _boo.CaptureScreen($"j6-nudge-{nudges}");
                        back.AsButton().Invoke();
                    }
                    return state == "Happy";
                }, TimeSpan.FromMinutes(9));
                var realSeconds = clock.Elapsed.TotalSeconds;
                _boo.CaptureScreen("j6-focus-happy");
                if (_boo.FindNotch() is { } n) _boo.CaptureRectangle("j6-focus-happy-notch", Inflate(n.BoundingRectangle, 80));

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
                    $"task='{FocusTask}' minutes={FocusMinutes} developerMode=true realSeconds={realSeconds:0} sawHappy={sawHappy} nudges={nudges}\n" +
                    $"states={string.Join(">", states)}\nfocusStatus='{NameOfId(panel, "FocusStatus")}'\nhistory='{Shorten(history, 600)}'");

                Assert.True(sawHappy, $"The notch never showed the happy face. States: {string.Join(">", states)}");
                Assert.True(complete, $"The session did not complete: '{NameOfId(panel, "FocusStatus")}'");
                // 25 focus-minutes at 4x is 375 real seconds; allow for check round trips and UI polling.
                Assert.InRange(realSeconds, 330, 540);
                Assert.True(historyRow is not null, "Session history shows no completed 25 min row.");
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

    /// <summary>Puts the target window on the half of the primary screen that the Boo panel does not cover.</summary>
    private static Rectangle PlaceTargetAwayFrom(Rectangle panel)
    {
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        var width = Math.Min(760, screen.Width * 2 / 5);
        var height = Math.Min(520, screen.Height / 2);
        var panelCentre = panel.Left + panel.Width / 2;
        var left = panelCentre > screen.Left + screen.Width / 2 ? screen.Left + 40 : screen.Right - width - 40;
        return new Rectangle(left, screen.Top + screen.Height / 4, width, height);
    }

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
            _boo.WriteObservation(step + "-FAILED", ex.ToString());
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
    internal static void Initialize() => SetProcessDpiAwarenessContext(new IntPtr(-4)); // PER_MONITOR_AWARE_V2

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
}

/// <summary>
/// A plain external window with one big, labelled button: a target whose position the test knows, so the pointer and
/// ring can be checked against it. It is owned by this test process, so closing it never touches the user's apps.
/// </summary>
internal sealed class TargetWindow : IDisposable
{
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
                BackColor = Color.FromArgb(0, 90, 200),
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
    }

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

/// <summary>Finds Boo's ring (AnnotationWindow.MarkColor, RGB 243,181,155) in a screenshot by comparing it with an earlier one.</summary>
internal static class RingProbe
{
    public static bool FindNewRing(string beforePath, string afterPath, Rectangle search, out Point centre, out int pixels)
    {
        centre = Point.Empty;
        pixels = 0;
        if (!File.Exists(beforePath) || !File.Exists(afterPath)) return false;
        using var before = new Bitmap(beforePath);
        using var after = new Bitmap(afterPath);
        var origin = System.Windows.Forms.SystemInformation.VirtualScreen.Location;
        var x0 = Math.Max(0, search.Left - origin.X);
        var y0 = Math.Max(0, search.Top - origin.Y);
        var x1 = Math.Min(Math.Min(after.Width, before.Width) - 1, search.Right - origin.X);
        var y1 = Math.Min(Math.Min(after.Height, before.Height) - 1, search.Bottom - origin.Y);
        long sumX = 0, sumY = 0;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var c = after.GetPixel(x, y);
                if (!IsMark(c) || IsMark(before.GetPixel(x, y))) continue;
                sumX += x;
                sumY += y;
                pixels++;
            }
        }
        if (pixels < 12) return false; // a 3 px ring stroke yields hundreds of pixels; a few hits are noise.
        centre = new Point((int)(sumX / pixels) + origin.X, (int)(sumY / pixels) + origin.Y);
        return true;
    }

    private static bool IsMark(Color c) => Math.Abs(c.R - 243) <= 18 && Math.Abs(c.G - 181) <= 18 && Math.Abs(c.B - 155) <= 18;
}
