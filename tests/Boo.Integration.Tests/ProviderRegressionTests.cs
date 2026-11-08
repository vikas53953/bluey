using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class ProviderRegressionTests
{
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    [Fact]
    public async Task Responses_request_declares_structured_result_context_and_constrained_action_schema()
    {
        var context = new ScreenContext(
            Guid.Parse("11111111-2222-3333-4444-555555555555"),
            73,
            new DisplayInfo("display-regression", "Fixture display", new RectD(-80, 20, 1920, 1080), 1.5),
            1280,
            720,
            new WindowIdentity(1234, 5678, "FixtureEditor", new RectD(17, 19, 800, 600)),
            DateTimeOffset.UnixEpoch);
        var frame = new CapturedFrame(context, new byte[] { 1, 2, 3 });
        var handler = new RegressionHandler((_, _) => Task.FromResult(JsonResponse("""
            {"output":[{"type":"message","content":[{"type":"output_text","text":"{\"text\":\"ok\"}"}]}]}
            """)));
        using var http = CreateHttpClient(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        await client.RespondAsync(
            new AssistantRequest("Explain the selected control.", context.Generation, frame, Research: true, AllowActions: true),
            CancellationToken.None);

        using var request = JsonDocument.Parse(handler.LastBody!);
        var root = request.RootElement;
        var instructions = root.GetProperty("instructions").GetString()!;
        var instructionText = instructions.ToLowerInvariant();
        foreach (var requiredTerm in new[] { "text", "marks", "actions", "pixel", "observation", "target", "untrusted", "instructions" })
            Assert.Contains(requiredTerm, instructionText, StringComparison.Ordinal);

        var format = root.GetProperty("text").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString()!);
        Assert.False(string.IsNullOrWhiteSpace(format.GetProperty("name").GetString()));
        Assert.True(format.GetProperty("strict").GetBoolean());
        var schema = format.GetProperty("schema");
        Assert.Equal("object", schema.GetProperty("type").GetString()!);
        Assert.False(schema.GetProperty("additionalProperties").GetBoolean());

        var properties = schema.GetProperty("properties");
        var resultRequired = ReadStringSet(schema.GetProperty("required"));
        Assert.Contains("text", resultRequired);
        Assert.Contains("marks", resultRequired);
        Assert.Contains("actions", resultRequired);
        Assert.Equal("string", properties.GetProperty("text").GetProperty("type").GetString()!);

        var markProperties = properties.GetProperty("marks").GetProperty("items").GetProperty("properties");
        foreach (var name in new[] { "kind", "x", "y", "width", "height", "label", "endX", "endY" })
            Assert.True(markProperties.TryGetProperty(name, out _), $"mark schema is missing {name}");

        var actionItem = properties.GetProperty("actions").GetProperty("items");
        var actionProperties = actionItem.GetProperty("properties");
        foreach (var name in new[] { "name", "target", "arguments" })
            Assert.True(actionProperties.TryGetProperty(name, out _), $"action schema is missing {name}");
        var actionRequired = ReadStringSet(actionItem.GetProperty("required"));
        Assert.True(actionProperties.EnumerateObject().All(property => actionRequired.Contains(property.Name)));
        var allowedNames = ReadStringSet(actionProperties.GetProperty("name").GetProperty("enum"));
        var expectedAllowedNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "search", "inspect", "open_url", "open_app", "click", "type_text", "write_file", "delete_file"
        };
        Assert.True(expectedAllowedNames.SetEquals(allowedNames));
        var arguments = actionProperties.GetProperty("arguments");
        Assert.Equal("object", arguments.GetProperty("type").GetString()!);
        Assert.False(arguments.GetProperty("additionalProperties").GetBoolean());
        var argumentProperties = arguments.GetProperty("properties");
        var argumentNames = argumentProperties.EnumerateObject().Select(property => property.Name).ToHashSet(StringComparer.Ordinal);
        var expectedArgumentNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "x", "y", "automationId", "name", "text", "path", "overwrite"
        };
        var expectedWithContents = new HashSet<string>(expectedArgumentNames, StringComparer.Ordinal) { "contents" };
        var expectedWithContent = new HashSet<string>(expectedArgumentNames, StringComparer.Ordinal) { "content" };
        var expectedWithBoth = new HashSet<string>(expectedWithContents, StringComparer.Ordinal) { "content" };
        Assert.True(
            expectedWithContents.SetEquals(argumentNames) ||
            expectedWithContent.SetEquals(argumentNames) ||
            expectedWithBoth.SetEquals(argumentNames));
        Assert.NotEmpty(argumentNames);
        var argumentRequired = ReadStringSet(arguments.GetProperty("required"));
        Assert.True(argumentNames.SetEquals(argumentRequired));
        foreach (var property in argumentProperties.EnumerateObject())
            AssertStringOrNullableString(property.Value);

        var inputText = string.Join("\n", root.GetProperty("input").EnumerateArray()
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(content => content.GetProperty("type").GetString() == "input_text")
            .Select(content => content.GetProperty("text").GetString()!));
        Assert.Contains(context.FrameWidth.ToString(), inputText, StringComparison.Ordinal);
        Assert.Contains(context.FrameHeight.ToString(), inputText, StringComparison.Ordinal);
        Assert.Contains(context.ObservationId.ToString(), inputText, StringComparison.Ordinal);
        Assert.Contains(context.Display.Id, inputText, StringComparison.Ordinal);
        Assert.Contains(context.Target.Handle.ToString(), inputText, StringComparison.Ordinal);
        Assert.Contains(context.Target.ProcessName, inputText, StringComparison.Ordinal);
        Assert.Contains("data:image/jpeg;base64,AQID", handler.LastBody!, StringComparison.Ordinal);
    }

    [Fact]
    public void Realtime_function_call_arguments_done_prefers_call_id_when_item_id_is_also_present()
    {
        var parsed = RealtimeProtocol.Parse("""
            {"type":"response.function_call_arguments.done","item_id":"item-17","call_id":"call-42","name":"screen_help","arguments":"{\"question\":\"Explain this\",\"mode\":\"teach\"}"}
            """);

        Assert.NotNull(parsed);
        Assert.Equal("call-42", parsed!.ItemId);
        Assert.Equal("{\"question\":\"Explain this\",\"mode\":\"teach\"}", parsed.Text);
    }

    [Fact]
    public void Realtime_output_audio_transcript_done_reads_transcript_field()
    {
        var parsed = RealtimeProtocol.Parse("""
            {"type":"response.output_audio_transcript.done","item_id":"item-18","transcript":"The explanation is complete."}
            """);

        Assert.NotNull(parsed);
        Assert.Equal("response.output_audio_transcript.done", parsed!.Kind);
        Assert.Equal("The explanation is complete.", parsed.Text);
        Assert.Equal("item-18", parsed.ItemId);
    }

    private static HashSet<string> ReadStringSet(JsonElement array)
    {
        Assert.Equal(JsonValueKind.Array, array.ValueKind);
        return array.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
    }

    private static void AssertStringOrNullableString(JsonElement schema)
    {
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            Assert.Equal("string", type.GetString()!);
            return;
        }

        Assert.Equal(JsonValueKind.Array, type.ValueKind);
        var types = type.EnumerateArray().Select(value => value.GetString()!).ToHashSet(StringComparer.Ordinal);
        Assert.Contains("string", types);
        Assert.Contains("null", types);
    }

    private static HttpClient CreateHttpClient(RegressionHandler handler) => new(handler)
    {
        BaseAddress = new Uri("https://api.openai.com/")
    };

    private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    private sealed class RegressionHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public RegressionHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            LastBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return await _responder(request, cancellationToken);
        }
    }
}
