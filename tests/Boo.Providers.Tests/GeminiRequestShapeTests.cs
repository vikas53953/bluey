using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Providers.Tests;

public sealed class GeminiRequestShapeTests
{
    private static readonly ProviderSettings Settings = new(GeminiModel: "gemini-4-argon", GeminiFocusModel: "gemini-3.8-flash");

    [Fact]
    public async Task Respond_posts_generateContent_with_header_key_schema_and_image()
    {
        var context = CreateContext(Guid.NewGuid(), 7);
        var frame = new CapturedFrame(context, new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "candidates": [
                {
                  "content": {
                    "role": "model",
                    "parts": [
                      { "thought": true, "text": "thinking about it" },
                      { "text": "{\"text\":\"Click Save in the toolbar.\",\"marks\":[{\"kind\":\"ring\",\"x\":10,\"y\":20,\"width\":30,\"height\":40,\"label\":\"Save\",\"endX\":0,\"endY\":0}],\"actions\":[]}" }
                    ]
                  },
                  "finishReason": "STOP"
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new GeminiClient(http, () => "fixture-key", Settings);
        var request = new AssistantRequest("What does this button do?", 7, frame);

        var reply = await client.RespondAsync(request, CancellationToken.None);

        var sent = Assert.IsType<HttpRequestMessage>(handler.LastRequest);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-4-argon:generateContent", sent.RequestUri!.ToString());
        Assert.Empty(sent.RequestUri.Query);
        Assert.Equal("fixture-key", Assert.Single(sent.Headers.GetValues("x-goog-api-key")));
        Assert.Null(sent.Headers.Authorization);

        var body = handler.LastBody!;
        Assert.DoesNotContain("fixture-key", body, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        Assert.Equal(StructuredReplies_StructuredPrefix, root.GetProperty("system_instruction").GetProperty("parts")[0].GetProperty("text").GetString()![..StructuredReplies_StructuredPrefix.Length]);
        var content = Assert.Single(root.GetProperty("contents").EnumerateArray());
        Assert.Equal("user", content.GetProperty("role").GetString());
        var parts = content.GetProperty("parts").EnumerateArray().ToList();
        Assert.Equal(2, parts.Count);
        Assert.Contains("User request: What does this button do?", parts[0].GetProperty("text").GetString());
        var inline = parts[1].GetProperty("inline_data");
        Assert.Equal("image/jpeg", inline.GetProperty("mime_type").GetString());
        Assert.Equal(Convert.ToBase64String(frame.Jpeg), inline.GetProperty("data").GetString());
        var generation = root.GetProperty("generationConfig");
        Assert.Equal("application/json", generation.GetProperty("responseMimeType").GetString());
        Assert.Equal(4096, generation.GetProperty("maxOutputTokens").GetInt32());
        var schema = generation.GetProperty("responseSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("additionalProperties", out _));
        var argumentSchema = schema.GetProperty("properties").GetProperty("actions").GetProperty("items").GetProperty("properties").GetProperty("arguments").GetProperty("properties").GetProperty("text");
        Assert.Equal("string", argumentSchema.GetProperty("type").GetString());
        Assert.True(argumentSchema.GetProperty("nullable").GetBoolean());
        Assert.False(root.TryGetProperty("tools", out _));

        Assert.Equal("Click Save in the toolbar.", reply.Text);
        Assert.NotNull(reply.Annotations);
        Assert.Equal(context.ObservationId, reply.Annotations!.ObservationId);
        Assert.Equal(7, reply.Annotations.Generation);
        var mark = Assert.Single(reply.Annotations.Marks);
        Assert.Equal("ring", mark.Kind);
        Assert.Equal(new RectD(10, 20, 30, 40), mark.Bounds);
        Assert.Empty(reply.Actions);
        Assert.Empty(reply.Sources);
    }

    [Fact]
    public async Task Plain_research_uses_google_search_and_maps_grounding_chunks()
    {
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "candidates": [
                {
                  "content": { "parts": [ { "text": "The answer is " }, { "text": "42." } ] },
                  "finishReason": "STOP",
                  "groundingMetadata": {
                    "groundingChunks": [
                      { "web": { "uri": "https://example.com/a", "title": "Example A" } },
                      { "web": { "uri": "https://example.com/a", "title": "Duplicate" } },
                      { "web": { "uri": "https://example.com/b" } },
                      { "web": { "uri": "ftp://example.com/c", "title": "Not web" } }
                    ]
                  }
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new GeminiClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(new AssistantRequest("What is the answer?", 1, Research: true), CancellationToken.None);

        using var document = JsonDocument.Parse(handler.LastBody!);
        var root = document.RootElement;
        Assert.True(root.GetProperty("tools")[0].TryGetProperty("google_search", out _));
        var generation = root.GetProperty("generationConfig");
        Assert.False(generation.TryGetProperty("responseSchema", out _));
        Assert.False(generation.TryGetProperty("responseMimeType", out _));
        Assert.Equal(8192, generation.GetProperty("maxOutputTokens").GetInt32());

        Assert.Equal("The answer is 42.", reply.Text);
        Assert.Collection(reply.Sources,
            source => { Assert.Equal("Example A", source.Title); Assert.Equal("https://example.com/a", source.Url); },
            source => { Assert.Equal("https://example.com/b", source.Title); Assert.Equal("https://example.com/b", source.Url); });
    }

    [Fact]
    public async Task Classify_uses_focus_model_and_validates_distribution()
    {
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 0x10, 0x20 });
        var session = new FocusSnapshot(
            Guid.NewGuid(), "Write the report", 600, DateTimeOffset.UnixEpoch, 30, 30,
            FocusStatus.Running, false, false, Array.Empty<FocusCheck>());
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "candidates": [
                {
                  "content": { "parts": [ { "text": "{\"activity\":\"off_task\",\"confidence\":0.93,\"probabilities\":{\"on_task\":0.2,\"off_task\":0.72,\"uncertain\":0.08}}" } ] },
                  "finishReason": "STOP"
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new GeminiClient(http, () => "fixture-key", Settings);

        var decision = await client.ClassifyAsync(frame, session, CancellationToken.None);

        Assert.EndsWith("/v1beta/models/gemini-3.8-flash:generateContent", handler.LastRequest!.RequestUri!.AbsoluteUri, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(handler.LastBody!);
        var schema = document.RootElement.GetProperty("generationConfig").GetProperty("responseSchema");
        Assert.Equal(new[] { "on_task", "off_task", "uncertain" }, schema.GetProperty("properties").GetProperty("activity").GetProperty("enum").EnumerateArray().Select(e => e.GetString()).ToArray());
        Assert.Equal(FocusActivity.OffTask, decision.Activity);
        Assert.Equal(0.72, decision.Probability);
        Assert.Equal(0.93, decision.Confidence);
        Assert.Equal(0.2, decision.Probabilities["on_task"]);
    }

    [Fact]
    public void Classification_rejects_a_distribution_that_does_not_sum_to_one()
    {
        var json = """{"candidates":[{"content":{"parts":[{"text":"{\"activity\":\"on_task\",\"confidence\":0.5,\"probabilities\":{\"on_task\":0.9,\"off_task\":0.5,\"uncertain\":0.1}}"}]}}]}""";
        var error = Assert.Throws<InvalidOperationException>(() => GeminiClient.DecodeClassification(json));
        Assert.Contains("distribution", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Blocked_prompt_and_http_errors_are_sanitised()
    {
        var blockedHandler = new RecordingHandler(_ => JsonResponse("""{"promptFeedback":{"blockReason":"SAFETY"}}"""));
        using var blockedHttp = CreateHttpClient(blockedHandler);
        var blocked = new GeminiClient(blockedHttp, () => "fixture-key", Settings);
        var declined = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            blocked.RespondAsync(new AssistantRequest("hi", 1), CancellationToken.None));
        Assert.Equal("Gemini declined this request.", declined.Message);

        const string secret = "super-secret-body-text";
        var errorHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("{\"error\":{\"code\":429,\"status\":\"RESOURCE_EXHAUSTED\",\"message\":\"" + secret + "\"}}", Encoding.UTF8, "application/json")
        });
        using var errorHttp = CreateHttpClient(errorHandler);
        var failing = new GeminiClient(errorHttp, () => "fixture-key", Settings);
        var failure = await Assert.ThrowsAsync<ProviderException>(() =>
            failing.RespondAsync(new AssistantRequest("hi", 1), CancellationToken.None));
        Assert.Equal(AiProvider.Gemini, failure.Provider);
        Assert.Equal(ProviderFailureKind.RateLimited, failure.Failure.Kind);
        Assert.Equal(429, failure.Failure.StatusCode);
        Assert.DoesNotContain(secret, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, failure.Failure.UserMessage, StringComparison.Ordinal);
        Assert.Contains("Gemini", failure.Failure.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Origin_credential_and_model_checks_run_before_any_request()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{}"));
        var request = new AssistantRequest("hi", 1);

        using var wrongOrigin = CreateHttpClient(handler, new Uri("https://example.com/"));
        var origin = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeminiClient(wrongOrigin, () => "k", Settings).RespondAsync(request, CancellationToken.None));
        Assert.Equal("Gemini requests require the official HTTPS API origin.", origin.Message);

        using var http = CreateHttpClient(handler);
        var missing = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeminiClient(http, () => " ", Settings).RespondAsync(request, CancellationToken.None));
        Assert.Equal(AiProviders.MissingCredentialMessage(AiProvider.Gemini), missing.Message);

        var badModel = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new GeminiClient(http, () => "k", Settings with { GeminiModel = "bad model/../x" }).RespondAsync(request, CancellationToken.None));
        Assert.Equal("Gemini model id is invalid. Check the models in Settings.", badModel.Message);

        Assert.Empty(handler.Requests);
    }

    private const string StructuredReplies_StructuredPrefix = "Return only the requested structured result.";

    private static HttpClient CreateHttpClient(RecordingHandler handler, Uri? baseAddress = null) =>
        new(handler) { BaseAddress = baseAddress ?? new Uri("https://generativelanguage.googleapis.com/") };

    private static ScreenContext CreateContext(Guid observationId, long generation) =>
        new(
            observationId,
            generation,
            new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
            1920,
            1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
            DateTimeOffset.UtcNow);

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

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
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _responder(request, cancellationToken);
        }
    }
}
