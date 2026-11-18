using System.Text.Json;
using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// U8 section B: voice focus commands (focus_start / pause / resume / end / status, go_to_sleep).
/// Parser cleans and clamps model-supplied arguments; the policy decides what to apply and the exact reply.
/// </summary>
public sealed class VoiceCommandTests
{
    private const string NoTaskError = "Say what you want to focus on.";
    private const string MalformedError = "Boo could not read that request.";
    private const string UnknownError = "Unknown voice command.";

    // ------------------------------------------------------------------ names

    [Fact]
    public void ToolNamesAreTheSixRileyToolsInOrder()
    {
        Assert.Equal(
            new[] { "focus_start", "focus_pause", "focus_resume", "focus_end", "focus_status", "go_to_sleep" },
            VoiceCommandParser.ToolNames);
    }

    [Fact]
    public void ConstantsMatchTheContract()
    {
        Assert.Equal(30d, VoiceCommandParser.DefaultMinutes);
        Assert.Equal(1d, VoiceCommandParser.MinMinutes);
        Assert.Equal(240d, VoiceCommandParser.MaxMinutes);
        Assert.Equal(120, VoiceCommandParser.MaxTaskLength);
    }

    [Theory]
    [InlineData("focus_start")]
    [InlineData("focus_pause")]
    [InlineData("focus_resume")]
    [InlineData("focus_end")]
    [InlineData("focus_status")]
    [InlineData("go_to_sleep")]
    public void VoiceToolsAreRecognised(string name)
    {
        Assert.True(VoiceCommandParser.IsVoiceTool(name));
    }

