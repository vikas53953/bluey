using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Boo.Core;
using Boo.Providers;

namespace Boo.Integration.Tests;

public sealed class ResponseDeadlineTests
{
    private const string TimeoutMessage = "OpenAI response timed out. Try again.";
    private const string InterruptedMessage = "OpenAI response was interrupted before completion. Try again.";
    private const string Sentinel = "provider-secret-sentinel";
    private static readonly TimeSpan Deadline = TimeSpan.FromMilliseconds(125);
    private static readonly TimeSpan Watchdog = TimeSpan.FromSeconds(3);
    private static readonly ProviderSettings Settings = new(
        ReasoningModel: "gpt-6.1-sol",
        RealtimeModel: "gpt-realtime-2.1",
        FocusModel: "gpt-6-luna",
        Voice: "marin");

    [Fact]
    public async Task Finite_timeout_covers_a_stalled_send_and_normalizes_it()
    {
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new InMemoryHandler(async (_, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("The stalled send unexpectedly completed.");
        });
        using var http = CreateHttpClient(handler, Deadline);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(
            cancellationToken => client.RespondAsync(CreateRequest(), cancellationToken));

        var timeout = Assert.IsType<ResponseTimeoutException>(error);
        Assert.Equal(TimeoutMessage, timeout.Message);
        Assert.True(cancellationObserved.Task.IsCompletedSuccessfully);
        AssertSafeMessage(timeout);
    }

