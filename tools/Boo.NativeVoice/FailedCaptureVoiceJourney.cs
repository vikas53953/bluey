using System.IO;
using System.Text.Json;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace Boo.NativeVoice;

/// <summary>
/// Real-provider acceptance for the fail-closed screen-action guard on the published Boo.exe (feature map A7): Boo runs
/// with speech on, the real OpenAI key and BOO_QA_CAPTURE_FAULT=1 (FailedCaptureVoiceFixture). A typed explicit task
/// is carried by Boo's own OpenAI Realtime session; the real model calls screen_help, Boo's capture fails before a
/// frame is taken, the structured reply comes back from the real /v1/responses with no observation, and
/// MainWindow.RenderReply + ActionPolicy.RealtimeToolResult decide what is offered and what the model is told. Then
/// the real model speaks its final answer through the virtual playback device.
/// Holds whatever the model proposes: a click or type_text is never previewed; anything else offered is cancelled
/// (one cancellation result); the voice turn still completes ("Reply complete.") and audio is rendered.
/// The tool call itself is required: without it the guard was never reached and the run says so as TEST SETUP.
/// One typed turn, no retries; virtual audio devices only (physical speakers UNVERIFIED).
/// </summary>
public sealed class FailedCaptureVoiceJourney : IClassFixture<FailedCaptureVoiceFixture>
{
    private const string Prompt = "task click the Start button at the left end of my Windows taskbar. This is a bounded screen task: call screen_help with mode task now, do not ask a clarifying question.";
    private const string CaptureFaultMarker = "BOO_QA_CAPTURE_FAULT=1";
    private const string NothingOfferedStatus = "Boo proposed a screen action, but no screen observation backs it. Nothing was offered.";
    private const string NoActionStatus = "Reply complete. No action was committed.";
    private const string HandedToVoiceStatus = "Sent to the voice conversation; waiting for the response…";
    /// <summary>The bounded 128-token follow-up may be cut by the cap; the turn has still ended.</summary>
    private const string IncompleteStatus = "Realtime response incomplete.";
    private static readonly TimeSpan TurnTimeout = TimeSpan.FromSeconds(120);

    private readonly FailedCaptureVoiceFixture _boo;
    public FailedCaptureVoiceJourney(FailedCaptureVoiceFixture boo) => _boo = boo;

    [Fact]
    public void PublishedApp_TypedTaskWithFailedCapture_RealVoiceTurnCompletesWithoutAScreenAction()
    {
        var statuses = new List<string>();
        var previews = new List<string>();
        var cancelled = false;
        try
        {
            _boo.WaitForNotch(TimeSpan.FromSeconds(30));
            var panel = _boo.EnsurePanel();
            var input = panel.FindFirstDescendant(cf => cf.ByAutomationId("InputBox"))!.AsTextBox();
            input.Text = Prompt;
            Assert.True(BooAppFixture.WaitUntil(() => string.Equals(input.Text, Prompt, StringComparison.OrdinalIgnoreCase), TimeSpan.FromSeconds(10)),
                $"TEST SETUP: could not type the task; the box reads '{input.Text}'.");
            panel.FindFirstDescendant(cf => cf.ByAutomationId("SendButton"))!.AsButton().Invoke();

            var started = DateTime.UtcNow;
            var sawReplyComplete = false;
            while (DateTime.UtcNow - started < TurnTimeout)
            {
                Observe(panel, statuses);
                var preview = VisiblePreviewText(panel);
                if (preview is not null)
                {
                    if (previews.Count == 0 || previews[^1] != preview) previews.Add(preview);
                    if (!cancelled && !IsScreenAction(preview))
                    {
                        _boo.CaptureScreen("fail-closed-offered");
                        panel.FindFirstDescendant(cf => cf.ByAutomationId("CancelButton"))!.AsButton().Invoke();
                        cancelled = true;
                    }
                }
                var toolReached = ToolReached(statuses, panel);
                if (toolReached && statuses.Count > 0 && TurnEnded(statuses[^1])
                    && Spoken(out _, out _) && VisiblePreviewText(panel) is null)
                {
                    sawReplyComplete = true;
                    break;
                }
                Thread.Sleep(150);
            }

            var rows = Rows(panel);
            var captureDetail = NameOfId(panel, "CaptureDetail");
            var toolCalled = ToolReached(statuses, panel);
            Spoken(out var rendered, out var nonzero);
            _boo.CaptureScreen("fail-closed-reply");
            _boo.WriteObservation("fail-closed-voice",
                $"prompt='{Prompt}'\ntoolCalled={toolCalled} replyComplete={sawReplyComplete} cancelledOffer={cancelled}\n" +
                $"captureDetail='{captureDetail}'\npreviews={string.Join(" | ", previews)}\nrows={string.Join(" | ", rows)}\n" +
                $"statuses={string.Join(" > ", statuses)}\nrenderedBytes={rendered} nonzeroBytes={nonzero}\n" +
                $"model={Environment.GetEnvironmentVariable("BOO_QA_VOICE_MODEL")}\n" +
                "Real OpenAI realtime + /v1/responses, capture faulted by BOO_QA_CAPTURE_FAULT=1; virtual devices, physical audio UNVERIFIED.");

            Assert.Contains(CaptureFaultMarker, captureDetail, StringComparison.Ordinal);
            Assert.Contains(statuses, s => s == HandedToVoiceStatus);
            Assert.True(toolCalled,
                "TEST SETUP: the real model answered without calling screen_help, so the fail-closed guard was never reached; no verdict. " +
                $"statuses={string.Join(" > ", statuses)} rows={string.Join(" | ", rows)}");
            Assert.DoesNotContain(previews, IsScreenAction);
            Assert.True(sawReplyComplete, "The voice turn did not complete with a spoken reply after the tool result; inspect fail-closed-voice.txt.");
            Assert.Null(VisiblePreviewText(panel));
            Assert.True(rendered >= 4800 && nonzero > 0, $"No audible reply was rendered (renderedBytes={rendered}, nonzeroBytes={nonzero}).");
            Assert.Contains(rows, r => r.StartsWith("Boo: ", StringComparison.Ordinal));
        }
        catch (Exception ex)
        {
            _boo.WriteObservation("fail-closed-failure", $"error={ex.Message}\nstatuses={string.Join(" > ", statuses)}\npreviews={string.Join(" | ", previews)}");
            _boo.CaptureScreen("fail-closed-FAILED");
            throw;
        }
    }

