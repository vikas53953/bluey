using System.Net;
using System.Text.Json;
using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Contract for <see cref="AnthropicClient"/>: the Messages API wire format, credential hygiene, and the shared
/// structured-reply binding. Fixtures are in-memory; nothing here reaches the network.
/// </summary>
public sealed class AnthropicClientTests
{
    private const string ApiKey = "sk-ant-fixture-key";
    private const string Endpoint = "https://api.anthropic.com/v1/messages";
    private static readonly Uri Origin = new("https://api.anthropic.com/");
    private static readonly ProviderSettings Settings = new(
        TextProvider: AiProvider.Anthropic,
        AnthropicModel: "claude-test-reasoning",
        AnthropicFocusModel: "claude-test-focus");

    private static AnthropicClient CreateClient(HttpClient http, string? key = ApiKey) =>
        new(http, () => key, Settings);

    private static string MessageJson(string text, string stopReason = "end_turn") =>
        "{\"id\":\"msg_fixture\",\"type\":\"message\",\"role\":\"assistant\",\"model\":\"claude-test-reasoning\"," +
        "\"stop_reason\":\"" + stopReason + "\",\"content\":[{\"type\":\"text\",\"text\":" + JsonSerializer.Serialize(text) + "}]}";

    private static string ResearchJson() => """
        {
          "id": "msg_research",
          "type": "message",
          "role": "assistant",
          "stop_reason": "end_turn",
          "content": [
            { "type": "server_tool_use", "id": "srvtoolu_1", "name": "web_search", "input": { "query": "boo companion" } },
            {
              "type": "web_search_tool_result",
              "tool_use_id": "srvtoolu_1",
              "content": [
                { "type": "web_search_result", "url": "https://example.com/source", "title": "Source", "encrypted_content": "x", "page_age": null },
                { "type": "web_search_result", "url": "https://example.org/second", "title": "Second", "encrypted_content": "y", "page_age": null }
              ]
            },
            {
              "type": "text",
              "text": "Boo is a companion app. ",
              "citations": [
                { "type": "web_search_result_location", "url": "https://example.com/source", "title": "Source", "cited_text": "Boo is", "encrypted_index": "z" }
              ]
            },
            { "type": "text", "text": "It lives in the notch." }
          ]
        }
        """;

