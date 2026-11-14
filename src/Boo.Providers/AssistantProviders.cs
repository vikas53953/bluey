using Boo.Core;

namespace Boo.Providers;

/// <summary>Builds the text-request clients for the provider chosen in Settings. Voice (OpenAI Realtime) is not routed here.</summary>
public static class AssistantProviders
{
    public static IScreenAssistant CreateScreenAssistant(AiProvider provider, HttpClient http, Func<string?> credential, ProviderSettings settings) =>
        provider switch
        {
            AiProvider.OpenAI => new ResponsesClient(http, credential, settings),
            AiProvider.Anthropic => new AnthropicClient(http, credential, settings),
            AiProvider.Gemini => new GeminiClient(http, credential, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
        };

    public static IFocusClassifier CreateFocusClassifier(AiProvider provider, HttpClient http, Func<string?> credential, ProviderSettings settings) =>
        provider switch
        {
            AiProvider.OpenAI => new DecisionsClient(http, credential, settings),
            AiProvider.Anthropic => new AnthropicClient(http, credential, settings),
            AiProvider.Gemini => new GeminiClient(http, credential, settings),
            _ => throw new ArgumentOutOfRangeException(nameof(provider), provider, "Unknown AI provider.")
        };
}
