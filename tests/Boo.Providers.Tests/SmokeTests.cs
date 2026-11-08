using Boo.Core;
using Boo.Providers;

namespace Boo.Providers.Tests;

public sealed class SmokeTests
{
    [Fact]
    public void Factory_returns_the_openai_clients_by_default()
    {
        using var http = new HttpClient();
        var settings = new ProviderSettings();
        Assert.IsType<ResponsesClient>(AssistantProviders.CreateScreenAssistant(settings.TextProvider, http, () => "k", settings));
        Assert.IsType<DecisionsClient>(AssistantProviders.CreateFocusClassifier(settings.TextProvider, http, () => "k", settings));
    }
}
