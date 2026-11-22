using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Boo.Core;
using Boo.Demo.Tests;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;
using FlaUI.Core.Input;
using FlaUI.Core.WindowsAPI;
using System.Drawing;

namespace Boo.NativeVoice;

/// <summary>
/// The real-key seed of <see cref="NativeVoiceFixture"/> plus issue #177's provider fault, injected:
/// BOO_QA_VOICE_SILENT_REPLY=1 (HostedVoiceOptions.SilentReply) makes Boo's own realtime client replace every reply audio
/// chunk from the real OpenAI session with digital silence of the same length, keeping the real transcript. Set in the seed
/// (BooAppFixture copies the environment after it) and cleared right after launch.
/// </summary>
public sealed class SilentReplyVoiceFixture : BooAppFixture
{
    public SilentReplyVoiceFixture() : base(data =>
    {
        NativeVoiceSeed.Apply(data, "audio-device-silent-reply.jsonl");
        Environment.SetEnvironmentVariable(HostedVoiceOptions.SilentReplyVariable, "1");
    })
    {
        Environment.SetEnvironmentVariable(HostedVoiceOptions.SilentReplyVariable, null);
    }
}

/// <summary>
/// PR #186 acceptance on the published Boo.exe (issue #177): one real hold-to-talk turn ("what is two plus three") on the
/// real OpenAI realtime model, whose reply audio is turned into digital silence by the QA fault switch above (labelled
/// "live with an injected fault": the silence is injected, the provider, transcript and recovery speech are real).
/// 1. The realtime reply plays as silence: no non-zero byte reaches the virtual output before Boo's recovery status.
/// 2. Boo recovers: the status says the reply came back silent and Boo is reading it aloud, and real OpenAI
///    text-to-speech is consumed by the virtual output device (non-zero bytes grow).
/// 3. Ctrl+Alt+F12 within 0.5 s of the recovered speech becoming audible, while at least 0.5 s of it is still queued in
///    Boo's playback buffer (a Stop after the speech ended fails as TEST SETUP), drops it at once: a fresh receipt shows
///    the queue empty and at most 0.2 s more consumed (RecoveredStopCheck, PCM bytes and a monotonic clock), and the panel
///    says the microphone stopped.
/// The output is Boo's hosted virtual device (HostedVoiceAudio): it reads Boo's real playback buffer at real-time pace.
/// Physical speakers stay UNVERIFIED.
/// </summary>
public sealed class SilentReplyRecoveryJourney : IClassFixture<SilentReplyVoiceFixture>
{
    private const string SilentStatusPrefix = "OpenAI's voice reply came back silent";

    private readonly SilentReplyVoiceFixture _boo;
    public SilentReplyRecoveryJourney(SilentReplyVoiceFixture boo) => _boo = boo;

    [Fact]
    public void PublishedApp_SilentReply_IsReadAloud_AndStopEndsTheRecoveredSpeech()
    {
        var statuses = new List<string>();
        var log = new StringBuilder();
        try
        {
            _boo.WaitForNotch(TimeSpan.FromSeconds(30));
            var panel = _boo.EnsurePanel();
            HoldToAsk(panel, statuses);

            // 1 + 2: wait for the recovery status, then for audible recovered speech on the virtual output.
            long nonzeroAtRecoveryStatus = -1;
            var audible = Poll(() =>
            {
                Observe(panel, statuses);
                var nonzero = Nonzero();
                if (nonzeroAtRecoveryStatus < 0 && statuses.LastOrDefault()?.StartsWith(SilentStatusPrefix, StringComparison.Ordinal) == true)
                    nonzeroAtRecoveryStatus = nonzero;
                return nonzeroAtRecoveryStatus >= 0 && nonzero > nonzeroAtRecoveryStatus;
            }, TimeSpan.FromSeconds(60));
            var audibleAt = System.Diagnostics.Stopwatch.StartNew();
            log.AppendLine($"recoveryStatusSeen={nonzeroAtRecoveryStatus >= 0} nonzeroAtRecoveryStatus={nonzeroAtRecoveryStatus} audible={audible} atAudible: {Receipt()}");
            Assert.True(nonzeroAtRecoveryStatus >= 0, $"Boo never said the reply came back silent (no recovery). Statuses: {string.Join(" > ", statuses)}");
            Assert.Equal(0, nonzeroAtRecoveryStatus); // the realtime reply itself played as pure silence
            Assert.True(audible, "Boo said it was reading the answer aloud, but no recovered speech reached the output device.");

            // 3: Stop while recovered speech is still queued (receipt read right before the key press; fails closed when the
            // speech already ended), then a fresh receipt must show the queue dropped. PCM bytes and the monotonic clock only.
            var beforeStop = Receipt();
            var msToStop = audibleAt.ElapsedMilliseconds;
            Keyboard.TypeSimultaneously(VirtualKeyShort.CONTROL, VirtualKeyShort.ALT, VirtualKeyShort.F12);
            Thread.Sleep(1500);
            var afterStop = Receipt();
            Observe(panel, statuses);
            var listening = BooAppFixture.SafeName(PanelViews.FindInView(panel, "ListeningStatus")!);
            var rows = Rows(panel);
            log.AppendLine($"msFromAudibleToStop={msToStop}");
            log.AppendLine($"beforeStop: {beforeStop}");
            log.AppendLine($"afterStop (1.5 s later): {afterStop}");
            log.AppendLine($"listening='{listening}'");
            log.AppendLine($"rows={string.Join(" | ", rows)}");
            log.AppendLine($"statuses={string.Join(" > ", statuses)}");
            log.AppendLine("Label: live with an injected fault (BOO_QA_VOICE_SILENT_REPLY=1 silences the real reply audio); virtual output device, physical speakers UNVERIFIED.");
            _boo.CaptureScreen("silent-reply-after-stop");
            _boo.WriteObservation("silent-reply-recovery", log.ToString());

            Assert.Contains(rows, s => s.StartsWith("Boo: ", StringComparison.Ordinal) && Regex.IsMatch(s, @"\b(five|5)\b", RegexOptions.IgnoreCase));
            Assert.True(msToStop <= 500, $"TEST SETUP: Stop was pressed {msToStop} ms after the recovered speech became audible (limit 500 ms).");
            Assert.Null(RecoveredStopCheck.PreconditionFailure(beforeStop));
            Assert.Null(RecoveredStopCheck.InterruptionFailure(beforeStop!, afterStop));
            Assert.StartsWith("Microphone stopped.", listening, StringComparison.Ordinal);
        }
        catch (Exception ex)
        {
            _boo.WriteObservation("silent-reply-failure", $"error={ex.Message}\n{log}\nstatuses={string.Join(" > ", statuses)}");
            _boo.CaptureScreen("silent-reply-FAILED");
            throw;
        }
    }

