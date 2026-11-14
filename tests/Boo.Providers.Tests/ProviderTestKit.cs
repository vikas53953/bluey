using System.Net;
using System.Text;
using System.Text.Json;
using Boo.Core;

namespace Boo.Providers.Tests;

/// <summary>
/// In-memory HTTP fixtures shared by the Anthropic and Gemini client tests. Mirrors the helpers in
/// tests/Boo.Integration.Tests/ProviderContractTests.cs so the three providers are tested the same way.
/// </summary>
internal static class ProviderTestKit
{
    /// <summary>Short deadline for timeout tests; the watchdog guards against a client that ignores HttpClient.Timeout.</summary>
    public static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(125);
    public static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(5);

    public static HttpClient CreateHttpClient(RecordingHandler handler, Uri baseAddress, TimeSpan? timeout = null)
    {
        var http = new HttpClient(handler) { BaseAddress = baseAddress };
        if (timeout is { } value)
            http.Timeout = value;
        return http;
    }

    public static ScreenContext CreateContext(Guid observationId, long generation) => new(
        observationId,
        generation,
        new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
        1920,
        1080,
        new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
        DateTimeOffset.UtcNow);

    public static FocusSnapshot CreateSession(string task = "Write the report") => new(
        Guid.NewGuid(), task, 600, DateTimeOffset.UnixEpoch, 30, 30,
        FocusStatus.Running, false, false, Array.Empty<FocusCheck>());

