using System.IO;
using Boo.Core;
using Boo.Demo.Tests;
using Boo.Storage;

namespace Boo.LiveSmoke;

// Shared by tools/Boo.LiveSmoke (one round trip) and tools/Boo.VerifyLive (the full live journeys), which links this
// file. Moved here from LiveRoundTripTests.cs (with the anti-mock guard from PR #14) so both live projects seed Boo the same way.

/// <summary>
/// What the live run needs, read from the environment the workflow sets. Fails closed: a missing or malformed key
/// throws with a plain message (never the key itself), so the run goes red instead of skipping as green.
/// </summary>
/// <param name="Model">Chat model (screen help, research, nudges) for the text provider; null = Boo's default.</param>
/// <param name="FocusModel">Focus-check model for the text provider; null = Boo's default.</param>
/// <param name="RealtimeModel">OpenAI Realtime (voice) model; voice is always OpenAI. null = Boo's default.</param>
public sealed record LiveConfig(AiProvider TextProvider, string Key, string? Model, string? FocusModel = null, string? RealtimeModel = null)
{
    public const string ProviderVariable = "BOO_LIVE_TEXT_PROVIDER";
    public const string ModelVariable = "BOO_LIVE_MODEL";
    public const string FocusModelVariable = "BOO_LIVE_FOCUS_MODEL";
    public const string RealtimeModelVariable = "BOO_LIVE_REALTIME_MODEL";

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

        // Every model Boo uses is chosen by the workflow (cheapest by default), never hard-coded here: empty = Boo's default.
        return new LiveConfig(provider, value.Trim(),
            ModelFromEnvironment(ModelVariable), ModelFromEnvironment(FocusModelVariable), ModelFromEnvironment(RealtimeModelVariable));
    }

    private static string? ModelFromEnvironment(string variable)
    {
        var model = Environment.GetEnvironmentVariable(variable)?.Trim();
        if (string.IsNullOrEmpty(model)) return null;
        if (!AiProviders.IsValidModelId(model))
        {
            throw new InvalidOperationException($"LIVE SMOKE FAILED CLOSED: {variable} '{model}' is not a valid model id.");
        }
        return model;
    }

    /// <summary>
    /// The settings Boo starts with: the chosen text provider, and each model override that was given. The chat and
    /// focus models go to the text provider's own fields; the realtime model is OpenAI's (Boo's voice is always OpenAI).
    /// </summary>
    public ProviderSettings ProviderSettings()
    {
        var settings = new ProviderSettings(TextProvider: TextProvider);
        if (Model is not null)
        {
            settings = TextProvider switch
            {
                AiProvider.OpenAI => settings with { ReasoningModel = Model },
                AiProvider.Anthropic => settings with { AnthropicModel = Model },
                AiProvider.Gemini => settings with { GeminiModel = Model },
                _ => settings,
            };
        }
        if (FocusModel is not null)
        {
            settings = TextProvider switch
            {
                AiProvider.OpenAI => settings with { FocusModel = FocusModel },
                AiProvider.Anthropic => settings with { AnthropicFocusModel = FocusModel },
                AiProvider.Gemini => settings with { GeminiFocusModel = FocusModel },
                _ => settings,
            };
        }
        if (RealtimeModel is not null)
        {
            settings = settings with { RealtimeModel = RealtimeModel };
        }
        return settings;
    }

    /// <summary>Evidence lines: which model ids Boo was seeded with and where each came from. Model ids only, never a key.</summary>
    public IEnumerable<string> ModelEvidence()
    {
        var seeded = ProviderSettings();
        static string Source(string variable, string? value) => value is null ? $"Boo default ({variable} empty)" : $"from {variable}";
        yield return $"text provider: {AiProviders.DisplayName(TextProvider)}";
        yield return $"chat model: {AiProviders.ReasoningModel(seeded, TextProvider)} ({Source(ModelVariable, Model)})";
        yield return $"focus model: {AiProviders.FocusModel(seeded, TextProvider)} ({Source(FocusModelVariable, FocusModel)})";
        yield return $"realtime (voice) model: {seeded.RealtimeModel} ({Source(RealtimeModelVariable, RealtimeModel)}; unused while speech is off)";
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

    public const string CaptureScopeVariable = "BOO_CAPTURE_SCOPE";

    private static LiveConfig? s_config;

    // Not a static initializer: a throw there would surface as a bare TypeInitializationException.
    public static LiveConfig Config => s_config ??= LiveConfig.FromEnvironment();

    private static void Seed(string dataDirectory)
    {
        // Screenshots of Boo's own windows only, never the whole desktop, wherever this runs (BooAppFixture.CaptureScreen).
        Environment.SetEnvironmentVariable(CaptureScopeVariable, "boo-windows");
        // Anti-mock guard, before Boo starts: every AI host must resolve only to public internet addresses and no proxy
        // may sit in between. Loopback, LAN/private, link-local, unique-local, CGNAT, IPv6-wrapped forms of them or a
        // proxy fail the run closed. What it accepted goes into the evidence (the workflow prints every .txt there).
        var accepted = RealAiHostGuard.EnsureRealProviderHosts();
        var evidence = Environment.GetEnvironmentVariable("BOO_UI_ARTIFACTS");
        if (!string.IsNullOrWhiteSpace(evidence))
        {
            Directory.CreateDirectory(evidence);
            File.WriteAllLines(Path.Combine(evidence, "live-00-anti-mock-guard.txt"),
                new[] { "anti-mock guard ran before Boo started: no proxy, every AI host public" }.Concat(accepted));
            File.WriteAllLines(Path.Combine(evidence, "live-00-models.txt"),
                new[] { "models seeded into Boo's settings.json before it started (ids only, never a key)" }.Concat(Config.ModelEvidence()));
        }

        new CredentialStore(Path.Combine(dataDirectory, AiProviders.CredentialFileName(Config.TextProvider))).Save(Config.Key);
        new SettingsStore(Path.Combine(dataDirectory, "settings.json"))
            .Save(new AppSettings(SpeechEnabled: false, Provider: Config.ProviderSettings()));
    }
}
