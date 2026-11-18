using System.Text.Json;
using Boo.Core;

namespace Boo.Core.Tests;

public sealed class AiProviderTests
{
    private static readonly JsonSerializerOptions StoreOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    [Fact]
    public void Provider_settings_default_to_openai_with_the_documented_models()
    {
        var settings = new ProviderSettings();

        Assert.Equal(AiProvider.OpenAI, settings.TextProvider);
        Assert.Equal("gpt-6.1-sol", settings.ReasoningModel);
        Assert.Equal("gpt-realtime-2.1", settings.RealtimeModel);
        Assert.Equal("gpt-6-luna", settings.FocusModel);
        Assert.Equal("marin", settings.Voice);
        Assert.Equal("claude-opus-5-5", settings.AnthropicModel);
        Assert.Equal("claude-haiku-5-5", settings.AnthropicFocusModel);
        Assert.Equal("gemini-4-argon", settings.GeminiModel);
        Assert.Equal("gemini-3.8-flash", settings.GeminiFocusModel);
        Assert.Equal(AiProviders.DefaultAnthropicModel, settings.AnthropicModel);
        Assert.Equal(AiProviders.DefaultAnthropicFocusModel, settings.AnthropicFocusModel);
        Assert.Equal(AiProviders.DefaultGeminiModel, settings.GeminiModel);
        Assert.Equal(AiProviders.DefaultGeminiFocusModel, settings.GeminiFocusModel);
    }

    [Fact]
    public void App_settings_with_gemini_round_trip_through_json_with_the_provider_as_a_string()
    {
        var settings = new AppSettings(Provider: new ProviderSettings(TextProvider: AiProvider.Gemini, GeminiModel: "gemini-custom"));

        var json = JsonSerializer.Serialize(settings, StoreOptions);
        var restored = JsonSerializer.Deserialize<AppSettings>(json, StoreOptions);

        Assert.Contains("\"TextProvider\": \"Gemini\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain("\"TextProvider\": 2", json, StringComparison.Ordinal);
        Assert.Equal(AiProvider.Gemini, restored!.Provider!.TextProvider);
        Assert.Equal("gemini-custom", restored.Provider.GeminiModel);
        Assert.Equal(settings, restored);
    }

    [Fact]
    public void App_settings_with_gemini_round_trip_with_default_serializer_options_too()
    {
        var settings = new AppSettings(Provider: new ProviderSettings(TextProvider: AiProvider.Gemini));

        var json = JsonSerializer.Serialize(settings);

        Assert.Contains("\"TextProvider\":\"Gemini\"", json, StringComparison.Ordinal);
        Assert.Equal(AiProvider.Gemini, JsonSerializer.Deserialize<AppSettings>(json)!.Provider!.TextProvider);
    }

    [Fact]
    public void Each_provider_serialises_as_its_name()
    {
        Assert.Equal("\"OpenAI\"", JsonSerializer.Serialize(AiProvider.OpenAI));
        Assert.Equal("\"Anthropic\"", JsonSerializer.Serialize(AiProvider.Anthropic));
        Assert.Equal("\"Gemini\"", JsonSerializer.Serialize(AiProvider.Gemini));
        Assert.Equal(AiProvider.Anthropic, JsonSerializer.Deserialize<AiProvider>("\"Anthropic\""));
    }

    [Fact]
    public void Old_settings_json_without_provider_fields_loads_with_openai_defaults()
    {
        const string legacy = """
            {
              "DisplayId": "display-1",
              "SpeechEnabled": true,
              "ChirpsEnabled": true,
              "ReducedMotion": false,
              "VerticalOffset": 0,
              "MicrophoneDevice": -1,
              "FileRoot": null,
              "Provider": {
                "ReasoningModel": "gpt-6.1-sol",
                "RealtimeModel": "gpt-realtime-2.1",
                "FocusModel": "gpt-6-luna",
                "Voice": "marin"
              },
              "StartWithWindows": false
            }
            """;

        var settings = JsonSerializer.Deserialize<AppSettings>(legacy, StoreOptions)!;

        Assert.Equal("display-1", settings.DisplayId);
        var provider = settings.Provider!;
        Assert.Equal(AiProvider.OpenAI, provider.TextProvider);
        Assert.Equal("gpt-6.1-sol", provider.ReasoningModel);
        Assert.Equal("gpt-6-luna", provider.FocusModel);
        Assert.Equal(AiProviders.DefaultAnthropicModel, provider.AnthropicModel);
        Assert.Equal(AiProviders.DefaultAnthropicFocusModel, provider.AnthropicFocusModel);
        Assert.Equal(AiProviders.DefaultGeminiModel, provider.GeminiModel);
        Assert.Equal(AiProviders.DefaultGeminiFocusModel, provider.GeminiFocusModel);
    }

