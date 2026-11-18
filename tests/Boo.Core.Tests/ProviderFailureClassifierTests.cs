using System.Net;
using System.Net.Http;
using Boo.Core;

namespace Boo.Core.Tests;

public sealed class ProviderFailureClassifierTests
{
    private const string Secret = "sk-proj-SECRETSECRET";

    private static string OpenAiError(string? code, string? type = null, string message = "details " + Secret) =>
        "{\"error\":{\"message\":\"" + message + "\",\"type\":" + Quote(type) + ",\"param\":null,\"code\":" + Quote(code) + "}}";

    private static string Quote(string? value) => value is null ? "null" : "\"" + value + "\"";

    private static void AssertSafe(ProviderFailure failure)
    {
        Assert.False(string.IsNullOrWhiteSpace(failure.UserMessage));
        Assert.DoesNotContain(Secret, failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("details", failure.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Unauthorized_is_an_invalid_key_and_not_retryable()
    {
        var failure = ProviderFailureClassifier.FromHttp(401, OpenAiError("invalid_api_key", "invalid_request_error"));
        Assert.Equal(ProviderFailureKind.InvalidCredential, failure.Kind);
        Assert.Equal(401, failure.StatusCode);
        Assert.False(failure.Retryable);
        Assert.Contains("API key", failure.UserMessage, StringComparison.Ordinal);
        AssertSafe(failure);
    }

    [Fact]
    public void Unauthorized_without_a_body_is_still_an_invalid_key()
    {
        var failure = ProviderFailureClassifier.FromHttp(401, null);
        Assert.Equal(ProviderFailureKind.InvalidCredential, failure.Kind);
    }

    [Fact]
    public void Forbidden_is_permission_denied()
    {
        var failure = ProviderFailureClassifier.FromHttp(403, OpenAiError("unsupported_country_region_territory"));
        Assert.Equal(ProviderFailureKind.PermissionDenied, failure.Kind);
        Assert.False(failure.Retryable);
        AssertSafe(failure);
    }

    [Fact]
    public void Too_many_requests_with_insufficient_quota_is_quota_exhausted_and_not_retryable()
    {
        var failure = ProviderFailureClassifier.FromHttp(429, OpenAiError("insufficient_quota", "insufficient_quota"));
        Assert.Equal(ProviderFailureKind.QuotaExhausted, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("billing", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
        AssertSafe(failure);
    }

    [Fact]
    public void Insufficient_quota_type_without_code_is_still_quota_exhausted()
    {
        var failure = ProviderFailureClassifier.FromHttp(429, OpenAiError(null, "insufficient_quota"));
        Assert.Equal(ProviderFailureKind.QuotaExhausted, failure.Kind);
    }

    [Fact]
    public void Too_many_requests_with_rate_limit_code_is_retryable_rate_limit()
    {
        var failure = ProviderFailureClassifier.FromHttp(429, OpenAiError("rate_limit_exceeded", "requests"));
        Assert.Equal(ProviderFailureKind.RateLimited, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Contains("moment", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
        AssertSafe(failure);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("provider echoed the private screen")]
    [InlineData("{\"error\":\"not an object\"}")]
    [InlineData("[1,2,3]")]
    public void Too_many_requests_with_unreadable_body_is_rate_limited_and_mentions_both_causes(string? body)
    {
        var failure = ProviderFailureClassifier.FromHttp(429, body);
        Assert.Equal(ProviderFailureKind.RateLimited, failure.Kind);
        Assert.True(failure.Retryable);
        Assert.Contains("billing", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("private screen", failure.UserMessage, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(500)]
    [InlineData(502)]
    [InlineData(503)]
    [InlineData(504)]
    public void Server_errors_are_retryable(int status)
    {
        var failure = ProviderFailureClassifier.FromHttp(status, OpenAiError("server_error", "server_error"));
        Assert.Equal(ProviderFailureKind.ServerError, failure.Kind);
        Assert.True(failure.Retryable);
        AssertSafe(failure);
    }

    [Fact]
    public void Model_not_found_is_model_unavailable()
    {
        Assert.Equal(ProviderFailureKind.ModelUnavailable,
            ProviderFailureClassifier.FromHttp(404, OpenAiError("model_not_found")).Kind);
        Assert.Equal(ProviderFailureKind.ModelUnavailable,
            ProviderFailureClassifier.FromHttp(400, OpenAiError("model_not_found")).Kind);
    }

    [Fact]
    public void Other_client_errors_are_bad_requests_and_not_retryable()
    {
        var failure = ProviderFailureClassifier.FromHttp(400, OpenAiError("invalid_value"));
        Assert.Equal(ProviderFailureKind.BadRequest, failure.Kind);
        Assert.False(failure.Retryable);
        AssertSafe(failure);
    }

    [Fact]
    public void Request_timeout_status_is_a_retryable_timeout()
    {
        var failure = ProviderFailureClassifier.FromHttp(408, null);
        Assert.Equal(ProviderFailureKind.Timeout, failure.Kind);
        Assert.True(failure.Retryable);
    }

    [Fact]
    public void Huge_error_bodies_are_not_parsed()
    {
        var padding = new string(' ', ProviderFailureClassifier.MaxErrorBodyChars + 10);
        var failure = ProviderFailureClassifier.FromHttp(429, padding + OpenAiError("insufficient_quota"));
        Assert.Equal(ProviderFailureKind.RateLimited, failure.Kind);
    }

    [Theory]
    [InlineData(HttpRequestError.NameResolutionError, ProviderFailureKind.Offline)]
    [InlineData(HttpRequestError.ConnectionError, ProviderFailureKind.Offline)]
    [InlineData(HttpRequestError.ProxyTunnelError, ProviderFailureKind.Offline)]
    [InlineData(HttpRequestError.SecureConnectionError, ProviderFailureKind.SecureConnection)]
    [InlineData(HttpRequestError.Unknown, ProviderFailureKind.Unknown)]
    [InlineData(HttpRequestError.ResponseEnded, ProviderFailureKind.Unknown)]
    public void Transport_errors_are_classified(HttpRequestError error, ProviderFailureKind expected)
    {
        var failure = ProviderFailureClassifier.FromTransport(error);
        Assert.Equal(expected, failure.Kind);
        Assert.Null(failure.StatusCode);
        Assert.True(failure.Retryable);
        Assert.False(string.IsNullOrWhiteSpace(failure.UserMessage));
    }

    [Fact]
    public void Offline_message_tells_the_user_to_check_the_connection()
    {
        var failure = ProviderFailureClassifier.FromTransport(HttpRequestError.NameResolutionError);
        Assert.Contains("internet", failure.UserMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("insufficient_quota", null, ProviderFailureKind.QuotaExhausted)]
    [InlineData(null, "insufficient_quota", ProviderFailureKind.QuotaExhausted)]
    [InlineData("rate_limit_exceeded", "tokens", ProviderFailureKind.RateLimited)]
    [InlineData("invalid_api_key", "invalid_request_error", ProviderFailureKind.InvalidCredential)]
    [InlineData(null, "server_error", ProviderFailureKind.ServerError)]
    [InlineData("model_not_found", "invalid_request_error", ProviderFailureKind.ModelUnavailable)]
    [InlineData("input_audio_buffer_commit_empty", "invalid_request_error", ProviderFailureKind.Unknown)]
    [InlineData(null, null, ProviderFailureKind.Unknown)]
    public void Error_codes_without_status_are_classified(string? code, string? type, ProviderFailureKind expected)
    {
        var failure = ProviderFailureClassifier.FromErrorCode(code, type);
        Assert.Equal(expected, failure.Kind);
        Assert.Null(failure.StatusCode);
    }

    [Fact]
    public void Error_object_json_is_read_for_realtime_events()
    {
        var failure = ProviderFailureClassifier.FromErrorJson("{\"type\":\"insufficient_quota\",\"code\":\"insufficient_quota\",\"message\":\"" + Secret + "\"}");
        Assert.Equal(ProviderFailureKind.QuotaExhausted, failure.Kind);
        AssertSafe(failure);
    }

    [Fact]
    public void FromException_reads_provider_exceptions_and_http_request_exceptions()
    {
        var quota = ProviderFailureClassifier.FromHttp(429, OpenAiError("insufficient_quota"));
        Assert.Same(quota, ProviderFailureClassifier.FromException(new ProviderException("responses", quota)));

        var wrapped = new InvalidOperationException("outer", new ProviderException("responses", quota));
        Assert.Same(quota, ProviderFailureClassifier.FromException(wrapped));

        var dns = new HttpRequestException(HttpRequestError.NameResolutionError, "no such host " + Secret);
        Assert.Equal(ProviderFailureKind.Offline, ProviderFailureClassifier.FromException(dns)!.Kind);

        var status = new HttpRequestException("bad", null, HttpStatusCode.Unauthorized);
        Assert.Equal(ProviderFailureKind.InvalidCredential, ProviderFailureClassifier.FromException(status)!.Kind);

        Assert.Null(ProviderFailureClassifier.FromException(new InvalidOperationException("unrelated")));
    }

    [Fact]
    public void Provider_exception_message_names_the_status_but_never_the_body()
    {
        var failure = ProviderFailureClassifier.FromHttp(429, OpenAiError("rate_limit_exceeded"));
        var exception = new ProviderException("responses", failure);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Contains("429", exception.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Same(failure, exception.Failure);
    }
}