    [Theory]
    [InlineData("screen_help")]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("focus")]
    [InlineData("focus_start ")]
    public void OtherNamesAreNotVoiceTools(string? name)
    {
        Assert.False(VoiceCommandParser.IsVoiceTool(name));
    }

    // ------------------------------------------------------------------ focus_start parsing

    private static string Args(object value) => JsonSerializer.Serialize(value);

    private static VoiceCommand ParseOk(string tool, string? json)
    {
        Assert.True(VoiceCommandParser.TryParse(tool, json, out var command, out var error), $"Expected {tool} {json ?? "<null>"} to parse; error '{error}'.");
        Assert.Null(error);
        Assert.NotNull(command);
        return command!;
    }

    private static string ParseFails(string? tool, string? json)
    {
        Assert.False(VoiceCommandParser.TryParse(tool, json, out var command, out var error), $"Expected {tool ?? "<null>"} {json ?? "<null>"} to fail.");
        Assert.Null(command);
        Assert.False(string.IsNullOrWhiteSpace(error), "A failed parse must give a user-safe error.");
        return error!;
    }

    [Fact]
    public void FocusStartWithTaskAndMinutes()
    {
        var command = ParseOk("focus_start", """{"task":"Codex video","minutes":45}""");

        Assert.Equal(new VoiceCommand(VoiceCommandKind.FocusStart, "Codex video", 45), command);
    }

    [Fact]
    public void FocusStartTaskIsTrimmed()
    {
        Assert.Equal("Codex video", ParseOk("focus_start", Args(new { task = "   Codex video  ", minutes = 30 })).Task);
    }

    [Fact]
    public void FocusStartTaskCollapsesWhitespaceRuns()
    {
        Assert.Equal("Write the intro", ParseOk("focus_start", Args(new { task = "Write    the  \t  intro", minutes = 30 })).Task);
    }

    [Fact]
    public void FocusStartTaskDropsControlCharacters()
    {
        Assert.Equal("Write the intro", ParseOk("focus_start", Args(new { task = "Write\u0007 the\u0000 intro\u001b", minutes = 30 })).Task);
        Assert.Equal("Inbox zero", ParseOk("focus_start", Args(new { task = "\u0001Inbox zero\u007f" })).Task);
    }

    [Fact]
    public void FocusStartTaskIsCutTo120Characters()
    {
        var task = ParseOk("focus_start", Args(new { task = new string('a', 200) })).Task;
        Assert.Equal(new string('a', 120), task);

        var cleanedFirst = ParseOk("focus_start", Args(new { task = "      " + new string('b', 130) })).Task;
        Assert.Equal(new string('b', 120), cleanedFirst);

        var exact = ParseOk("focus_start", Args(new { task = new string('c', 120) })).Task;
        Assert.Equal(new string('c', 120), exact);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{"minutes":30}""")]
    [InlineData("""{"task":""}""")]
    [InlineData("""{"task":"    "}""")]
    [InlineData("""{"task":null}""")]
    [InlineData("""{"task":42}""")]
    [InlineData("""{"task":["a"]}""")]
    [InlineData("""{"task":"\u0001\u0002\t"}""")]
    public void FocusStartWithoutUsableTaskAsksWhatToFocusOn(string json)
    {
        Assert.Equal(NoTaskError, ParseFails("focus_start", json));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void FocusStartWithNoArgumentsFails(string? json)
    {
        ParseFails("focus_start", json);
    }

    [Theory]
    [InlineData("""{"task":"Essay"}""")]
    [InlineData("""{"task":"Essay","minutes":null}""")]
    [InlineData("""{"task":"Essay","minutes":"soon"}""")]
    [InlineData("""{"task":"Essay","minutes":""}""")]
    [InlineData("""{"task":"Essay","minutes":true}""")]
    [InlineData("""{"task":"Essay","minutes":{"value":45}}""")]
    [InlineData("""{"task":"Essay","minutes":[45]}""")]
    [InlineData("""{"task":"Essay","minutes":"NaN"}""")]
    [InlineData("""{"task":"Essay","minutes":"Infinity"}""")]
    [InlineData("""{"task":"Essay","minutes":"-Infinity"}""")]
    public void FocusStartMinutesFallBackToDefault(string json)
    {
        var command = ParseOk("focus_start", json);

        Assert.Equal(VoiceCommandKind.FocusStart, command.Kind);
        Assert.Equal("Essay", command.Task);
        Assert.Equal(30d, command.Minutes);
    }

    [Theory]
    [InlineData("""{"task":"Essay","minutes":"45"}""", 45d)]
    [InlineData("""{"task":"Essay","minutes":45}""", 45d)]
    [InlineData("""{"task":"Essay","minutes":44.6}""", 44.6d)]
    [InlineData("""{"task":"Essay","minutes":1}""", 1d)]
    [InlineData("""{"task":"Essay","minutes":240}""", 240d)]
    [InlineData("""{"task":"Essay","minutes":0}""", 1d)]
    [InlineData("""{"task":"Essay","minutes":0.5}""", 1d)]
    [InlineData("""{"task":"Essay","minutes":-20}""", 1d)]
    [InlineData("""{"task":"Essay","minutes":241}""", 240d)]
    [InlineData("""{"task":"Essay","minutes":100000}""", 240d)]
    [InlineData("""{"task":"Essay","minutes":"300"}""", 240d)]
    [InlineData("""{"task":"Essay","minutes":"0"}""", 1d)]
    public void FocusStartMinutesAreAcceptedAndClamped(string json, double expected)
    {
        Assert.Equal(expected, ParseOk("focus_start", json).Minutes, 6);
    }

    [Fact]
    public void FocusStartIgnoresExtraProperties()
    {
        var command = ParseOk("focus_start", """{"task":"Slides","minutes":20,"mood":"great","nested":{"a":1}}""");

        Assert.Equal(new VoiceCommand(VoiceCommandKind.FocusStart, "Slides", 20), command);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("""{"task":"Slides",""")]
    [InlineData("task=Slides")]
    public void MalformedJsonCannotBeRead(string json)
    {
        Assert.Equal(MalformedError, ParseFails("focus_start", json));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("""["Slides",30]""")]
    [InlineData("\"Slides\"")]
    [InlineData("42")]
    public void NonObjectArgumentsAreRejected(string json)
    {
        ParseFails("focus_start", json);
    }

    [Theory]
    [InlineData("screen_help")]
    [InlineData("delete_everything")]
    [InlineData("")]
    [InlineData(null)]
    public void UnknownToolIsRejected(string? tool)
    {
        Assert.Equal(UnknownError, ParseFails(tool, """{"task":"Slides"}"""));
    }

    // ------------------------------------------------------------------ no-argument tools

    [Theory]
    [InlineData("focus_pause", VoiceCommandKind.FocusPause, null)]
    [InlineData("focus_pause", VoiceCommandKind.FocusPause, "")]
    [InlineData("focus_pause", VoiceCommandKind.FocusPause, "{}")]
    [InlineData("focus_resume", VoiceCommandKind.FocusResume, null)]
    [InlineData("focus_resume", VoiceCommandKind.FocusResume, "")]
    [InlineData("focus_resume", VoiceCommandKind.FocusResume, "{}")]
    [InlineData("focus_end", VoiceCommandKind.FocusEnd, null)]
    [InlineData("focus_end", VoiceCommandKind.FocusEnd, "")]
    [InlineData("focus_end", VoiceCommandKind.FocusEnd, "{}")]
    [InlineData("focus_status", VoiceCommandKind.FocusStatus, null)]
    [InlineData("focus_status", VoiceCommandKind.FocusStatus, "")]
    [InlineData("focus_status", VoiceCommandKind.FocusStatus, "{}")]
    [InlineData("go_to_sleep", VoiceCommandKind.GoToSleep, null)]
    [InlineData("go_to_sleep", VoiceCommandKind.GoToSleep, "")]
    [InlineData("go_to_sleep", VoiceCommandKind.GoToSleep, "{}")]
    public void NoArgumentToolsParseWithEmptyArguments(string tool, VoiceCommandKind kind, string? json)
    {
        Assert.Equal(kind, ParseOk(tool, json).Kind);
    }

    [Fact]
    public void NoArgumentToolsIgnoreTheirArguments()
    {
        Assert.Equal(VoiceCommandKind.FocusPause, ParseOk("focus_pause", """{"task":"ignored","minutes":5}""").Kind);
        Assert.Equal(VoiceCommandKind.GoToSleep, ParseOk("go_to_sleep", """{"reason":"bye"}""").Kind);
    }

    // ------------------------------------------------------------------ policy

    private static FocusSnapshot Snapshot(FocusStatus status, string task = "Quarterly report") =>
        new(Guid.Parse("8a1b2c3d-0000-4000-8000-000000000001"), task, 1800, DateTimeOffset.UnixEpoch, 120, 120, status,
            false, false, Array.Empty<FocusCheck>());

    private static VoiceCommand Start(string task = "Codex video", double minutes = 30) =>
        new(VoiceCommandKind.FocusStart, task, minutes);

    private static void AssertDecision(VoiceFocusDecision decision, bool apply, string reply)
    {
        Assert.Equal(reply, decision.Reply);
        Assert.Equal(apply, decision.Apply);
    }

    [Fact]
    public void FocusStartWithNoSessionStarts()
    {
        AssertDecision(VoiceFocusPolicy.Decide(Start("Codex video", 30), null), true, "Focus started: Codex video for 30 minutes.");
        AssertDecision(VoiceFocusPolicy.Decide(Start("Essay", 45), null), true, "Focus started: Essay for 45 minutes.");
        AssertDecision(VoiceFocusPolicy.Decide(Start("Email", 1), null), true, "Focus started: Email for 1 minutes.");
        AssertDecision(VoiceFocusPolicy.Decide(Start("Deep work", 240), null), true, "Focus started: Deep work for 240 minutes.");
    }

    [Fact]
    public void FocusStartMinutesAreRoundedToWholeMinutes()
    {
        AssertDecision(VoiceFocusPolicy.Decide(Start("Essay", 44.6), null), true, "Focus started: Essay for 45 minutes.");
        AssertDecision(VoiceFocusPolicy.Decide(Start("Essay", 20.2), null), true, "Focus started: Essay for 20 minutes.");
    }

    [Fact]
    public void FocusStartAfterAnEndedSessionStartsTheNewTask()
    {
        AssertDecision(
            VoiceFocusPolicy.Decide(Start("Codex video", 30), Snapshot(FocusStatus.Ended, "Old task")),
            true,
            "Focus started: Codex video for 30 minutes.");
    }

    [Theory]
    [InlineData(FocusStatus.Running)]
    [InlineData(FocusStatus.Paused)]
    public void FocusStartDuringASessionIsRefused(FocusStatus status)
    {
        AssertDecision(
            VoiceFocusPolicy.Decide(Start("Codex video", 30), Snapshot(status, "Quarterly report")),
            false,
            "A focus session is already going (Quarterly report). End it first.");
    }

    [Fact]
    public void FocusPauseOnlyPausesARunningSession()
    {
        var pause = new VoiceCommand(VoiceCommandKind.FocusPause);
        AssertDecision(VoiceFocusPolicy.Decide(pause, Snapshot(FocusStatus.Running)), true, "Focus paused.");

        const string refused = "There is no running focus session to pause.";
        AssertDecision(VoiceFocusPolicy.Decide(pause, null), false, refused);
        AssertDecision(VoiceFocusPolicy.Decide(pause, Snapshot(FocusStatus.Paused)), false, refused);
        AssertDecision(VoiceFocusPolicy.Decide(pause, Snapshot(FocusStatus.Ended)), false, refused);
    }

    [Fact]
    public void FocusResumeOnlyResumesAPausedSession()
    {
        var resume = new VoiceCommand(VoiceCommandKind.FocusResume);
        AssertDecision(VoiceFocusPolicy.Decide(resume, Snapshot(FocusStatus.Paused)), true, "Focus resumed.");

        const string refused = "There is no paused focus session to resume.";
        AssertDecision(VoiceFocusPolicy.Decide(resume, null), false, refused);
        AssertDecision(VoiceFocusPolicy.Decide(resume, Snapshot(FocusStatus.Running)), false, refused);
        AssertDecision(VoiceFocusPolicy.Decide(resume, Snapshot(FocusStatus.Ended)), false, refused);
    }

    [Fact]
    public void FocusEndEndsRunningOrPausedSessions()
    {
        var end = new VoiceCommand(VoiceCommandKind.FocusEnd);
        AssertDecision(VoiceFocusPolicy.Decide(end, Snapshot(FocusStatus.Running)), true, "Focus ended.");
        AssertDecision(VoiceFocusPolicy.Decide(end, Snapshot(FocusStatus.Paused)), true, "Focus ended.");

        const string refused = "There is no focus session to end.";
        AssertDecision(VoiceFocusPolicy.Decide(end, null), false, refused);
        AssertDecision(VoiceFocusPolicy.Decide(end, Snapshot(FocusStatus.Ended)), false, refused);
    }

    [Fact]
    public void FocusStatusReportsRunningSession()
    {
        var status = new VoiceCommand(VoiceCommandKind.FocusStatus);
        AssertDecision(
            VoiceFocusPolicy.Decide(status, Snapshot(FocusStatus.Running, "Quarterly report"), remainingSeconds: 600),
            false,
            "Focusing on Quarterly report, 10 minutes left.");
    }

    [Fact]
    public void FocusStatusReportsPausedSession()
    {
        var status = new VoiceCommand(VoiceCommandKind.FocusStatus);
        AssertDecision(
            VoiceFocusPolicy.Decide(status, Snapshot(FocusStatus.Paused, "Quarterly report"), remainingSeconds: 1500),
            false,
            "Focus on Quarterly report is paused with 25 minutes left.");
    }

    [Fact]
    public void FocusStatusWithoutASessionSaysSo()
    {
        var status = new VoiceCommand(VoiceCommandKind.FocusStatus);
        AssertDecision(VoiceFocusPolicy.Decide(status, null), false, "No focus session is running.");
        AssertDecision(VoiceFocusPolicy.Decide(status, Snapshot(FocusStatus.Ended), remainingSeconds: 600), false, "No focus session is running.");
    }

    [Theory]
    [InlineData(600, 10)]
    [InlineData(601, 11)]
    [InlineData(659.9, 11)]
    [InlineData(61, 2)]
    [InlineData(60, 1)]
    [InlineData(59.5, 1)]
    [InlineData(30, 1)]
    [InlineData(1, 1)]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    public void FocusStatusRoundsMinutesLeftUpWithMinimumOne(double remainingSeconds, int left)
    {
        var status = new VoiceCommand(VoiceCommandKind.FocusStatus);
        AssertDecision(
            VoiceFocusPolicy.Decide(status, Snapshot(FocusStatus.Running, "Slides"), remainingSeconds),
            false,
            $"Focusing on Slides, {left} minutes left.");
        AssertDecision(
            VoiceFocusPolicy.Decide(status, Snapshot(FocusStatus.Paused, "Slides"), remainingSeconds),
            false,
            $"Focus on Slides is paused with {left} minutes left.");
    }

    [Fact]
    public void GoToSleepAlwaysApplies()
    {
        var sleep = new VoiceCommand(VoiceCommandKind.GoToSleep);
        AssertDecision(VoiceFocusPolicy.Decide(sleep, null), true, "Going quiet.");
        AssertDecision(VoiceFocusPolicy.Decide(sleep, Snapshot(FocusStatus.Running)), true, "Going quiet.");
        AssertDecision(VoiceFocusPolicy.Decide(sleep, Snapshot(FocusStatus.Paused)), true, "Going quiet.");
        AssertDecision(VoiceFocusPolicy.Decide(sleep, Snapshot(FocusStatus.Ended)), true, "Going quiet.");
    }

    [Fact]
    public void ParsedStartFlowsIntoPolicyReply()
    {
        var command = ParseOk("focus_start", """{"task":"  I'm making a   Codex video ","minutes":"30"}""");

        AssertDecision(VoiceFocusPolicy.Decide(command, null), true, "Focus started: I'm making a Codex video for 30 minutes.");
    }
}
