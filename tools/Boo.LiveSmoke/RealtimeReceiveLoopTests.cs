using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Boo.Core;
using Boo.Providers;

namespace Boo.LiveSmoke;

/// <summary>
/// Offline check of Boo's real realtime receive loop (no key, no network beyond the local machine): a scripted server
/// on a localhost socket pair sends messages the parser keeps and messages it drops. The raw-message observer that the
/// live voice test relies on must see EVERY message, once and in order, including the dropped ones (Codex mutation M1
/// on PR #118 limited it to parsed messages and survived), while normal events stay filtered.
/// </summary>
public sealed class RealtimeReceiveLoopTests
{
    [Fact]
    public async Task Every_server_message_reaches_the_raw_observer_once_and_in_order_even_ones_the_parser_drops()
    {
        var messages = new[]
        {
            "{\"type\":\"session.updated\",\"session\":{}}",
            "{\"type\":\"response.audio.delta\",\"delta\":\"AQI=\"}",
            "this is not json",
            "{\"type\":\"response.output_audio.delta\",\"item_id\":\"i\",\"content_index\":0,\"delta\":\"AQ\"}",
            "{\"type\":\"response.output_audio.delta\",\"item_id\":\"i\",\"content_index\":-1,\"delta\":\"AQI=\"}",
            "{\"type\":\"response.output_audio.delta\",\"item_id\":\"i\",\"content_index\":0,\"delta\":\"AQI=\"}",
        };
        Assert.Null(RealtimeProtocol.Parse(messages[1]));
        Assert.Null(RealtimeProtocol.Parse(messages[2]));
        Assert.Null(RealtimeProtocol.Parse(messages[3]));
        Assert.Null(RealtimeProtocol.Parse(messages[4]));

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var clientTcp = new TcpClient();
        var accept = listener.AcceptTcpClientAsync();
        await clientTcp.ConnectAsync(IPAddress.Loopback, port);
        using var serverTcp = await accept;
        using var server = WebSocket.CreateFromStream(serverTcp.GetStream(), isServer: true, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);
        using var socket = WebSocket.CreateFromStream(clientTcp.GetStream(), isServer: false, subProtocol: null, keepAliveInterval: Timeout.InfiniteTimeSpan);

        await using var client = new RealtimeClient(() => "not-used-offline", new ProviderSettings(TextProvider: AiProvider.OpenAI));
        var raw = new List<string>();
        var received = new List<RealtimeEvent>();
        client.RawFrameObserver = raw.Add;
        client.Received += received.Add;

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var loop = client.ReceiveForTestAsync(socket, timeout.Token);
        foreach (var message in messages)
            await server.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(message)), WebSocketMessageType.Text, true, timeout.Token);
        await server.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        await loop.WaitAsync(timeout.Token);

        Assert.Equal(messages, raw);
        Assert.Equal(new[] { "session.updated", "response.output_audio.delta", "disconnect" }, received.Select(e => e.Kind).ToArray());
        Assert.Equal(new byte[] { 1, 2 }, received[1].Audio);
    }
}
