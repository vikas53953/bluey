using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

/// <summary>
/// U8 section B, provider side: the Realtime session.update advertises the six voice focus tools after
/// screen_help, and function-call events for them are recognised (and only them).
/// </summary>
public sealed class VoiceToolProtocolTests
{
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    private static readonly string[] VoiceTools =
        { "focus_start", "focus_pause", "focus_resume", "focus_end", "focus_status", "go_to_sleep" };

    private static JsonElement[] Tools(bool pushToTalk)
    {
        using var document = JsonDocument.Parse(RealtimeProtocol.SessionUpdate(Settings, pushToTalk));
        return document.RootElement.GetProperty("session").GetProperty("tools").EnumerateArray().Select(t => t.Clone()).ToArray();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SessionUpdate_lists_screen_help_then_the_six_voice_tools_in_order(bool pushToTalk)
    {
        var names = Tools(pushToTalk).Select(t => t.GetProperty("name").GetString()).ToArray();

        Assert.Equal(new[] { "screen_help" }.Concat(VoiceTools).ToArray(), names);
        Assert.Equal(VoiceTools, VoiceCommandParser.ToolNames);
    }

    [Fact]
    public void SessionUpdate_voice_tools_are_functions_with_object_parameters()
    {
        foreach (var tool in Tools(false).Skip(1))
        {
            var name = tool.GetProperty("name").GetString();
            Assert.True(tool.GetProperty("type").GetString() == "function", $"{name} is not type function.");
            var parameters = tool.GetProperty("parameters");
            Assert.True(parameters.ValueKind == JsonValueKind.Object, $"{name} parameters is not an object.");
            Assert.True(parameters.GetProperty("type").GetString() == "object", $"{name} parameters schema type is not object.");
            Assert.False(string.IsNullOrWhiteSpace(tool.GetProperty("description").GetString()), $"{name} has no description.");
        }
    }

    [Fact]
    public void SessionUpdate_focus_start_requires_task_and_offers_optional_minutes()
    {
        var focusStart = Tools(false).Single(t => t.GetProperty("name").GetString() == "focus_start");
        var parameters = focusStart.GetProperty("parameters");

        var required = parameters.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Contains("task", required);
        Assert.DoesNotContain("minutes", required);

        var properties = parameters.GetProperty("properties");
        Assert.Equal("string", properties.GetProperty("task").GetProperty("type").GetString());
        Assert.Equal("number", properties.GetProperty("minutes").GetProperty("type").GetString());
    }

    [Fact]
    public void SessionUpdate_screen_help_is_unchanged()
    {
        var screenHelp = Tools(false)[0];
        Assert.Equal("function", screenHelp.GetProperty("type").GetString());
        var parameters = screenHelp.GetProperty("parameters");
        var required = parameters.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();
        Assert.Equal(new[] { "question", "mode" }, required);
        Assert.Equal(
            new[] { "teach", "research", "task" },
            parameters.GetProperty("properties").GetProperty("mode").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
    }

    [Fact]
    public void SessionUpdate_instructions_describe_the_focus_tools_and_keep_the_existing_rules()
    {
        using var document = JsonDocument.Parse(RealtimeProtocol.SessionUpdate(Settings));
        var instructions = document.RootElement.GetProperty("session").GetProperty("instructions").GetString()!;

        Assert.Contains("focus_start", instructions, StringComparison.Ordinal);
        Assert.Contains("go_to_sleep", instructions, StringComparison.Ordinal);
        Assert.Contains("screen_help", instructions, StringComparison.Ordinal);
        Assert.Contains("untrusted", instructions, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ function-call events

    private static string CallDone(string name, string? arguments, string? callId = "call-7", string type = "response.function_call_arguments.done")
    {
        var fields = new Dictionary<string, object?> { ["type"] = type, ["item_id"] = "item-3", ["name"] = name };
        if (callId is not null) fields["call_id"] = callId;
        if (arguments is not null) fields["arguments"] = arguments;
        return JsonSerializer.Serialize(fields);
    }

    [Fact]
    public void TryGetVoiceCommand_reads_a_focus_start_call()
    {
        var json = CallDone("focus_start", """{"task":"Codex video","minutes":45}""", "call-42");

        Assert.True(RealtimeProtocol.TryGetVoiceCommand(json, out var callId, out var command, out var error));
        Assert.Equal("call-42", callId);
        Assert.Null(error);
        Assert.Equal(new VoiceCommand(VoiceCommandKind.FocusStart, "Codex video", 45), command);
    }

    [Theory]
    [InlineData("focus_pause", VoiceCommandKind.FocusPause)]
    [InlineData("focus_resume", VoiceCommandKind.FocusResume)]
    [InlineData("focus_end", VoiceCommandKind.FocusEnd)]
    [InlineData("focus_status", VoiceCommandKind.FocusStatus)]
    [InlineData("go_to_sleep", VoiceCommandKind.GoToSleep)]
    public void TryGetVoiceCommand_reads_no_argument_calls(string name, VoiceCommandKind kind)
    {
        Assert.True(RealtimeProtocol.TryGetVoiceCommand(CallDone(name, "{}"), out var callId, out var command, out var error));
        Assert.Equal("call-7", callId);
        Assert.Null(error);
        Assert.Equal(kind, command!.Kind);
    }

    [Theory]
    [InlineData("""{"minutes":30}""", "Say what you want to focus on.")]
    [InlineData("{not json", "Boo could not read that request.")]
    public void TryGetVoiceCommand_returns_true_with_an_error_for_bad_arguments(string arguments, string expectedError)
    {
        Assert.True(RealtimeProtocol.TryGetVoiceCommand(CallDone("focus_start", arguments, "call-9"), out var callId, out var command, out var error));
        Assert.Equal("call-9", callId);
        Assert.Null(command);
        Assert.Equal(expectedError, error);
    }

    [Fact]
    public void TryGetVoiceCommand_ignores_screen_help()
    {
        var json = CallDone("screen_help", """{"question":"Explain this","mode":"teach"}""");

        Assert.False(RealtimeProtocol.TryGetVoiceCommand(json, out _, out var command, out _));
        Assert.Null(command);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public void TryGetVoiceCommand_requires_a_call_id(string? callId)
    {
        Assert.False(RealtimeProtocol.TryGetVoiceCommand(CallDone("focus_start", """{"task":"Essay"}""", callId), out _, out _, out _));
    }

    [Theory]
    [InlineData("response.function_call_arguments.delta")]
    [InlineData("response.output_item.done")]
    [InlineData("response.done")]
    [InlineData("conversation.item.added")]
    public void TryGetVoiceCommand_ignores_other_event_types(string type)
    {
        Assert.False(RealtimeProtocol.TryGetVoiceCommand(CallDone("focus_start", """{"task":"Essay"}""", "call-1", type), out _, out _, out _));
    }

    [Theory]
    [InlineData("""{"type":"response.output_audio_transcript.delta","delta":"focus_start"}""")]
    [InlineData("""{"type":"session.updated"}""")]
    [InlineData("not json at all")]
    [InlineData("")]
    public void TryGetVoiceCommand_ignores_unrelated_or_unreadable_events(string json)
    {
        Assert.False(RealtimeProtocol.TryGetVoiceCommand(json, out _, out _, out _));
    }

    [Fact]
    public void TryGetVoiceCommand_ignores_unknown_tool_names()
    {
        Assert.False(RealtimeProtocol.TryGetVoiceCommand(CallDone("delete_everything", "{}"), out _, out _, out _));
    }
}
