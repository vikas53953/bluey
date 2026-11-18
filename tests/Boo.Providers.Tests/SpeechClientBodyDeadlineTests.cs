using System.IO;
using System.Net;
using System.Net.Http;
using static Boo.Providers.Tests.ProviderTestKit;

namespace Boo.Providers.Tests;

/// <summary>
/// Review of PR #186 (Codex BLOCK on 3cf25ad, P2): HttpClient.Timeout ends when the response headers arrive, so a speech
/// response that sends its headers and then stalls, or drips bytes forever, must still end at Boo's deadline as an
/// "unavailable" result (InvalidOperationException, "timed out"), never hang the recovery. The caller's own cancel during
/// the body read stays a cancellation. A watchdog makes a client that never ends fail instead of hanging the run.
/// </summary>
public sealed class SpeechClientBodyDeadlineTests
{
    private static readonly Uri Origin = new("https://api.openai.com/");

    private static SpeechClient Client(Stream body, TimeSpan timeout) =>
        new(CreateHttpClient(new RecordingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(body) }), Origin, timeout),
            () => "fixture-key", "marin");

    [Fact]
    public async Task Headers_then_a_stalled_body_ends_at_the_deadline_as_unavailable()
    {
        var error = await CaptureFailureWithWatchdogAsync(token => Client(new HeldBody(), Deadline).SpeakAsync("Five", token));

        var timeout = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("timed out", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Headers_then_a_dripping_body_ends_at_the_deadline_as_unavailable()
    {
        var error = await CaptureFailureWithWatchdogAsync(token => Client(new DrippingBody(), Deadline).SpeakAsync("Five", token));

        var timeout = Assert.IsType<InvalidOperationException>(error);
        Assert.Contains("timed out", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_callers_cancel_during_the_body_read_stays_a_cancel()
    {
        using var cancel = new CancellationTokenSource(Deadline);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Client(new HeldBody(), Watchdog).SpeakAsync("Five", cancel.Token));
    }

    /// <summary>A body whose first read never returns until it is cancelled (headers already sent).</summary>
    private sealed class HeldBody : ReadOnlyBody
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }

    /// <summary>A body that sends two bytes of silence every 20 ms and never ends.</summary>
    private sealed class DrippingBody : ReadOnlyBody
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await Task.Delay(20, cancellationToken);
            var n = Math.Min(2, buffer.Length);
            buffer.Span[..n].Clear();
            return n;
        }
    }

    private abstract class ReadOnlyBody : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
