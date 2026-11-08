using Boo.Core;

namespace Boo.Providers;

/// <summary>Placeholder: implemented in this branch by the provider build lane.</summary>
public sealed class GeminiClient : IScreenAssistant, IFocusClassifier
{
    private readonly HttpClient _http;
    private readonly Func<string?> _credential;
    private readonly ProviderSettings _settings;

    public GeminiClient(HttpClient http, Func<string?> credential, ProviderSettings settings)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
    }

    public Task<AssistantReply> RespondAsync(AssistantRequest request, CancellationToken cancellationToken, ControlObservation? controls = null) =>
        throw new NotImplementedException(_http.ToString() + _credential + _settings);

    public Task<FocusNudge> NudgeAsync(CapturedFrame frame, string task, CancellationToken cancellationToken) =>
        throw new NotImplementedException();

    public Task<FocusDecision> ClassifyAsync(CapturedFrame frame, FocusSnapshot session, CancellationToken cancellationToken) =>
        throw new NotImplementedException();
}