    [Fact]
    public async Task Finite_timeout_covers_headers_followed_by_a_stalled_body()
    {
        var content = CancellableContent.Stalled();
        var handler = new InMemoryHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        }));
        using var http = CreateHttpClient(handler, Deadline);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(async watchdogToken =>
        {
            using var drain = watchdogToken.Register(content.Cancel);
            await client.RespondAsync(CreateRequest(), watchdogToken);
        });

        var timeout = Assert.IsType<ResponseTimeoutException>(error);
        Assert.Equal(TimeoutMessage, timeout.Message);
        Assert.True(content.CancellationObserved);
        AssertSafeMessage(timeout);
    }

    [Fact]
    public async Task Direct_timeout_during_body_read_normalizes_to_the_fixed_public_message()
    {
        var handler = BodyFailureHandler(new TimeoutException(Sentinel));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(
            cancellationToken => client.RespondAsync(CreateRequest(), cancellationToken));

        var timeout = Assert.IsType<ResponseTimeoutException>(error);
        Assert.Equal(TimeoutMessage, timeout.Message);
        AssertSafeMessage(timeout);
    }

    [Fact]
    public async Task Inner_timeout_during_body_read_normalizes_to_the_fixed_public_message()
    {
        var handler = BodyFailureHandler(new HttpRequestException(
            $"body transport {Sentinel}",
            new TimeoutException(Sentinel)));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(
            cancellationToken => client.RespondAsync(CreateRequest(), cancellationToken));

        var timeout = Assert.IsType<ResponseTimeoutException>(error);
        Assert.Equal(TimeoutMessage, timeout.Message);
        AssertSafeMessage(timeout);
    }

    [Fact]
    public async Task Caller_cancellation_before_send_remains_operation_canceled()
    {
        using var callerCancellation = new CancellationTokenSource();
        callerCancellation.Cancel();
        var handler = new InMemoryHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new InvalidOperationException("The handler must not complete a pre-canceled request.")));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(async watchdogToken =>
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation.Token, watchdogToken);
            await client.RespondAsync(CreateRequest(), combined.Token);
        });

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.DoesNotContain(TimeoutMessage, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(InterruptedMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_during_send_remains_operation_canceled()
    {
        using var callerCancellation = new CancellationTokenSource();
        var sendStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationObserved = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new InMemoryHandler(async (_, cancellationToken) =>
        {
            sendStarted.TrySetResult(true);
            callerCancellation.Cancel();
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                cancellationObserved.TrySetResult(true);
                throw;
            }

            throw new InvalidOperationException("The stalled send unexpectedly completed.");
        });
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(async watchdogToken =>
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation.Token, watchdogToken);
            await client.RespondAsync(CreateRequest(), combined.Token);
        });

        Assert.True(sendStarted.Task.IsCompletedSuccessfully);
        Assert.True(cancellationObserved.Task.IsCompletedSuccessfully);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.DoesNotContain(TimeoutMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_during_body_remains_operation_canceled()
    {
        using var callerCancellation = new CancellationTokenSource();
        var content = CancellableContent.Stalled(() => callerCancellation.Cancel());
        var handler = new InMemoryHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = content
        }));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(async watchdogToken =>
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation.Token, watchdogToken);
            await client.RespondAsync(CreateRequest(), combined.Token);
        });

        Assert.True(content.CancellationObserved);
        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.DoesNotContain(TimeoutMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Direct_timeout_exception_normalizes_to_the_fixed_public_message()
    {
        var handler = new InMemoryHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new TimeoutException(Sentinel)));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(
            cancellationToken => client.RespondAsync(CreateRequest(), cancellationToken));

        var timeout = Assert.IsType<ResponseTimeoutException>(error);
        Assert.Equal(TimeoutMessage, timeout.Message);
        AssertSafeMessage(timeout);
    }

    [Fact]
    public async Task Inner_timeout_exception_normalizes_to_the_fixed_public_message()
    {
        var handler = new InMemoryHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new HttpRequestException($"transport {Sentinel}", new TimeoutException(Sentinel))));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(
            cancellationToken => client.RespondAsync(CreateRequest(), cancellationToken));

        var timeout = Assert.IsType<ResponseTimeoutException>(error);
        Assert.Equal(TimeoutMessage, timeout.Message);
        AssertSafeMessage(timeout);
    }

    [Fact]
    public async Task Noncaller_cancellation_without_timeout_becomes_a_fixed_interruption()
    {
        var handler = new InMemoryHandler((_, _) => Task.FromException<HttpResponseMessage>(
            new OperationCanceledException(Sentinel, CancellationToken.None)));
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(
            cancellationToken => client.RespondAsync(CreateRequest(), cancellationToken));

        var interrupted = Assert.IsType<InvalidOperationException>(error);
        Assert.Equal(InterruptedMessage, interrupted.Message);
        AssertSafeMessage(interrupted);
    }

    [Fact]
    public async Task Infinite_timeout_still_honors_caller_cancellation()
    {
        using var callerCancellation = new CancellationTokenSource();
        var handler = new InMemoryHandler(async (_, cancellationToken) =>
        {
            callerCancellation.Cancel();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The stalled send unexpectedly completed.");
        });
        using var http = CreateHttpClient(handler, Timeout.InfiniteTimeSpan);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(async watchdogToken =>
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation.Token, watchdogToken);
            await client.RespondAsync(CreateRequest(), combined.Token);
        });

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.DoesNotContain(TimeoutMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Caller_cancellation_wins_when_deadline_is_also_canceled()
    {
        using var callerCancellation = new CancellationTokenSource();
        var handler = new InMemoryHandler(async (_, cancellationToken) =>
        {
            await Task.Delay(TimeSpan.FromMilliseconds(100));
            callerCancellation.Cancel();
            await Task.Delay(TimeSpan.FromMilliseconds(125));
            throw new OperationCanceledException(Sentinel, cancellationToken);
        });
        using var http = CreateHttpClient(handler, TimeSpan.FromMilliseconds(150));
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var error = await CaptureFailureWithWatchdogAsync(async watchdogToken =>
        {
            using var combined = CancellationTokenSource.CreateLinkedTokenSource(callerCancellation.Token, watchdogToken);
            await client.RespondAsync(CreateRequest(), combined.Token);
        });

        Assert.IsAssignableFrom<OperationCanceledException>(error);
        Assert.DoesNotContain(TimeoutMessage, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Successful_response_decodes_with_the_selected_model_and_preserves_http_timeout()
    {
        var configuredTimeout = TimeSpan.FromSeconds(12);
        var handler = new InMemoryHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"output\":[{\"content\":[{\"type\":\"output_text\",\"text\":\"Fixture answer.\"}]}]}",
                Encoding.UTF8,
                "application/json")
        }));
        using var http = CreateHttpClient(handler, configuredTimeout);
        var client = new ResponsesClient(http, () => "fixture-key", Settings);

        var reply = await client.RespondAsync(CreateRequest(), CancellationToken.None);

        Assert.Equal("Fixture answer.", reply.Text);
        Assert.Equal(configuredTimeout, http.Timeout);
        using var requestBody = JsonDocument.Parse(handler.LastBody!);
        Assert.Equal(Settings.ReasoningModel, requestBody.RootElement.GetProperty("model").GetString());
    }

    private static AssistantRequest CreateRequest() => new("Explain the selected control.", 7);

    private static HttpClient CreateHttpClient(HttpMessageHandler handler, TimeSpan timeout) => new(handler)
    {
        BaseAddress = new Uri("https://api.openai.com/"),
        Timeout = timeout
    };

    private static InMemoryHandler BodyFailureHandler(Exception error) =>
        new((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new BodyFailureContent(error)
        }));

    private static void AssertSafeMessage(Exception error)
    {
        Assert.DoesNotContain(Sentinel, error.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(Sentinel, error.ToString(), StringComparison.Ordinal);
    }

    private static async Task<Exception> CaptureFailureWithWatchdogAsync(
        Func<CancellationToken, Task> operation,
        TimeSpan? watchdog = null)
    {
        using var watchdogCancellation = new CancellationTokenSource();
        var pending = operation(watchdogCancellation.Token);
        try
        {
            var finished = await Task.WhenAny(pending, Task.Delay(watchdog ?? Watchdog));
            if (!ReferenceEquals(finished, pending))
                throw new Xunit.Sdk.XunitException("The cooperative response operation exceeded its outer watchdog.");

            try
            {
                await pending;
            }
            catch (Exception error)
            {
                return error;
            }

            throw new Xunit.Sdk.XunitException("The response operation completed when a failure was expected.");
        }
        finally
        {
            watchdogCancellation.Cancel();
            try
            {
                await pending;
            }
            catch (OperationCanceledException)
            {
                // Expected while draining a cooperative send or body read.
            }
            catch (Exception)
            {
                // The already-captured failure is intentionally discarded during cleanup.
            }
        }
    }

    private sealed class InMemoryHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _responder;

        public InMemoryHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> responder)
        {
            _responder = responder;
        }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return await _responder(request, cancellationToken);
        }
    }

    private sealed class CancellableContent : HttpContent
    {
        private readonly Action? _onStarted;
        private readonly bool _stalled;
        private readonly CancellationTokenSource _externalCancellation = new();

        private CancellableContent(bool stalled, Action? onStarted)
        {
            _stalled = stalled;
            _onStarted = onStarted;
        }

        public bool CancellationObserved { get; private set; }

        public static CancellableContent Stalled(Action? onStarted = null) => new(true, onStarted);

        public void Cancel() => _externalCancellation.Cancel();

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            SerializeToStreamAsync(stream, context, CancellationToken.None);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new CancellableReadStream(this));

        protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
            Task.FromResult<Stream>(new CancellableReadStream(this));

        protected override async Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken)
        {
            _onStarted?.Invoke();
            if (_stalled)
            {
                using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken,
                    _externalCancellation.Token);
                try
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, combinedCancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    CancellationObserved = true;
                    throw;
                }
            }

            var bytes = Encoding.UTF8.GetBytes("unused");
            await stream.WriteAsync(bytes, cancellationToken);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _externalCancellation.Cancel();
            base.Dispose(disposing);
        }

        protected override bool TryComputeLength(out long length)
        {
            length = _stalled ? 0 : 6;
            return !_stalled;
        }

        private sealed class CancellableReadStream : Stream
        {
            private readonly CancellableContent _owner;
            private bool _returnedData;

            public CancellableReadStream(CancellableContent owner)
            {
                _owner = owner;
            }

            public override bool CanRead => true;
            public override bool CanSeek => false;
            public override bool CanWrite => false;
            public override long Length => 0;
            public override long Position { get; set; }

            public override async ValueTask<int> ReadAsync(
                Memory<byte> buffer,
                CancellationToken cancellationToken = default)
            {
                _owner._onStarted?.Invoke();
                if (_owner._stalled)
                {
                    using var combinedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
                        cancellationToken,
                        _owner._externalCancellation.Token);
                    try
                    {
                        await Task.Delay(Timeout.InfiniteTimeSpan, combinedCancellation.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _owner.CancellationObserved = true;
                        throw;
                    }
                }

                if (_returnedData)
                    return 0;
                _returnedData = true;
                var bytes = Encoding.UTF8.GetBytes("unused");
                bytes.AsSpan().CopyTo(buffer.Span);
                return bytes.Length;
            }

            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
            public override void Flush() => throw new NotSupportedException();
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
            public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    private sealed class BodyFailureContent : HttpContent
    {
        private readonly Exception _error;

        public BodyFailureContent(Exception error)
        {
            _error = error;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
            Task.FromException(_error);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            Task.FromException(_error);

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }
}
