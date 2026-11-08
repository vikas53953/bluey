using System.Text;
using Boo.Core;

namespace Boo.Providers;

/// <summary>Turns a failed OpenAI HTTP exchange into a classified, body-free <see cref="ProviderException"/>.</summary>
internal static class ProviderHttpFailures
{
    public static ProviderException Transport(string operation, HttpRequestException exception) =>
        new(operation, ProviderFailureClassifier.FromTransport(exception.HttpRequestError));

    /// <summary>
    /// Reads at most <see cref="ProviderFailureClassifier.MaxErrorBodyChars"/> of the error body only to find the
    /// OpenAI error code (quota vs rate limit). Any read failure falls back to classifying by status alone.
    /// </summary>
    public static async Task<ProviderException> StatusAsync(string operation, HttpResponseMessage response, CancellationToken cancellationToken)
    {
        string? body = null;
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
            body = new string(buffer, 0, total);
        }
        catch (Exception)
        {
            body = null;
        }
        return new ProviderException(operation, ProviderFailureClassifier.FromHttp((int)response.StatusCode, body));
    }
}