    public static HttpResponseMessage JsonResponse(string json, HttpStatusCode status = HttpStatusCode.OK) => new(status)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };

    /// <summary>The structured screen-help JSON a model returns, with invented binding ids that must be ignored.</summary>
    public const string ScreenHelpJson =
        "{\"text\":\"The control is here.\",\"marks\":[{\"kind\":\"ring\",\"x\":10,\"y\":20,\"width\":30,\"height\":40,\"label\":\"Primary action\",\"endX\":50,\"endY\":60}],\"actions\":[{\"name\":\"open_url\",\"target\":\"docs\",\"arguments\":{\"url\":\"https://example.com\"},\"generation\":999,\"observationId\":\"model-invented\"}]}";

    public static string ScreenHelpJsonWithActions(string actionsJsonArray) =>
        "{\"text\":\"Done.\",\"marks\":[],\"actions\":" + actionsJsonArray + "}";

    public const string OutsideCatalogJson =
        "{\"text\":\"Run it.\",\"marks\":[],\"actions\":[{\"name\":\"run_shell\",\"target\":\"terminal\",\"arguments\":{\"text\":\"rm -rf /\"}}]}";

    /// <summary>The action list from ProviderContractTests/ControlObservationProviderTests used to prove identical control binding.</summary>
    public const string BindingActionsJson = """
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

    public static ControlObservation BindingControls(ScreenContext context) => ControlObservation.Create(context, new[]
    {
        new ControlCandidate(
            "button-text", "Button text", "Button", "Visible label",
            new RectD(40, 50, 120, 36), new[] { 4, 5 },
            IsEnabled: true, IsOffscreen: false, IsPassword: false,
            CanInvoke: true, CanSetValue: false),
        new ControlCandidate(
            "edit-field", "Editor", "Edit", "old value",
            new RectD(40, 100, 200, 36), new[] { 6 },
            IsEnabled: true, IsOffscreen: false, IsPassword: false,
            CanInvoke: false, CanSetValue: true)
    });

    /// <summary>Asserts the ControlObservationProviderTests binding outcome on a reply from any provider.</summary>
    public static void AssertBindingOutcome(AssistantReply reply)
    {
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

    /// <summary>Asserts a structured screen-help reply was bound to the request's own observation/generation.</summary>
    public static void AssertScreenHelpBound(AssistantReply reply, AssistantRequest request, ScreenContext context)
    {
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
        Assert.NotEqual(999, action.Generation);
        Assert.Equal(context.ObservationId, annotations.ObservationId);
        Assert.Equal(context.Generation, annotations.Generation);
        Assert.Equal(context.Display.Id, annotations.DisplayId);
    }

    public static string ClassificationJson(
        string activity = "off_task",
        string confidence = "0.93",
        string onTask = "0.21",
        string offTask = "0.72",
        string uncertain = "0.07") =>
        $"{{\"activity\":\"{activity}\",\"confidence\":{confidence},\"probabilities\":{{\"on_task\":{onTask},\"off_task\":{offTask},\"uncertain\":{uncertain}}}}}";

    public static void AssertClassification(FocusDecision decision)
    {
        Assert.Equal(FocusActivity.OffTask, decision.Activity);
        Assert.Equal(0.72, decision.Probability, 3);
        Assert.Equal(0.93, decision.Confidence, 3);
        Assert.Equal(3, decision.Probabilities.Count);
        Assert.Equal(0.21, decision.Probabilities["on_task"], 3);
        Assert.Equal(0.72, decision.Probabilities["off_task"], 3);
        Assert.Equal(0.07, decision.Probabilities["uncertain"], 3);
    }

    /// <summary>True when the request would reveal the key anywhere: URL (path or query), any header value, or the body.</summary>
    public static bool CarriesKey(HttpRequestMessage request, string? body, string key)
    {
        if (request.RequestUri is { } uri && uri.ToString().Contains(key, StringComparison.Ordinal))
            return true;
        if (body is not null && body.Contains(key, StringComparison.Ordinal))
            return true;
        foreach (var header in request.Headers)
        {
            if (header.Value.Any(value => value.Contains(key, StringComparison.Ordinal)))
                return true;
        }
        if (request.Content is not null)
        {
            foreach (var header in request.Content.Headers)
            {
                if (header.Value.Any(value => value.Contains(key, StringComparison.Ordinal)))
                    return true;
            }
        }
        return false;
    }

    public static void AssertNoRequestCarriesKey(RecordingHandler handler, string key)
    {
        Assert.Equal(handler.Requests.Count, handler.Bodies.Count);
        for (var index = 0; index < handler.Requests.Count; index++)
        {
            Assert.False(CarriesKey(handler.Requests[index], handler.Bodies[index], key), $"Request {index} carried the key.");
        }
    }

    public static string SingleHeader(HttpRequestMessage request, string name)
    {
        Assert.True(request.Headers.TryGetValues(name, out var values), $"Header '{name}' was not sent.");
        return Assert.Single(values!);
    }

    /// <summary>Finds a JSON property by one of several spellings (Gemini accepts snake_case and camelCase).</summary>
    public static JsonElement? Property(JsonElement element, params string[] names)
    {
        if (element.ValueKind != JsonValueKind.Object)
            return null;
        foreach (var name in names)
        {
            if (element.TryGetProperty(name, out var value))
                return value;
        }
        return null;
    }

    /// <summary>Every string value anywhere in the document, so wording checks do not depend on exact body layout.</summary>
    public static IEnumerable<string> AllStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    foreach (var value in AllStrings(property.Value))
                        yield return value;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in AllStrings(item))
                        yield return value;
                }
                break;
        }
    }

    public static async Task<Exception> AssertNotStubAsync(Func<Task> action)
    {
        var error = await Record.ExceptionAsync(action);
        Assert.NotNull(error);
        Assert.IsNotType<NotImplementedException>(error!);
        return error!;
    }

    /// <summary>Runs an operation with a watchdog so a client that never honours its deadline fails instead of hanging the run.</summary>
    public static async Task<Exception> CaptureFailureWithWatchdogAsync(Func<CancellationToken, Task> action)
    {
        using var watchdog = new CancellationTokenSource(Watchdog);
        var pending = action(watchdog.Token);
        var finished = await Task.WhenAny(pending, Task.Delay(Timeout.InfiniteTimeSpan, watchdog.Token).ContinueWith(_ => { }, TaskScheduler.Default));
        Assert.Same(pending, finished);
        var error = await Record.ExceptionAsync(() => pending);
        Assert.NotNull(error);
        Assert.IsNotType<NotImplementedException>(error!);
        return error!;
    }

    public sealed class RecordingHandler : HttpMessageHandler
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

        /// <summary>A handler that never answers until the request is cancelled.</summary>
        public static RecordingHandler Stalled() => new(async (_, cancellationToken) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The stalled send unexpectedly completed.");
        });

        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string?> Bodies { get; } = [];
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }
        public TaskCompletionSource<bool> RequestStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool CancellationObserved { get; private set; }

        public JsonElement LastBodyJson() => JsonDocument.Parse(LastBody!).RootElement.Clone();

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            LastRequest = request;
            string? body = null;
            if (request.Content is not null)
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            Bodies.Add(body);
            LastBody = body;
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
