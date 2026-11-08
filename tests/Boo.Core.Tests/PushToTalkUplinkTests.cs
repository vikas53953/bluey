using Boo.Core;

namespace Boo.Core.Tests;

public sealed class PushToTalkUplinkTests
{
    private static byte[] Frame(byte value) => new byte[] { value, value };

    [Fact]
    public async Task Frames_before_attach_are_sent_in_order_then_complete_returns_bytes()
    {
        var uplink = new PushToTalkUplink();
        var sent = new List<byte>();
        Assert.True(uplink.Append(Frame(1)));
        Assert.True(uplink.Append(Frame(2)));
        uplink.Attach((pcm, _) => { lock (sent) sent.Add(pcm[0]); return Task.CompletedTask; });
        Assert.True(uplink.Append(Frame(3)));
        var bytes = await uplink.CompleteAsync(CancellationToken.None);
        Assert.Equal(new byte[] { 1, 2, 3 }, sent.ToArray());
        Assert.Equal(6, bytes);
        Assert.False(uplink.Append(Frame(4)));
    }

    [Fact]
    public async Task Complete_before_attach_waits_for_transport()
    {
        var uplink = new PushToTalkUplink();
        uplink.Append(Frame(1));
        var completion = uplink.CompleteAsync(CancellationToken.None);
        await Task.Delay(20);
        Assert.False(completion.IsCompleted);
        uplink.Attach((_, _) => Task.CompletedTask);
        Assert.Equal(2, await completion);
    }

    [Fact]
    public async Task Discard_drops_unsent_frames_and_never_cancels_a_send()
    {
        var uplink = new PushToTalkUplink();
        var gate = new TaskCompletionSource();
        var tokens = new List<CancellationToken>();
        var count = 0;
        uplink.Append(Frame(1));
        uplink.Append(Frame(2));
        uplink.Attach(async (_, token) => { tokens.Add(token); Interlocked.Increment(ref count); await gate.Task; });
        await Task.Delay(20);
        var discard = uplink.Discard();
        gate.SetResult();
        await discard;
        Assert.InRange(count, 0, 1);
        Assert.All(tokens, t => Assert.False(t.CanBeCanceled));
        Assert.Equal(0, await uplink.CompleteAsync(CancellationToken.None));
    }

    [Fact]
    public void Buffer_cap_rejects_frames_beyond_limit()
    {
        var uplink = new PushToTalkUplink(maxBufferedBytes: 4);
        Assert.True(uplink.Append(Frame(1)));
        Assert.True(uplink.Append(Frame(2)));
        Assert.False(uplink.Append(Frame(3)));
    }

    [Fact]
    public async Task Discard_before_attach_releases_a_pending_complete()
    {
        var uplink = new PushToTalkUplink();
        uplink.Append(Frame(1));
        var completion = uplink.CompleteAsync(CancellationToken.None);
        await uplink.Discard();
        Assert.Equal(0, await completion.WaitAsync(TimeSpan.FromSeconds(5)));
        var called = false;
        uplink.Attach((_, _) => { called = true; return Task.CompletedTask; });
        Assert.False(called);
    }
}
