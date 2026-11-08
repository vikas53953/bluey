using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Providers.Tests;

public sealed class AnthropicRequestShapeTests
{
    private static readonly ProviderSettings Settings = new(AnthropicModel: "claude-opus-5-5", AnthropicFocusModel: "claude-haiku-5-5");
    private const string ApiKey = "anthropic-fixture-key";

    [Fact]
    public async Task Structured_request_uses_messages_wire_format_and_binds_the_decoded_reply()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "id": "msg_fixture",
              "type": "message",
              "role": "assistant",
              "stop_reason": "end_turn",
              "content": [
                {
                  "type": "text",
                  "text": "{\"text\":\"The control is here.\",\"marks\":[{\"kind\":\"ring\",\"x\":10,\"y\":20,\"width\":30,\"height\":40,\"label\":\"Primary action\",\"endX\":50,\"endY\":60}],\"actions\":[{\"name\":\"open_url\",\"target\":\"docs\",\"arguments\":{\"url\":\"https://example.com\"},\"generation\":999,\"observationId\":\"model-invented\"}]}"
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new AnthropicClient(http, () => ApiKey, Settings);
        var request = new AssistantRequest("Explain the selected control.", 41, frame, Research: false, AllowActions: true);

        var reply = await client.RespondAsync(request, CancellationToken.None);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://api.anthropic.com/v1/messages", sent.RequestUri!.ToString());
        Assert.Equal(ApiKey, Assert.Single(sent.Headers.GetValues("x-api-key")));
        Assert.Equal("2026-10-21", Assert.Single(sent.Headers.GetValues("anthropic-version")));
        Assert.Null(sent.Headers.Authorization);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("claude-opus-5-5", root.GetProperty("model").GetString());
        Assert.Equal(4096, root.GetProperty("max_tokens").GetInt32());
        Assert.StartsWith("Return only the requested structured result.", root.GetProperty("system").GetString(), StringComparison.Ordinal);
        Assert.Contains($"observation={context.ObservationId}", root.GetProperty("system").GetString(), StringComparison.Ordinal);
        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var blocks = message.GetProperty("content").EnumerateArray().ToArray();
        Assert.Equal(2, blocks.Length);
        Assert.Equal("text", blocks[0].GetProperty("type").GetString());
        Assert.Contains("Explain the selected control.", blocks[0].GetProperty("text").GetString(), StringComparison.Ordinal);
        Assert.Equal("image", blocks[1].GetProperty("type").GetString());
        var source = blocks[1].GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Equal(Convert.ToBase64String(frame.Jpeg), source.GetProperty("data").GetString());
        var outputConfig = root.GetProperty("output_config");
        Assert.Equal("low", outputConfig.GetProperty("effort").GetString());
        var format = outputConfig.GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("schema");
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal(new[] { "text", "marks", "actions" }, schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.False(root.TryGetProperty("tools", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));

        Assert.Equal("The control is here.", reply.Text);
        Assert.Empty(reply.Sources);
        var annotations = reply.Annotations!;
        Assert.Equal(context.ObservationId, annotations.ObservationId);
        Assert.Equal(41, annotations.Generation);
        Assert.Equal("display-1", annotations.DisplayId);
        var mark = Assert.Single(annotations.Marks);
        Assert.Equal("ring", mark.Kind);
        Assert.Equal(new RectD(10, 20, 30, 40), mark.Bounds);
        Assert.Equal("Primary action", mark.Label);
        var action = Assert.Single(reply.Actions);
        Assert.Equal("open_url", action.Name);
        Assert.Equal("docs", action.Target);
        Assert.Equal("https://example.com", action.Arguments["url"]);
        Assert.Equal(41, action.Generation);
    }

    [Fact]
    public async Task Plain_research_uses_web_search_without_a_schema_and_collects_deduplicated_sources()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "type": "message",
              "stop_reason": "end_turn",
              "content": [
                { "type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": { "query": "boo companion" } },
                {
                  "type": "web_search_tool_result",
                  "tool_use_id": "srvtoolu_1",
                  "content": [
                    { "type": "web_search_result", "url": "https://example.com/source", "title": "Source" },
                    { "type": "web_search_result", "url": "https://example.org/second", "title": "Second" },
                    { "type": "web_search_result", "url": "ftp://example.net/skip", "title": "Skipped" }
                  ]
                },
                { "type": "web_search_tool_result", "tool_use_id": "srvtoolu_2", "content": { "type": "web_search_tool_result_error", "error_code": "max_uses_exceeded" } },
                { "type": "text", "text": "Boo is a companion app. " },
                {
                  "type": "text",
                  "text": "It lives in the notch.",
                  "citations": [ { "type": "web_search_result_location", "url": "https://example.com/source", "title": "Source", "cited_text": "notch" } ]
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new AnthropicClient(http, () => ApiKey, Settings);

        var reply = await client.RespondAsync(new AssistantRequest("What is Boo?", 7, Research: true), CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal(8192, root.GetProperty("max_tokens").GetInt32());
        Assert.StartsWith("Answer the user's research question in plain prose", root.GetProperty("system").GetString(), StringComparison.Ordinal);
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("web_search_20260209", tool.GetProperty("type").GetString());
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.Equal(5, tool.GetProperty("max_uses").GetInt32());
        Assert.Equal("low", root.GetProperty("output_config").GetProperty("effort").GetString());
        Assert.False(root.GetProperty("output_config").TryGetProperty("format", out _));
        Assert.Equal("Boo is a companion app. It lives in the notch.", reply.Text);
        Assert.Equal(new[] { "https://example.com/source", "https://example.org/second" }, reply.Sources.Select(s => s.Url).ToArray());
        Assert.Equal(new[] { "Source", "Second" }, reply.Sources.Select(s => s.Title).ToArray());
        Assert.Null(reply.Annotations);
        Assert.Empty(reply.Actions);
    }

    [Fact]
    public async Task Classification_uses_the_focus_model_and_validates_the_distribution()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "type": "message",
              "stop_reason": "end_turn",
              "content": [ { "type": "text", "text": "{\"activity\":\"off_task\",\"confidence\":0.93,\"probabilities\":{\"on_task\":0.21,\"off_task\":0.72,\"uncertain\":0.07}}" } ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new AnthropicClient(http, () => ApiKey, Settings);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 3), new byte[] { 0x10 });
        var session = new FocusSnapshot(Guid.NewGuid(), "Write the report", 1500, DateTimeOffset.UtcNow, 120, 125, FocusStatus.Running, false, false, []);

        var decision = await client.ClassifyAsync(frame, session, CancellationToken.None);

        using var body = JsonDocument.Parse(handler.LastBody!);
        var root = body.RootElement;
        Assert.Equal("claude-haiku-5-5", root.GetProperty("model").GetString());
        var schema = root.GetProperty("output_config").GetProperty("format").GetProperty("schema");
        Assert.Equal(new[] { "on_task", "off_task", "uncertain" }, schema.GetProperty("properties").GetProperty("activity").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(FocusActivity.OffTask, decision.Activity);
        Assert.Equal(0.72, decision.Probability);
        Assert.Equal(0.93, decision.Confidence);
        Assert.Equal(0.21, decision.Probabilities["on_task"]);

        var invalid = Record.Exception(() => AnthropicClient.DecodeDecision("""
            { "stop_reason": "end_turn", "content": [ { "type": "text", "text": "{\"activity\":\"off_task\",\"confidence\":0.9,\"probabilities\":{\"on_task\":0.5,\"off_task\":0.5,\"uncertain\":0.5}}" } ] }
            """));
        Assert.IsType<InvalidOperationException>(invalid);
    }

    [Fact]
    public async Task Refusals_and_http_errors_are_reported_without_echoing_the_body()
    {
        var refusing = new RecordingHandler(_ => JsonResponse("""{ "type": "message", "stop_reason": "refusal", "content": [] }"""));
        using (var http = CreateHttpClient(refusing))
        {
            var client = new AnthropicClient(http, () => ApiKey, Settings);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RespondAsync(new AssistantRequest("Hi", 1), CancellationToken.None));
            Assert.Equal("Anthropic declined this request.", error.Message);
        }

        const string secretBody = """{"type":"error","error":{"type":"authentication_error","message":"SECRET-DETAIL"}}""";
        var failing = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.Unauthorized)
        {
            Content = new StringContent(secretBody, Encoding.UTF8, "application/json")
        });
        using (var http = CreateHttpClient(failing))
        {
            var client = new AnthropicClient(http, () => ApiKey, Settings);
            var error = await Assert.ThrowsAsync<ProviderException>(() => client.NudgeAsync(new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 }), "task", CancellationToken.None));
            Assert.Equal(AiProvider.Anthropic, error.Provider);
            Assert.Equal(ProviderFailureKind.InvalidCredential, error.Failure.Kind);
            Assert.Equal(401, error.Failure.StatusCode);
            Assert.DoesNotContain("SECRET-DETAIL", error.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("SECRET-DETAIL", error.Failure.UserMessage, StringComparison.Ordinal);
            Assert.Contains("Anthropic", error.Failure.UserMessage, StringComparison.Ordinal);
        }

