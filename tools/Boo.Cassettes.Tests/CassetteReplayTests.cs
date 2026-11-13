using System.Diagnostics;
using System.Text.Json.Nodes;
using Boo.Core;
using Boo.Providers;

namespace Boo.Cassettes;

/// <summary>
/// BOO-CASS-001 (docs/quality/cassettes.md): every cassette under cassettes/ is replayed through Boo's real OpenAI
/// client (<see cref="ResponsesClient"/>), with its recorded latency and faults. These are MOCK runs: they prove how the
/// client handles real-world reply shapes, never that the real AI works. Live runs (live-ai-smoke) stay the truth check.
/// </summary>
public sealed class CassetteReplayTests
{
    private static readonly string Folder = Path.Combine(AppContext.BaseDirectory, "cassettes");
    private const string Credential = "cassette-replay-placeholder";
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(20);

    private static IEnumerable<string> Names() =>
        Directory.EnumerateFiles(Folder, "*.json").Select(f => Path.GetFileNameWithoutExtension(f)).Order(StringComparer.Ordinal);

    public static TheoryData<string> AllCassettes() => new(Names());

    public static TheoryData<string, string> SuccessCassettesTimesFaults()
    {
        var data = new TheoryData<string, string>();
        foreach (var name in Names())
        {
            var cassette = Read(name);
            if (cassette.Fault is null && cassette.Expect.Outcome == "reply")
                foreach (var fault in new[] { "hang", "connect-refused", "reset-before-headers", "cut-mid-chunk" })
                    data.Add(name, fault);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(AllCassettes))]
    public async Task Replay_matches_the_cassette_expectation(string name)
    {
        var cassette = Read(name);
        var (outcome, handler, elapsed) = await RunAsync(cassette);

        AssertRequestShape(cassette, handler);
        var expect = cassette.Expect;
        switch (expect.Outcome)
        {
            case "reply":
                Assert.True(outcome.Error is null, $"{name}: expected a reply but got {outcome.Error?.GetType().Name}: {outcome.Error?.Message}");
                if (expect.Text is not null)
                    Assert.Equal(expect.Text, outcome.Text);
                else
                    Assert.False(string.IsNullOrWhiteSpace(outcome.Text));
                if (expect.Sources is { } sources)
                    Assert.Equal(sources, outcome.Sources);
                if (expect.PointerTarget is not null)
                    Assert.Equal(expect.PointerTarget, outcome.PointerTarget);
                // The recorded latency is really replayed (Task.Delay may wake up to a timer tick early).
                var recorded = (cassette.Response!.HeadersDelayMs + cassette.Response.Chunks.Sum(c => c.DelayMs));
                Assert.True(elapsed.TotalMilliseconds >= recorded - 20, $"{name}: replay took {elapsed.TotalMilliseconds:0} ms, recorded {recorded} ms");
                break;
            case "provider-failure":
                var failure = Assert.IsType<ProviderException>(outcome.Error);
                Assert.Equal(expect.FailureKind, failure.Failure.Kind.ToString());
                Assert.Equal(expect.Status, failure.Failure.StatusCode);
                if (expect.Retryable is { } retryable)
                    Assert.Equal(retryable, failure.Failure.Retryable);
                if (expect.Message is not null)
                    Assert.Equal(expect.Message, failure.Failure.UserMessage);
                break;
            case "timeout":
                Assert.IsType<ResponseTimeoutException>(outcome.Error);
                break;
            case "invalid":
                Assert.NotNull(outcome.Error);
                Assert.IsNotType<ProviderException>(outcome.Error);
                Assert.IsType<InvalidOperationException>(outcome.Error);
                if (expect.Message is not null)
                    Assert.Equal(expect.Message, outcome.Error!.Message);
                break;
            default:
                Assert.Fail($"{name}: unknown expected outcome '{expect.Outcome}'");
                break;
        }

        if (outcome.Error is not null)
            AssertNoBodyLeak(cassette, outcome.Error);
    }

