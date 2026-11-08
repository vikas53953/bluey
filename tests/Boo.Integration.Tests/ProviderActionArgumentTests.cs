using System.Net;
using System.Text;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class ProviderActionArgumentTests
{
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    [Fact]
    public async Task Responses_omits_null_unused_action_arguments_without_omitting_meaningful_fields()
    {
        var handler = new ActionArgumentHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\",\"marks\":[],\"actions\":[{\"name\":\"click\",\"target\":\"save\",\"arguments\":{\"x\":null,\"y\":null,\"name\":null,\"automationId\":\"save-button\"}}]}"}]}]}
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Click save.", 11, AllowActions: true),
            CancellationToken.None);

        var action = Assert.Single(reply.Actions);
        Assert.Equal("save-button", action.Arguments["automationId"]);
        Assert.DoesNotContain("x", action.Arguments.Keys);
        Assert.DoesNotContain("y", action.Arguments.Keys);
        Assert.DoesNotContain("name", action.Arguments.Keys);
    }

    [Fact]
    public async Task Responses_preserves_meaningful_empty_string_arguments_while_omitting_null_aliases()
    {
        var handler = new ActionArgumentHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\",\"marks\":[],\"actions\":[{\"name\":\"write_file\",\"target\":\"notes.txt\",\"arguments\":{\"content\":\"\",\"contents\":null,\"path\":\"notes.txt\",\"overwrite\":\"false\"}}]}"}]}]}
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Prepare notes.txt.", 12, AllowActions: true),
            CancellationToken.None);

        var action = Assert.Single(reply.Actions);
        Assert.True(action.Arguments.ContainsKey("content"));
        Assert.Equal(string.Empty, action.Arguments["content"]);
        Assert.DoesNotContain("contents", action.Arguments.Keys);
        Assert.Equal("notes.txt", action.Arguments["path"]);
        Assert.Equal("false", action.Arguments["overwrite"]);
    }

    private static HttpClient CreateHttpClient(ActionArgumentHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.openai.com/")
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class ActionArgumentHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public ActionArgumentHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(_responder(request));
    }
}
