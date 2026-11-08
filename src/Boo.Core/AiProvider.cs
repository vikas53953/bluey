using System.Text.Json.Serialization;

namespace Boo.Core;

/// <summary>Which cloud model family answers Boo's text requests (screen explain, research, focus nudges and classification).</summary>
/// <remarks>Voice always uses OpenAI Realtime; this selects the provider for the non-voice requests only.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<AiProvider>))]
public enum AiProvider
{
    OpenAI,
    Anthropic,
    Gemini
}

/// <summary>Static facts about each provider: display name, official API origin, default models and the per-user credential file.</summary>
public static class AiProviders
{
    public static readonly IReadOnlyList<AiProvider> All = [AiProvider.OpenAI, AiProvider.Anthropic, AiProvider.Gemini];

    public const string DefaultAnthropicModel = "claude-opus-5-5";
    public const string DefaultAnthropicFocusModel = "claude-haiku-5-5";
    public const string DefaultGeminiModel = "gemini-4-argon";
    public const string DefaultGeminiFocusModel = "gemini-3.8-flash";

    public static string DisplayName(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "OpenAI",
        AiProvider.Anthropic => "Anthropic Claude",
        AiProvider.Gemini => "Google Gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
    };

    /// <summary>Short vendor name used in failure messages ("Anthropic rejected the API key").</summary>
    public static string VendorName(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "OpenAI",
        AiProvider.Anthropic => "Anthropic",
        AiProvider.Gemini => "Gemini",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
    };

    /// <summary>The only HTTPS origin a provider client may send its credential to.</summary>
    public static Uri Origin(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => new Uri("https://api.openai.com/"),
        AiProvider.Anthropic => new Uri("https://api.anthropic.com/"),
        AiProvider.Gemini => new Uri("https://generativelanguage.googleapis.com/"),
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
    };

    public static bool IsOfficialOrigin(AiProvider provider, Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        var origin = Origin(provider);
        return uri.Scheme == Uri.UriSchemeHttps &&
               string.Equals(uri.Host, origin.Host, StringComparison.OrdinalIgnoreCase) &&
               (uri.IsDefaultPort || uri.Port == 443);
    }

    /// <summary>
    /// File name (inside Boo's data directory) of the DPAPI-protected credential for a provider.
    /// OpenAI keeps its historical name so existing installs keep their key.
    /// </summary>
    public static string CredentialFileName(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "credential.bin",
        AiProvider.Anthropic => "credential.anthropic.bin",
        AiProvider.Gemini => "credential.gemini.bin",
        _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
    };

    /// <summary>Exact message a client throws (as <see cref="InvalidOperationException"/>) when no key is saved. The app matches it, never shows it raw.</summary>
    public static string MissingCredentialMessage(AiProvider provider) => $"{VendorName(provider)} credential is not configured.";

    public static bool IsMissingCredentialMessage(string? message) =>
        message is not null && All.Any(provider => string.Equals(message, MissingCredentialMessage(provider), StringComparison.Ordinal));

    /// <summary>Which provider a missing-credential message names, or null.</summary>
    public static AiProvider? ProviderOfMissingCredentialMessage(string? message)
    {
        foreach (var provider in All)
        {
            if (string.Equals(message, MissingCredentialMessage(provider), StringComparison.Ordinal))
                return provider;
        }
        return null;
    }

    /// <summary>The model a settings record uses for screen help, research and nudges on the given provider.</summary>
    public static string ReasoningModel(ProviderSettings settings, AiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return provider switch
        {
            AiProvider.OpenAI => settings.ReasoningModel,
            AiProvider.Anthropic => settings.AnthropicModel,
            AiProvider.Gemini => settings.GeminiModel,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
        };
    }

    /// <summary>The model a settings record uses for focus classification on the given provider.</summary>
    public static string FocusModel(ProviderSettings settings, AiProvider provider)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return provider switch
        {
            AiProvider.OpenAI => settings.FocusModel,
            AiProvider.Anthropic => settings.AnthropicFocusModel,
            AiProvider.Gemini => settings.GeminiFocusModel,
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
        };
    }

    /// <summary>True when a model id is a plausible identifier: no whitespace, path or query characters that could change the request URL.</summary>
    public static bool IsValidModelId(string? model) =>
        !string.IsNullOrWhiteSpace(model) && model.Length <= 128 &&
        model.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or ':');
}

/// <summary>Answers screen-help, research and nudge requests. Implemented per provider; the app only sees this interface.</summary>
public interface IScreenAssistant
{
    Task<AssistantReply> RespondAsync(AssistantRequest request, CancellationToken cancellationToken, ControlObservation? controls = null);

    Task<FocusNudge> NudgeAsync(CapturedFrame frame, string task, CancellationToken cancellationToken);
}

/// <summary>Judges whether the current screen is on the declared focus task.</summary>
public interface IFocusClassifier
{
    Task<FocusDecision> ClassifyAsync(CapturedFrame frame, FocusSnapshot session, CancellationToken cancellationToken);
}
