using System.Net.Http;
using System.Text.Json;

namespace Boo.Core;

/// <summary>Why an OpenAI call failed, in terms the user can act on.</summary>
public enum ProviderFailureKind
{
    Unknown,
    InvalidCredential,
    PermissionDenied,
    QuotaExhausted,
    RateLimited,
    ServerError,
    BadRequest,
    ModelUnavailable,
    Offline,
    SecureConnection,
    Timeout
}

/// <param name="StatusCode">The HTTP status, or null for transport failures and status-less error events.</param>
/// <param name="Retryable">True when trying the same request again later can succeed without user changes.</param>
/// <param name="UserMessage">Short, plain-English, actionable text. Never contains provider body text.</param>
public sealed record ProviderFailure(ProviderFailureKind Kind, int? StatusCode, bool Retryable, string UserMessage);

/// <summary>
/// Thrown by provider clients for a classified failure. Derives from <see cref="InvalidOperationException"/>
/// so existing callers that catch provider failures keep working. Its message names the operation and
/// status only; the provider's error body is never included.
/// </summary>
public sealed class ProviderException : InvalidOperationException
{
    public ProviderException(string operation, ProviderFailure failure)
        : base(BuildMessage(operation, failure))
    {
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public ProviderFailure Failure { get; }

    private static string BuildMessage(string operation, ProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        return failure.StatusCode is { } status
            ? $"OpenAI {operation} returned HTTP {status} ({failure.Kind})."
            : $"OpenAI {operation} failed ({failure.Kind}).";
    }
}

/// <summary>
/// Pure mapping from HTTP status / OpenAI error code / transport error to a <see cref="ProviderFailure"/>.
/// OpenAI reports both an exhausted quota and a rate limit as HTTP 429; only the error code
/// ("insufficient_quota" vs "rate_limit_exceeded") tells them apart.
/// </summary>
public static class ProviderFailureClassifier
{
    /// <summary>Error bodies longer than this are not parsed (classification falls back to the status).</summary>
    public const int MaxErrorBodyChars = 16 * 1024;

    public const string InvalidCredentialMessage = "OpenAI rejected the API key. Paste a valid key in Settings.";
    public const string PermissionDeniedMessage = "OpenAI denied access for this key or model. Check your OpenAI project permissions.";
    public const string QuotaExhaustedMessage = "OpenAI quota is used up. Add credit or raise the limit in your OpenAI billing settings.";
    public const string RateLimitedMessage = "OpenAI is busy with too many requests. Wait a moment and try again.";
    public const string RateLimitedOrQuotaMessage = "OpenAI is limiting requests. Wait a moment and try again; if it repeats, check your OpenAI billing.";
    public const string ServerErrorMessage = "OpenAI is having trouble right now. Try again in a moment.";
    public const string BadRequestMessage = "OpenAI could not accept this request. Try again; if it repeats, check the models in Settings.";
    public const string ModelUnavailableMessage = "The configured OpenAI model is not available for this key. Check the models in Settings.";
    public const string OfflineMessage = "Can't reach OpenAI. Check your internet connection and try again.";
    public const string SecureConnectionMessage = "A secure connection to OpenAI failed. Check your network, proxy, or system clock.";
    public const string TimeoutMessage = "OpenAI took too long to answer. Try again.";
    public const string UnknownMessage = "The OpenAI request failed. Try again in a moment.";

    public static ProviderFailure FromHttp(int statusCode, string? errorBody)
    {
        var (code, type) = ReadErrorBody(errorBody);
        var byCode = ClassifyCode(code, type);
        if (byCode is ProviderFailureKind.QuotaExhausted or ProviderFailureKind.ModelUnavailable or ProviderFailureKind.InvalidCredential)
            return Create(byCode, statusCode);

        return statusCode switch
        {
            401 => Create(ProviderFailureKind.InvalidCredential, statusCode),
            403 => Create(ProviderFailureKind.PermissionDenied, statusCode),
            404 => Create(ProviderFailureKind.ModelUnavailable, statusCode),
            408 => Create(ProviderFailureKind.Timeout, statusCode),
            429 => byCode == ProviderFailureKind.RateLimited
                ? Create(ProviderFailureKind.RateLimited, statusCode)
                : new ProviderFailure(ProviderFailureKind.RateLimited, statusCode, true, RateLimitedOrQuotaMessage),
            >= 500 and <= 599 => Create(ProviderFailureKind.ServerError, statusCode),
            >= 400 and <= 499 => Create(ProviderFailureKind.BadRequest, statusCode),
            _ => Create(ProviderFailureKind.Unknown, statusCode)
        };
    }

