using Boo.Core;

namespace Boo.Providers.Tests;

public sealed class AssistantProvidersTests
{
    private static readonly ProviderSettings Settings = new();

    [Fact]
    public void Screen_assistant_factory_maps_each_provider_to_its_client()
    {
        using var http = new HttpClient();

        Assert.IsType<ResponsesClient>(AssistantProviders.CreateScreenAssistant(AiProvider.OpenAI, http, () => "k", Settings));
        Assert.IsType<AnthropicClient>(AssistantProviders.CreateScreenAssistant(AiProvider.Anthropic, http, () => "k", Settings));
        Assert.IsType<GeminiClient>(AssistantProviders.CreateScreenAssistant(AiProvider.Gemini, http, () => "k", Settings));
    }

    [Fact]
    public void Focus_classifier_factory_maps_each_provider_to_its_client()
    {
        using var http = new HttpClient();

        Assert.IsType<DecisionsClient>(AssistantProviders.CreateFocusClassifier(AiProvider.OpenAI, http, () => "k", Settings));
        Assert.IsType<AnthropicClient>(AssistantProviders.CreateFocusClassifier(AiProvider.Anthropic, http, () => "k", Settings));
        Assert.IsType<GeminiClient>(AssistantProviders.CreateFocusClassifier(AiProvider.Gemini, http, () => "k", Settings));
    }

    [Fact]
    public void Factory_follows_the_text_provider_in_settings()
    {
        using var http = new HttpClient();
        var gemini = Settings with { TextProvider = AiProvider.Gemini };
        var anthropic = Settings with { TextProvider = AiProvider.Anthropic };

        Assert.IsType<GeminiClient>(AssistantProviders.CreateScreenAssistant(gemini.TextProvider, http, () => "k", gemini));
        Assert.IsType<GeminiClient>(AssistantProviders.CreateFocusClassifier(gemini.TextProvider, http, () => "k", gemini));
        Assert.IsType<AnthropicClient>(AssistantProviders.CreateScreenAssistant(anthropic.TextProvider, http, () => "k", anthropic));
        Assert.IsType<AnthropicClient>(AssistantProviders.CreateFocusClassifier(anthropic.TextProvider, http, () => "k", anthropic));
    }

    [Fact]
    public void Factory_creates_a_fresh_instance_per_call()
    {
        using var http = new HttpClient();

        Assert.NotSame(
            AssistantProviders.CreateScreenAssistant(AiProvider.Anthropic, http, () => "k", Settings),
            AssistantProviders.CreateScreenAssistant(AiProvider.Anthropic, http, () => "k", Settings));
    }

    [Fact]
    public void Unknown_provider_values_are_rejected()
    {
        using var http = new HttpClient();
        var unknown = (AiProvider)999;

        Assert.Throws<ArgumentOutOfRangeException>(() => AssistantProviders.CreateScreenAssistant(unknown, http, () => "k", Settings));
        Assert.Throws<ArgumentOutOfRangeException>(() => AssistantProviders.CreateFocusClassifier(unknown, http, () => "k", Settings));
    }

    [Fact]
    public void Clients_reject_null_dependencies()
    {
        using var http = new HttpClient();

        Assert.Throws<ArgumentNullException>(() => new AnthropicClient(null!, () => "k", Settings));
        Assert.Throws<ArgumentNullException>(() => new AnthropicClient(http, null!, Settings));
        Assert.Throws<ArgumentNullException>(() => new AnthropicClient(http, () => "k", null!));
        Assert.Throws<ArgumentNullException>(() => new GeminiClient(null!, () => "k", Settings));
        Assert.Throws<ArgumentNullException>(() => new GeminiClient(http, null!, Settings));
        Assert.Throws<ArgumentNullException>(() => new GeminiClient(http, () => "k", null!));
    }
}
