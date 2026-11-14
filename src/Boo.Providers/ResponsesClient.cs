using System.Net.Http.Headers;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Boo.Core;

namespace Boo.Providers;

public sealed class ResponsesClient : IScreenAssistant
{
    private const string Vendor = "OpenAI responses";
    private const string InterruptedMessage = "OpenAI response was interrupted before completion. Try again.";
    private static readonly JsonSerializerOptions JsonOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private readonly HttpClient _http;
    private readonly Func<string?> _credential;
    private readonly ProviderSettings _settings;
    private readonly Func<bool> _hasSelectedFileRoot;

    public ResponsesClient(HttpClient http, Func<string?> credential, ProviderSettings settings, Func<bool>? hasSelectedFileRoot = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _hasSelectedFileRoot = hasSelectedFileRoot ?? (() => false);
    }

    public async Task<AssistantReply> RespondAsync(AssistantRequest request, CancellationToken cancellationToken, ControlObservation? controls = null)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        StructuredReplies.ValidateControlObservation(request, controls);
        var endpoint = GetOfficialEndpoint("responses");
        var key = GetCredential();
        var configuredTimeout = _http.Timeout;

        var content = new List<object>();
        if (request.Frame is not null)
        {
            content.Add(new
            {
                type = "input_image",
                image_url = $"data:image/jpeg;base64,{Convert.ToBase64String(request.Frame.Jpeg)}"
            });
        }

        var contextText = StructuredReplies.ContextText(request, request.AllowActions && _hasSelectedFileRoot());
        content.Insert(0, new { type = "input_text", text = $"User request: {request.Text}\n{contextText}" });
        if (controls is not null)
        {
            content.Add(new
            {
                type = "input_text",
                text = JsonSerializer.Serialize(StructuredReplies.ObservedControlsPayload(controls), JsonOptions)
            });
        }

        var body = new Dictionary<string, object?>
        {
            ["model"] = _settings.ReasoningModel,
            ["store"] = false,
            ["reasoning"] = new { effort = "low" },
            ["instructions"] = !StructuredReplies.UsesStructuredOutput(request)
                ? StructuredReplies.PlainResearchInstructions
                : StructuredReplies.StructuredInstructions + contextText,
            ["input"] = new[] { new { role = "user", content } }
        };
        if (request.Research)
        {
            body["tools"] = new[] { new { type = "web_search" } };
            body["include"] = new[] { "web_search_call.action.sources" };
        }
        // A plain research question gets a prose answer so URL citations attach
        // to the text itself; every other request keeps the strict schema.
        if (StructuredReplies.UsesStructuredOutput(request))
        {
            body["text"] = new
            {
                format = new
                {
                    type = "json_schema",
                    name = StructuredReplies.ScreenHelpSchemaName,
                    strict = true,
                    schema = StructuredReplies.ScreenHelpSchema()
                }
            };
        }

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (configuredTimeout != Timeout.InfiniteTimeSpan)
            requestCancellation.CancelAfter(configuredTimeout);

