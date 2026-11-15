using System.IO;
using System.Text;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using Xunit.Abstractions;

namespace Boo.Demo.Tests;

/// <summary>
/// App-boundary regression for the VOICE completion of the fail-closed guard (MainWindow.CompleteRealtimeToolAsync
/// -> ActionPolicy.RealtimeToolResult), with no AI involved. Boo runs under BOO_QA_CAPTURE_FAULT=1
/// (FaultedCaptureFixture) and is given the mock's realtime key, so a typed request with speech enabled is carried
/// by the mock's wss://api.openai.com/v1/realtime session: the mock answers the text turn with one screen_help
/// function call, Boo resolves it through /v1/responses (the blind routes), and the mock logs every
/// function_call_output Boo returns to BOO_UI_ARTIFACTS\mock-requests.log.
/// 0 settings (realtime key, file root)
/// 1 blind click-only reply -> exactly one function result, the refusal text, at once; nothing on the preview
/// 2 blind mixed reply -> the write_file is offered and the call stays open; Cancel returns exactly one result.
/// A build that hands the voice completion the model's first proposed action instead of the offered one
/// (result = RealtimeToolResult(reply.Actions.FirstOrDefault(), ...)) parks the click-only call forever, so step 1
/// times out waiting for the result line.
/// </summary>
[TestCaseOrderer(StepOrderer.TypeName, StepOrderer.AssemblyName)]
public sealed class UnobservedScreenActionVoiceDemoTests : IClassFixture<FaultedCaptureFixture>
{
    private static readonly TimeSpan UiTimeout = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan SettleTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Must equal Boo.QaMockAi QaConstants.RealtimeKey: the only key the mock's realtime socket accepts.</summary>
    private const string RealtimeKey = "sk-qa-mock-realtime-key-000000000000000";
    private const string CaptureFaultMarker = "BOO_QA_CAPTURE_FAULT=1";
    private const string OutputLogMarker = "[realtime] function_call_output call_id=";
    private const string RefusalResult = "Boo proposed a screen action, but no screen observation backs it, so nothing was offered. Explain without taking an action.";
    private const string CancelledResult = "Action cancelled; no side effect was requested.";

    private static string? s_fileRoot;

    private readonly FaultedCaptureFixture _boo;
    private readonly ITestOutputHelper _output;

    public UnobservedScreenActionVoiceDemoTests(FaultedCaptureFixture boo, ITestOutputHelper output)
    {
        _boo = boo;
        _output = output;
    }

    private string MockLogPath => Path.Combine(_boo.ArtifactsDirectory, "mock-requests.log");

    [Fact, Step(0)]
    public void Step0_Settings_RealtimeKeyAndFileRoot()
    {
        Evidence("v0-settings", () =>
        {
            s_fileRoot = Path.Combine(Path.GetTempPath(), "boo-voice-blind-root-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(s_fileRoot);

            _boo.WaitForNotch(UiTimeout);
            var panel = _boo.EnsurePanel();

            var credential = Find(panel, "CredentialBox");
            credential.Focus();
            credential.Click();
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.KEY_A);
            Keyboard.Type(RealtimeKey);
            Thread.Sleep(200);

            SetText(panel, "FileRootBox", s_fileRoot);
            Find(panel, "SaveSettingsButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => StatusText(panel).StartsWith("Settings saved", StringComparison.Ordinal), UiTimeout),
                $"Status did not report 'Settings saved'. Status: '{StatusText(panel)}'");
            Assert.True(File.Exists(MockLogPath), $"The mock request log is missing at {MockLogPath}; the demo job starts the mock with --log there.");
            _boo.WriteObservation("v0-settings", $"fileRoot={s_fileRoot} mockLog={MockLogPath} status='{StatusText(panel)}'");
            _boo.CaptureScreen("v0-settings-saved");
        });
    }

