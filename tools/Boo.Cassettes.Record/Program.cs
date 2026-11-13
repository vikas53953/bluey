using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Boo.Cassettes;
using Boo.Core;
using Boo.Providers;

// BOO-CASS-001 recorder: sends a few cheap requests through Boo's real OpenAI client and writes what came back as
// redacted cassettes for a human to review (docs/quality/cassettes.md). Never prints the key, never commits anything.
//   OPENAI_KEY      the key (from the BOO_LIVE_OPENAI_KEY secret); required
//   CASSETTE_MODEL  the chat model (default: gpt-5-mini, a cheap one)
//   --out DIR       where the cassettes go
var outIndex = Array.IndexOf(args, "--out");
if (outIndex < 0 || outIndex + 1 >= args.Length)
{
    Console.Error.WriteLine("usage: Boo.Cassettes.Record --out DIR");
    return 2;
}
var outDir = args[outIndex + 1];
var key = Environment.GetEnvironmentVariable("OPENAI_KEY");
if (string.IsNullOrWhiteSpace(key))
{
    Console.Error.WriteLine("BOO-CASS-001: OPENAI_KEY is not set; nothing was recorded. This is a failure, not a skip.");
    return 1;
}
var model = Environment.GetEnvironmentVariable("CASSETTE_MODEL");
if (string.IsNullOrWhiteSpace(model))
    model = "gpt-5-mini";
var runId = Environment.GetEnvironmentVariable("GITHUB_RUN_ID") ?? "local";
Directory.CreateDirectory(outDir);
Console.WriteLine($"Recording with model {model} (key present, {key.Length} characters).");

var cases = new (string Name, string Call, string Model)[]
{
    ("recorded-chat-structured", "respond", model),
    ("recorded-research-prose", "research", model),
    // A model id that does not exist: a free, real error reply.
    ("recorded-model-not-found", "respond", "boo-cassette-no-such-model"),
};

var failures = 0;
foreach (var (name, call, caseModel) in cases)
{
    var tap = new Tap(new SocketsHttpHandler());
    using var http = new HttpClient(tap) { BaseAddress = new Uri("https://api.openai.com/"), Timeout = TimeSpan.FromSeconds(90) };
    var client = new ResponsesClient(http, () => key, new ProviderSettings(ReasoningModel: caseModel));
    var expect = new CassetteExpect();
    try
    {
        var reply = call == "research"
            ? await client.RespondAsync(new AssistantRequest("In one sentence, what is the latest stable .NET version? Cite a source.", 1, Research: true), CancellationToken.None)
            : await client.RespondAsync(new AssistantRequest("In one short sentence: what does the keyboard shortcut Ctrl+S usually do?", 1), CancellationToken.None);
        expect.Outcome = "reply";
        expect.Text = reply.Text;
        if (call == "research")
            expect.Sources = reply.Sources.Count;
    }
    catch (ProviderException exception)
    {
        expect.Outcome = "provider-failure";
        expect.FailureKind = exception.Failure.Kind.ToString();
        expect.Status = exception.Failure.StatusCode;
        expect.Retryable = exception.Failure.Retryable;
        expect.Message = exception.Failure.UserMessage;
    }
    catch (ResponseTimeoutException)
    {
        expect.Outcome = "timeout";
    }
    catch (InvalidOperationException exception)
    {
        expect.Outcome = "invalid";
        expect.Message = exception.Message;
    }

    if (tap.Response is null)
    {
        Console.Error.WriteLine($"{name}: no HTTP reply was captured ({expect.Outcome}).");
        failures++;
        continue;
    }

    try
    {
        var cassette = new Cassette
        {
            Name = name,
            Origin = "recorded",
            Note = $"Recorded by record-cassettes.yml (run {runId}) from the real OpenAI API with model {caseModel}; redacted per BOO-CASS-001. Review before committing.",
            Call = call,
            Request = tap.Request,
            Response = tap.Response,
            Expect = expect,
        };
        var text = Cassettes.Serialize(cassette);
        Cassettes.RefuseSecrets(text, key);
        await File.WriteAllTextAsync(Path.Combine(outDir, name + ".json"), text);
        Console.WriteLine($"{name}: HTTP {tap.Response.Status}, {tap.Response.Chunks.Count} chunk(s), outcome {expect.Outcome}.");
    }
    catch (InvalidOperationException exception)
    {
        Console.Error.WriteLine($"{name}: {exception.Message}");
        failures++;
    }
}
return failures == 0 ? 0 : 1;

/// <summary>Passes the request through and keeps a redacted copy of the reply: status, allowlisted headers, timed chunks.</summary>
internal sealed class Tap : DelegatingHandler
{
    public Tap(HttpMessageHandler inner) : base(inner) { }

    public CassetteRequest Request { get; } = new();
    public CassetteResponse? Response { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Request.Method = request.Method.Method;
        Request.Path = request.RequestUri?.AbsolutePath ?? "";
        if (request.Content is not null && JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken)) is JsonObject body)
        {
            Request.Model = (string?)body["model"] ?? "";
            Request.Format = (string?)body["text"]?["format"]?["name"];
            Request.Tools = (body["tools"] as JsonArray)?.Select(t => (string?)t?["type"] ?? "").ToList() ?? new List<string>();
            Request.BodyKeys = body.Select(p => p.Key).ToList();
        }

        var clock = Stopwatch.StartNew();
        var response = await base.SendAsync(request, cancellationToken);
        var headersDelay = (int)clock.ElapsedMilliseconds;

        // Read the body as it arrives and note when each piece came.
        var pieces = new List<(int DelayMs, byte[] Bytes)>();
        await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken))
        {
            var buffer = new byte[16 * 1024];
            var last = clock.ElapsedMilliseconds;
            int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) > 0)
            {
                var now = clock.ElapsedMilliseconds;
                pieces.Add(((int)(now - last), buffer[..read]));
                last = now;
            }
        }

        var all = pieces.SelectMany(p => p.Bytes).ToArray();
        var redacted = Cassettes.RedactBody(Encoding.UTF8.GetString(all), secret: null);
        var headers = Cassettes.RedactHeaders(response.Headers.Concat(response.Content.Headers));
        Response = new CassetteResponse
        {
            Status = (int)response.StatusCode,
            HeadersDelayMs = headersDelay,
            Headers = headers,
            Chunks = Split(redacted, pieces.Select(p => (p.DelayMs, p.Bytes.Length)).ToList()),
        };

        // Hand the client an identical, already-read body.
        var copy = new ByteArrayContent(all);
        foreach (var header in response.Content.Headers)
            copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
        response.Content = copy;
        return response;
    }

    /// <summary>Re-cuts the redacted text into as many chunks as arrived, in the same proportions, with the same delays.</summary>
    private static List<CassetteChunk> Split(string text, IReadOnlyList<(int DelayMs, int Length)> pieces)
    {
        var chunks = new List<CassetteChunk>();
        if (pieces.Count == 0)
            return chunks;
        var total = Math.Max(1, pieces.Sum(p => p.Length));
        var start = 0;
        var seen = 0;
        for (var i = 0; i < pieces.Count; i++)
        {
            seen += pieces[i].Length;
            var end = i == pieces.Count - 1 ? text.Length : (int)((long)text.Length * seen / total);
            if (end > start && end < text.Length && char.IsHighSurrogate(text[end - 1]))
                end++;
            chunks.Add(new CassetteChunk { DelayMs = pieces[i].DelayMs, Text = text[start..end] });
            start = end;
        }
        return chunks;
    }
}
