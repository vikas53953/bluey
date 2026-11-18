namespace Boo.Core;

/// <summary>The transient face flags that are held for a while and then cleared.</summary>
[Flags]
public enum FaceMomentKind
{
    None = 0,
    Responding = 1,
    Nudge = 2,
    Happy = 4,
}

/// <summary>
/// One expiry per transient face flag, so a Happy moment (focus session complete) and a Responding hold (Boo is
/// speaking) no longer share a single timer: a reply finishing used to clear Happy and Nudge with it, and a Happy
/// or Nudge moment arriving mid-reply used to cut the Responding hold short. Times are monotonic milliseconds.
/// </summary>
public sealed class FaceMomentSchedule
{
    private const long Unset = long.MinValue;
    private long _respondingUntil = Unset;
    private long _nudgeUntil = Unset;
    private long _happyUntil = Unset;

    /// <summary>Holds <paramref name="kinds"/> until <paramref name="nowMs"/> + <paramref name="duration"/>; the latest hold for a flag wins.</summary>
    public void Hold(FaceMomentKind kinds, long nowMs, TimeSpan duration)
    {
        var until = nowMs + Math.Max(0L, (long)duration.TotalMilliseconds);
        if (kinds.HasFlag(FaceMomentKind.Responding)) _respondingUntil = until;
        if (kinds.HasFlag(FaceMomentKind.Nudge)) _nudgeUntil = until;
        if (kinds.HasFlag(FaceMomentKind.Happy)) _happyUntil = until;
    }

    /// <summary>Forgets the holds for <paramref name="kinds"/> (the flag was switched off explicitly).</summary>
    public void Clear(FaceMomentKind kinds)
    {
        if (kinds.HasFlag(FaceMomentKind.Responding)) _respondingUntil = Unset;
        if (kinds.HasFlag(FaceMomentKind.Nudge)) _nudgeUntil = Unset;
        if (kinds.HasFlag(FaceMomentKind.Happy)) _happyUntil = Unset;
    }

    /// <summary>Returns the flags whose hold has run out by <paramref name="nowMs"/> and forgets them; the others keep their expiry.</summary>
    public FaceMomentKind Expire(long nowMs)
    {
        var expired = FaceMomentKind.None;
        if (_respondingUntil != Unset && _respondingUntil <= nowMs) { expired |= FaceMomentKind.Responding; _respondingUntil = Unset; }
        if (_nudgeUntil != Unset && _nudgeUntil <= nowMs) { expired |= FaceMomentKind.Nudge; _nudgeUntil = Unset; }
        if (_happyUntil != Unset && _happyUntil <= nowMs) { expired |= FaceMomentKind.Happy; _happyUntil = Unset; }
        return expired;
    }

    /// <summary>Time until the earliest remaining hold runs out (at least 1 ms), or null when nothing is held.</summary>
    public TimeSpan? NextDue(long nowMs)
    {
        var next = long.MaxValue;
        if (_respondingUntil != Unset) next = Math.Min(next, _respondingUntil);
        if (_nudgeUntil != Unset) next = Math.Min(next, _nudgeUntil);
        if (_happyUntil != Unset) next = Math.Min(next, _happyUntil);
        if (next == long.MaxValue) return null;
        return TimeSpan.FromMilliseconds(Math.Max(1L, next - nowMs));
    }
}