    [Fact]
    public async Task Respond_posts_the_messages_contract_with_structured_output_and_binds_the_reply_to_the_request()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);
        var request = new AssistantRequest("Explain the selected control.", 41, frame, AllowActions: true);

        var reply = await client.RespondAsync(request, CancellationToken.None);

        AssertScreenHelpBound(reply, request, context);
        Assert.Empty(reply.Sources);

        var sent = Assert.Single(handler.Requests);
        Assert.Equal(HttpMethod.Post, sent.Method);
        Assert.Equal(Endpoint, sent.RequestUri!.ToString());
        Assert.Equal(ApiKey, SingleHeader(sent, "x-api-key"));
        Assert.Equal("2026-10-21", SingleHeader(sent, "anthropic-version"));
        Assert.Null(sent.Headers.Authorization);
        Assert.Equal("application/json", sent.Content!.Headers.ContentType!.MediaType);
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, sent.RequestUri.ToString(), StringComparison.Ordinal);

        var root = handler.LastBodyJson();
        Assert.Equal(Settings.AnthropicModel, root.GetProperty("model").GetString());
        Assert.True(root.GetProperty("max_tokens").GetInt32() > 0);
        Assert.Contains("untrusted data", SystemText(root), StringComparison.Ordinal);
        Assert.Contains(context.ObservationId.ToString(), SystemText(root), StringComparison.Ordinal);

        var message = Assert.Single(root.GetProperty("messages").EnumerateArray());
        Assert.Equal("user", message.GetProperty("role").GetString());
        var image = Assert.Single(message.GetProperty("content").EnumerateArray(), part => part.GetProperty("type").GetString() == "image");
        var source = image.GetProperty("source");
        Assert.Equal("base64", source.GetProperty("type").GetString());
        Assert.Equal("image/jpeg", source.GetProperty("media_type").GetString());
        Assert.Equal("AQL+", source.GetProperty("data").GetString());
        Assert.Contains(message.GetProperty("content").EnumerateArray(), part =>
            part.GetProperty("type").GetString() == "text" &&
            part.GetProperty("text").GetString()!.Contains("Explain the selected control.", StringComparison.Ordinal));

        var format = root.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        var schema = format.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString());
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());
        Assert.True(schema.GetProperty("properties").TryGetProperty("actions", out _));
        Assert.False(root.TryGetProperty("tool_choice", out _));
        Assert.False(root.TryGetProperty("tools", out _));
    }

    [Fact]
    public async Task Respond_plain_research_uses_the_web_search_tool_without_a_schema_and_collects_deduplicated_sources()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ResearchJson()));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var reply = await client.RespondAsync(new AssistantRequest("What is Boo?", 3, Research: true), CancellationToken.None);

        Assert.Equal("Boo is a companion app. It lives in the notch.", reply.Text);
        Assert.Null(reply.Annotations);
        Assert.Empty(reply.Actions);
        Assert.Equal(2, reply.Sources.Count);
        Assert.Contains(reply.Sources, link => link.Url == "https://example.com/source" && link.Title == "Source");
        Assert.Contains(reply.Sources, link => link.Url == "https://example.org/second" && link.Title == "Second");
        Assert.Equal(reply.Sources.Select(link => link.Url).Distinct(StringComparer.Ordinal).Count(), reply.Sources.Count);

        var root = handler.LastBodyJson();
        var tool = Assert.Single(root.GetProperty("tools").EnumerateArray());
        Assert.Equal("web_search_20260209", tool.GetProperty("type").GetString());
        Assert.Equal("web_search", tool.GetProperty("name").GetString());
        Assert.False(root.TryGetProperty("output_config", out var outputConfig) && outputConfig.TryGetProperty("format", out _));
        Assert.Contains("untrusted data", SystemText(root), StringComparison.Ordinal);
        Assert.Equal(Settings.AnthropicModel, root.GetProperty("model").GetString());
        Assert.DoesNotContain(ApiKey, handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Respond_rejects_actions_outside_the_bounded_catalog()
    {
        var context = CreateContext(Guid.NewGuid(), 5);
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(OutsideCatalogJson)));
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
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ScreenHelpJsonWithActions(BindingActionsJson))));
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
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(json)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var nudge = await client.NudgeAsync(frame, "Write the report", CancellationToken.None);

        Assert.Equal(text, nudge.Text);
        Assert.Equal(pointerTarget, nudge.PointerTarget);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint, sent.RequestUri!.ToString());
        Assert.Equal(ApiKey, SingleHeader(sent, "x-api-key"));
        Assert.Null(sent.Headers.Authorization);
        var root = handler.LastBodyJson();
        Assert.Equal(Settings.AnthropicModel, root.GetProperty("model").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
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
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(
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
    public async Task Classify_uses_the_focus_model_and_maps_probabilities_into_a_decision()
    {
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 7), new byte[] { 0x01, 0x02, 0xFE });
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ClassificationJson())));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var decision = await client.ClassifyAsync(frame, CreateSession(), CancellationToken.None);

        AssertClassification(decision);
        var sent = Assert.Single(handler.Requests);
        Assert.Equal(Endpoint, sent.RequestUri!.ToString());
        Assert.Equal(ApiKey, SingleHeader(sent, "x-api-key"));
        Assert.Equal("2026-10-21", SingleHeader(sent, "anthropic-version"));
        Assert.Null(sent.Headers.Authorization);
        var root = handler.LastBodyJson();
        Assert.Equal(Settings.AnthropicFocusModel, root.GetProperty("model").GetString());
        Assert.Equal("json_schema", root.GetProperty("output_config").GetProperty("format").GetProperty("type").GetString());
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
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(answer)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.ClassifyAsync(
            new CapturedFrame(CreateContext(Guid.NewGuid(), 7), new byte[] { 1 }), CreateSession(), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.DoesNotContain("napping", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(401, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key private-screen-echo\"}}", ProviderFailureKind.InvalidCredential, false)]
    [InlineData(529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded private-screen-echo\"}}", ProviderFailureKind.ServerError, true)]
    [InlineData(429, "{\"type\":\"error\",\"error\":{\"type\":\"rate_limit_error\",\"message\":\"private-screen-echo\"}}", ProviderFailureKind.RateLimited, true)]
    [InlineData(500, "private-screen-echo not even json", ProviderFailureKind.ServerError, true)]
    public async Task Http_failures_are_classified_for_anthropic_and_never_echo_the_body(int status, string body, ProviderFailureKind kind, bool retryable)
    {
        var handler = new RecordingHandler(_ => JsonResponse(body, (HttpStatusCode)status));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));

        var provider = Assert.IsType<ProviderException>(error);
        Assert.Equal(AiProvider.Anthropic, provider.Provider);
        Assert.Equal(kind, provider.Failure.Kind);
        Assert.Equal(status, provider.Failure.StatusCode);
        Assert.Equal(retryable, provider.Failure.Retryable);
        Assert.Contains(status.ToString(), provider.Message, StringComparison.Ordinal);
        Assert.StartsWith("Anthropic ", provider.Message, StringComparison.Ordinal);
        Assert.Contains("Anthropic", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("private-screen-echo", provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private-screen-echo", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, provider.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Classify_and_nudge_report_http_failures_as_anthropic_provider_exceptions()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"nope\"}}", HttpStatusCode.Unauthorized));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 7), new byte[] { 1 });

        var classify = Assert.IsType<ProviderException>(await AssertNotStubAsync(() => client.ClassifyAsync(frame, CreateSession(), CancellationToken.None)));
        var nudge = Assert.IsType<ProviderException>(await AssertNotStubAsync(() => client.NudgeAsync(frame, "Write", CancellationToken.None)));

        foreach (var error in new[] { classify, nudge })
        {
            Assert.Equal(AiProvider.Anthropic, error.Provider);
            Assert.Equal(ProviderFailureKind.InvalidCredential, error.Failure.Kind);
            Assert.Contains("Anthropic", error.Failure.UserMessage, StringComparison.Ordinal);
            Assert.DoesNotContain("nope", error.ToString(), StringComparison.Ordinal);
        }
    }

    public static TheoryData<string> UnusableBodies => new()
    {
        "",
        "{}",
        "{\"content\":[]}",
        "{\"content\":[{\"type\":\"text\",\"text\":\"\"}]}",
        "provider body contains screen secret",
        "{\"id\":\"msg\",\"stop_reason\":\"refusal\",\"content\":[{\"type\":\"text\",\"text\":\"screen secret\"}]}",
        "{\"id\":\"msg\",\"stop_reason\":\"max_tokens\",\"content\":[{\"type\":\"text\",\"text\":\"{\\\"text\\\":\\\"screen secret\"}]}"
    };

    [Theory]
    [MemberData(nameof(UnusableBodies))]
    public async Task Respond_rejects_empty_malformed_refused_or_truncated_results_without_echoing_the_body(string body)
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
    public async Task Refusal_stop_reason_names_anthropic_and_not_the_refused_text()
    {
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson("screen secret", stopReason: "refusal")));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 2, Research: true), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Contains("Anthropic", error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("screen secret", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Missing_key_throws_the_exact_missing_credential_message_and_sends_nothing(string? key)
    {
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http, key);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 });

        var respond = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));
        var nudge = await AssertNotStubAsync(() => client.NudgeAsync(frame, "Write", CancellationToken.None));
        var classify = await AssertNotStubAsync(() => client.ClassifyAsync(frame, CreateSession(), CancellationToken.None));

        foreach (var error in new[] { respond, nudge, classify })
        {
            Assert.IsType<InvalidOperationException>(error);
            Assert.Equal(AiProviders.MissingCredentialMessage(AiProvider.Anthropic), error.Message);
            Assert.True(AiProviders.IsMissingCredentialMessage(error.Message));
        }
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("https://untrusted.example/")]
    [InlineData("http://api.anthropic.com/")]
    [InlineData("https://api.anthropic.com:8443/")]
    [InlineData("https://api.anthropic.com.evil.example/")]
    public async Task Non_official_origins_never_receive_the_key(string baseAddress)
    {
        const string key = "do-not-leak";
        var credentialReads = 0;
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, new Uri(baseAddress));
        var client = new AnthropicClient(http, () => { credentialReads++; return key; }, Settings);
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
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ScreenHelpJson)));
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
    public async Task Transport_failures_are_classified_for_anthropic()
    {
        var handler = new RecordingHandler((_, _) => throw new HttpRequestException(HttpRequestError.NameResolutionError, "no such host " + ApiKey));
        using var http = CreateHttpClient(handler, Origin);
        var client = CreateClient(http);

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));

        var provider = Assert.IsType<ProviderException>(error);
        Assert.Equal(AiProvider.Anthropic, provider.Provider);
        Assert.Equal(ProviderFailureKind.Offline, provider.Failure.Kind);
        Assert.Contains("Anthropic", provider.Failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain(ApiKey, provider.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invalid_model_ids_are_refused_before_any_request()
    {
        var handler = new RecordingHandler(_ => JsonResponse(MessageJson(ScreenHelpJson)));
        using var http = CreateHttpClient(handler, Origin);
        var client = new AnthropicClient(http, () => ApiKey, Settings with { AnthropicModel = "claude/../evil" });

        var error = await AssertNotStubAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));

        Assert.IsAssignableFrom<InvalidOperationException>(error);
        Assert.Empty(handler.Requests);
    }

    /// <summary>Anthropic's system prompt may be a string or an array of text blocks; both are read.</summary>
    private static string SystemText(JsonElement root)
    {
        Assert.True(root.TryGetProperty("system", out var system), "The request has no system prompt.");
        return system.ValueKind == JsonValueKind.String
            ? system.GetString()!
            : string.Concat(AllStrings(system));
    }
}
