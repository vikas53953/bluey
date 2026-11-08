namespace Boo.Core;

/// <summary>
/// Decides whether the notch needs a per-frame render callback at all. Boo sits at the top of the
/// screen all day, so the loop must be off whenever nothing can move: the reveal timeline is in its
/// hidden phase, the face is Hidden and any width/height tween has finished. Everything else (a reveal
/// or retract in flight, a visible face that blinks and follows the pointer, a size tween) needs frames.
/// </summary>
public static class NotchRenderPolicy
{
    /// <summary>True when the window must keep (or start) its CompositionTarget.Rendering subscription.</summary>
    public static bool NeedsFrames(RevealPhase phase, NotchFaceState state, bool sizeSettled)
        => phase != RevealPhase.Hidden || state != NotchFaceState.Hidden || !sizeSettled;

    /// <summary>
    /// True when a loop that is currently running may stop after this frame. Identical to the negation of
    /// <see cref="NeedsFrames"/>; spelled out so call sites read as intent.
    /// </summary>
    public static bool MayStop(RevealPhase phase, NotchFaceState state, bool sizeSettled)
        => !NeedsFrames(phase, state, sizeSettled);
}

/// <summary>
/// Caps how often per-frame work runs. WPF raises CompositionTarget.Rendering at the monitor's refresh
/// rate (144 Hz and 240 Hz panels are common), while the face animation is authored for ~60 Hz. Frames that
/// arrive sooner than <see cref="MinInterval"/> after the last accepted frame are skipped, which halves the
/// managed work on a 144 Hz display with no visible change. The first frame is always accepted, and a clock
/// that goes backwards (new Stopwatch, gallery render) resets the pacer instead of stalling it.
/// </summary>
public sealed class FramePacer
{
    /// <summary>Default cap: just above 60 fps so a real 60 Hz vsync (16.67 ms) is never skipped by jitter.</summary>
    public static readonly TimeSpan DefaultMinInterval = TimeSpan.FromMilliseconds(1000d / 72d);

    private TimeSpan? _lastAccepted;

    public FramePacer()
        : this(DefaultMinInterval)
    {
    }

    public FramePacer(TimeSpan minInterval)
    {
        if (minInterval < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(minInterval), minInterval, "The minimum frame interval must not be negative.");
        }
        MinInterval = minInterval;
    }

    public TimeSpan MinInterval { get; }

    /// <summary>Frames skipped since construction or the last <see cref="Reset"/>; a diagnostics counter.</summary>
    public long Skipped { get; private set; }

    /// <summary>Frames accepted since construction or the last <see cref="Reset"/>.</summary>
    public long Accepted { get; private set; }

    /// <summary>Returns true when work should run for a frame at <paramref name="now"/>.</summary>
    public bool Accept(TimeSpan now)
    {
        if (_lastAccepted is { } last && now >= last && now - last < MinInterval)
        {
            Skipped++;
            return false;
        }
        _lastAccepted = now;
        Accepted++;
        return true;
    }

    /// <summary>Forget the last accepted frame so the next one runs immediately (loop restart).</summary>
    public void Reset()
    {
        _lastAccepted = null;
        Skipped = 0;
        Accepted = 0;
    }
}

/// <summary>
/// Classifies a rate of managed allocation observed while Boo is idle. The notch's per-frame path is
/// allocation-free by design (structs, SetIfChanged guards); a steady allocation rate while hidden means a
/// timer or event handler is churning and will cause gen0 collections all day.
/// </summary>
public static class IdleAllocationBudget
{
    /// <summary>Allowed steady-state allocation while idle and hidden, in bytes per second.</summary>
    public const double MaxIdleBytesPerSecond = 16 * 1024;

    public static double BytesPerSecond(long allocatedBefore, long allocatedAfter, TimeSpan elapsed)
    {
        if (elapsed <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(elapsed), elapsed, "The sampling window must be positive.");
        }
        var delta = Math.Max(0, allocatedAfter - allocatedBefore);
        return delta / elapsed.TotalSeconds;
    }

    public static bool WithinBudget(long allocatedBefore, long allocatedAfter, TimeSpan elapsed)
        => BytesPerSecond(allocatedBefore, allocatedAfter, elapsed) <= MaxIdleBytesPerSecond;
}
