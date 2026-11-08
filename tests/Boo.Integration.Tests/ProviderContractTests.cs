using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class ProviderContractTests
{
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    [Fact]
    public async Task Responses_uses_official_transport_contract_and_binds_output_to_submitted_context()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 0x01, 0x02, 0xFE });
        const string apiKey = "fixture-key";
        var handler = new RecordingHandler(_ => JsonResponse("""
            {
              "id": "resp_fixture",
              "output": [
                {
                  "type": "message",
                  "role": "assistant",
                  "content": [
                    {
                      "type": "output_text",
                      "text": "{\"text\":\"The control is here.\",\"marks\":[{\"kind\":\"ring\",\"x\":10,\"y\":20,\"width\":30,\"height\":40,\"label\":\"Primary action\",\"endX\":50,\"endY\":60}],\"actions\":[{\"name\":\"open_url\",\"target\":\"docs\",\"arguments\":{\"url\":\"https://example.com\"},\"generation\":999,\"observationId\":\"model-invented\"}]}",
                      "annotations": [
                        { "type": "url_citation", "url": "https://example.com/source", "title": "Source" }
                      ]
                    }
                  ]
                }
              ]
            }
            """));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => apiKey, Settings);
        var request = new AssistantRequest("Explain the selected control.", 41, frame, Research: true, AllowActions: true);

        var reply = await client.RespondAsync(request, CancellationToken.None);

        Assert.Equal("The control is here.", reply.Text);
        var annotations = reply.Annotations!;
        var mark = Assert.Single(annotations.Marks);
        Assert.Equal("ring", mark.Kind);
        Assert.Equal(new RectD(10, 20, 30, 40), mark.Bounds);
        Assert.Equal("Primary action", mark.Label);
        Assert.Equal(50, mark.EndX);
        Assert.Equal(60, mark.EndY);
        var action = Assert.Single(reply.Actions);
        Assert.Equal("open_url", action.Name);
        Assert.Equal("docs", action.Target);
        Assert.Equal("https://example.com", action.Arguments["url"]);
        Assert.Equal(request.Generation, action.Generation);
        Assert.Equal(context.ObservationId, annotations.ObservationId);
        Assert.Equal(context.Generation, annotations.Generation);
        Assert.Equal(context.Display.Id, annotations.DisplayId);
        Assert.Equal(new[] { new SourceLink("Source", "https://example.com/source") }, reply.Sources);

        using var sent = JsonDocument.Parse(handler.LastBody!);
        var root = sent.RootElement;
        Assert.Equal(Settings.ReasoningModel, root.GetProperty("model").GetString()!);
        Assert.Equal("https://api.openai.com/v1/responses", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Equal(apiKey, handler.LastRequest.Headers.Authorization!.Parameter);
        Assert.Contains(root.GetProperty("tools").EnumerateArray(), tool => tool.GetProperty("type").GetString() == "web_search");
        Assert.Contains("data:image/jpeg;base64,AQL+", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain(apiKey, handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_never_leaks_credentials_to_a_non_official_origin()
    {
        var handler = new RecordingHandler(_ => JsonResponse("{\"output\":[]}"));
        using var http = CreateHttpClient(handler, new Uri("https://untrusted.example/"));
        var client = new ResponsesClient(http, () => "do-not-leak", Settings);

        await AssertNotStubAsync(() => client.RespondAsync(
            new AssistantRequest("hello", 1), CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.Headers.Authorization?.Parameter, "do-not-leak", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Responses_rejects_empty_or_malformed_results_without_echoing_provider_body()
    {
        foreach (var body in new[]
        {
            "{}",
            "{\"output\":[]}",
            "provider body contains screen secret"
        })
        {
            var handler = new RecordingHandler(_ => JsonResponse(body));
            using var http = CreateHttpClient(handler);
            var client = new ResponsesClient(http, () => "fixture-key", Settings);

            var error = await AssertNotStubAsync(() => client.RespondAsync(
                new AssistantRequest("hello", 1), CancellationToken.None));

            Assert.DoesNotContain("screen secret", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(body, error.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Responses_sanitizes_http_errors_and_honors_cancellation()
    {
        var errorHandler = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
        {
            Content = new StringContent("provider echoed the private screen", Encoding.UTF8, "application/json")
        });
        using (var http = CreateHttpClient(errorHandler))
        {
            var client = new ResponsesClient(http, () => "fixture-key", Settings);
            var error = await AssertNotStubAsync(() => client.RespondAsync(
                new AssistantRequest("hello", 1), CancellationToken.None));
            Assert.DoesNotContain("provider echoed the private screen", error.Message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("fixture-key", error.Message, StringComparison.Ordinal);
            Assert.Contains("429", error.Message, StringComparison.Ordinal);
        }

        var cancellationHandler = new RecordingHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return JsonResponse("{}");
        });
        using var cancellationHttp = CreateHttpClient(cancellationHandler);
        var cancellationClient = new ResponsesClient(cancellationHttp, () => "fixture-key", Settings);
        using var cts = new CancellationTokenSource();
        var pending = cancellationClient.RespondAsync(new AssistantRequest("hello", 1), cts.Token);
        var startedOrTimedOut = await Task.WhenAny(
            cancellationHandler.RequestStarted.Task,
            Task.Delay(TimeSpan.FromSeconds(2)));
        Assert.Same(cancellationHandler.RequestStarted.Task, startedOrTimedOut);
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.True(cancellationHandler.CancellationObserved);
    }

    [Fact]
    public async Task Decisions_posts_the_pinned_image_contract_with_the_focus_model()
    {
        var context = CreateContext(Guid.NewGuid(), 7);
        var frame = new CapturedFrame(context, new byte[] { 0x10, 0x20 });
        var session = new FocusSnapshot(
            Guid.NewGuid(), "Write the report", 600, DateTimeOffset.UnixEpoch, 30, 30,
            FocusStatus.Running, false, false, Array.Empty<FocusCheck>());
        var handler = new RecordingHandler(_ => JsonResponse(ChoiceJson()));
        using var http = CreateHttpClient(handler);
        var client = new DecisionsClient(http, () => "fixture-key", Settings);

        var decision = await client.ClassifyAsync(frame, session, CancellationToken.None);

        Assert.Equal(FocusActivity.OffTask, decision.Activity);
        Assert.Equal(0.72, decision.Probability, 3);
        Assert.Equal(0.93, decision.Confidence, 3);
        Assert.Equal("https://api.openai.com/v1/decisions", handler.LastRequest!.RequestUri!.ToString());
        Assert.Equal(Settings.FocusModel, JsonDocument.Parse(handler.LastBody!).RootElement.GetProperty("model").GetString()!);
        Assert.Equal("Bearer", handler.LastRequest.Headers.Authorization!.Scheme);
        Assert.Contains("data:image/jpeg;base64,ECA=", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-key", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Decisions_never_leaks_credentials_to_a_non_official_origin()
    {
        var handler = new RecordingHandler(_ => JsonResponse(ChoiceJson()));
        using var http = CreateHttpClient(handler, new Uri("https://untrusted.example/"));
        var client = new DecisionsClient(http, () => "do-not-leak", Settings);
        var frame = new CapturedFrame(CreateContext(Guid.NewGuid(), 1), new byte[] { 1 });
        var session = new FocusSnapshot(
            Guid.NewGuid(), "Write", 600, DateTimeOffset.UnixEpoch, 0, 0,
            FocusStatus.Running, false, false, Array.Empty<FocusCheck>());

        await AssertNotStubAsync(() => client.ClassifyAsync(frame, session, CancellationToken.None));

        Assert.DoesNotContain(handler.Requests, request =>
            string.Equals(request.Headers.Authorization?.Parameter, "do-not-leak", StringComparison.Ordinal));
    }

    [Fact]
    public void Decisions_decode_preserves_choice_probabilities_separately_from_confidence()
    {
        var decision = DecisionsClient.Decode(ChoiceJson());

        Assert.Equal(FocusActivity.OffTask, decision.Activity);
        Assert.Equal(0.72, decision.Probability, 3);
        Assert.Equal(0.93, decision.Confidence, 3);
        Assert.Equal(3, decision.Probabilities.Count);
        Assert.Equal(0.21, decision.Probabilities["on_task"], 3);
        Assert.Equal(0.72, decision.Probabilities["off_task"], 3);
        Assert.Equal(0.07, decision.Probabilities["uncertain"], 3);
    }

    [Fact]
    public void Decisions_rejects_malformed_nonfinite_and_out_of_range_answers()
    {
        var malformed = new[]
        {
            "{}",
            "{\"answers\":[]}",
            "{\"answers\":[{\"name\":\"activity\",\"type\":\"predicate\"}]}"
        };
        foreach (var json in malformed)
            AssertNotStub(() => DecisionsClient.Decode(json));

        foreach (var json in new[]
        {
            ChoiceJson(confidence: "1.01"),
            ChoiceJson(offTaskProbability: "-0.01"),
            ChoiceJson(offTaskProbability: "1e999"),
            ChoiceJson().Replace("\"uncertain\"", "\"other\"", StringComparison.Ordinal),
            ChoiceJson().Replace("\"uncertain\", \"probability\": 0.07", "\"off_task\", \"probability\": 0.07", StringComparison.Ordinal)
        })
        {
            AssertNotStub(() => DecisionsClient.Decode(json));
        }
    }

    [Fact]
    public void Realtime_session_update_uses_current_pcm24k_server_vad_transcription_and_audio_shape()
    {
        using var document = JsonDocument.Parse(RealtimeProtocol.SessionUpdate(Settings));
        var root = document.RootElement;
        Assert.Equal("session.update", root.GetProperty("type").GetString()!);
        var session = root.GetProperty("session");
        Assert.Equal("realtime", session.GetProperty("type").GetString()!);
        Assert.Equal(Settings.RealtimeModel, session.GetProperty("model").GetString()!);
        Assert.Contains(session.GetProperty("output_modalities").EnumerateArray(),
            modality => modality.GetString() == "audio");

        var audio = session.GetProperty("audio");
        var input = audio.GetProperty("input");
        Assert.Equal("audio/pcm", input.GetProperty("format").GetProperty("type").GetString()!);
        Assert.Equal(24000, input.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal("server_vad", input.GetProperty("turn_detection").GetProperty("type").GetString()!);
        Assert.True(input.GetProperty("transcription").TryGetProperty("model", out _));

        var output = audio.GetProperty("output");
        Assert.Equal("audio/pcm", output.GetProperty("format").GetProperty("type").GetString()!);
        Assert.Equal(24000, output.GetProperty("format").GetProperty("rate").GetInt32());
        Assert.Equal(Settings.Voice, output.GetProperty("voice").GetString()!);
    }

    [Fact]
    public void Realtime_protocol_parses_documented_transcript_audio_turn_error_and_tool_events()
    {
        var inputTranscript = RealtimeProtocol.Parse("""
            {"type":"conversation.item.input_audio_transcription.delta","item_id":"item-1","delta":"hello"}
            """);
        Assert.Equal("conversation.item.input_audio_transcription.delta", inputTranscript!.Kind);
        Assert.Equal("hello", inputTranscript.Text);
        Assert.Equal("item-1", inputTranscript.ItemId);

        var outputTranscript = RealtimeProtocol.Parse("""
            {"type":"response.output_audio_transcript.delta","item_id":"item-2","delta":"there"}
            """);
        Assert.Equal("response.output_audio_transcript.delta", outputTranscript!.Kind);
        Assert.Equal("there", outputTranscript.Text);

        var audio = RealtimeProtocol.Parse("""
            {"type":"response.output_audio.delta","item_id":"item-2","delta":"AQID"}
            """);
        Assert.Equal("response.output_audio.delta", audio!.Kind);
        Assert.Equal(new byte[] { 1, 2, 3 }, audio.Audio!);

        var turn = RealtimeProtocol.Parse("""
            {"type":"input_audio_buffer.speech_started"}
            """);
        Assert.Equal("input_audio_buffer.speech_started", turn!.Kind);

        var error = RealtimeProtocol.Parse("""
            {"type":"error","error":{"type":"invalid_request_error","message":"bad request"}}
            """);
        Assert.Equal("error", error!.Kind);
        Assert.False(string.IsNullOrWhiteSpace(error.Text));
        Assert.DoesNotContain("fixture-key", error.Text!, StringComparison.OrdinalIgnoreCase);

        var tool = RealtimeProtocol.Parse("""
            {"type":"response.function_call_arguments.done","call_id":"call-1","name":"open_url","arguments":"{\"url\":\"https://example.com\"}"}
            """);
        Assert.Equal("response.function_call_arguments.done", tool!.Kind);
        Assert.Equal("call-1", tool.ItemId);
        Assert.Equal("{\"url\":\"https://example.com\"}", tool.Text);

        Assert.Null(RealtimeProtocol.Parse("{\"type\":\"rate_limits.updated\"}"));
        Assert.Null(RealtimeProtocol.Parse("not-json"));
    }

    [Fact]
    public void Realtime_text_turn_is_a_current_conversation_item_create_event()
    {
        using var document = JsonDocument.Parse(RealtimeProtocol.TextTurn("show the selected control"));
        var root = document.RootElement;
        Assert.Equal("conversation.item.create", root.GetProperty("type").GetString()!);
        var item = root.GetProperty("item");
        Assert.Equal("message", item.GetProperty("type").GetString()!);
        Assert.Equal("user", item.GetProperty("role").GetString()!);
        var content = Assert.Single(item.GetProperty("content").EnumerateArray());
        Assert.Equal("input_text", content.GetProperty("type").GetString()!);
        Assert.Equal("show the selected control", content.GetProperty("text").GetString()!);
    }

    private static HttpClient CreateHttpClient(RecordingHandler handler, Uri? baseAddress = null)
    {
        return new HttpClient(handler)
        {
            BaseAddress = baseAddress ?? new Uri("https://api.openai.com/")
        };
    }

    private static ScreenContext CreateContext(Guid observationId, long generation)
    {
        return new ScreenContext(
            observationId,
            generation,
            new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
            1920,
            1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
            DateTimeOffset.UtcNow);
    }

    private static HttpResponseMessage JsonResponse(string json)
    {
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private static string ChoiceJson(string confidence = "0.93", string offTaskProbability = "0.72") => $$"""
        {
          "answers": [
            {
              "type": "choice",
              "name": "activity",
              "choice": "off_task",
              "probabilities": [
                { "value": "on_task", "probability": 0.21 },
                { "value": "off_task", "probability": {{offTaskProbability}} },
                { "value": "uncertain", "probability": 0.07 }
              ],
              "confidence": {{confidence}}
            }
          ]
        }
        """;

    private static async Task<Exception> AssertNotStubAsync(Func<Task> action)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.NotNull(error);
        Assert.IsNotType<NotImplementedException>(error!);
        return error!;
    }

    private static Exception AssertNotStub(Action action)
    {
        var error = Record.Exception(action);
        Assert.NotNull(error);
        Assert.IsNotType<NotImplementedException>(error!);
        return error!;
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> responder)
            : this((request, _) => Task.FromResult(responder(request)))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> responder)
            : this((request, _) => responder(request))
        {
        }

        public RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public List<HttpRequestMessage> Requests { get; } = [];
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            RequestStarted.TrySetResult(true);
            try
            {
                return await _responder(request, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                CancellationObserved = true;
                throw;
            }
        }
    }
}
