using System.Net;
using System.Text.Json;
using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Contract for <see cref="GeminiClient"/>: the generateContent wire format, credential hygiene (header only, never
/// the query string), and the shared structured-reply binding. Fixtures are in-memory; nothing here reaches the network.
/// </summary>
public sealed class GeminiClientTests
{
    private const string ApiKey = "AIza-fixture-key";
    private static readonly Uri Origin = new("https://generativelanguage.googleapis.com/");
    private static readonly ProviderSettings Settings = new(
        TextProvider: AiProvider.Gemini,
        GeminiModel: "gemini-test-reasoning",
        GeminiFocusModel: "gemini-test-focus");

    private static string Endpoint(string model) => $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent";

    private static GeminiClient CreateClient(HttpClient http, string? key = ApiKey) =>
        new(http, () => key, Settings);

    private static string CandidateJson(string text, string finishReason = "STOP") =>
        "{\"candidates\":[{\"content\":{\"role\":\"model\",\"parts\":[{\"text\":" + JsonSerializer.Serialize(text) + "}]}," +
        "\"finishReason\":\"" + finishReason + "\",\"index\":0}],\"modelVersion\":\"gemini-test-reasoning\"}";

    private static string ResearchJson() => """
        {
          "candidates": [
            {
              "content": {
                "role": "model",
                "parts": [
                  { "text": "Boo is a companion app. " },
                  { "text": "It lives in the notch." }
                ]
              },
              "finishReason": "STOP",
              "groundingMetadata": {
                "webSearchQueries": [ "boo companion" ],
                "groundingChunks": [
                  { "web": { "uri": "https://example.com/source", "title": "Source" } },
                  { "web": { "uri": "https://example.org/second", "title": "Second" } },
                  { "web": { "uri": "https://example.com/source", "title": "Source again" } }
                ],
                "groundingSupports": [
                  { "segment": { "startIndex": 0, "endIndex": 23, "text": "Boo is a companion app." }, "groundingChunkIndices": [ 0, 2 ] }
                ]
              }
            }
          ]
        }
        """;

    [Fact]
    public async Task Respond_posts_the_generate_content_contract_with_a_response_schema_and_binds_the_reply_to_the_request()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);
        var request = new AssistantRequest("Explain the selected control.", 41, frame, AllowActions: true);

        var reply = await client.RespondAsync(request, CancellationToken.None);

        AssertScreenHelpBound(reply, request, context);
        Assert.Empty(reply.Sources);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Endpoint(Settings.GeminiModel), sent.RequestUri!.ToString());
        Assert.Equal(string.Empty, sent.RequestUri.Query);
        Assert.Equal(ApiKey, SingleHeader(sent, "x-goog-api-key"));
        Assert.Null(sent.Headers.Authorization);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, sent.RequestUri.ToString(), StringComparison.Ordinal);

        var root = handler.LastBodyJson();
        Assert.False(root.TryGetProperty("model", out _));
        Assert.Contains("untrusted data", SystemText(root), StringComparison.Ordinal);
        Assert.Contains(context.ObservationId.ToString(), SystemText(root), StringComparison.Ordinal);

        var content = Assert.Single(root.GetProperty("contents").EnumerateArray());
        Assert.Equal("user", content.GetProperty("role").GetString());
        var parts = content.GetProperty("parts").EnumerateArray().ToList();
        var image = Assert.Single(parts, part => Property(part, "inline_data", "inlineData") is not null);
        var inline = Property(image, "inline_data", "inlineData")!.Value;
        Assert.Equal("image/jpeg", Property(inline, "mime_type", "mimeType")!.Value.GetString());
        Assert.Equal("AQL+", inline.GetProperty("data").GetString());
        Assert.Contains(parts, part => part.TryGetProperty("text", out var text) &&
            text.GetString()!.Contains("Explain the selected control.", StringComparison.Ordinal));

        var generationConfig = root.GetProperty("generationConfig");
        Assert.Equal("application/json", generationConfig.GetProperty("responseMimeType").GetString());
        var schema = generationConfig.GetProperty("responseSchema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.TryGetProperty("additionalProperties", out _));
        Assert.True(schema.GetProperty("properties").TryGetProperty("actions", out _));
        Assert.False(root.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Respond_plain_research_uses_google_search_without_a_schema_and_collects_deduplicated_sources()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ResearchJson()));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var reply = await client.RespondAsync(new AssistantRequest("What is Boo?", 3, Research: true), CancellationToken.None);

        Assert.Equal("Boo is a companion app. It lives in the notch.", reply.Text);
        Assert.Null(reply.Annotations);
        Assert.Empty(reply.Actions);
        Assert.Equal(2, reply.Sources.Count);
        Assert.Equal("https://example.com/source", reply.Sources[0].Url);
        Assert.Equal("Source", reply.Sources[0].Title);
        Assert.Equal("https://example.org/second", reply.Sources[1].Url);
        Assert.Equal("Second", reply.Sources[1].Title);

        var root = handler.LastBodyJson();
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.True(tool.TryGetProperty("google_search", out _));
        Assert.False(root.TryGetProperty("generationConfig", out var generationConfig) && generationConfig.TryGetProperty("responseSchema", out _));
        Assert.Contains("untrusted data", SystemText(root), StringComparison.Ordinal);
        Assert.Equal(Endpoint(Settings.GeminiModel), handler.LastRequest!.RequestUri!.ToString());
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Respond_rejects_actions_outside_the_bounded_catalog()
    {
        var context = CreateContext(Guid.NewGuid(), 5);
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(OutsideCatalogJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(
            new AssistantRequest("Do it.", 5, new CapturedFrame(context, new byte[] { 1 }), AllowActions: true),
            CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.DoesNotContain("rm -rf", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Respond_binds_click_and_type_text_to_observed_controls_exactly_like_the_openai_client()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 1, 2, 3 });
        var controls = BindingControls(context);
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ScreenHelpJsonWithActions(BindingActionsJson))));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var reply = await client.RespondAsync(
            new AssistantRequest("Do the requested task.", context.Generation, frame, AllowActions: true),
            CancellationToken.None,
            controls);

        AssertBindingOutcome(reply);
        Assert.Contains("\"observedControls\"", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("button-text", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeId", handler.LastBody!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"text\":\"Back to the report?\",\"pointer_target\":null}", "Back to the report?", null)]
    [InlineData("{\"text\":\"Back to the report?\",\"pointer_target\":\"Cat videos\"}", "Back to the report?", "Cat videos")]
    public async Task Nudge_uses_the_reasoning_model_and_decodes_text_and_pointer_target(string json, string text, string? pointerTarget)
    {
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 9), new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(json)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var nudge = await client.NudgeAsync(frame, "Write the report", CancellationToken.None);

        Assert.Equal(text, nudge.Text);
        Assert.Equal(pointerTarget, nudge.PointerTarget);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint(Settings.GeminiModel), sent.RequestUri!.ToString());
        Assert.Equal(ApiKey, SingleHeader(sent, "x-goog-api-key"));
        Assert.Null(sent.Headers.Authorization);
        var root = handler.LastBodyJson();
        var generationConfig = root.GetProperty("generationConfig");
        Assert.Equal("application/json", generationConfig.GetProperty("responseMimeType").GetString());
        Assert.True(generationConfig.TryGetProperty("responseSchema", out _));
        Assert.Contains("AQL+", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("Write the report", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("untrusted data", SystemText(root), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nudge_truncates_long_text_to_160_and_pointer_target_to_80_characters()
    {
        var longText = new string('a', 200);
        var longTarget = new string('b', 100);
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(
            "{\"text\":\"" + longText + "\",\"pointer_target\":\"" + longTarget + "\"}")));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var nudge = await client.NudgeAsync(new CapturedFrame(CreateContext(Guid.NewGuid(), 9), new byte[] { 1 }), "Write", CancellationToken.None);

        Assert.Equal(160, nudge.Text.Length);
        Assert.Equal(new string('a', 160), nudge.Text);
        Assert.Equal(80, nudge.PointerTarget!.Length);
        Assert.Equal(new string('b', 80), nudge.PointerTarget);
    }

    [Fact]
    public async Task Classify_uses_the_focus_model_in_the_path_and_maps_probabilities_into_a_decision()
    {
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 7), new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ClassificationJson())));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var decision = await client.ClassifyAsync(frame, CreateSession(), CancellationToken.None);

        AssertClassification(decision);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint(Settings.GeminiFocusModel), sent.RequestUri!.ToString());
        Assert.Equal(string.Empty, sent.RequestUri.Query);
        Assert.Equal(ApiKey, SingleHeader(sent, "x-goog-api-key"));
        Assert.Null(sent.Headers.Authorization);
        var root = handler.LastBodyJson();
        var generationConfig = root.GetProperty("generationConfig");
        Assert.Equal("application/json", generationConfig.GetProperty("responseMimeType").GetString());
        Assert.True(generationConfig.TryGetProperty("responseSchema", out _));
        Assert.Contains("AQL+", handler.LastBody!, StringComparison.Ordinal);
        Assert.Contains("Write the report", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);
    }

    public static TheoryData<string> InvalidClassifications => new()
    {
        ClassificationJson(onTask: "0.5", offTask: "0.5", uncertain: "0.5"),
        ClassificationJson(confidence: "1.5"),
        ClassificationJson(offTask: "-0.01"),
        ClassificationJson(activity: "napping"),
        "{\"activity\":\"off_task\"}",
        "not json"
    };

    [Theory]
    [MemberData(nameof(InvalidClassifications))]
    public async Task Classify_rejects_answers_that_are_not_a_valid_distribution(string answer)
    {
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(answer)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.ClassifyAsync(
            new CapturedFrame(CreateContext(Guid.NewGuid(), 7), new byte[] { 1 }), CreateSession(), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.DoesNotContain("napping", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, "{\"error\":{\"code\":401,\"message\":\"private-screen-echo\",\"status\":\"UNAUTHENTICATED\"}}", ProviderFailureKind.InvalidCredential, false)]
    [InlineData(400, "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\"}}", ProviderFailureKind.InvalidCredential, false)]
    [InlineData(429, "{\"error\":{\"code\":429,\"message\":\"private-screen-echo\",\"status\":\"RESOURCE_EXHAUSTED\"}}", ProviderFailureKind.RateLimited, true)]
    [InlineData(403, "{\"error\":{\"code\":403,\"message\":\"private-screen-echo\",\"status\":\"PERMISSION_DENIED\"}}", ProviderFailureKind.PermissionDenied, false)]
    [InlineData(503, "{\"error\":{\"code\":503,\"message\":\"private-screen-echo\",\"status\":\"UNAVAILABLE\"}}", ProviderFailureKind.ServerError, true)]
    [InlineData(500, "private-screen-echo not even json", ProviderFailureKind.ServerError, true)]
    public async Task Http_failures_are_classified_for_gemini_and_never_echo_the_body(int status, string body, ProviderFailureKind kind, bool retryable)
    {
        var handler = new RecordingHandler(_ => JsonResponse(body, (HttpStatusCode)status));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));

        var provider = Assert.IsType<ProviderException>(error);
        Assert.Equal(AiProvider.Gemini, provider.Provider);
        Assert.Equal(kind, provider.Failure.Kind);
        Assert.Equal(status, provider.Failure.StatusCode);
        Assert.Equal(retryable, provider.Failure.Retryable);
        Assert.Contains(status.ToString(), provider.Message, StringComparison.Ordinal);
        Assert.StartsWith("Gemini ", provider.Message, StringComparison.Ordinal);
        Assert.Contains("Gemini", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-screen-echo", provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Please pass a valid API key", provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, provider.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Classify_and_nudge_report_http_failures_as_gemini_provider_exceptions()
    {
        var handler = new RecordingHandler(_ => JsonResponse(
            "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\"}}",
            HttpStatusCode.BadRequest));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 7), new byte[] { 1 });

        var classify = Assert.IsType<ProviderException>(await AssertNotStubAsync(() => client.ClassifyAsync(frame, CreateSession(), CancellationToken.None)));
        var nudge = Assert.IsType<ProviderException>(await AssertNotStubAsync(() => client.NudgeAsync(frame, "Write", CancellationToken.None)));

        foreach (var error in new[] { classify, nudge })
        {
            Assert.Equal(AiProvider.Gemini, error.Provider);
            Assert.Equal(ProviderFailureKind.InvalidCredential, error.Failure.Kind);
            Assert.Contains("Gemini", error.Failure.UserMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("Please pass", error.ToString(), StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> UnusableBodies => new()
    {
        "",
        "{}",
        "{\"candidates\":[]}",
        "{\"candidates\":[{\"content\":{\"parts\":[]},\"finishReason\":\"STOP\"}]}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"\"}]},\"finishReason\":\"STOP\"}]}",
        "provider body contains screen secret",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"screen secret\"}]},\"finishReason\":\"SAFETY\"}]}",
        "{\"candidates\":[{\"finishReason\":\"PROHIBITED_CONTENT\"}]}",
        "{\"promptFeedback\":{\"blockReason\":\"SAFETY\"},\"candidates\":[]}",
        "{\"candidates\":[{\"content\":{\"parts\":[{\"text\":\"{\\\"text\\\":\\\"screen secret\"}]},\"finishReason\":\"MAX_TOKENS\"}]}"
    };

    [Theory]
    [MemberData(nameof(UnusableBodies))]
    public async Task Respond_rejects_empty_malformed_blocked_or_truncated_results_without_echoing_the_body(string body)
    {
        var context = CreateContext(Guid.NewGuid(), 2);
        var handler = new RecordingHandler(_ => JsonResponse(body));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(
            new AssistantRequest("hello", 2, new CapturedFrame(context, new byte[] { 1 })), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.IsNotType<ProviderException>(error);
        Assert.DoesNotContain("screen secret", error.Message, StringComparison.OrdinalIgnoreCase);
        if (body.Length > 0)
            Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Safety_finish_reason_names_gemini_and_not_the_blocked_text()
    {
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson("screen secret", finishReason: "SAFETY")));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 2, Research: true), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Contains("Gemini", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("screen secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_key_throws_the_exact_missing_credential_message_and_sends_nothing(string? key)
    {
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http, key);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 });

        var respond = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));
        var nudge = await AssertNotStubAsync(() => client.NudgeAsync(frame, "Write", CancellationToken.None));
        var classify = await AssertNotStubAsync(() => client.ClassifyAsync(frame, CreateSession(), CancellationToken.None));

        foreach (var error in new[] { respond, nudge, classify })
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(AiProviders.MissingCredentialMessage(AiProvider.Gemini), error.Message);
            Assert.True(AiProviders.IsMissingCredentialMessage(error.Message));
        }
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://untrusted.example/")]
    [InlineData("http://generativelanguage.googleapis.com/")]
    [InlineData("https://generativelanguage.googleapis.com:8443/")]
    [InlineData("https://generativelanguage.googleapis.com.evil.example/")]
    public async Task Non_official_origins_never_receive_the_key(string baseAddress)
    {
        const string key = "do-not-leak";
        var credentialReads = 0;
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, new Uri(baseAddress));
        var client = new GeminiClient(http, () => { credentialReads++; return key; }, Settings);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 });

        var respond = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));
        await AssertNotStubAsync(() => client.NudgeAsync(frame, "Write", CancellationToken.None));
        await AssertNotStubAsync(() => client.ClassifyAsync(frame, CreateSession(), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(respond);
        Assert.Equal(0, credentialReads);
        AssertNoRequestCarriesKey(handler, key);
        Assert.DoesNotContain(key, respond.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_surfaces_as_operation_canceled()
    {
        var handler = RecordingHandler.Stalled();
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);
        using var cts = new CancellationTokenSource();

        var pending = client.RespondAsync(new AssistantRequest("hello", 1), cts.Token);
        var startedOrTimedOut = await Task.WhenAny(handler.RequestStarted.Task, Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(handler.RequestStarted.Task, startedOrTimedOut);
        cts.Cancel();

        var error = await Record.ExceptionAsync(() => pending);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.IsNotType<ResponseTimeoutException>(error);
        Assert.True(handler.CancellationObserved);
    }

    [Fact]
    public async Task Cancellation_before_send_throws_without_sending()
    {
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.RespondAsync(new AssistantRequest("hello", 1), cts.Token));
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Elapsed_http_timeout_surfaces_as_response_timeout()
    {
        var handler = RecordingHandler.Stalled();
        using var http = CreateHttpClient(handler, Origin, Deadline);
        var client = CreateClient(http);

        var error = await CaptureFailureWithWatchdogAsync(token => client.RespondAsync(new AssistantRequest("hello", 1), token));

        Assert.IsType<ResponseTimeoutException>(error);
        Assert.True(handler.CancellationObserved);
        Assert.DoesNotContain(ApiKey, error.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Nudge_and_classify_honour_the_http_timeout()
    {
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 });

        var nudgeHandler = RecordingHandler.Stalled();
        using (var http = CreateHttpClient(nudgeHandler, Origin, Deadline))
        {
            var client = CreateClient(http);
            var error = await CaptureFailureWithWatchdogAsync(token => client.NudgeAsync(frame, "Write", token));
            Assert.IsType<ResponseTimeoutException>(error);
        }

        var classifyHandler = RecordingHandler.Stalled();
        using (var http = CreateHttpClient(classifyHandler, Origin, Deadline))
        {
            var client = CreateClient(http);
            var error = await CaptureFailureWithWatchdogAsync(token => client.ClassifyAsync(frame, CreateSession(), token));
            Assert.IsType<ResponseTimeoutException>(error);
        }
    }

    [Fact]
    public async Task Transport_failures_are_classified_for_gemini()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException(HttpRequestError.SecureConnectionError, "tls " + ApiKey));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));

        var provider = Assert.IsType<ProviderException>(error);
        Assert.Equal(AiProvider.Gemini, provider.Provider);
        Assert.Equal(ProviderFailureKind.SecureConnection, provider.Failure.Kind);
        Assert.Contains("Gemini", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, provider.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("gemini/../evil")]
    [InlineData("gemini?key=steal")]
    [InlineData("gemini model")]
    [InlineData("")]
    public async Task Invalid_model_ids_are_refused_before_any_request_because_the_model_is_in_the_path(string model)
    {
        var handler = new RecordingHandler(_ => JsonResponse(CandidateJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = new GeminiClient(http, () => ApiKey, Settings with { GeminiModel = model, GeminiFocusModel = model });
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 });

        var respond = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));
        await AssertNotStubAsync(() => client.ClassifyAsync(frame, CreateSession(), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(respond);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Gemini accepts <c>system_instruction</c> or <c>systemInstruction</c>; the test reads either.</summary>
    private static string SystemText(JsonElement root)
    {
        var system = Property(root, "system_instruction", "systemInstruction");
        Assert.True(system is not null, "The request has no system instruction.");
        return string.Concat(AllStrings(system!.Value));
    }
}
