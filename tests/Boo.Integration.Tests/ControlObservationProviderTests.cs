using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class ControlObservationProviderTests
{
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    [Fact]
    public async Task Responses_sends_observed_controls_as_a_separate_exact_untrusted_json_input()
    {
        var context = CreateContext(Guid.Parse("11111111-2222-3333-4444-555555555555"), 41);
        var frame = new CapturedFrame(context, new byte[] { 0x01, 0x02, 0xFE });
        var controls = ControlObservation.Create(context, new[]
        {
            new ControlCandidate(
                "button-text", "Button text", "Button", "Visible label",
                new RectD(40, 50, 120, 36), new[] { 4, 5 },
                IsEnabled: true, IsOffscreen: false, IsPassword: false,
                CanInvoke: true, CanSetValue: false),
            new ControlCandidate(
                "secret-control", "Secret", "Edit", "do not send this",
                new RectD(40, 100, 200, 36), new[] { 9 },
                IsEnabled: true, IsOffscreen: false, IsPassword: true,
                CanInvoke: false, CanSetValue: true)
        });
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse("[]")));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        await client.RespondAsync(
            new AssistantRequest("Explain the selected control.", context.Generation, frame, AllowActions: true),
            CancellationToken.None,
            controls);

        using var request = JsonDocument.Parse(handler.LastBody!);
        var userInput = Assert.Single(
            request.RootElement.GetProperty("input").EnumerateArray(),
            item => item.GetProperty("role").GetString() == "user");
        var controlContent = Assert.Single(
            userInput.GetProperty("content").EnumerateArray(),
            IsObservedControlsInput);
        var controlText = controlContent.GetProperty("text").GetString()!;
        using var controlsDocument = JsonDocument.Parse(controlText);
        var controlsObject = controlsDocument.RootElement;
        AssertExactProperties(controlsObject, "observedControls");
        var observed = Assert.Single(controlsObject.GetProperty("observedControls").EnumerateArray());
        AssertExactProperties(observed, "automationId", "name", "controlType", "value", "bounds", "canInvoke", "canSetValue");
        Assert.Equal("button-text", observed.GetProperty("automationId").GetString());
        Assert.Equal("Button text", observed.GetProperty("name").GetString());
        Assert.Equal("Button", observed.GetProperty("controlType").GetString());
        Assert.Equal("Visible label", observed.GetProperty("value").GetString());
        var bounds = observed.GetProperty("bounds");
        AssertExactProperties(bounds, "x", "y", "width", "height");
        Assert.Equal(40, bounds.GetProperty("x").GetDouble());
        Assert.Equal(50, bounds.GetProperty("y").GetDouble());
        Assert.Equal(120, bounds.GetProperty("width").GetDouble());
        Assert.Equal(36, bounds.GetProperty("height").GetDouble());
        Assert.True(observed.GetProperty("canInvoke").GetBoolean());
        Assert.False(observed.GetProperty("canSetValue").GetBoolean());

        var instructions = request.RootElement.GetProperty("instructions").GetString()!;
        Assert.DoesNotContain("Button text", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("Visible label", instructions, StringComparison.Ordinal);
        Assert.DoesNotContain("runtimeId", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-control", handler.LastBody!, StringComparison.Ordinal);
        Assert.DoesNotContain("do not send this", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Responses_rejects_controls_without_a_frame_before_credentials_or_transport()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var controls = ControlObservation.Create(context, new[] { ButtonCandidate() });
        var credentialReads = 0;
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse("[]")));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () =>
        {
            credentialReads++;
            return "fixture-key";
        }, Settings);

        var error = await Record.ExceptionAsync(() => client.RespondAsync(
            new AssistantRequest("Explain", context.Generation),
            CancellationToken.None,
            controls));

        Assert.NotNull(error);
        Assert.Equal(0, credentialReads);
        Assert.Empty(handler.Requests);
    }

    [Theory]
    [InlineData("observation-id")]
    [InlineData("display")]
    [InlineData("target")]
    [InlineData("frame")]
    [InlineData("captured-at")]
    public async Task Responses_rejects_a_control_observation_with_mismatched_exact_context_before_credentials_or_transport(string changedPart)
    {
        var frameContext = CreateContext(Guid.NewGuid(), 41);
        var controlsContext = changedPart switch
        {
            "observation-id" => frameContext with { ObservationId = Guid.NewGuid() },
            "display" => frameContext with
            {
                Display = new DisplayInfo("display-2", "Other fixture", frameContext.Display.PixelBounds, frameContext.Display.Scale)
            },
            "target" => frameContext with
            {
                Target = new WindowIdentity(12, frameContext.Target.ProcessId, frameContext.Target.ProcessName, frameContext.Target.Bounds)
            },
            "frame" => frameContext with { FrameWidth = 1600, FrameHeight = 900 },
            "captured-at" => frameContext with { CapturedAt = frameContext.CapturedAt.AddMinutes(1) },
            _ => throw new ArgumentOutOfRangeException(nameof(changedPart), changedPart, "Unknown context mutation")
        };
        var controls = ControlObservation.Create(controlsContext, new[] { ButtonCandidate() });
        var credentialReads = 0;
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse("[]")));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () =>
        {
            credentialReads++;
            return "fixture-key";
        }, Settings);

        var error = await Record.ExceptionAsync(() => client.RespondAsync(
            new AssistantRequest("Explain", frameContext.Generation, new CapturedFrame(frameContext, new byte[] { 1 })),
            CancellationToken.None,
            controls));

        Assert.NotNull(error);
        Assert.Equal(0, credentialReads);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Responses_rejects_a_request_generation_mismatch_before_credentials_or_transport()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var controls = ControlObservation.Create(context, new[] { ButtonCandidate() });
        var credentialReads = 0;
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse("[]")));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () =>
        {
            credentialReads++;
            return "fixture-key";
        }, Settings);

        var error = await Record.ExceptionAsync(() => client.RespondAsync(
            new AssistantRequest("Explain", context.Generation + 1, new CapturedFrame(context, new byte[] { 1 })),
            CancellationToken.None,
            controls));

        Assert.NotNull(error);
        Assert.Equal(0, credentialReads);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task Responses_filters_observed_click_and_type_targets_fills_names_and_preserves_other_proposal_order()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var frame = new CapturedFrame(context, new byte[] { 1, 2, 3 });
        var controls = ControlObservation.Create(context, new[]
        {
            ButtonCandidate(),
            new ControlCandidate(
                "edit-field", "Editor", "Edit", "old value",
                new RectD(40, 100, 200, 36), new[] { 6 },
                IsEnabled: true, IsOffscreen: false, IsPassword: false,
                CanInvoke: false, CanSetValue: true)
        });
        var actions = """
            [
              { "name": "click", "target": "known-button", "arguments": { "automationId": "button-text", "name": "Button text" } },
              { "name": "click", "target": "filled-name", "arguments": { "automationId": "button-text" } },
              { "name": "click", "target": "case-mismatched-id", "arguments": { "automationId": "BUTTON-TEXT", "name": "Button text" } },
              { "name": "click", "target": "case-mismatched-name", "arguments": { "automationId": "button-text", "name": "button text" } },
              { "name": "click", "target": "unknown", "arguments": { "automationId": "missing", "name": "Missing" } },
              { "name": "click", "target": "wrong-name", "arguments": { "automationId": "button-text", "name": "Wrong" } },
              { "name": "type_text", "target": "editor", "arguments": { "automationId": "edit-field", "text": "hello" } },
              { "name": "type_text", "target": "editor-without-id", "arguments": { "name": "Editor", "text": "no-id" } },
              { "name": "type_text", "target": "button", "arguments": { "automationId": "button-text", "text": "not-settable" } },
              { "name": "search", "target": "other-proposal", "arguments": { "text": "needle" } },
              { "name": "click", "target": "pixel", "arguments": { "x": "14", "y": "17" } }
            ]
            """;
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse(actions)));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Do the requested task.", context.Generation, frame, AllowActions: true),
            CancellationToken.None,
            controls);

        Assert.Equal(new[] { "click", "click", "type_text", "search", "click" }, reply.Actions.Select(action => action.Name));

        var exactClick = reply.Actions[0];
        Assert.Equal("button-text", exactClick.Arguments["automationId"]);
        Assert.Equal("Button text", exactClick.Arguments["name"]);

        var filledClick = reply.Actions[1];
        Assert.Equal("button-text", filledClick.Arguments["automationId"]);
        Assert.Equal("Button text", filledClick.Arguments["name"]);

        var matchedType = reply.Actions[2];
        Assert.Equal("edit-field", matchedType.Arguments["automationId"]);
        Assert.Equal("Editor", matchedType.Arguments["name"]);
        Assert.Equal("hello", matchedType.Arguments["text"]);

        Assert.Equal("needle", reply.Actions[3].Arguments["text"]);
        Assert.Equal("14", reply.Actions[4].Arguments["x"]);
        Assert.Equal("17", reply.Actions[4].Arguments["y"]);
        Assert.DoesNotContain("automationId", reply.Actions[4].Arguments.Keys);
    }

    [Fact]
    public async Task Responses_with_empty_observation_suppresses_id_actions_and_no_id_typing_but_keeps_coordinate_click()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var controls = ControlObservation.Create(context, Array.Empty<ControlCandidate>());
        var actions = """
            [
              { "name": "click", "target": "observed-id", "arguments": { "automationId": "button-text", "name": "Button text" } },
              { "name": "type_text", "target": "no-id", "arguments": { "name": "Button text", "text": "should be blocked" } },
              { "name": "type_text", "target": "id", "arguments": { "automationId": "button-text", "text": "should be blocked" } },
              { "name": "click", "target": "pixel", "arguments": { "x": "14", "y": "17" } }
            ]
            """;
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse(actions)));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Do the requested task.", context.Generation, new CapturedFrame(context, new byte[] { 1 }), AllowActions: true),
            CancellationToken.None,
            controls);

        var action = Assert.Single(reply.Actions);
        Assert.Equal("click", action.Name);
        Assert.Equal("14", action.Arguments["x"]);
        Assert.Equal("17", action.Arguments["y"]);
        Assert.DoesNotContain("automationId", action.Arguments.Keys);
    }

    [Fact]
    public async Task Responses_preserves_the_old_two_argument_contract_without_observation_filtering()
    {
        var context = CreateContext(Guid.NewGuid(), 41);
        var handler = new RecordingHandler(_ => JsonResponse(StructuredResponse("""
            [{ "name": "click", "target": "model-target", "arguments": { "automationId": "model-invented", "name": "Model name" } }]
            """)));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(
            new AssistantRequest("Explain", context.Generation, new CapturedFrame(context, new byte[] { 1 }), AllowActions: true),
            CancellationToken.None);

        var action = Assert.Single(reply.Actions);
        Assert.Equal("model-invented", action.Arguments["automationId"]);
        Assert.Equal("Model name", action.Arguments["name"]);
        Assert.DoesNotContain("observedControls", handler.LastBody!, StringComparison.Ordinal);
    }

    private static void AssertExactProperties(JsonElement element, params string[] expectedNames)
    {
        Assert.Equal(JsonValueKind.Object, element.ValueKind);
        var actualNames = element.EnumerateObject().Select(property => property.Name).ToArray();
        Assert.Equal(expectedNames.Length, actualNames.Length);
        Assert.True(
            expectedNames.ToHashSet(StringComparer.Ordinal).SetEquals(actualNames),
            $"Expected properties [{string.Join(", ", expectedNames)}], got [{string.Join(", ", actualNames)}].");
    }

    private static bool IsObservedControlsInput(JsonElement content)
    {
        if (content.GetProperty("type").GetString() != "input_text")
            return false;

        var text = content.GetProperty("text").GetString();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        try
        {
            using var candidate = JsonDocument.Parse(text);
            return candidate.RootElement.ValueKind == JsonValueKind.Object &&
                   candidate.RootElement.TryGetProperty("observedControls", out _);
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static ControlCandidate ButtonCandidate() => new(
        "button-text", "Button text", "Button", "Visible label",
        new RectD(40, 50, 120, 36), new[] { 4, 5 },
        IsEnabled: true, IsOffscreen: false, IsPassword: false,
        CanInvoke: true, CanSetValue: false);

    private static ScreenContext CreateContext(Guid observationId, long generation) => new(
        observationId,
        generation,
        new DisplayInfo("display-1", "Fixture", new RectD(-100, 0, 1920, 1080), 1),
        1920,
        1080,
        new WindowIdentity(11, 22, "fixture", new RectD(10, 20, 800, 600)),
        DateTimeOffset.UnixEpoch);

    private static HttpClient CreateHttpClient(RecordingHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.openai.com/")
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

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
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _responder(request, cancellationToken);
        }
    }
}
