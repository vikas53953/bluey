using Boo.Core;

namespace Boo.Core.Tests;

/// <summary>
/// Issue #111 (behavioural guards, clock-controlled): the Happy face after a focus session ends is held exactly to its
/// deadline by <see cref="FaceMomentSchedule"/>, on the caller's monotonic milliseconds. These pin the schedule maths
/// that stays unchanged when the app's face-moment timer moves from Background to Normal priority: never expired
/// early, reported once, overlapping moments independent, and a re-hold or Clear moves or cancels the old deadline.
/// No real time is involved: every instant is passed in.
/// </summary>
public sealed class FaceMomentDeadlineTests
{
    private static readonly TimeSpan HappyHold = TimeSpan.FromSeconds(2.5);

    [Fact]
    public void HappyIsNotExpiredOneMillisecondBeforeItsDeadline_AndIsExpiredExactlyAtIt()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Happy, nowMs: 10_000, HappyHold);

        Assert.Equal(TimeSpan.FromMilliseconds(2_500), schedule.NextDue(10_000));
        Assert.Equal(FaceMomentKind.None, schedule.Expire(12_499));
        Assert.Equal(TimeSpan.FromMilliseconds(1), schedule.NextDue(12_499));
        Assert.Equal(FaceMomentKind.Happy, schedule.Expire(12_500));
        Assert.Equal(FaceMomentKind.None, schedule.Expire(12_501));
        Assert.Null(schedule.NextDue(12_501));
    }

    [Fact]
    public void ALateTickExpiresOnlyTheMomentsThatAreDue_AndRearmsForTheRest()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Happy, 0, HappyHold);
        schedule.Hold(FaceMomentKind.Nudge, 0, TimeSpan.FromSeconds(6));
        schedule.Hold(FaceMomentKind.Responding, 0, FaceMoments.AfterReplyDone(TimeSpan.FromSeconds(3)));

        // A tick delivered 90 ms late (the #111 red sample) still expires only what is due by then.
        Assert.Equal(FaceMomentKind.Happy, schedule.Expire(2_590));
        Assert.Equal(TimeSpan.FromMilliseconds(3_300 - 2_590), schedule.NextDue(2_590));
        Assert.Equal(FaceMomentKind.Responding, schedule.Expire(3_300));
        Assert.Equal(FaceMomentKind.Nudge, schedule.Expire(6_000));
        Assert.Null(schedule.NextDue(6_000));
    }

    [Fact]
    public void AReHoldMovesTheHappyDeadline_AndAClearCancelsItBeforeItIsDue()
    {
        var schedule = new FaceMomentSchedule();
        schedule.Hold(FaceMomentKind.Happy, 0, HappyHold);
        schedule.Hold(FaceMomentKind.Happy, 1_000, HappyHold);

        Assert.Equal(FaceMomentKind.None, schedule.Expire(2_500));
        Assert.Equal(TimeSpan.FromMilliseconds(1_000), schedule.NextDue(2_500));
        schedule.Clear(FaceMomentKind.Happy);
        Assert.Null(schedule.NextDue(2_500));
        Assert.Equal(FaceMomentKind.None, schedule.Expire(9_000));
    }
}
