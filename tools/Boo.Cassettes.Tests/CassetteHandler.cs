using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text.Json.Nodes;

namespace Boo.Cassettes;

/// <summary>
/// Replays one cassette as the HTTP transport under Boo's real OpenAI client (the client takes an
/// <see cref="HttpClient"/>, so this handler is the seam; no production code changes). Keeps the recorded latency
/// (headers delay and per-chunk delays) and injects the cassette's fault the way .NET's own socket handler reports
/// it. Records what the client sent so the test can check the request shape.
/// </summary>
public sealed class CassetteHandler : HttpMessageHandler
{
    private readonly Cassette _cassette;

    public CassetteHandler(Cassette cassette) => _cassette = cassette;

    public int Calls { get; private set; }
    public HttpMethod? SentMethod { get; private set; }
    public Uri? SentUri { get; private set; }
    public JsonObject? SentBody { get; private set; }
    public bool SentBearer { get; private set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++;
        SentMethod = request.Method;
        SentUri = request.RequestUri;
        SentBearer = request.Headers.Authorization?.Scheme == "Bearer";
        if (request.Content is not null)
            SentBody = JsonNode.Parse(await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)) as JsonObject;

        switch (_cassette.Fault?.Kind)
        {
            case "hang":
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                break;
            case "connect-refused":
                throw new HttpRequestException(HttpRequestError.ConnectionError, "No connection could be made because the target machine actively refused it. (api.openai.com:443)",
                    new SocketException((int)SocketError.ConnectionRefused));
            case "reset-before-headers":
                throw new HttpRequestException(HttpRequestError.ResponseEnded, "The response ended prematurely.",
                    new IOException("Unable to read data from the transport connection: An existing connection was forcibly closed by the remote host.",
                        new SocketException((int)SocketError.ConnectionReset)));
        }

        var reply = _cassette.Response ?? throw new InvalidDataException($"{_cassette.Name}: no response and no transport fault.");
        if (reply.HeadersDelayMs > 0)
            await Task.Delay(reply.HeadersDelayMs, cancellationToken).ConfigureAwait(false);

        var content = new ChunkedContent(reply.Chunks, _cassette.Fault?.Kind == "cut-mid-chunk" ? _cassette.Fault.AtChunk : -1);
        var response = new HttpResponseMessage((HttpStatusCode)reply.Status) { Content = content, RequestMessage = request };
        foreach (var (name, value) in reply.Headers)
        {
            if (string.Equals(name, "content-type", StringComparison.OrdinalIgnoreCase))
                content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
            else
                response.Headers.TryAddWithoutValidation(name, value);
        }
        return response;
    }

    /// <summary>A body that arrives chunk by chunk with the recorded delays, and can drop mid-chunk like a reset socket.</summary>
    private sealed class ChunkedContent : HttpContent
    {
        private readonly IReadOnlyList<CassetteChunk> _chunks;
        private readonly int _cutAt;

        public ChunkedContent(IReadOnlyList<CassetteChunk> chunks, int cutAt)
        {
            _chunks = chunks;
            _cutAt = cutAt;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            for (var i = 0; i < _chunks.Count; i++)
            {
                var chunk = _chunks[i];
                if (chunk.DelayMs > 0)
                    await Task.Delay(chunk.DelayMs, cancellationToken).ConfigureAwait(false);
                var bytes = chunk.Bytes();
                if (i == _cutAt)
                {
                    await stream.WriteAsync(bytes.AsMemory(0, bytes.Length / 2), cancellationToken).ConfigureAwait(false);
                    // What SocketsHttpHandler raises when the peer resets the connection part-way through a body.
                    throw new HttpIOException(HttpRequestError.ResponseEnded, "The response ended prematurely.",
                        new SocketException((int)SocketError.ConnectionReset));
                }
                await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
            }
        }

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
