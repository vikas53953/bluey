using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class ResearchRequestShapeTests
{
    private const string IncludeSources = "web_search_call.action.sources";

    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    private const string PlainAnswer = """
        {"output":[{"type":"message","role":"assistant","content":[{"type":"output_text","text":"Plain answer.","annotations":[]}]}]}
        """;

    [Fact]
    public async Task Research_without_frame_or_actions_omits_the_json_schema_and_requests_search_sources()
    {
        var handler = new ShapeHandler(_ => JsonResponse(PlainAnswer));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(new AssistantRequest("find the latest .NET release", 5, Frame: null, Research: true, AllowActions: false), CancellationToken.None);

        Assert.Equal("Plain answer.", reply.Text);
        using var sent = JsonDocument.Parse(handler.LastBody!);
        var root = sent.RootElement;
        Assert.False(HasJsonSchemaFormat(root), "research without a frame or actions must not force a json_schema text format");
        Assert.Contains(IncludeSources, ReadInclude(root));
        Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("type").GetString() == "web_search");
    }

    [Fact]
    public async Task Research_with_a_frame_keeps_the_strict_json_schema_and_requests_search_sources()
    {
        var handler = new ShapeHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\",\"marks\":[],\"actions\":[]}"}]}]}
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);
        var context = CreateContext(9);
        var frame = new CapturedFrame(context, new byte[] { 1, 2, 3 });

        await client.RespondAsync(new AssistantRequest("compare these with sources", 9, frame, Research: true, AllowActions: false), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var root = sent.RootElement;
        AssertStrictJsonSchema(root);
        Assert.Contains(IncludeSources, ReadInclude(root));
    }

    [Fact]
    public async Task Research_with_actions_allowed_keeps_the_strict_json_schema()
    {
        var handler = new ShapeHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\",\"marks\":[],\"actions\":[]}"}]}]}
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        await client.RespondAsync(new AssistantRequest("research and open it", 3, Frame: null, Research: true, AllowActions: true), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var root = sent.RootElement;
        AssertStrictJsonSchema(root);
        Assert.Contains(IncludeSources, ReadInclude(root));
    }

    [Fact]
    public async Task Non_research_requests_keep_the_strict_json_schema_and_do_not_request_search_sources()
    {
        var handler = new ShapeHandler(_ => JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\",\"marks\":[],\"actions\":[]}"}]}]}
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        await client.RespondAsync(new AssistantRequest("what is this", 2, Frame: null, Research: false, AllowActions: false), CancellationToken.None);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var root = sent.RootElement;
        AssertStrictJsonSchema(root);
        Assert.DoesNotContain(IncludeSources, ReadInclude(root));
    }

    [Fact]
    public async Task Sources_come_from_url_citation_annotations_and_are_normalised()
    {
        var handler = new ShapeHandler(_ => JsonResponse("""
            {
              "output": [
                { "type": "web_search_call", "action": { "sources": [ { "url": "https://fallback.example.com/x", "title": "Fallback" } ] } },
                {
                  "type": "message",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "Answer with citations.",
                      "annotations": [
                        { "type": "url_citation", "url": "https://Docs.Example.com/Release?utm_source=openai#notes", "title": "Release notes" },
                        { "type": "url_citation", "url": "https://docs.example.com/Release", "title": "Duplicate" },
                        { "type": "url_citation", "url": "javascript:alert(1)", "title": "Bad" },
                        { "type": "url_citation", "url": "https://blog.example.com/post" }
                      ]
                    }
                  ]
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(new AssistantRequest("research release notes", 4, Frame: null, Research: true), CancellationToken.None);

        Assert.Equal("Answer with citations.", reply.Text);
        Assert.Equal(new[]
        {
            new SourceLink("Release notes", "https://docs.example.com/Release"),
            new SourceLink("https://blog.example.com/post", "https://blog.example.com/post")
        }, reply.Sources);
    }

    [Fact]
    public async Task Sources_fall_back_to_web_search_call_action_sources_when_there_are_no_citations()
    {
        var handler = new ShapeHandler(_ => JsonResponse("""
            {
              "output": [
                {
                  "type": "web_search_call",
                  "status": "completed",
                  "action": {
                    "type": "search",
                    "query": "latest dotnet",
                    "sources": [
                      { "type": "url", "url": "https://one.example.com/a?utm_medium=x", "title": "One" },
                      { "type": "url", "url": "https://two.example.com/b" }
                    ]
                  }
                },
                { "type": "message", "content": [ { "type": "output_text", "text": "Uncited answer.", "annotations": [] } ] }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(new AssistantRequest("latest dotnet", 6, Frame: null, Research: true), CancellationToken.None);

        Assert.Equal("Uncited answer.", reply.Text);
        Assert.Equal(new[]
        {
            new SourceLink("One", "https://one.example.com/a"),
            new SourceLink("https://two.example.com/b", "https://two.example.com/b")
        }, reply.Sources);
    }

    [Fact]
    public async Task Research_with_no_sources_returns_an_empty_source_list()
    {
        var handler = new ShapeHandler(_ => JsonResponse(PlainAnswer));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(new AssistantRequest("look up nothing", 8, Frame: null, Research: true), CancellationToken.None);

        Assert.Empty(reply.Sources);
    }

    private static bool HasJsonSchemaFormat(JsonElement root)
    {
        if (!root.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.Object)
            return false;
        if (!text.TryGetProperty("format", out var format) || format.ValueKind != JsonValueKind.Object)
            return false;
        return format.TryGetProperty("type", out var type) &&
               type.ValueKind == JsonValueKind.String &&
               type.GetString() == "json_schema";
    }

    private static void AssertStrictJsonSchema(JsonElement root)
    {
        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString()!);
        Assert.True(format.GetProperty("strict").GetBoolean());
        Assert.Equal(JsonValueKind.Object, format.GetProperty("schema").ValueKind);
    }

    private static IReadOnlyList<string> ReadInclude(JsonElement root)
    {
        if (!root.TryGetProperty("include", out var include) || include.ValueKind != JsonValueKind.Array)
            return Array.Empty<string>();
        return include.EnumerateArray()
            .Where(item => item.ValueKind == JsonValueKind.String)
            .Select(item => item.GetString()!)
            .ToArray();
    }

    private static HttpClient CreateHttpClient(ShapeHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
    }

    private static ScreenContext CreateContext(long generation)
    {
        return new ScreenContext(
            Guid.Parse("77777777-7777-7777-7777-777777777777"),
            generation,
            new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
            1920,
            1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
            DateTimeOffset.UnixEpoch);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class ShapeHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;

        public ShapeHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
        {
            _responder = responder;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return _responder(request);
        }
    }
}
