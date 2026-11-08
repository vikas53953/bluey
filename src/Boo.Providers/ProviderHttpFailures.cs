using System.Text;
using Boo.Core;

namespace Boo.Providers;

/// <summary>Turns a failed provider HTTP exchange into a classified, body-free <see cref="ProviderException"/>.</summary>
internal static class ProviderHttpFailures
{
    public static ProviderException Transport(string operation, HttpRequestException exception) =>
        new(operation, ProviderFailureClassifier.FromTransport(exception.HttpRequestError));

    /// <summary>Provider-aware transport failure (Anthropic, Gemini); the OpenAI overload above is unchanged.</summary>
    public static ProviderException Transport(AiProvider provider, string operation, HttpRequestException exception) =>
        new(provider, operation, ProviderFailureClassifier.FromTransport(exception.HttpRequestError, provider));

    /// <summary>
    /// Reads at most <see cref="ProviderFailureClassifier.MaxErrorBodyChars"/> of the error body only to find the
    /// OpenAI error code (quota vs rate limit). Any read failure falls back to classifying by status alone.
    /// </summary>
    public static async Task<ProviderException> StatusAsync(string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return new ProviderException(operation, ProviderFailureClassifier.FromHttp((int)response.StatusCode, body));
    }

    /// <summary>
    /// Provider-aware status failure: the same bounded body read, classified with the vendor's error shape
    /// (Anthropic <c>error.type</c>, Gemini <c>error.status</c>). The body never reaches the exception message.
    /// </summary>
    public static async Task<ProviderException> StatusAsync(AiProvider provider, string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await ReadBoundedBodyAsync(response, cancellationToken).ConfigureAwait(false);
        return new ProviderException(provider, operation, ProviderFailureClassifier.FromHttp((int)response.StatusCode, body, provider));
    }

    private static async Task<string?> ReadBoundedBodyAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new char[ProviderFailureClassifier.MaxErrorBodyChars + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var read = await reader.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
                if (read == 0)
                    break;
                total += read;
            }
            return new string(buffer, 0, total);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
