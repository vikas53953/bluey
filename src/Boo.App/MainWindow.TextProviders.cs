using System.Net.Http;
using Boo.Core;
using Boo.Providers;

namespace Boo.App;

/// <summary>Text-provider construction and selected-folder context wiring.</summary>
public partial class MainWindow
{
    private IScreenAssistant EnsureResponses()
    {
        if (_responses is not null)
        {
            return _responses;
        }

        var providerSettings = _settings.Provider ?? new ProviderSettings();
        var provider = providerSettings.TextProvider;
        _http ??= new HttpClient { Timeout = TimeSpan.FromSeconds(45) };
        _responses = AssistantProviders.CreateScreenAssistant(provider, _http, () => StoreFor(provider).Load(), providerSettings,
            () => !string.IsNullOrWhiteSpace(_settings.FileRoot));
        return _responses;
    }
}
