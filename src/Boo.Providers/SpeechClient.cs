using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Boo.Core;

namespace Boo.Providers;

/// <summary>
/// Reads a short text aloud with OpenAI's text-to-speech (POST /v1/audio/speech), returning raw 24 kHz 16-bit mono PCM:
/// the same format the realtime voice sends, so Boo's player plays it unchanged. Used only to recover a spoken reply
/// whose audio came back silent while its transcript had words (issue #177; <see cref="SilentReplyRecovery"/>).
/// One request per call, never retried. The key is sent only in the Authorization header and never logged.
/// </summary>
public sealed class SpeechClient
{
    /// <summary>OpenAI's cheapest text-to-speech model.</summary>
    public const string DefaultModel = "gpt-4o-mini-tts";

    /// <summary>The most audio accepted back (about two minutes of 24 kHz 16-bit mono).</summary>
    public const int MaxAudioBytes = 120 * 48000;

    private readonly HttpClient _http;
    private readonly Func<string?> _credential;
    private readonly string _voice;
    private readonly string _model;

    public SpeechClient(HttpClient http, Func<string?> credential, string voice, string? model = null)
    {
        _http = http ?? throw new ArgumentNullException(nameof(http));
        _credential = credential ?? throw new ArgumentNullException(nameof(credential));
        _voice = string.IsNullOrWhiteSpace(voice) ? throw new ArgumentException("A voice is required.", nameof(voice)) : voice;
        _model = string.IsNullOrWhiteSpace(model) ? DefaultModel : model;
    }

    /// <summary>The exact request body sent for <paramref name="text"/> (model, input, voice, raw PCM).</summary>
    public string RequestBody(string text) => JsonSerializer.Serialize(new
    {
        model = _model,
        input = text,
        voice = _voice,
        response_format = "pcm"
    });

    public async Task<byte[]> SpeakAsync(string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ArgumentException("There is no text to read aloud.", nameof(text));
        if (text.Length > SilentReplyRecovery.MaxRecoveryCharacters)
            throw new ArgumentOutOfRangeException(nameof(text), "The text is longer than a spoken reply recovery allows.");
        var baseUri = _http.BaseAddress ?? ProviderEndpoint.Origin;
        if (!ProviderEndpoint.IsOfficial(baseUri))
            throw new InvalidOperationException("OpenAI requests require the official HTTPS API origin.");
        var key = _credential();
        if (string.IsNullOrWhiteSpace(key))
            throw new InvalidOperationException("OpenAI credential is not configured.");

        using var message = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, "/v1/audio/speech"))
        {
            Content = new StringContent(RequestBody(text), Encoding.UTF8, "application/json")
        };
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key.Trim());

        HttpResponseMessage response;
        try
        {
            response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw ProviderHttpFailures.Transport("speech", exception);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw await ProviderHttpFailures.StatusAsync("speech", response, cancellationToken).ConfigureAwait(false);
            }
            var audio = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
            if (audio.Length == 0 || audio.Length > MaxAudioBytes)
                throw new InvalidOperationException("OpenAI speech returned no usable audio.");
            return audio.Length % 2 == 0 ? audio : audio[..^1];
        }
    }
}