    private static bool TurnEnded(string status) =>
        status.StartsWith("Reply complete.", StringComparison.Ordinal) || status == IncompleteStatus;

    /// <summary>
    /// The guard ran only if RenderReply ran on the tool's structured reply: its status line (nothing offered / no action)
    /// was seen, an action was previewed, or the transcript holds the structured answer beside the spoken one.
    /// </summary>
    private static bool ToolReached(List<string> statuses, Window panel) =>
        statuses.Any(s => s == NothingOfferedStatus || s == NoActionStatus || s.StartsWith("Review", StringComparison.OrdinalIgnoreCase))
        || VisiblePreviewText(panel) is not null
        || Rows(panel).Count(r => r.StartsWith("Boo: ", StringComparison.Ordinal)) >= 2;

    private static bool IsScreenAction(string preview) =>
        preview.StartsWith("Click", StringComparison.OrdinalIgnoreCase) || preview.StartsWith("Type text", StringComparison.OrdinalIgnoreCase)
        || preview.Contains(": Click", StringComparison.Ordinal) || preview.Contains(": Type text", StringComparison.Ordinal);

    private static string? VisiblePreviewText(Window panel)
    {
        try
        {
            var preview = panel.FindFirstDescendant(cf => cf.ByAutomationId("ActionPreview"));
            if (preview is null || preview.IsOffscreen || preview.BoundingRectangle.Width <= 0) return null;
            return NameOfId(panel, "ActionPreviewText");
        }
        catch (Exception)
        {
            return null;
        }
    }

    private bool Spoken(out long rendered, out long nonzero)
    {
        rendered = 0;
        nonzero = 0;
        var path = Path.Combine(_boo.ArtifactsDirectory, "audio-device-fail-closed.jsonl");
        if (!File.Exists(path)) return false;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            var lines = reader.ReadToEnd();
            var complete = lines.LastIndexOf('\n');
            if (complete < 0) return false;
            using var doc = JsonDocument.Parse(lines[..complete].Split('\n')[^1]);
            rendered = doc.RootElement.GetProperty("renderedBytes").GetInt64();
            nonzero = doc.RootElement.GetProperty("nonzeroBytes").GetInt64();
            return rendered >= 4800 && nonzero > 0;
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            return false;
        }
    }

    private static string[] Rows(Window panel) => panel.FindFirstDescendant(cf => cf.ByAutomationId("TranscriptList")) is { } list
        ? list.FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(BooAppFixture.SafeName).ToArray()
        : Array.Empty<string>();

    private static void Observe(Window panel, List<string> statuses)
    {
        var s = NameOfId(panel, "StatusText");
        if (s.Length > 0 && (statuses.Count == 0 || statuses[^1] != s)) statuses.Add(s);
    }

    private static string NameOfId(Window panel, string automationId) =>
        panel.FindFirstDescendant(cf => cf.ByAutomationId(automationId)) is { } e ? BooAppFixture.SafeName(e) : string.Empty;
}
