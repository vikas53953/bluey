using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Review follow-up: the app held Responding, Nudge and Happy on one shared timer, so a focus session that
/// completed while Boo was speaking never showed the happy face, and a Happy/Nudge moment cut a reply's hold short.
/// </summary>
public sealed class FaceMomentScheduleTests
{
    [Fact]
    public void HappyArrivingMidReplyOutlivesTheRespondingHold()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Responding, nowMs: 0, TimeSpan.FromSeconds(4));
        schedule.Hold(FaceMomentKind.Happy, nowMs: 3_000, TimeSpan.FromSeconds(2.5));

        Assert.Equal(TimeSpan.FromSeconds(1), schedule.NextDue(3_000));
        Assert.Equal(FaceMomentKind.Responding, schedule.Expire(4_000));
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), schedule.NextDue(4_000));
        Assert.Equal(FaceMomentKind.Happy, schedule.Expire(5_500));
        Assert.Null(schedule.NextDue(5_500));
    }

    [Fact]
    public void ReplyFinishingDoesNotShortenANudgeOrHappyMoment()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Nudge, 0, TimeSpan.FromSeconds(6));
        schedule.Hold(FaceMomentKind.Responding, 1_000, TimeSpan.FromSeconds(1.5));

        Assert.Equal(FaceMomentKind.None, schedule.Expire(2_000));
        Assert.Equal(FaceMomentKind.Responding, schedule.Expire(2_500));
        Assert.Equal(FaceMomentKind.None, schedule.Expire(5_999));
        Assert.Equal(FaceMomentKind.Nudge, schedule.Expire(6_000));
    }

    [Fact]
    public void LatestHoldForAFlagWinsSoAudioDeltasKeepExtendingResponding()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Responding, 0, TimeSpan.FromSeconds(4));
        schedule.Hold(FaceMomentKind.Responding, 3_000, TimeSpan.FromSeconds(4));

        Assert.Equal(FaceMomentKind.None, schedule.Expire(4_000));
        Assert.Equal(TimeSpan.FromSeconds(3), schedule.NextDue(4_000));
        Assert.Equal(FaceMomentKind.Responding, schedule.Expire(7_000));
    }

    [Fact]
    public void ClearForgetsAHoldAndExpiredFlagsAreReportedOnce()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Responding | FaceMomentKind.Happy, 0, TimeSpan.FromSeconds(2));
        schedule.Clear(FaceMomentKind.Responding);

        Assert.Equal(FaceMomentKind.Happy, schedule.Expire(2_000));
        Assert.Equal(FaceMomentKind.None, schedule.Expire(9_000));
        Assert.Null(schedule.NextDue(9_000));
    }

    [Fact]
    public void NextDueIsNeverZeroSoATimerCanAlwaysBeArmed()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Happy, 0, TimeSpan.Zero);
        Assert.Equal(TimeSpan.FromMilliseconds(1), schedule.NextDue(0));
        Assert.Equal(FaceMomentKind.Happy, schedule.Expire(0));
    }
}