        var untouched = new RecordingHandler(_ => JsonResponse("{}"));
        using (var http = CreateHttpClient(untouched, new Uri("https://api.openai.com/")))
        {
            var client = new AnthropicClient(http, () => ApiKey, Settings);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RespondAsync(new AssistantRequest("Hi", 1), CancellationToken.None));
            Assert.Equal("Anthropic requests require the official HTTPS API origin.", error.Message);
        }
        using (var http = CreateHttpClient(untouched))
        {
            var client = new AnthropicClient(http, () => "  ", Settings);
            var error = await Assert.ThrowsAsync<InvalidOperationException>(() => client.RespondAsync(new AssistantRequest("Hi", 1), CancellationToken.None));
            Assert.Equal(AiProviders.MissingCredentialMessage(AiProvider.Anthropic), error.Message);
        }
        Assert.Empty(untouched.Requests);
    }

    private static HttpClient CreateHttpClient(RecordingHandler handler, Uri? baseAddress = null) =>
        new(handler) { BaseAddress = baseAddress ?? new Uri("https://api.anthropic.com/") };

    private static ScreenContext CreateContext(Guid observationId, long generation) => new(
        observationId,
        generation,
        new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
        1920,
        1080,
        new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
        DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _responder(request, cancellationToken);
        }
    }
}
