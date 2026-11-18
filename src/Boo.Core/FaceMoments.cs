namespace Boo.Core;

/// <summary>
/// How long the notch keeps a transient face (Responding) after the reply's last event, so the face does not
/// drop back to idle while queued speech is still playing.
/// </summary>
public static class FaceMoments
{
    /// <summary>Minimum Responding tail after response.done.</summary>
    public static readonly TimeSpan ReplyDoneTail = TimeSpan.FromSeconds(1.5);

    /// <summary>Extra time after the queued playback ends.</summary>
    public static readonly TimeSpan PlaybackSlack = TimeSpan.FromMilliseconds(300);

    /// <summary>Longest Responding hold, so a bad playback estimate cannot pin the face.</summary>
    public static readonly TimeSpan MaximumHold = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Responding hold after response.done: the remaining playback plus slack, never shorter than
    /// <see cref="ReplyDoneTail"/> nor longer than <see cref="MaximumHold"/>. Negative remaining = nothing queued.
    /// </summary>
    public static TimeSpan AfterReplyDone(TimeSpan remainingPlayback)
    {
        if (remainingPlayback <= TimeSpan.Zero)
        {
            return ReplyDoneTail;
        }
        var hold = remainingPlayback + PlaybackSlack;
        if (hold < ReplyDoneTail) return ReplyDoneTail;
        return hold > MaximumHold ? MaximumHold : hold;
    }
}