    /// <summary>Fault injection: every good reply cassette, cut by every transport fault, must fail cleanly.</summary>
    [Theory]
    [MemberData(nameof(SuccessCassettesTimesFaults))]
    public async Task Every_fault_on_a_good_reply_fails_cleanly(string name, string fault)
    {
        var cassette = Read(name);
        cassette.Fault = new CassetteFault { Kind = fault, AtChunk = Math.Max(0, cassette.Response!.Chunks.Count - 1) };
        // Only the hang needs a short deadline; the others keep the default so the recorded latency still fits.
        if (fault == "hang")
            cassette.Expect.TimeoutMs = 400;

        var (outcome, _, _) = await RunAsync(cassette);

        Assert.NotNull(outcome.Error);
        switch (fault)
        {
            case "hang":
                Assert.IsType<ResponseTimeoutException>(outcome.Error);
                break;
            case "connect-refused":
                Assert.Equal(ProviderFailureKind.Offline, Assert.IsType<ProviderException>(outcome.Error).Failure.Kind);
                break;
            case "reset-before-headers":
                var reset = Assert.IsType<ProviderException>(outcome.Error).Failure;
                Assert.True(reset.Retryable, "a dropped connection must be reported as worth retrying");
                break;
            case "cut-mid-chunk" when cassette.Call == "nudge":
                // KNOWN GAP 1 (docs/quality/cassettes.md): NudgeAsync lets .NET's own HttpRequestException out when the
                // body is cut. The app swallows it (the nudge is optional), so the user sees nothing wrong. Pinned
                // here so a fix shows up as a change to this line, not as silence.
                Assert.IsType<HttpRequestException>(outcome.Error);
                break;
            case "cut-mid-chunk":
                // A body cut part-way must surface as Boo's own plain message, never .NET's transport wording.
                var cut = Assert.IsType<InvalidOperationException>(outcome.Error);
                Assert.StartsWith("OpenAI ", cut.Message, StringComparison.Ordinal);
                break;
        }
        AssertNoBodyLeak(cassette, outcome.Error!);
    }

    [Fact]
    public void Cassette_set_covers_the_bad_day_list()
    {
        var names = Names().ToHashSet(StringComparer.Ordinal);
        foreach (var required in new[]
                 {
                     "bad-malformed-json", "bad-truncated-body", "bad-cut-mid-chunk", "bad-timeout-hang", "bad-slow-body-timeout",
                     "bad-refusal", "bad-429-rate-limit-retry-after", "bad-429-insufficient-quota", "bad-500-server-error",
                     "bad-503-html", "bad-empty-body", "bad-wrong-content-type", "bad-socket-reset", "bad-connect-refused"
                 })
            Assert.Contains(required, names);
        Assert.Contains(names, n => Read(n).Expect.Outcome == "reply" && Read(n).Call == "respond");
        Assert.Contains(names, n => Read(n).Expect.Outcome == "reply" && Read(n).Call == "research");
        Assert.Contains(names, n => Read(n).Expect.Outcome == "reply" && Read(n).Call == "nudge");
    }

    /// <summary>Every committed cassette is safe to commit: allowlisted headers only, no key-shaped text, honest origin.</summary>
    [Theory]
    [MemberData(nameof(AllCassettes))]
    public void Cassette_is_redacted_and_labelled(string name)
    {
        var raw = File.ReadAllText(Path.Combine(Folder, name + ".json"));
        Assert.DoesNotMatch(Cassettes.KeyShape, raw);
        foreach (var marker in new[] { "authorization", "openai-organization", "openai-project", "x-request-id", "set-cookie", "org-", "proj_", "req_" })
            Assert.DoesNotContain(marker, raw, StringComparison.OrdinalIgnoreCase);

        var cassette = Read(name);
        Assert.Equal(name, cassette.Name);
        Assert.Contains(cassette.Origin, new[] { "synthetic", "recorded" });
        if (cassette.Origin == "synthetic")
            Assert.Contains("synthetic", cassette.Note, StringComparison.OrdinalIgnoreCase);
        foreach (var header in cassette.Response?.Headers.Keys ?? Enumerable.Empty<string>())
            Assert.Contains(header, Cassettes.HeaderAllowlist);
    }

    [Fact]
    public void Recorder_redaction_removes_ids_and_refuses_the_key()
    {
        var fakeKey = string.Concat("s", "k-", new string('Z', 24));
        var body = "{\"id\":\"resp_0123456789abcdef\",\"user\":\"someone\",\"safety_identifier\":\"abc\",\"output\":[{\"id\":\"msg_0123456789abcdef\",\"note\":\"org-ABCDEF123456 proj_ABCDEF123456 req_ABCDEF123456\"}]}";
        var redacted = Cassettes.RedactBody(body, fakeKey);
        Assert.DoesNotContain("0123456789abcdef", redacted);
        Assert.DoesNotContain("someone", redacted);
        Assert.DoesNotContain("ABCDEF123456", redacted);
        Assert.Contains("resp_redacted", redacted);

        var masked = Cassettes.RedactBody("{\"error\":{\"message\":\"Incorrect API key provided: abcd1234*****************wxyz. You can find your API key at https://platform.openai.com/account/api-keys.\"}}", fakeKey);
        Assert.DoesNotContain("abcd1234", masked);
        Assert.DoesNotContain("wxyz", masked);
        Assert.Contains("Incorrect API key provided: [redacted-key]. You can find", masked);

        var leaked = Assert.Throws<InvalidOperationException>(() => Cassettes.RedactBody("{\"echo\":\"" + fakeKey + "\"}", fakeKey));
        Assert.DoesNotContain(fakeKey, leaked.Message);
        Assert.Throws<InvalidOperationException>(() => Cassettes.RedactBody("{\"echo\":\"" + fakeKey + "\"}", null));

        var headers = Cassettes.RedactHeaders(new Dictionary<string, IEnumerable<string>>
        {
            ["Content-Type"] = new[] { "application/json" },
            ["Retry-After"] = new[] { "2" },
            ["openai-organization"] = new[] { "someone" },
            ["x-request-id"] = new[] { "abc" },
            ["Authorization"] = new[] { "Bearer x" },
        });
        Assert.Equal(new[] { "content-type", "retry-after" }, headers.Keys.Order(StringComparer.Ordinal));
    }