    /// <summary>Holds Boo's eyes until the whole synthetic question WAV was captured, then releases (as NativeVoiceJourney).</summary>
    private void HoldToAsk(Window panel, List<string> statuses)
    {
        var notch = _boo.WaitForNotch(TimeSpan.FromSeconds(30));
        var rect = notch.BoundingRectangle;
        Mouse.MoveTo(new Point(rect.Left + rect.Width / 2, rect.Top + 2));
        Assert.True(BooAppFixture.WaitUntil(() =>
            notch.FindFirstDescendant(cf => cf.ByAutomationId("OpenButton")) is { IsOffscreen: false } button
            && button.BoundingRectangle.Height > 0, TimeSpan.FromSeconds(10)), "TEST SETUP: notch eyes did not reveal.");
        var eyes = notch.FindFirstDescendant(cf => cf.ByAutomationId("OpenButton"))!.BoundingRectangle;
        Mouse.MoveTo(new Point(eyes.Left + eyes.Width / 2, eyes.Top + Math.Min(20, eyes.Height / 2)));
        Mouse.Down(MouseButton.Left);
        try
        {
            Assert.True(BooAppFixture.WaitUntil(() =>
            {
                Observe(panel, statuses);
                using var doc = Last();
                return doc is not null && doc.RootElement.GetProperty("inputBytes").GetInt64()
                    == doc.RootElement.GetProperty("expectedInputBytes").GetInt64();
            }, TimeSpan.FromSeconds(20)), "TEST SETUP: the synthetic WAV was not fully captured before release.");
        }
        finally
        {
            Mouse.Up(MouseButton.Left);
        }
    }

    /// <summary>Like BooAppFixture.WaitUntil, but polls every 15 ms: the Stop must land inside the first 0.5 s of speech.</summary>
    private static bool Poll(Func<bool> condition, TimeSpan timeout)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.Elapsed < timeout)
        {
            try { if (condition()) return true; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { }
            Thread.Sleep(15);
        }
        return false;
    }

    private static string[] Rows(Window panel) => PanelViews.FindInView(panel, "TranscriptList")!
        .FindAllChildren(cf => cf.ByControlType(ControlType.ListItem)).Select(BooAppFixture.SafeName).ToArray();

    private static void Observe(Window panel, List<string> statuses)
    {
        var s = BooAppFixture.SafeName(PanelViews.FindInView(panel, "StatusText")!);
        if (statuses.Count == 0 || statuses[^1] != s) statuses.Add(s);
    }

    private static PlaybackReceipt? Receipt() => PlaybackReceipt.ReadLast(Environment.GetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE")!);

    private static long Nonzero() => Receipt()?.NonzeroBytes ?? 0;

    /// <summary>The last complete line of this fixture's device receipt (read from the end; the file grows every 20 ms).</summary>
    private static JsonDocument? Last()
    {
        var path = Environment.GetEnvironmentVariable("BOO_QA_VOICE_EVIDENCE")!;
        if (!File.Exists(path)) return null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var start = Math.Max(0, stream.Length - 8192);
        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        var text = reader.ReadToEnd();
        var end = text.LastIndexOf('\n');
        if (end < 0) return null;
        var line = text[..end].Split('\n')[^1];
        return line.TrimStart().StartsWith('{') ? JsonDocument.Parse(line) : null;
    }
}