        cancellationToken.ThrowIfCancellationRequested();

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCancellationOrTimeout(exception))
        {
            throw NormalizeCancellationOrTimeout(exception, cancellationToken, requestCancellation.Token);
        }
        catch (HttpRequestException exception)
        {
            throw ProviderHttpFailures.Transport("responses", exception);
        }

        using (response)
        {
            ThrowIfCancellationRequested(cancellationToken, requestCancellation.Token);

            if (!response.IsSuccessStatusCode)
            {
                throw await ProviderHttpFailures.StatusAsync("responses", response, requestCancellation.Token).ConfigureAwait(false);
            }

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(requestCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCancellationOrTimeout(exception))
            {
                throw NormalizeCancellationOrTimeout(exception, cancellationToken, requestCancellation.Token);
            }
            catch (Exception exception)
            {
                throw new InvalidOperationException("OpenAI responses returned an unreadable result.", exception);
            }

            ThrowIfCancellationRequested(cancellationToken, requestCancellation.Token);
            var reply = Decode(json, request, controls);
            ThrowIfCancellationRequested(cancellationToken, requestCancellation.Token);
            return reply;
        }
    }

    /// <summary>
    /// Words a focus nudge and optionally names a short on-screen phrase (for example the
    /// distracting tab's title) that Boo's pointer should fly to after Windows OCR finds it.
    /// The frame is sent once and never persisted.
    /// </summary>
    public async Task<FocusNudge> NudgeAsync(CapturedFrame frame, string task, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        var endpoint = GetOfficialEndpoint("responses");
        var key = GetCredential();
        var configuredTimeout = _http.Timeout;
        var body = new Dictionary<string, object?>
        {
            ["model"] = _settings.ReasoningModel,
            ["store"] = false,
            ["reasoning"] = new { effort = "low" },
            ["instructions"] = StructuredReplies.NudgeInstructions,
            ["input"] = new[]
            {
                new
                {
                    role = "user",
                    content = new object[]
                    {
                        new { type = "input_text", text = $"Declared task: {task}" },
                        new { type = "input_image", image_url = $"data:image/jpeg;base64,{Convert.ToBase64String(frame.Jpeg)}" }
                    }
                }
            },
            ["text"] = new
            {
                format = new
                {
                    type = "json_schema",
                    name = StructuredReplies.NudgeSchemaName,
                    strict = true,
                    schema = StructuredReplies.NudgeSchema()
                }
            }
        };

        using var message = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new StringContent(JsonSerializer.Serialize(body, JsonOptions), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        using var requestCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        if (configuredTimeout != Timeout.InfiniteTimeSpan)
            requestCancellation.CancelAfter(configuredTimeout);

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, requestCancellation.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (IsCancellationOrTimeout(exception))
        {
            throw NormalizeCancellationOrTimeout(exception, cancellationToken, requestCancellation.Token);
        }
        catch (HttpRequestException exception)
        {
            throw ProviderHttpFailures.Transport("responses", exception);
        }

        using (response)
        {
            ThrowIfCancellationRequested(cancellationToken, requestCancellation.Token);
            if (!response.IsSuccessStatusCode)
                throw await ProviderHttpFailures.StatusAsync("responses", response, requestCancellation.Token).ConfigureAwait(false);

            string json;
            try
            {
                json = await response.Content.ReadAsStringAsync(requestCancellation.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (IsCancellationOrTimeout(exception))
            {
                throw NormalizeCancellationOrTimeout(exception, cancellationToken, requestCancellation.Token);
            }

            ThrowIfCancellationRequested(cancellationToken, requestCancellation.Token);
            return DecodeNudge(json);
        }
    }

    /// <summary>Decodes a <c>boo_focus_nudge</c> Responses result. The pointer target is optional.</summary>
    public static FocusNudge DecodeNudge(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var texts = new StringBuilder();
            foreach (var item in ReadOutput(document.RootElement, "OpenAI nudge returned an invalid result.").EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;
                foreach (var part in content.EnumerateArray())
                {
                    if (IsOutputText(part, out var textElement))
                        texts.Append(textElement.GetString());
                }
            }

            return StructuredReplies.BuildNudge("OpenAI", texts.ToString());
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException or FormatException)
        {
            throw new InvalidOperationException("OpenAI nudge returned an invalid result.", exception);
        }
    }

    private static void ThrowIfCancellationRequested(CancellationToken callerToken, CancellationToken requestToken)
    {
        callerToken.ThrowIfCancellationRequested();
        if (requestToken.IsCancellationRequested)
        {
            if (callerToken.IsCancellationRequested)
                throw new OperationCanceledException(callerToken);
            throw new ResponseTimeoutException();
        }
    }

    private static bool IsCancellationOrTimeout(Exception exception) =>
        exception is OperationCanceledException || ContainsTimeout(exception);

    private static Exception NormalizeCancellationOrTimeout(
        Exception exception,
        CancellationToken callerToken,
        CancellationToken requestToken)
    {
        if (callerToken.IsCancellationRequested)
            return new OperationCanceledException(callerToken);

        if (ContainsTimeout(exception))
            return new ResponseTimeoutException();

        if (exception is not OperationCanceledException)
        {
            return exception;
        }
        if (!requestToken.IsCancellationRequested)
        {
            return new InvalidOperationException(InterruptedMessage);
        }
        if (callerToken.IsCancellationRequested)
            return new OperationCanceledException(callerToken);
        return new ResponseTimeoutException();
    }

    private static bool ContainsTimeout(Exception exception)
    {
        var pending = new Stack<Exception>();
        pending.Push(exception);
        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (current is TimeoutException)
                return true;

            if (current is AggregateException aggregate)
            {
                foreach (var inner in aggregate.InnerExceptions)
                    pending.Push(inner);
            }
            else if (current.InnerException is not null)
            {
                pending.Push(current.InnerException);
            }
        }

        return false;
    }

    private static AssistantReply Decode(string json, AssistantRequest request, ControlObservation? controls)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var output = ReadOutput(document.RootElement, "OpenAI responses returned an invalid result.");
            var texts = new List<string>();
            var sources = ResearchSources.Extract(document.RootElement);
            foreach (var item in output.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object ||
                    !item.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array)
                    continue;

                foreach (var part in content.EnumerateArray())
                {
                    if (IsOutputText(part, out var textElement))
                    {
                        texts.Add(textElement.GetString() ?? string.Empty);
                    }
                }
            }

            return StructuredReplies.Build(Vendor, string.Concat(texts), sources, request, controls);
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or KeyNotFoundException)
        {
            throw new InvalidOperationException("OpenAI responses returned an invalid result.", exception);
        }
    }

    /// <summary>
    /// The "output" array of a Responses result. A root or output of the wrong JSON kind is reported with the vendor
    /// message instead of leaking <see cref="JsonElement"/>'s own wording to the user.
    /// </summary>
    private static JsonElement ReadOutput(JsonElement root, string invalidMessage)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidOperationException(invalidMessage);
        return output;
    }

    private static bool IsOutputText(JsonElement part, out JsonElement textElement)
    {
        textElement = default;
        return part.ValueKind == JsonValueKind.Object &&
               part.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "output_text" &&
               part.TryGetProperty("text", out textElement) && textElement.ValueKind == JsonValueKind.String;
    }

    private Uri GetOfficialEndpoint(string path)
    {
        var baseUri = _http.BaseAddress ?? ProviderEndpoint.Origin;
        if (!ProviderEndpoint.IsOfficial(baseUri))
            throw new InvalidOperationException("OpenAI requests require the official HTTPS API origin.");
        return new Uri(baseUri, $"/v1/{path}");
    }

    private string GetCredential()
    {
        var key = _credential();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAI credential is not configured.");
        return key;
    }

}

