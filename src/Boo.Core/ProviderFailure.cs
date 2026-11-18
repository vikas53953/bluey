using System.Net.Http;
using System.Text.Json;

namespace Boo.Core;

/// <summary>Why a provider (OpenAI, Anthropic or Gemini) call failed, in terms the user can act on.</summary>
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
        : this(AiProvider.OpenAI, operation, failure)
    {
    }

    public ProviderException(AiProvider provider, string operation, ProviderFailure failure)
        : base(BuildMessage(provider, operation, failure))
    {
        Provider = provider;
        Failure = failure ?? throw new ArgumentNullException(nameof(failure));
    }

    public AiProvider Provider { get; }

    public ProviderFailure Failure { get; }

    private static string BuildMessage(AiProvider provider, string operation, ProviderFailure failure)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var vendor = AiProviders.VendorName(provider);
        return failure.StatusCode is { } status
            ? $"{vendor} {operation} returned HTTP {status} ({failure.Kind})."
            : $"{vendor} {operation} failed ({failure.Kind}).";
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

    public static ProviderFailure FromHttp(int statusCode, string? errorBody) => FromHttp(statusCode, errorBody, AiProvider.OpenAI);

    /// <summary>
    /// Provider-aware classification. Anthropic reports a typed "error.type" (authentication_error, rate_limit_error,
    /// overloaded_error, ...); Gemini reports a gRPC-style "error.status" (RESOURCE_EXHAUSTED, PERMISSION_DENIED, ...)
    /// and signals a bad key as HTTP 400 with "API key not valid" in the message. User messages name the vendor.
    /// </summary>
    public static ProviderFailure FromHttp(int statusCode, string? errorBody, AiProvider provider)
    {
        var (code, type) = ReadErrorBody(errorBody);
        var byCode = ClassifyCode(code, type);
        if (byCode is ProviderFailureKind.QuotaExhausted or ProviderFailureKind.ModelUnavailable or ProviderFailureKind.InvalidCredential)
            return Create(byCode, statusCode, provider);

        return statusCode switch
        {
            401 => Create(ProviderFailureKind.InvalidCredential, statusCode, provider),
            403 => Create(ProviderFailureKind.PermissionDenied, statusCode, provider),
            404 => Create(ProviderFailureKind.ModelUnavailable, statusCode, provider),
            408 => Create(ProviderFailureKind.Timeout, statusCode, provider),
            429 => byCode == ProviderFailureKind.RateLimited
                ? Create(ProviderFailureKind.RateLimited, statusCode, provider)
                : new ProviderFailure(ProviderFailureKind.RateLimited, statusCode, true, ForVendor(RateLimitedOrQuotaMessage, provider)),
            >= 500 and <= 599 => Create(ProviderFailureKind.ServerError, statusCode, provider),
            >= 400 and <= 499 => Create(ProviderFailureKind.BadRequest, statusCode, provider),
            _ => Create(ProviderFailureKind.Unknown, statusCode, provider)
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

    public static ProviderFailure FromTransport(HttpRequestError error) => FromTransport(error, AiProvider.OpenAI);

    public static ProviderFailure FromTransport(HttpRequestError error, AiProvider provider) => error switch
    {
        HttpRequestError.NameResolutionError or HttpRequestError.ConnectionError or HttpRequestError.ProxyTunnelError
            => Create(ProviderFailureKind.Offline, null, provider),
        HttpRequestError.SecureConnectionError => Create(ProviderFailureKind.SecureConnection, null, provider),
        _ => new ProviderFailure(ProviderFailureKind.Unknown, null, true, ForVendor(UnknownMessage, provider))
    };

    public static ProviderFailure Timeout() => Create(ProviderFailureKind.Timeout, null);

    public static ProviderFailure Timeout(AiProvider provider) => Create(ProviderFailureKind.Timeout, null, provider);

    /// <summary>Rewrites an OpenAI-worded user message for another vendor. OpenAI text is returned unchanged.</summary>
    public static string ForVendor(string message, AiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(message);
        return provider == AiProvider.OpenAI ? message : message.Replace("OpenAI", AiProviders.VendorName(provider), StringComparison.Ordinal);
    }

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

    private static ProviderFailure Create(ProviderFailureKind kind, int? status) => Create(kind, status, AiProvider.OpenAI);

    private static ProviderFailure Create(ProviderFailureKind kind, int? status, AiProvider provider)
    {
        var failure = kind switch
        {
            ProviderFailureKind.InvalidCredential => new ProviderFailure(kind, status, false, InvalidCredentialMessage),
            ProviderFailureKind.PermissionDenied => new ProviderFailure(kind, status, false, PermissionDeniedMessage),
            ProviderFailureKind.QuotaExhausted => new ProviderFailure(kind, status, false, QuotaExhaustedMessage),
            ProviderFailureKind.RateLimited => new ProviderFailure(kind, status, true, RateLimitedMessage),
            ProviderFailureKind.ServerError => new ProviderFailure(kind, status, true, ServerErrorMessage),
            ProviderFailureKind.BadRequest => new ProviderFailure(kind, status, false, BadRequestMessage),
            ProviderFailureKind.ModelUnavailable => new ProviderFailure(kind, status, false, ModelUnavailableMessage),
            ProviderFailureKind.Offline => new ProviderFailure(kind, status, true, OfflineMessage),
            ProviderFailureKind.SecureConnection => new ProviderFailure(kind, status, true, SecureConnectionMessage),
            ProviderFailureKind.Timeout => new ProviderFailure(kind, status, true, TimeoutMessage),
            _ => new ProviderFailure(ProviderFailureKind.Unknown, status, false, UnknownMessage)
        };
        return provider == AiProvider.OpenAI ? failure : failure with { UserMessage = ForVendor(failure.UserMessage, provider) };
    }

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

        // Anthropic error types.
        switch (type)
        {
            case "permission_error": return ProviderFailureKind.PermissionDenied;
            case "not_found_error": return ProviderFailureKind.ModelUnavailable;
            case "rate_limit_error": return ProviderFailureKind.RateLimited;
            case "billing_error": return ProviderFailureKind.QuotaExhausted;
            case "overloaded_error" or "api_error": return ProviderFailureKind.ServerError;
        }

        // Gemini (Google API) statuses.
        switch (type)
        {
            case "UNAUTHENTICATED": return ProviderFailureKind.InvalidCredential;
            case "PERMISSION_DENIED": return ProviderFailureKind.PermissionDenied;
            // RESOURCE_EXHAUSTED covers both a burst rate limit and an exhausted quota; leaving it unclassified lets the
            // 429 path use the combined "wait, then check billing" wording.
            case "NOT_FOUND": return ProviderFailureKind.ModelUnavailable;
            case "UNAVAILABLE" or "INTERNAL": return ProviderFailureKind.ServerError;
        }
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
        var code = ReadString(error, "code");
        // Gemini puts the gRPC status where OpenAI/Anthropic put "type", and reports a bad key only in the message.
        var type = ReadString(error, "type") ?? ReadString(error, "status");
        if (code is null && type is "INVALID_ARGUMENT" or "PERMISSION_DENIED" &&
            ReadString(error, "message") is { } message &&
            message.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            code = "invalid_api_key";
        }
        return (code, type);
    }

    private static string? ReadString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}
