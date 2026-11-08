using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FaceMomentsTests
{
    [Fact]
    public void Nothing_queued_uses_the_minimum_tail()
    {
        Assert.Equal(FaceMoments.ReplyDoneTail, FaceMoments.AfterReplyDone(TimeSpan.Zero));
        Assert.Equal(FaceMoments.ReplyDoneTail, FaceMoments.AfterReplyDone(TimeSpan.FromSeconds(-3)));
    }

    [Fact]
    public void Short_playback_still_gets_the_minimum_tail()
    {
        Assert.Equal(FaceMoments.ReplyDoneTail, FaceMoments.AfterReplyDone(TimeSpan.FromMilliseconds(200)));
    }

    [Fact]
    public void Long_playback_keeps_responding_until_speech_ends_plus_slack()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(6300), FaceMoments.AfterReplyDone(TimeSpan.FromSeconds(6)));
    }

    [Fact]
    public void Hold_is_capped()
    {
        Assert.Equal(FaceMoments.MaximumHold, FaceMoments.AfterReplyDone(TimeSpan.FromMinutes(10)));
    }
}
