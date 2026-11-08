using System.IO;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.Demo.Tests;

/// <summary>
/// Demo story against the published Boo.exe while a mock OpenAI server (hosts-file impersonation of
/// api.openai.com) answers. Steps run in order on one live app:
/// 0 settings (fake key, workspace copy)  1 explain + pointer ring  2 edit -> Apply -> Undo
/// 3 research sources  4 focus -> nudge -> "Back to it" -> happy face.
/// Every step saves screenshots (NN-*.png) and observations (NN-*.txt) under BOO_UI_ARTIFACTS and
/// asserts on live UI Automation state or files, never on Boo's own text alone.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class DemoTests : IClassFixture<BooAppFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private const string FakeKey = "sk-qa-mock-key-000000000000000000000000";
    private const string EditMarker = "QA-EDIT-APPLIED";
    private const int RingToleranceDip = 40;

    // Shared across steps (one class instance per test, so static).
    private static string? s_workspace;
    private static byte[]? s_originalXaml;

    private readonly BooAppFixture _boo;
    private readonly ITestOutputHelper _output;

    public DemoTests(BooAppFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    private static string FixtureXamlPath(string workspace) => Path.Combine(workspace, "MainWindow.xaml");

    [Fact, Step(0)]
    public void Step0_Settings_FakeKeyAndWorkspaceCopy()
    {
        Evidence("00-settings", () =>
        {
            s_workspace = PrepareWorkspaceCopy();
            s_originalXaml = File.ReadAllBytes(FixtureXamlPath(s_workspace));
            Assert.DoesNotContain(EditMarker, System.Text.Encoding.UTF8.GetString(s_originalXaml));

            _boo.WaitForNotch(UiTimeout);
            var panel = _boo.EnsurePanel();
            _boo.CaptureScreen("00-panel-open");

            // PasswordBox: UIA exposes it as an Edit without a readable Value, so type after focusing.
            var credential = Find(panel, "CredentialBox");
            credential.Focus();
            credential.Click();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(FakeKey);
            Thread.Sleep(200);

            SetText(panel, "FileRootBox", s_workspace);
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel).StartsWith("Settings saved", StringComparison.Ordinal), UiTimeout),
                $"Status did not report 'Settings saved'. Status: '{StatusText(panel)}'");
            Assert.True(
                BooAppFixture.WaitUntil(() => File.Exists(_boo.SettingsPath) && BooAppFixture.ReadShared(_boo.SettingsPath).Contains("Boo.QaWorkspace", StringComparison.OrdinalIgnoreCase), UiTimeout),
                $"settings.json did not record the workspace FileRoot: {ReadOrMissing(_boo.SettingsPath)}");

            var display = TryFind(panel, "DisplayBox")?.AsComboBox();
            var displayText = display?.SelectedItem?.Text ?? display?.Value ?? "<none>";
            _boo.WriteObservation("00-settings", $"workspace={s_workspace} status='{StatusText(panel)}' display='{displayText}' settings={ReadOrMissing(_boo.SettingsPath)}");
            _boo.CaptureScreen("00-settings-saved");
        });
    }

    [Fact, Step(1)]
    public void Step1_Explain_DrawsPointerRingAtExpectedCoordinate()
    {
        Evidence("01-explain", () =>
        {
            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);
            Ask(panel, "Explain what is on my screen");

            var expected = ExpectedRingCentre();
            _output.WriteLine($"Expected ring centre (physical px): {expected}");

            // The AnnotationWindow is a separate click-through HWND (no title, WS_EX_TOOLWINDOW). Wait for
            // either a transcript answer or the annotation outcome text, then give the fly + stroke time.
            Assert.True(
                BooAppFixture.WaitUntil(() => TranscriptHasBooReply(panel) || TryFind(panel, "AnnotationDetail") is not null, UiTimeout),
                $"No answer arrived. Status: '{StatusText(panel)}'");
            var detail = _boo.WaitFor(() => TryFind(panel, "AnnotationDetail"), UiTimeout, "the annotation outcome text");
            var detailText = BooAppFixture.SafeName(detail);
            _output.WriteLine($"AnnotationDetail: {detailText}");
            Thread.Sleep(1800); // Fly (<=620ms) + Land (120ms) + DrawRing (620ms) + margin, inside the 10 s hold.

            var shotPath = _boo.CaptureScreen("01-explain-ring-fullscreen");
            var transcript = TranscriptText(panel);
            _boo.WriteObservation("01-explain", $"expected={expected} annotationDetail='{detailText}' status='{StatusText(panel)}' transcript='{transcript}'");

            Assert.StartsWith("Screen explanation is shown", detailText, StringComparison.Ordinal);
            Assert.True(TranscriptHasBooReply(panel), "The transcript has no Boo answer row.");

            // UIA: the overlay window has no AutomationId on the Ellipse, so probe the screenshot for the
            // mark colour (RGB 243,181,155, 3 px stroke) near the expected centre.
            var found = ProbeRing(shotPath, expected, RingToleranceDip + 60, out var centre);
            _boo.WriteObservation("01-explain-ring-probe", $"found={found} probedCentre={centre} expected={expected} distance={Distance(centre, expected):0}");
            Assert.True(found, $"No ring-coloured pixels found near {expected} in {shotPath}.");
            Assert.True(Distance(centre, expected) <= RingToleranceDip,
                $"Ring centre {centre} is {Distance(centre, expected):0}px from the expected {expected} (tolerance {RingToleranceDip}).");
        });
    }

    [Fact, Step(2)]
    public void Step2_Edit_ApplyThenUndo_RoundTripsFixtureFile()
    {
        Evidence("02-edit", () =>
        {
            Assert.NotNull(s_workspace);
            var xamlPath = FixtureXamlPath(s_workspace!);
            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);

            Ask(panel, "Edit the title in MainWindow.xaml");
            var preview = _boo.WaitFor(() => VisibleOrNull(TryFind(panel, "ActionPreview")), UiTimeout, "the ActionPreview border");
            var previewText = NameOfId(panel, "ActionPreviewText");
            _output.WriteLine($"ActionPreviewText: {previewText}");
            _boo.CaptureScreen("02-edit-preview");
            Assert.Null(VisibleOrNull(TryFind(panel, "ActionBlockedReason")));

            Find(panel, "ApproveButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => File.ReadAllText(xamlPath).Contains(EditMarker, StringComparison.Ordinal), UiTimeout),
                $"{xamlPath} does not contain '{EditMarker}' after Approve. Status: '{StatusText(panel)}'");
            _boo.CaptureScreen("02-edit-applied");
            _boo.WriteObservation("02-edit-applied", $"preview='{previewText}' status='{StatusText(panel)}' file={File.ReadAllText(xamlPath)}");

            // Undo is handled locally by RequestIntent ("undo" -> RequestKind.Undo): Boo plans the reverse
            // step from FileUndoJournal (a write_file of the previous text) and shows it in the same
            // ActionPreview for approval. Nothing changes until Approve.
            Ask(panel, "undo");
            _boo.WaitFor(() => VisibleOrNull(TryFind(panel, "ActionPreview")), UiTimeout, "the Undo ActionPreview");
            var undoPreviewText = NameOfId(panel, "ActionPreviewText");
            _boo.CaptureScreen("02-undo-preview");
            Assert.Null(VisibleOrNull(TryFind(panel, "ActionBlockedReason")));
            Find(panel, "ApproveButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => File.ReadAllBytes(xamlPath).AsSpan().SequenceEqual(s_originalXaml), UiTimeout),
                $"File did not return to its original bytes after Undo. Preview: '{undoPreviewText}' Status: '{StatusText(panel)}' Transcript: '{TranscriptText(panel)}'");
            _boo.CaptureScreen("02-undo-done");
            _boo.WriteObservation("02-undo-result", $"undoPreview='{undoPreviewText}' fileReverted=true status='{StatusText(panel)}'");
            _boo.WriteObservation("02-undo-done", $"status='{StatusText(panel)}' transcript='{TranscriptText(panel)}'");
        });
    }

    [Fact, Step(3)]
    public void Step3_Research_ShowsThreeExampleSourceLinks()
    {
        Evidence("03-research", () =>
        {
            var panel = _boo.EnsurePanel();
            Ask(panel, "Research the Boo project and give sources");
            List<AutomationElement> links;
            try
            {
                links = _boo.WaitFor(() => SourceLinks(panel) is { Count: >= 3 } found ? found : null, UiTimeout, "at least 3 source links");
            }
            catch (TimeoutException)
            {
                // Say what the window did show, so the next run's log explains itself.
                var hyperlinks = panel.FindAllDescendants().Where(e => e.ControlType == ControlType.Hyperlink).Select(BooAppFixture.SafeName).ToList();
                _boo.WriteObservation("03-research-timeout",
                    $"hyperlinks={string.Join(" | ", hyperlinks)} sourceStatus='{NameOfId(panel, "SourceStatusText")}' status='{StatusText(panel)}' transcript='{TranscriptText(panel)}'");
                _boo.CaptureScreen("03-research-timeout");
                throw;
            }
            _boo.CaptureScreen("03-research-sources");
            var names = links.Select(BooAppFixture.SafeName).ToList();
            _boo.WriteObservation("03-research", $"links={string.Join(" | ", names)} sourceStatus='{NameOfId(panel, "SourceStatusText")}'");
            // Boo names each link "Open source <title>" (MainWindow.RenderSources); the mock titles are "QA source A/B/C".
            Assert.True(names.Count(n => n.Contains("example.com", StringComparison.OrdinalIgnoreCase) || n.Contains("QA source", StringComparison.Ordinal)) >= 3,
                $"Expected 3 source links, got: {string.Join(" | ", names)}");
        });
    }

    [Fact, Step(4)]
    public void Step4_Focus_NudgeThenBackToIt_EndsHappy()
    {
        Evidence("04-focus", () =>
        {
            var panel = _boo.EnsurePanel();
            EnsureDisplaySelected(panel);
            var dev = Find(panel, "DeveloperModeBox").AsCheckBox();
            dev.IsChecked = true;
            SetText(panel, "FocusTaskBox", "Demo focus");
            // 3 minutes at the 4x Developer Mode clock = ~45 real seconds: room for checks at 10, 20 and 30 s (the mock
            // answers off_task with P=0.96 three times; NudgePolicy nudges once two of them are 40 focus-seconds apart)
            // before the session ends. 1 minute (15 real seconds) only ever fitted a single check; two P=0.90 answers
            // exactly 40 focus-seconds apart fell short of the 44 s the policy asks for at that strength.
            SetText(panel, "FocusDurationBox", "3");
            Find(panel, "FocusStartButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => NameOfId(panel, "FocusStatus").StartsWith("Running", StringComparison.Ordinal), UiTimeout),
                $"FocusStatus did not become Running: '{NameOfId(panel, "FocusStatus")}'");
            _boo.CaptureScreen("04-focus-running");

            // Hide the panel so the notch is photographed alone; first check is due after 10 real seconds.
            Find(panel, "CloseButton").AsButton().Invoke();
            var seen = new List<string>();
            var nudge = _boo.WaitFor(() =>
            {
                var notch = _boo.FindNotch();
                if (notch is null) return null;
                var state = ItemStatus(notch);
                if (seen.Count == 0 || seen[^1] != state) seen.Add(state);
                return notch.FindFirstDescendant(cf => cf.ByAutomationId("NudgeBackButton")) ?? (state == "Nudge" ? notch : null);
            }, TimeSpan.FromSeconds(150), "the focus nudge on the notch"); // two off-task checks 10 s apart plus the nudge round trip; run 2 saw one check in 60 s
            _boo.CaptureScreen("04-nudge-fullscreen");
            var notchWindow = _boo.WaitForNotch(UiTimeout);
            _boo.CaptureRectangle("04-nudge-notch", Inflate(notchWindow.BoundingRectangle, 60));
            _boo.WriteObservation("04-nudge", $"states={string.Join(">", seen)} notch={notchWindow.BoundingRectangle} help='{HelpText(notchWindow)}'");
            Assert.Contains("Nudge", seen);

            var back = notchWindow.FindFirstDescendant(cf => cf.ByAutomationId("NudgeBackButton"))
                ?? notchWindow.FindFirstDescendant(cf => cf.ByName("Back to it"));
            Assert.NotNull(back);
            back!.AsButton().Invoke();

            // The session ends ~45 real seconds after Start; the happy face is a 2.5 s moment, so poll for it.
            var sawHappy = BooAppFixture.WaitUntil(() =>
            {
                var notch = _boo.FindNotch();
                if (notch is null) return false;
                var state = ItemStatus(notch);
                if (seen[^1] != state) seen.Add(state);
                return state == "Happy";
            }, UiTimeout);
            _boo.CaptureScreen("04-happy-fullscreen");
            if (_boo.FindNotch() is { } n2)
            {
                _boo.CaptureRectangle("04-happy-notch", Inflate(n2.BoundingRectangle, 60));
            }
            BooAppFixture.PressOpenShortcut();
            panel = _boo.WaitForPanel(UiTimeout);
            var ended = BooAppFixture.WaitUntil(() => NameOfId(panel, "FocusStatus").StartsWith("Ended", StringComparison.Ordinal), UiTimeout);
            _boo.WriteObservation("04-happy", $"sawHappy={sawHappy} states={string.Join(">", seen)} focusStatus='{NameOfId(panel, "FocusStatus")}' ended={ended}");
            _boo.CaptureScreen("04-focus-ended");
            Assert.True(sawHappy, $"Notch never reported ItemStatus 'Happy'. States seen: {string.Join(">", seen)}");
            Assert.True(ended, $"Focus session did not end: '{NameOfId(panel, "FocusStatus")}'");
        });
    }

    // ---- helpers ------------------------------------------------------------------------------

    private static string PrepareWorkspaceCopy()
    {
        var configured = Environment.GetEnvironmentVariable("BOO_DEMO_WORKSPACE");
        var source = !string.IsNullOrWhiteSpace(configured)
            ? Path.GetFullPath(configured)
            : Path.Combine(FindRepoRoot(), "tests", "fixtures", "native", "Boo.QaWorkspace");
        if (!Directory.Exists(source))
        {
            throw new DirectoryNotFoundException($"QA workspace fixture not found at '{source}' (set BOO_DEMO_WORKSPACE).");
        }
        var copy = Path.Combine(Path.GetTempPath(), "boo-demo-ws-" + Guid.NewGuid().ToString("N"), "Boo.QaWorkspace");
        Directory.CreateDirectory(copy);
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(source, file);
            if (relative.StartsWith("bin", StringComparison.OrdinalIgnoreCase) || relative.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            var target = Path.Combine(copy, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
        return copy;
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Boo.sln")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? Directory.GetCurrentDirectory();
    }

    private static Point ExpectedRingCentre()
    {
        var fx = ParseFraction("BOO_DEMO_RING_X", 0.25);
        var fy = ParseFraction("BOO_DEMO_RING_Y", 0.40);
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, 1920, 1080);
        return new Point(screen.Left + (int)Math.Round(screen.Width * fx), screen.Top + (int)Math.Round(screen.Height * fy));
    }

    private static double ParseFraction(string name, double fallback) =>
        double.TryParse(Environment.GetEnvironmentVariable(name), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 0 and <= 1 ? v : fallback;

    /// <summary>Finds pixels close to the mark colour (243,181,155) within <paramref name="radius"/> of <paramref name="expected"/> and returns their centroid.</summary>
    private static bool ProbeRing(string screenshotPath, Point expected, int radius, out Point centre)
    {
        centre = expected;
        if (!File.Exists(screenshotPath)) return false;
        using var bitmap = new Bitmap(screenshotPath);
        var screen = System.Windows.Forms.Screen.PrimaryScreen?.Bounds ?? new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var virtualOrigin = System.Windows.Forms.SystemInformation.VirtualScreen.Location;
        long sumX = 0, sumY = 0, count = 0;
        var x0 = Math.Max(0, expected.X - virtualOrigin.X - radius);
        var y0 = Math.Max(0, expected.Y - virtualOrigin.Y - radius);
        var x1 = Math.Min(bitmap.Width - 1, expected.X - virtualOrigin.X + radius);
        var y1 = Math.Min(bitmap.Height - 1, expected.Y - virtualOrigin.Y + radius);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                var c = bitmap.GetPixel(x, y);
                if (Math.Abs(c.R - 243) <= 18 && Math.Abs(c.G - 181) <= 18 && Math.Abs(c.B - 155) <= 18)
                {
                    sumX += x; sumY += y; count++;
                }
            }
        }
        if (count < 12) return false; // a 3 px ring stroke yields hundreds of pixels; a few hits are noise.
        centre = new Point((int)(sumX / count) + virtualOrigin.X, (int)(sumY / count) + virtualOrigin.Y);
        _ = screen;
        return true;
    }

    private static double Distance(Point a, Point b) => Math.Sqrt(Math.Pow(a.X - b.X, 2) + Math.Pow(a.Y - b.Y, 2));

    private static Rectangle Inflate(Rectangle r, int by) =>
        new(Math.Max(0, r.Left - by), Math.Max(0, r.Top - by), r.Width + 2 * by, r.Height + 2 * by);

    private void Ask(Window panel, string text)
    {
        SetText(panel, "InputBox", text);
        Find(panel, "SendButton").AsButton().Invoke();
        _output.WriteLine($"Sent: {text}");
    }

    /// <summary>Explain/edit/undo need a captured frame, which needs a selected display; pick the first if none is.</summary>
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

    private static bool TranscriptHasBooReply(Window panel)
    {
        var list = TryFind(panel, "TranscriptList");
        return list is not null && list.FindAllDescendants().Any(e => BooAppFixture.SafeName(e).StartsWith("Boo", StringComparison.Ordinal));
    }

    private static string TranscriptText(Window panel)
    {
        var list = TryFind(panel, "TranscriptList");
        return list is null ? string.Empty : string.Join(" || ", list.FindAllDescendants().Select(BooAppFixture.SafeName).Where(s => s.Length > 0));
    }

    private static List<AutomationElement> SourceLinks(Window panel)
    {
        // SourceLinksList is a bare StackPanel: WPF gives panels no automation peer, so it is not in the
        // UIA tree and a lookup by its AutomationId finds nothing. The Hyperlink inlines Boo adds are
        // (HyperlinkAutomationPeer), so search the whole companion window for them instead.
        return panel.FindAllDescendants()
            .Where(e => e.ControlType is ControlType.Hyperlink or ControlType.Button or ControlType.Text)
            .Where(e => BooAppFixture.SafeName(e).Contains("http", StringComparison.OrdinalIgnoreCase) || BooAppFixture.SafeName(e).Contains("example.com", StringComparison.OrdinalIgnoreCase) || BooAppFixture.SafeName(e).StartsWith("Open source ", StringComparison.Ordinal))
            .ToList();
    }

    private static AutomationElement? VisibleOrNull(AutomationElement? element)
    {
        try
        {
            return element is not null && !element.IsOffscreen && element.BoundingRectangle.Width > 0 ? element : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string ItemStatus(AutomationElement element)
    {
        try
        {
            return element.Properties.ItemStatus.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private static string HelpText(AutomationElement element)
    {
        try
        {
            return element.Properties.HelpText.ValueOrDefault ?? string.Empty;
        }
        catch (Exception)
        {
            return string.Empty;
        }
    }

    private void Evidence(string step, Action body)
    {
        try
        {
            body();
        }
        catch (Exception ex)
        {
            var shot = _boo.CaptureScreen(step + "-FAILED");
            _boo.WriteObservation(step + "-FAILED", ex.ToString());
            _output.WriteLine($"Failure screenshot: {shot}");
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
        Assert.True(
            BooAppFixture.WaitUntil(() => string.Equals(box.Text, value, StringComparison.OrdinalIgnoreCase), UiTimeout),
            $"Could not set {automationId} to {value}; it reads '{box.Text}'.");
    }

    private static string StatusText(Window panel) => NameOfId(panel, "StatusText");

    private static string NameOfId(Window panel, string automationId) =>
        TryFind(panel, automationId) is { } e ? BooAppFixture.SafeName(e) : string.Empty;

    private static string ReadOrMissing(string path) => File.Exists(path) ? BooAppFixture.ReadShared(path) : "<missing>";
}
