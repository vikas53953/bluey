using System.IO;
using System.Net;
using System.Net.Http;
using Boo.Core;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Issue #177, review of PR #186 (P2): the recovery speech request is bounded and its failures are classified. The body
/// is read as a stream and refused past MaxAudioBytes; a body-read error, a timeout, an error status and empty audio all
/// become InvalidOperationException (so the app says "unavailable"); only the caller's own cancellation is reported as a
/// cancellation. The request carries the cheapest model, Boo's voice and raw PCM, and goes only to the official origin.
/// </summary>
public sealed class SpeechClientTests
{
    private static readonly Uri Origin = new("https://api.openai.com/");

    private static HttpResponseMessage Pcm(HttpContent content) => new(HttpStatusCode.OK) { Content = content };

    private static SpeechClient Client(RecordingHandler handler, TimeSpan? timeout = null) =>
        new(CreateHttpClient(handler, Origin, timeout), () => "fixture-key", "marin");

    [Fact]
    public async Task The_request_asks_the_cheapest_model_for_raw_pcm_in_Boos_voice()
    {
        var handler = new RecordingHandler(_ => Pcm(new ByteArrayContent(new byte[] { 1, 0, 2, 0, 3 })));

        var audio = await Client(handler).SpeakAsync("The answer is five.", CancellationToken.None);

        Assert.Equal(new byte[] { 1, 0, 2, 0 }, audio);   // an odd trailing byte is dropped
        Assert.Equal("https://api.openai.com/v1/audio/speech", handler.LastRequest!.RequestUri!.ToString());
        var body = handler.LastBodyJson();
        Assert.Equal(SpeechClient.DefaultModel, body.GetProperty("model").GetString());
        Assert.Equal("gpt-4o-mini-tts", body.GetProperty("model").GetString());
        Assert.Equal("marin", body.GetProperty("voice").GetString());
        Assert.Equal("pcm", body.GetProperty("response_format").GetString());
        Assert.Equal("The answer is five.", body.GetProperty("input").GetString());
    }

    [Fact]
    public async Task More_audio_than_the_limit_is_refused_while_reading()
    {
        var handler = new RecordingHandler(_ => Pcm(new StreamContent(new EndlessZeros(SpeechClient.MaxAudioBytes + 2))));

        var error = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Client(handler).SpeakAsync("Five", CancellationToken.None));

        Assert.Contains("more audio", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_body_that_breaks_while_reading_is_an_unavailable_result()
    {
        var handler = new RecordingHandler(_ => Pcm(new StreamContent(new BreaksAfter(4096))));

        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Client(handler).SpeakAsync("Five", CancellationToken.None));
    }

    [Fact]
    public async Task Empty_audio_and_an_error_status_are_unavailable_results()
    {
        var empty = new RecordingHandler(_ => Pcm(new ByteArrayContent(Array.Empty<byte>())));
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => Client(empty).SpeakAsync("Five", CancellationToken.None));

        var refused = new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = new StringContent("{}") });
        await Assert.ThrowsAsync<ProviderException>(() => Client(refused).SpeakAsync("Five", CancellationToken.None));
    }

    [Fact]
    public async Task A_timeout_is_an_unavailable_result_but_the_callers_cancel_stays_a_cancel()
    {
        var timedOut = Client(RecordingHandler.Stalled(), Deadline);
        var timeout = await Assert.ThrowsAnyAsync<InvalidOperationException>(() => timedOut.SpeakAsync("Five", CancellationToken.None));
        Assert.Contains("timed out", timeout.Message, StringComparison.Ordinal);

        using var cancel = new CancellationTokenSource(Deadline);
        var stalled = Client(RecordingHandler.Stalled(), Watchdog);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => stalled.SpeakAsync("Five", cancel.Token));
    }

    [Fact]
    public async Task Only_the_official_origin_and_bounded_text_are_accepted()
    {
        var handler = new RecordingHandler(_ => Pcm(new ByteArrayContent(new byte[] { 1, 0 })));
        var elsewhere = new SpeechClient(CreateHttpClient(handler, new Uri("https://example.com/")), () => "k", "marin");
        await Assert.ThrowsAsync<InvalidOperationException>(() => elsewhere.SpeakAsync("Five", CancellationToken.None));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            Client(handler).SpeakAsync(new string('a', SilentReplyRecovery.MaxRecoveryCharacters + 1), CancellationToken.None));
        Assert.Empty(handler.Requests);
    }

    /// <summary>A read-only stream of zeros of a given length, produced on demand (nothing large is allocated up front).</summary>
    private sealed class EndlessZeros(long length) : Stream
    {
        private long _left = length;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>A stream that returns some bytes, then fails as a dropped connection does.</summary>
    private sealed class BreaksAfter(int bytes) : Stream
    {
        private int _left = bytes;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_left == 0) throw new IOException("The connection was dropped.");
            var n = Math.Min(count, _left);
            Array.Clear(buffer, offset, n);
            _left -= n;
            return n;
        }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
