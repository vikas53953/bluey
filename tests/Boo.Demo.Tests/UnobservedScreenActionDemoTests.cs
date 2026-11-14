using System.IO;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.Demo.Tests;

/// <summary>
/// Launches its own Boo with BOO_QA_CAPTURE_FAULT=1 (Boo.Core.CaptureFaultPolicy): every per-request screen
/// capture fails before a frame is taken, so a task reply reaches MainWindow.RenderReply with no frame and
/// no control observation. The base fixture copies the test process environment into Boo's start info
/// AFTER the seed callback runs (ProcessStartInfo.Environment is read lazily on first access), so the
/// callback sets the variable and the constructor body clears it again once Boo has been launched, leaving
/// the other fixtures in this run unaffected. Test parallelisation is off for the assembly
/// (OrderedSteps.cs + xunit.runner.json), so Boo (single-instance) never runs twice.
/// </summary>
public sealed class FaultedCaptureFixture : BooAppFixture
{
    public const string Variable = "BOO_QA_CAPTURE_FAULT";

    public FaultedCaptureFixture()
        : base(_ => Environment.SetEnvironmentVariable(Variable, "1"))
    {
        Environment.SetEnvironmentVariable(Variable, null);
    }
}

/// <summary>
/// App-boundary regression for the fail-closed screen-action guard (ActionPolicy.WithoutUnobservedScreenActions
/// as consumed by MainWindow.RenderReply). Against the published Boo.exe and the mock api.openai.com:
/// 0 settings (fake key, file root)  1 blind mixed reply -> only the write_file is offered, no step queue
/// 2 blind click-only reply -> nothing is offered and the panel says why.
/// A build that drops the RenderReply filter (offered = reply.Actions) offers "Step 1 of 3: Click" in step 1
/// and a "Click" preview in step 2, so both steps fail on live UI Automation state.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class UnobservedScreenActionDemoTests : IClassFixture<FaultedCaptureFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    private const string FakeKey = "sk-qa-mock-key-000000000000000000000000";
    private const string CaptureFaultMarker = "BOO_QA_CAPTURE_FAULT=1";
    private const string NothingOfferedStatus = "Boo proposed a screen action, but no screen observation backs it. Nothing was offered.";

    private static string? s_fileRoot;

    private readonly FaultedCaptureFixture _boo;
    private readonly ITestOutputHelper _output;

    public UnobservedScreenActionDemoTests(FaultedCaptureFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    [Fact, Step(0)]
    public void Step0_Settings_FakeKeyAndFileRoot()
    {
        Evidence("b0-settings", () =>
        {
            s_fileRoot = Path.Combine(Path.GetTempPath(), "boo-blind-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(s_fileRoot);

            _boo.WaitForNotch(UiTimeout);
            var panel = _boo.EnsurePanel();

            var credential = Find(panel, "CredentialBox");
            credential.Focus();
            credential.Click();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(FakeKey);
            Thread.Sleep(200);

            SetText(panel, "FileRootBox", s_fileRoot);
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel).StartsWith("Settings saved", StringComparison.Ordinal), UiTimeout),
                $"Status did not report 'Settings saved'. Status: '{StatusText(panel)}'");
            _boo.WriteObservation("b0-settings", $"fileRoot={s_fileRoot} status='{StatusText(panel)}'");
            _boo.CaptureScreen("b0-settings-saved");
        });
    }

    [Fact, Step(1)]
    public void Step1_BlindMixedReply_OffersOnlyTheWriteFile_WithoutAStepQueue()
    {
        Evidence("b1-blind-mixed", () =>
        {
            var panel = _boo.EnsurePanel();
            Ask(panel, "task qa-blind-mixed: do all three things");

            Assert.True(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null || StatusText(panel) == NothingOfferedStatus, UiTimeout),
                $"No preview and no refusal arrived. Status: '{StatusText(panel)}' Capture: '{NameOfId(panel, "CaptureDetail")}'");
            var captureDetail = NameOfId(panel, "CaptureDetail");
            var previewText = NameOfId(panel, "ActionPreviewText");
            _output.WriteLine($"CaptureDetail: {captureDetail}");
            _output.WriteLine($"ActionPreviewText: {previewText}");
            _boo.CaptureScreen("b1-blind-mixed-preview");
            _boo.WriteObservation("b1-blind-mixed", $"capture='{captureDetail}' preview='{previewText}' status='{StatusText(panel)}' transcript='{TranscriptText(panel)}'");

            Assert.Contains(CaptureFaultMarker, captureDetail, StringComparison.Ordinal);
            Assert.NotNull(VisibleOrNull(TryFind(panel, "ActionPreview")));
            Assert.StartsWith("Write file", previewText, StringComparison.Ordinal);
            Assert.DoesNotContain("Step 1 of", previewText, StringComparison.Ordinal);
            Assert.DoesNotContain("Click", previewText, StringComparison.Ordinal);
            Assert.DoesNotContain("Type text", previewText, StringComparison.Ordinal);

            Find(panel, "CancelButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is null, UiTimeout),
                $"The preview did not close after Cancel. Status: '{StatusText(panel)}'");
            Assert.False(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null, SettleTimeout),
                $"A queued action was offered after Cancel: '{NameOfId(panel, "ActionPreviewText")}'");
            Assert.Empty(Directory.EnumerateFileSystemEntries(s_fileRoot!));
            _boo.CaptureScreen("b1-blind-mixed-cancelled");
            _boo.WriteObservation("b1-blind-mixed-cancelled", $"status='{StatusText(panel)}' fileRootEntries=0");
        });
    }

    [Fact, Step(2)]
    public void Step2_BlindClickOnlyReply_OffersNothing_AndSaysWhy()
    {
        Evidence("b2-blind-click", () =>
        {
            var panel = _boo.EnsurePanel();
            Ask(panel, "task qa-blind-click: press the button");

            Assert.True(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null || StatusText(panel) == NothingOfferedStatus, UiTimeout),
                $"No preview and no refusal arrived. Status: '{StatusText(panel)}' Capture: '{NameOfId(panel, "CaptureDetail")}'");
            var captureDetail = NameOfId(panel, "CaptureDetail");
            var previewText = NameOfId(panel, "ActionPreviewText");
            _boo.CaptureScreen("b2-blind-click");
            _boo.WriteObservation("b2-blind-click", $"capture='{captureDetail}' preview='{previewText}' status='{StatusText(panel)}' transcript='{TranscriptText(panel)}'");

            Assert.Contains(CaptureFaultMarker, captureDetail, StringComparison.Ordinal);
            Assert.True(TranscriptHasBooReply(panel), "The transcript has no Boo answer row.");
            Assert.Null(VisibleOrNull(TryFind(panel, "ActionPreview")));
            Assert.Equal(NothingOfferedStatus, StatusText(panel));
            Assert.False(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null, SettleTimeout),
                $"A screen action was offered without an observation: '{NameOfId(panel, "ActionPreviewText")}'");
        });
    }

    private void Ask(Window panel, string text)
    {
        SetText(panel, "InputBox", text);
        Find(panel, "SendButton").AsButton().Invoke();
        _output.WriteLine($"Sent: {text}");
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
}