    [Fact]
    public void Old_settings_json_without_any_provider_block_still_loads()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"SpeechEnabled\":false}", StoreOptions)!;

        Assert.False(settings.SpeechEnabled);
        Assert.Null(settings.Provider);
        Assert.Equal(AiProvider.OpenAI, (settings.Provider ?? new ProviderSettings()).TextProvider);
    }

    [Fact]
    public void Credential_file_names_are_distinct_and_openai_keeps_its_historical_name()
    {
        var names = AiProviders.All.Select(AiProviders.CredentialFileName).ToList();

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("credential.bin", AiProviders.CredentialFileName(AiProvider.OpenAI));
        Assert.Equal("credential.anthropic.bin", AiProviders.CredentialFileName(AiProvider.Anthropic));
        Assert.Equal("credential.gemini.bin", AiProviders.CredentialFileName(AiProvider.Gemini));
        Assert.All(names, name => Assert.DoesNotContain(Path.DirectorySeparatorChar, name));
    }

    [Fact]
    public void Origins_are_https_and_official_origin_checks_are_strict()
    {
        foreach (var provider in AiProviders.All)
        {
            var origin = AiProviders.Origin(provider);
            Assert.Equal(Uri.UriSchemeHttps, origin.Scheme);
            Assert.True(AiProviders.IsOfficialOrigin(provider, origin));
            Assert.True(AiProviders.IsOfficialOrigin(provider, new Uri($"https://{origin.Host}:443/")));
            Assert.True(AiProviders.IsOfficialOrigin(provider, new Uri($"https://{origin.Host.ToUpperInvariant()}/")));
            Assert.False(AiProviders.IsOfficialOrigin(provider, new Uri($"http://{origin.Host}/")));
            Assert.False(AiProviders.IsOfficialOrigin(provider, new Uri($"https://{origin.Host}:8443/")));
            Assert.False(AiProviders.IsOfficialOrigin(provider, new Uri("https://untrusted.example/")));
            Assert.False(AiProviders.IsOfficialOrigin(provider, new Uri($"https://{origin.Host}.evil.example/")));
        }

        Assert.Equal("api.anthropic.com", AiProviders.Origin(AiProvider.Anthropic).Host);
        Assert.Equal("generativelanguage.googleapis.com", AiProviders.Origin(AiProvider.Gemini).Host);
        Assert.Equal("api.openai.com", AiProviders.Origin(AiProvider.OpenAI).Host);
        Assert.False(AiProviders.IsOfficialOrigin(AiProvider.Anthropic, AiProviders.Origin(AiProvider.Gemini)));
        Assert.False(AiProviders.IsOfficialOrigin(AiProvider.Gemini, AiProviders.Origin(AiProvider.Anthropic)));
    }

    [Theory]
    [InlineData("claude-opus-5-5", true)]
    [InlineData("gemini-3.8-flash", true)]
    [InlineData("gpt-6.1-sol", true)]
    [InlineData("models:tuned_1", true)]
    [InlineData("a/b", false)]
    [InlineData("a b", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    [InlineData("a?x", false)]
    [InlineData("a#x", false)]
    [InlineData("../a", false)]
    public void Model_ids_that_could_change_the_url_are_rejected(string? model, bool valid)
    {
        Assert.Equal(valid, AiProviders.IsValidModelId(model));
    }

    [Fact]
    public void Over_long_model_ids_are_rejected()
    {
        Assert.True(AiProviders.IsValidModelId(new string('a', 128)));
        Assert.False(AiProviders.IsValidModelId(new string('a', 129)));
    }

    [Fact]
    public void Model_helpers_pick_the_provider_specific_fields()
    {
        var settings = new ProviderSettings(
            ReasoningModel: "o", FocusModel: "of",
            AnthropicModel: "a", AnthropicFocusModel: "af",
            GeminiModel: "g", GeminiFocusModel: "gf");

        Assert.Equal("o", AiProviders.ReasoningModel(settings, AiProvider.OpenAI));
        Assert.Equal("of", AiProviders.FocusModel(settings, AiProvider.OpenAI));
        Assert.Equal("a", AiProviders.ReasoningModel(settings, AiProvider.Anthropic));
        Assert.Equal("af", AiProviders.FocusModel(settings, AiProvider.Anthropic));
        Assert.Equal("g", AiProviders.ReasoningModel(settings, AiProvider.Gemini));
        Assert.Equal("gf", AiProviders.FocusModel(settings, AiProvider.Gemini));
    }

    [Fact]
    public void Missing_credential_messages_name_the_vendor_and_are_recognised()
    {
        Assert.Equal("OpenAI credential is not configured.", AiProviders.MissingCredentialMessage(AiProvider.OpenAI));
        Assert.Equal("Anthropic credential is not configured.", AiProviders.MissingCredentialMessage(AiProvider.Anthropic));
        Assert.Equal("Gemini credential is not configured.", AiProviders.MissingCredentialMessage(AiProvider.Gemini));

        foreach (var provider in AiProviders.All)
        {
            var message = AiProviders.MissingCredentialMessage(provider);
            Assert.True(AiProviders.IsMissingCredentialMessage(message));
            Assert.Equal(provider, AiProviders.ProviderOfMissingCredentialMessage(message));
        }
        Assert.False(AiProviders.IsMissingCredentialMessage("Gemini credential is not configured"));
        Assert.False(AiProviders.IsMissingCredentialMessage(null));
        Assert.Null(AiProviders.ProviderOfMissingCredentialMessage("unrelated"));
    }

    [Fact]
    public void Display_and_vendor_names_are_distinct_per_provider()
    {
        Assert.Equal("Anthropic Claude", AiProviders.DisplayName(AiProvider.Anthropic));
        Assert.Equal("Google Gemini", AiProviders.DisplayName(AiProvider.Gemini));
        Assert.Equal("OpenAI", AiProviders.DisplayName(AiProvider.OpenAI));
        Assert.Equal("Anthropic", AiProviders.VendorName(AiProvider.Anthropic));
        Assert.Equal("Gemini", AiProviders.VendorName(AiProvider.Gemini));
        Assert.Equal(3, AiProviders.All.Select(AiProviders.VendorName).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Rate_limit_messages_name_anthropic_while_the_openai_overload_is_unchanged()
    {
        var anthropic = ProviderFailureClassifier.FromHttp(429, null, AiProvider.Anthropic);
        var openAi = ProviderFailureClassifier.FromHttp(429, null);

        Assert.Equal(ProviderFailureKind.RateLimited, anthropic.Kind);
        Assert.Contains("Anthropic", anthropic.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("OpenAI", anthropic.UserMessage, StringComparison.Ordinal);
        Assert.Equal(ProviderFailureClassifier.RateLimitedOrQuotaMessage, openAi.UserMessage);
        Assert.Contains("OpenAI", openAi.UserMessage, StringComparison.Ordinal);
        Assert.Equal(openAi, ProviderFailureClassifier.FromHttp(429, null, AiProvider.OpenAI));
    }

    [Fact]
    public void Every_vendor_message_names_its_own_vendor()
    {
        foreach (var provider in new[] { AiProvider.Anthropic, AiProvider.Gemini })
        {
            var vendor = AiProviders.VendorName(provider);
            foreach (var status in new[] { 400, 401, 403, 404, 408, 429, 500, 503 })
            {
                var failure = ProviderFailureClassifier.FromHttp(status, null, provider);
                Assert.Contains(vendor, failure.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("OpenAI", failure.UserMessage, StringComparison.Ordinal);
            }
            Assert.Contains(vendor, ProviderFailureClassifier.Timeout(provider).UserMessage, StringComparison.Ordinal);
            Assert.Contains(vendor, ProviderFailureClassifier.FromTransport(System.Net.Http.HttpRequestError.NameResolutionError, provider).UserMessage, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Anthropic_overloaded_error_is_a_retryable_server_error()
    {
        var failure = ProviderFailureClassifier.FromHttp(529, "{\"type\":\"error\",\"error\":{\"type\":\"overloaded_error\",\"message\":\"Overloaded secret-body\"}}", AiProvider.Anthropic);

        Assert.Equal(ProviderFailureKind.ServerError, failure.Kind);
        Assert.Equal(529, failure.StatusCode);
        Assert.True(failure.Retryable);
        Assert.Contains("Anthropic", failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", failure.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Anthropic_authentication_error_is_an_invalid_credential()
    {
        var failure = ProviderFailureClassifier.FromHttp(401, "{\"type\":\"error\",\"error\":{\"type\":\"authentication_error\",\"message\":\"invalid x-api-key\"}}", AiProvider.Anthropic);

        Assert.Equal(ProviderFailureKind.InvalidCredential, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("Anthropic", failure.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Gemini_resource_exhausted_is_a_retryable_rate_limit()
    {
        var failure = ProviderFailureClassifier.FromHttp(429, "{\"error\":{\"code\":429,\"message\":\"Quota exceeded secret-body\",\"status\":\"RESOURCE_EXHAUSTED\"}}", AiProvider.Gemini);

        Assert.Equal(ProviderFailureKind.RateLimited, failure.Kind);
        Assert.Equal(429, failure.StatusCode);
        Assert.True(failure.Retryable);
        Assert.Contains("Gemini", failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("secret-body", failure.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Gemini_invalid_api_key_on_400_is_an_invalid_credential()
    {
        var failure = ProviderFailureClassifier.FromHttp(400, "{\"error\":{\"code\":400,\"message\":\"API key not valid. Please pass a valid API key.\",\"status\":\"INVALID_ARGUMENT\"}}", AiProvider.Gemini);

        Assert.Equal(ProviderFailureKind.InvalidCredential, failure.Kind);
        Assert.False(failure.Retryable);
        Assert.Contains("Gemini", failure.UserMessage, StringComparison.Ordinal);
        Assert.DoesNotContain("Please pass", failure.UserMessage, StringComparison.Ordinal);
    }

    [Fact]
    public void Provider_exception_message_starts_with_the_vendor_and_carries_the_provider()
    {
        var failure = ProviderFailureClassifier.FromHttp(503, null, AiProvider.Gemini);
        var exception = new ProviderException(AiProvider.Gemini, "generateContent", failure);

        Assert.StartsWith("Gemini ", exception.Message, StringComparison.Ordinal);
        Assert.Contains("generateContent", exception.Message, StringComparison.Ordinal);
        Assert.Contains("503", exception.Message, StringComparison.Ordinal);
        Assert.Equal(AiProvider.Gemini, exception.Provider);
        Assert.Same(failure, exception.Failure);
        Assert.IsAssignableFrom<InvalidOperationException>(exception);
        Assert.Same(failure, ProviderFailureClassifier.FromException(exception));

        var anthropic = new ProviderException(AiProvider.Anthropic, "messages", ProviderFailureClassifier.Timeout(AiProvider.Anthropic));
        Assert.StartsWith("Anthropic messages failed", anthropic.Message, StringComparison.Ordinal);

        var legacy = new ProviderException("responses", ProviderFailureClassifier.FromHttp(429, null));
        Assert.Equal(AiProvider.OpenAI, legacy.Provider);
        Assert.StartsWith("OpenAI ", legacy.Message, StringComparison.Ordinal);
    }
}
