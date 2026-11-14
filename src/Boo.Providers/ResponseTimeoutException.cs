using Boo.Core;

namespace Boo.Providers;

public sealed class ResponseTimeoutException : TimeoutException
{
    private const string OpenAiMessage = "OpenAI response timed out. Try again.";

    public ResponseTimeoutException() : this(AiProvider.OpenAI)
    {
    }

    public ResponseTimeoutException(AiProvider provider) : base(ProviderFailureClassifier.ForVendor(OpenAiMessage, provider))
    {
        Provider = provider;
    }

    public AiProvider Provider { get; }
}
