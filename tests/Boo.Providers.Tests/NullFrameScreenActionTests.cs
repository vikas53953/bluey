using System.Net;
using System.Text.Json;
using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Review regression (PR #3, 13 Nov 2026): when the screen capture for an explicit task failed, the request carries
/// no frame and no control observation. A model-proposed click or type_text must then be dropped before it reaches
/// the approval path, because nothing observed on screen backs it. Non-screen file actions stay proposable so a
/// task that never needed the screen keeps working.
/// </summary>
public sealed class NullFrameScreenActionTests
{
    private static readonly ProviderSettings Settings = new(ReasoningModel: "gpt-6.1-sol");
    private static readonly Uri Origin = new("https://api.openai.com/");

    private const string MixedActions = """
        [
          { "name": "click", "target": "pixel", "arguments": { "x": "14", "y": "17" } },
          { "name": "click", "target": "by-id", "arguments": { "automationId": "button-text", "name": "Button text" } },
          { "name": "type_text", "target": "editor", "arguments": { "automationId": "edit-field", "text": "hello" } },
          { "name": "type_text", "target": "editor-by-name", "arguments": { "name": "Editor", "text": "hello" } },
          { "name": "write_file", "target": "notes", "arguments": { "path": "notes.txt", "content": "hello" } },
          { "name": "search", "target": "docs", "arguments": { "text": "needle" } }
        ]
        """;

    [Fact]
    public async Task Explicit_task_without_a_frame_drops_every_click_and_type_text_but_keeps_file_and_search_actions()
    {
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse(MixedActions)));
        var client = new ResponsesClient(CreateHttpClient(handler, Origin), () => "sk-test", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Save my notes and click the button.", 41, Frame: null, AllowActions: true),
            CancellationToken.None);

        Assert.Equal(new[] { "write_file", "search" }, reply.Actions.Select(action => action.Name));
        Assert.All(reply.Actions, action => Assert.Equal(41, action.Generation));
    }

    [Theory]
    [InlineData("click", "{ \"x\": \"14\", \"y\": \"17\" }")]
    [InlineData("click", "{ \"automationId\": \"button-text\", \"name\": \"Button text\" }")]
    [InlineData("type_text", "{ \"automationId\": \"edit-field\", \"text\": \"hello\" }")]
    public async Task Explicit_task_without_a_frame_returns_no_screen_action_at_all(string name, string arguments)
    {
        var actions = $$"""[{ "name": "{{name}}", "target": "t", "arguments": {{arguments}} }]""";
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse(actions)));
        var client = new ResponsesClient(CreateHttpClient(handler, Origin), () => "sk-test", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Do the requested task.", 41, Frame: null, AllowActions: true),
            CancellationToken.None);

        Assert.Empty(reply.Actions);
        Assert.Equal("ok", reply.Text);
    }

    [Fact]
    public async Task Explicit_task_with_a_frame_still_accepts_a_pixel_click_bound_to_its_observation()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 1, 2, 3 });
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse(MixedActions)));
        var client = new ResponsesClient(CreateHttpClient(handler, Origin), () => "sk-test", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Do the requested task.", 41, frame, AllowActions: true),
            CancellationToken.None,
            BindingControls(context));

        Assert.Equal(new[] { "click", "click", "type_text", "write_file", "search" }, reply.Actions.Select(action => action.Name));
    }

    private static string StructuredResponse(string actionsJson)
    {
        var payload = $$"""{"text":"ok","marks":[],"actions":{{actionsJson}}}""";
        return JsonSerializer.Serialize(new
        {
            output = new[]
            {
                new
                {
                    type = "message",
                    content = new[] { new { type = "output_text", text = payload } }
                }
            }
        });
    }
}
