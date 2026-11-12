using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;

namespace Boo.LiveSmoke;

// Shared by tools/Boo.LiveSmoke (one round trip) and tools/Boo.VerifyLive (the full live journeys), which links this
// file. Moved here unchanged from LiveRoundTripTests.cs so both live projects seed Boo the same way.

/// <summary>
/// What the live run needs, read from the environment the workflow sets. Fails closed: a missing or malformed key
/// throws with a plain message (never the key itself), so the run goes red instead of skipping as green.
/// </summary>
public sealed record LiveConfig(AiProvider TextProvider, string Key, string? Model)
{
    public const string ProviderVariable = "BOO_LIVE_TEXT_PROVIDER";
    public const string ModelVariable = "BOO_LIVE_MODEL";

    public static string KeyVariable(AiProvider provider) => provider switch
    {
        AiProvider.OpenAI => "BOO_LIVE_OPENAI_KEY",
        AiProvider.Anthropic => "BOO_LIVE_ANTHROPIC_KEY",
        AiProvider.Gemini => "BOO_LIVE_GEMINI_KEY",
        _ => throw new ArgumentOutOfRangeException(nameof(provider)),
    };

    public static LiveConfig FromEnvironment()
    {
        var providerText = (Environment.GetEnvironmentVariable(ProviderVariable) ?? string.Empty).Trim();
        var provider = providerText.ToLowerInvariant() switch
        {
            "" or "openai" => AiProvider.OpenAI,
            "anthropic" or "claude" => AiProvider.Anthropic,
            "gemini" or "google" => AiProvider.Gemini,
            _ => throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: {ProviderVariable} must be openai, anthropic or gemini (got '{providerText}')."),
        };

        // Only the chosen provider's key is read and seeded. Any other key in Boo's data folder would let a different
        // service (for example OpenAI voice, which always uses the OpenAI key) answer and turn the run into a false green.
        var value = Environment.GetEnvironmentVariable(KeyVariable(provider));
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: text provider is {AiProviders.DisplayName(provider)} but the repository secret " +
                $"{KeyVariable(provider)} is missing or empty. Add it under Settings > Secrets and variables > Actions " +
                "(see docs/LIVE_AI_SMOKE.md). This run is not a pass.");
        }
        if (AiProviders.CredentialRejectReason(provider, value) is { } reason)
        {
            throw new InvalidOperationException(
                $"LIVE SMOKE FAILED CLOSED: the {KeyVariable(provider)} secret is not a usable key: {reason}");
        }

        var model = Environment.GetEnvironmentVariable(ModelVariable)?.Trim();
        if (string.IsNullOrEmpty(model))
        {
            model = null;
        }
        else if (!AiProviders.IsValidModelId(model))
        {
            throw new InvalidOperationException($"LIVE SMOKE FAILED CLOSED: {ModelVariable} '{model}' is not a valid model id.");
        }

        return new LiveConfig(provider, value.Trim(), model);
    }

    /// <summary>The settings Boo starts with: the chosen text provider, and the model override for it if one was given.</summary>
    public ProviderSettings ProviderSettings()
    {
        var settings = new ProviderSettings(TextProvider: TextProvider);
        if (Model is null) return settings;
        return TextProvider switch
        {
            AiProvider.OpenAI => settings with { ReasoningModel = Model },
            AiProvider.Anthropic => settings with { AnthropicModel = Model },
            AiProvider.Gemini => settings with { GeminiModel = Model },
            _ => settings,
        };
    }
}

/// <summary>
/// The demo harness's fixture, with Boo's data folder pre-seeded before launch: the chosen provider's key (and only
/// that one) goes through Boo's own DPAPI <see cref="CredentialStore"/> (so no key is ever typed into the UI or visible
/// in a screenshot), and settings.json selects the text provider with speech OFF. With speech on, Boo hands a typed
/// question to the OpenAI voice conversation instead of the text provider, which would test the wrong path.
/// The temp data folder is deleted when the fixture is disposed.
/// </summary>
public sealed class LiveAppFixture : BooAppFixture
{
    public LiveAppFixture()
        : base(Seed)
    {
    }

    private static LiveConfig? s_config;

    // Not a static initializer: a throw there would surface as a bare TypeInitializationException.
    public static LiveConfig Config => s_config ??= LiveConfig.FromEnvironment();

    private static void Seed(string dataDirectory)
    {
        new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(Config.TextProvider))).Save(Config.Key);
        new SettingsStore(Path.Combine(dataDirectory, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: Config.ProviderSettings()));
    }
}