    [Fact, Step(1)]
    public void Step1_VoiceBlindClickOnlyReply_ReturnsTheRefusalAtOnce_AndOffersNothing()
    {
        Evidence("v1-voice-blind-click", () =>
        {
            var panel = _boo.EnsurePanel();
            var before = OutputLines().Count;
            Ask(panel, "task qa-blind-click: press the button");

            Assert.True(
                BooAppFixture.WaitUntil(() => OutputLines().Count > before || VisibleOrNull(TryFind(panel, "ActionPreview")) is not null, UiTimeout),
                $"No function result reached the mock and nothing was previewed. Status: '{StatusText(panel)}' Capture: '{NameOfId(panel, "CaptureDetail")}' Listening: '{NameOfId(panel, "ListeningStatus")}'");
            Thread.Sleep(1000);
            var outputs = OutputLines().Skip(before).ToList();
            var captureDetail = NameOfId(panel, "CaptureDetail");
            var previewText = NameOfId(panel, "ActionPreviewText");
            _output.WriteLine($"CaptureDetail: {captureDetail}");
            _output.WriteLine($"Function results: {string.Join(" || ", outputs)}");
            _boo.CaptureScreen("v1-voice-blind-click");
            _boo.WriteObservation("v1-voice-blind-click", $"capture='{captureDetail}' preview='{previewText}' status='{StatusText(panel)}' results='{string.Join(" || ", outputs)}' transcript='{TranscriptText(panel)}'");

            Assert.Contains(CaptureFaultMarker, captureDetail, StringComparison.Ordinal);
            Assert.Null(VisibleOrNull(TryFind(panel, "ActionPreview")));
            Assert.Single(outputs);
            Assert.Equal(RefusalResult, OutputOf(outputs[0]));
            Assert.True(TranscriptHasBooReply(panel), "The transcript has no Boo answer row.");
            Assert.False(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null || OutputLines().Count > before + 1, SettleTimeout),
                $"A late preview or a second function result appeared: preview='{NameOfId(panel, "ActionPreviewText")}' results='{string.Join(" || ", OutputLines().Skip(before))}'");
        });
    }

    [Fact, Step(2)]
    public void Step2_VoiceBlindMixedReply_HoldsTheCallWhileTheWriteFileIsOffered_ThenCancelReturnsOneResult()
    {
        Evidence("v2-voice-blind-mixed", () =>
        {
            var panel = _boo.EnsurePanel();
            var before = OutputLines().Count;
            Ask(panel, "task qa-blind-mixed: do all three things");

            Assert.True(
                BooAppFixture.WaitUntil(() => VisibleOrNull(TryFind(panel, "ActionPreview")) is not null || OutputLines().Count > before, UiTimeout),
                $"Neither a preview nor a function result arrived. Status: '{StatusText(panel)}' Capture: '{NameOfId(panel, "CaptureDetail")}'");
            var previewText = NameOfId(panel, "ActionPreviewText");
            _boo.CaptureScreen("v2-voice-blind-mixed-preview");
            _boo.WriteObservation("v2-voice-blind-mixed", $"preview='{previewText}' status='{StatusText(panel)}' results='{string.Join(" || ", OutputLines().Skip(before))}'");

            Assert.NotNull(VisibleOrNull(TryFind(panel, "ActionPreview")));
            Assert.StartsWith("Write file", previewText, StringComparison.Ordinal);
            Assert.DoesNotContain("Step 1 of", previewText, StringComparison.Ordinal);
            Assert.Empty(OutputLines().Skip(before));

            Find(panel, "CancelButton").AsButton().Invoke();
            Assert.True(
                BooAppFixture.WaitUntil(() => OutputLines().Count > before, UiTimeout),
                $"Cancel returned no function result. Status: '{StatusText(panel)}'");
            Thread.Sleep(1000);
            var outputs = OutputLines().Skip(before).ToList();
            _output.WriteLine($"Function results after Cancel: {string.Join(" || ", outputs)}");
            Assert.Single(outputs);
            Assert.Equal(CancelledResult, OutputOf(outputs[0]));
            Assert.Null(VisibleOrNull(TryFind(panel, "ActionPreview")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(s_fileRoot!));
            _boo.CaptureScreen("v2-voice-blind-mixed-cancelled");
            _boo.WriteObservation("v2-voice-blind-mixed-cancelled", $"status='{StatusText(panel)}' results='{string.Join(" || ", outputs)}' fileRootEntries=0");
        });
    }

    /// <summary>Every function_call_output line the mock has logged so far (the file is still being appended to).</summary>
    private List<string> OutputLines()
    {
        var lines = new List<string>();
        if (!File.Exists(MockLogPath))
        {
            return lines;
        }
        using var stream = new FileStream(MockLogPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        while (reader.ReadLine() is { } line)
        {
            if (line.Contains(OutputLogMarker, StringComparison.Ordinal))
            {
                lines.Add(line);
            }
        }
        return lines;
    }

    private static string OutputOf(string logLine)
    {
        const string marker = " output=";
        var index = logLine.IndexOf(marker, StringComparison.Ordinal);
        return index < 0 ? string.Empty : logLine[(index + marker.Length)..];
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
