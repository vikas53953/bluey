using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

/// <summary>
/// The provider clients surface classified, body-free failures (bad key, quota vs rate limit, outage, offline)
/// instead of one generic "request failed" message.
/// </summary>
public sealed class ProviderFailureClientTests
{
    private const string Secret = "sk-proj-LEAKCANARY";
    private static readonly ProviderSettings Settings = new();

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        public int Calls;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Calls);
            return respond(request);
        }
    }

    private static Handler Status(HttpStatusCode status, string? body) => new(_ => Task.FromResult(
        new HttpResponseMessage(status)
        {
            Content = new StringContent(body ?? string.Empty, Encoding.UTF8, "application/json")
        }));

    private static string OpenAiError(string code, string type) =>
        "{\"error\":{\"message\":\"echo " + Secret + "\",\"type\":\"" + type + "\",\"param\":null,\"code\":\"" + code + "\"}}";

    private static HttpClient Http(HttpMessageHandler handler) =>
        new(handler) { BaseAddress = new Uri("https://api.openai.com/") };

    private static async Task<ProviderException> RespondFailureAsync(Handler handler)
    {
        using var http = Http(handler);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);
        var error = await Record.ExceptionAsync(() => client.RespondAsync(new AssistantRequest("hello", 1), CancellationToken.None));
        var provider = Assert.IsType<ProviderException>(error);
        Assert.DoesNotContain(Secret, provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("fixture-key", provider.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, provider.Failure.UserMessage, StringComparison.Ordinal);
        return provider;
    }

    [Fact]
    public async Task Responses_quota_exhaustion_is_distinguished_from_rate_limiting()
    {
        var quota = await RespondFailureAsync(Status(HttpStatusCode.TooManyRequests, OpenAiError("insufficient_quota", "insufficient_quota")));
        Assert.Equal(ProviderFailureKind.QuotaExhausted, quota.Failure.Kind);
        Assert.False(quota.Failure.Retryable);
        Assert.Contains("429", quota.Message, StringComparison.Ordinal);

        var rate = await RespondFailureAsync(Status(HttpStatusCode.TooManyRequests, OpenAiError("rate_limit_exceeded", "requests")));
        Assert.Equal(ProviderFailureKind.RateLimited, rate.Failure.Kind);
        Assert.True(rate.Failure.Retryable);
    }

    [Fact]
    public async Task Responses_bad_key_and_outage_are_classified_and_sent_once()
    {
        var unauthorized = Status(HttpStatusCode.Unauthorized, OpenAiError("invalid_api_key", "invalid_request_error"));
        Assert.Equal(ProviderFailureKind.InvalidCredential, (await RespondFailureAsync(unauthorized)).Failure.Kind);
        Assert.Equal(1, unauthorized.Calls);

        var outage = Status(HttpStatusCode.ServiceUnavailable, "<html>upstream " + Secret + "</html>");
        Assert.Equal(ProviderFailureKind.ServerError, (await RespondFailureAsync(outage)).Failure.Kind);
        Assert.Equal(1, outage.Calls);
    }

    [Fact]
    public async Task Responses_dns_failure_is_offline()
    {
        var handler = new Handler(_ => Task.FromException<HttpResponseMessage>(
            new HttpRequestException(HttpRequestError.NameResolutionError, "No such host is known " + Secret)));
        var error = await RespondFailureAsync(handler);
        Assert.Equal(ProviderFailureKind.Offline, error.Failure.Kind);
        Assert.Contains("internet", error.Failure.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Decisions_classify_quota_and_offline_failures()
    {
        var context = new ScreenContext(
            Guid.NewGuid(), 1, new DisplayInfo("display-1", "Fixture", new RectD(0, 0, 1920, 1080), 1), 1920, 1080,
            new WindowIdentity(11, 22, "fixture", new RectD(0, 0, 800, 600)), DateTimeOffset.UtcNow);
        var frame = new CapturedFrame(context, new byte[] { 1, 2 });
        var session = new FocusSnapshot(Guid.NewGuid(), "Write", 600, DateTimeOffset.UnixEpoch, 30, 30,
            FocusStatus.Running, false, false, Array.Empty<FocusCheck>());

        using (var http = Http(Status(HttpStatusCode.TooManyRequests, OpenAiError("insufficient_quota", "insufficient_quota"))))
        {
            var client = new DecisionsClient(http, () => "fixture-key", Settings);
            var error = Assert.IsType<ProviderException>(await Record.ExceptionAsync(() => client.ClassifyAsync(frame, session, CancellationToken.None)));
            Assert.Equal(ProviderFailureKind.QuotaExhausted, error.Failure.Kind);
            Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        }

        using (var http = Http(new Handler(_ => Task.FromException<HttpResponseMessage>(
                   new HttpRequestException(HttpRequestError.ConnectionError, "refused")))))
        {
            var client = new DecisionsClient(http, () => "fixture-key", Settings);
            var error = Assert.IsType<ProviderException>(await Record.ExceptionAsync(() => client.ClassifyAsync(frame, session, CancellationToken.None)));
            Assert.Equal(ProviderFailureKind.Offline, error.Failure.Kind);
        }
    }

    [Fact]
    public void Realtime_error_events_carry_a_classified_message_without_provider_text()
    {
        var quota = RealtimeProtocol.Parse(
            "{\"type\":\"error\",\"error\":{\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\",\"message\":\"" + Secret + "\"}}");
        Assert.Equal("error", quota!.Kind);
        Assert.Equal(ProviderFailureClassifier.QuotaExhaustedMessage, quota.Text);

        var other = RealtimeProtocol.Parse(
            "{\"type\":\"error\",\"error\":{\"type\":\"invalid_request_error\",\"code\":\"input_audio_buffer_commit_empty\",\"message\":\"" + Secret + "\"}}");
        Assert.Equal(RealtimeProtocol.GenericErrorText, other!.Text);
    }

    [Fact]
    public void Realtime_failed_response_reports_quota_exhaustion()
    {
        var done = RealtimeProtocol.Parse(
            "{\"type\":\"response.done\",\"response\":{\"status\":\"failed\",\"status_details\":{\"type\":\"failed\",\"error\":{\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\",\"message\":\"" + Secret + "\"}}}}");
        Assert.Equal(ProviderFailureClassifier.QuotaExhaustedMessage, done!.Text);

        var plain = RealtimeProtocol.Parse("{\"type\":\"response.done\",\"response\":{\"status\":\"failed\"}}");
        Assert.Equal("Realtime response failed.", plain!.Text);
    }

    [Fact]
    public void Realtime_handshake_failures_are_classified_from_status_or_transport()
    {
        Assert.Equal(ProviderFailureKind.InvalidCredential, RealtimeFailures.Classify(401, new WebSocketException("x"))!.Kind);
        Assert.Equal(ProviderFailureKind.RateLimited, RealtimeFailures.Classify(429, new WebSocketException("x"))!.Kind);
        Assert.Equal(ProviderFailureKind.ServerError, RealtimeFailures.Classify(503, null)!.Kind);

        var dns = new WebSocketException("Unable to connect",
            new HttpRequestException(HttpRequestError.NameResolutionError, "no host"));
        Assert.Equal(ProviderFailureKind.Offline, RealtimeFailures.Classify(0, dns)!.Kind);
        Assert.Null(RealtimeFailures.Classify(0, new WebSocketException("unknown")));

        Assert.Equal(ProviderFailureKind.Timeout, RealtimeFailures.ConnectTimeout().Kind);
        Assert.True(RealtimeClient.ConnectTimeout <= TimeSpan.FromSeconds(20));
        Assert.True(RealtimeClient.KeepAliveTimeout > TimeSpan.Zero);
    }
}
