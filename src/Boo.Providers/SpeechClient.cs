using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Boo.Core;

namespace Boo.Providers;

/// <summary>
/// Reads a short text aloud with OpenAI's text-to-speech (POST /v1/audio/speech), returning raw 24 kHz 16-bit mono PCM:
/// the same format the realtime voice sends, so Boo's player plays it unchanged. Used only to recover a spoken reply
/// whose audio came back silent while its transcript had words (issue #177; <see cref="SilentReplyRecovery"/>).
/// One request per call, never retried. One deadline (the HttpClient's Timeout, or <see cref="DefaultDeadline"/> when it
/// is infinite) covers the whole request, headers and body: HttpClient's own Timeout ends when the headers arrive, so a
/// body that stalls or drips after them would otherwise hang the recovery (PR #186 review). The body is read as a stream and refused past <see cref="MaxAudioBytes"/>, so
/// the limit bounds memory too. Only a cancellation the caller asked for is reported as cancellation; a timeout, a
/// transport or body-read error, an error status or unusable audio is an <see cref="InvalidOperationException"/>
/// (a <see cref="ProviderException"/> where classified). The key is sent only in the Authorization header and never
/// logged; nothing here logs the text or the response body.
/// </summary>
public sealed class SpeechClient
{
    /// <summary>OpenAI's cheapest text-to-speech model.</summary>
    public const string DefaultModel = "gpt-4o-mini-tts";

    /// <summary>The most audio accepted back (about two minutes of 24 kHz 16-bit mono).</summary>
    public const int MaxAudioBytes = 120 * 48000;

    /// <summary>The whole-request deadline used when the HttpClient has no finite Timeout.</summary>
    public static readonly TimeSpan DefaultDeadline = TimeSpan.FromSeconds(45);

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

        using var request = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        request.CancelAfter(_http.Timeout > TimeSpan.Zero && _http.Timeout != Timeout.InfiniteTimeSpan ? _http.Timeout : DefaultDeadline);
        try
        {
            using var response = await _http.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, request.Token).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                throw await ProviderHttpFailures.StatusAsync("speech", response, request.Token).ConfigureAwait(false);
            }
            await using var body = await response.Content.ReadAsStreamAsync(request.Token).ConfigureAwait(false);
            return await ReadBoundedPcmAsync(body, request.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("OpenAI speech timed out.", exception);
        }
        catch (HttpRequestException exception)
        {
            throw ProviderHttpFailures.Transport("speech", exception);
        }
        catch (IOException exception)
        {
            throw new InvalidOperationException("OpenAI speech returned an unreadable result.", exception);
        }
    }

    /// <summary>Reads raw PCM up to <see cref="MaxAudioBytes"/>; more is refused, nothing is refused as empty audio.</summary>
    internal static async Task<byte[]> ReadBoundedPcmAsync(Stream body, CancellationToken cancellationToken)
    {
        using var audio = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int read;
        while ((read = await body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (audio.Length + read > MaxAudioBytes)
                throw new InvalidOperationException("OpenAI speech returned more audio than a recovery allows.");
            audio.Write(buffer, 0, read);
        }
        if (audio.Length < 2)
            throw new InvalidOperationException("OpenAI speech returned no usable audio.");
        var pcm = audio.ToArray();
        return pcm.Length % 2 == 0 ? pcm : pcm[..^1];
    }
}