    /// <summary>Classifies an OpenAI error object's code/type when no HTTP status is available (Realtime error events).</summary>
    public static ProviderFailure FromErrorCode(string? code, string? type) => Create(ClassifyCode(code, type), null);

    /// <summary>Classifies the JSON of an OpenAI "error" object (the value of the "error" property).</summary>
    public static ProviderFailure FromErrorJson(string? errorObjectJson)
    {
        var (code, type) = ReadErrorObject(errorObjectJson);
        return FromErrorCode(code, type);
    }

    public static ProviderFailure FromTransport(HttpRequestError error) => error switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError
            => Create(ProviderFailureKind.Offline, null),
        HttpRequestError.SecureConnectionError => Create(ProviderFailureKind.SecureConnection, null),
        _ => new ProviderFailure(ProviderFailureKind.Unknown, null, true, UnknownMessage)
    };

    public static ProviderFailure Timeout() => Create(ProviderFailureKind.Timeout, null);

    /// <summary>
    /// Finds a classified failure in an exception chain: a <see cref="ProviderException"/>, or an
    /// <see cref="HttpRequestException"/> with a status code or transport error. Null when nothing matches.
    /// </summary>
    public static ProviderFailure? FromException(Exception? exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            switch (current)
            {
                case ProviderException provider:
                    return provider.Failure;
                case HttpRequestException { StatusCode: { } status }:
                    return FromHttp((int)status, null);
                case HttpRequestException http when http.HttpRequestError != HttpRequestError.Unknown:
                    return FromTransport(http.HttpRequestError);
            }
        }
        return null;
    }

    private static ProviderFailure Create(ProviderFailureKind kind, int? status) => kind switch
    {
        ProviderFailureKind.InvalidCredential => new(kind, status, false, InvalidCredentialMessage),
        ProviderFailureKind.PermissionDenied => new(kind, status, false, PermissionDeniedMessage),
        ProviderFailureKind.QuotaExhausted => new(kind, status, false, QuotaExhaustedMessage),
        ProviderFailureKind.RateLimited => new(kind, status, true, RateLimitedMessage),
        ProviderFailureKind.ServerError => new(kind, status, true, ServerErrorMessage),
        ProviderFailureKind.BadRequest => new(kind, status, false, BadRequestMessage),
        ProviderFailureKind.ModelUnavailable => new(kind, status, false, ModelUnavailableMessage),
        ProviderFailureKind.Offline => new(kind, status, true, OfflineMessage),
        ProviderFailureKind.SecureConnection => new(kind, status, true, SecureConnectionMessage),
        ProviderFailureKind.Timeout => new(kind, status, true, TimeoutMessage),
        _ => new(ProviderFailureKind.Unknown, status, false, UnknownMessage)
    };

    private static ProviderFailureKind ClassifyCode(string? code, string? type)
    {
        if (code == "insufficient_quota" || type == "insufficient_quota")
            return ProviderFailureKind.QuotaExhausted;
        if (code is "rate_limit_exceeded" || type is "rate_limit_exceeded" || type is "requests" or "tokens" && code is null)
            return ProviderFailureKind.RateLimited;
        if (code is "invalid_api_key" || type is "authentication_error")
            return ProviderFailureKind.InvalidCredential;
        if (code is "model_not_found")
            return ProviderFailureKind.ModelUnavailable;
        if (code is "server_error" || type is "server_error")
            return ProviderFailureKind.ServerError;
        return ProviderFailureKind.Unknown;
    }

    private static (string? Code, string? Type) ReadErrorBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Length > MaxErrorBodyChars)
            return (null, null);
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.TryGetProperty("error", out var error)
                ? ReadCodeAndType(error)
                : (null, null);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static (string? Code, string? Type) ReadErrorObject(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > MaxErrorBodyChars)
            return (null, null);
        try
        {
            using var document = JsonDocument.Parse(json);
            return ReadCodeAndType(document.RootElement);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static (string? Code, string? Type) ReadCodeAndType(JsonElement error)
    {
        if (error.ValueKind != JsonValueKind.Object)
            return (null, null);
        return (ReadString(error, "code"), ReadString(error, "type"));
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