    private static Cassette Read(string name) => Cassettes.Load(Path.Combine(Folder, name + ".json"));

    private sealed record Outcome(string? Text, int Sources, string? PointerTarget, Exception? Error);

    private static async Task<(Outcome Outcome, CassetteHandler Handler, TimeSpan Elapsed)> RunAsync(Cassette cassette)
    {
        var handler = new CassetteHandler(cassette);
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://api.openai.com/") };
        http.Timeout = TimeSpan.FromMilliseconds(cassette.Expect.TimeoutMs ?? 10_000);
        var client = new ResponsesClient(http, () => Credential, new ProviderSettings(ReasoningModel: cassette.Request.Model));
        using var watchdog = new CancellationTokenSource(Watchdog);
        var clock = Stopwatch.StartNew();
        Outcome outcome;
        try
        {
            switch (cassette.Call)
            {
                case "nudge":
                    var nudge = await client.NudgeAsync(Frame(), "Write the report", watchdog.Token);
                    outcome = new Outcome(nudge.Text, 0, nudge.PointerTarget, null);
                    break;
                case "research":
                    var research = await client.RespondAsync(new AssistantRequest("What changed in the latest release?", 1, Research: true), watchdog.Token);
                    outcome = new Outcome(research.Text, research.Sources.Count, null, null);
                    break;
                default:
                    var reply = await client.RespondAsync(new AssistantRequest("What does this button do?", 1), watchdog.Token);
                    outcome = new Outcome(reply.Text, reply.Sources.Count, null, null);
                    break;
            }
        }
        catch (Exception exception)
        {
            Assert.False(watchdog.IsCancellationRequested, $"{cassette.Name}: the client ignored its own timeout and hit the test watchdog");
            outcome = new Outcome(null, 0, null, exception);
        }
        return (outcome, handler, clock.Elapsed);
    }

    private static void AssertRequestShape(Cassette cassette, CassetteHandler handler)
    {
        var expected = cassette.Request;
        Assert.Equal(1, handler.Calls);
        Assert.Equal(expected.Method, handler.SentMethod?.Method);
        Assert.Equal(expected.Path, handler.SentUri?.AbsolutePath);
        Assert.True(handler.SentBearer, "the client must send its key as a bearer header");
        var body = Assert.IsType<JsonObject>(handler.SentBody);
        Assert.Equal(expected.Model, (string?)body["model"]);
        Assert.Equal(expected.Format, (string?)body["text"]?["format"]?["name"]);
        var tools = (body["tools"] as JsonArray)?.Select(t => (string?)t?["type"] ?? "").ToList() ?? new List<string>();
        Assert.Equal(expected.Tools, tools);
        if (expected.BodyKeys.Count > 0)
            Assert.Equal(expected.BodyKeys.Order(StringComparer.Ordinal), body.Select(p => p.Key).Order(StringComparer.Ordinal));
    }

    /// <summary>An error shown to the user names the operation and status only, never text from the reply body.</summary>
    private static void AssertNoBodyLeak(Cassette cassette, Exception error)
    {
        foreach (var chunk in cassette.Response?.Chunks ?? new List<CassetteChunk>())
        {
            var message = (string?)chunk.Json?["error"]?["message"];
            if (!string.IsNullOrEmpty(message))
                Assert.DoesNotContain(message, error.Message, StringComparison.Ordinal);
            if (chunk.Text is { Length: >= 24 } text)
                Assert.DoesNotContain(text[..24], error.Message, StringComparison.Ordinal);
        }
    }

    private static CapturedFrame Frame() => new(
        new ScreenContext(
            Guid.NewGuid(), 1,
            new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1),
            1920, 1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)),
            DateTimeOffset.UnixEpoch),
        new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 });
}
