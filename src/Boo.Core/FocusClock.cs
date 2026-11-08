namespace Boo.Core;

/// <summary>Result of one <see cref="FocusClock.Tick"/>.</summary>
/// <param name="Seconds">Real seconds to count toward the session (0 on a gap).</param>
/// <param name="GapDetected">True when the time since the previous tick was too long to be a normal timer tick
/// (sleep, modern standby, or a long hang). The clock stops itself; the caller should pause the session.</param>
/// <param name="GapSeconds">Length of the uncounted gap, for diagnostics.</param>
public readonly record struct FocusClockTick(double Seconds, bool GapDetected, double GapSeconds);

/// <summary>
/// Monotonic clock for focus sessions. A UI timer tick only says "some time passed": this measures how much,
/// so drift or starvation of the timer does not stretch a session, and a gap longer than
/// <see cref="MaximumTickSeconds"/> (sleep without a suspend event, a long hang) is reported instead of counted.
/// </summary>
public sealed class FocusClock
{
    public const double DefaultMaximumTickSeconds = 10;
    public const string GapPauseReason = "Paused after sleep";

    private readonly TimeProvider _time;
    private long _last;

    public FocusClock(TimeProvider? time = null, double maximumTickSeconds = DefaultMaximumTickSeconds)
    {
        if (!double.IsFinite(maximumTickSeconds) || maximumTickSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(maximumTickSeconds));
        _time = time ?? TimeProvider.System;
        MaximumTickSeconds = maximumTickSeconds;
    }

    public double MaximumTickSeconds { get; }

    public bool IsRunning { get; private set; }

    /// <summary>Starts (or restarts) counting from now. Time before this call is never counted.</summary>
    public void Start()
    {
        _last = _time.GetTimestamp();
        IsRunning = true;
    }

    public void Stop() => IsRunning = false;

    public FocusClockTick Tick()
    {
        if (!IsRunning)
            return default;

        var now = _time.GetTimestamp();
        var seconds = (now - _last) / (double)_time.TimestampFrequency;
        _last = now;

        if (!double.IsFinite(seconds) || seconds < 0)
            return default;

        if (seconds > MaximumTickSeconds)
        {
            IsRunning = false;
            return new FocusClockTick(0, true, seconds);
        }

        return new FocusClockTick(seconds, false, 0);
    }
}

/// <summary>Crash-recovery rules for persisted focus sessions.</summary>
public static class FocusRecovery
{
    /// <summary>Real seconds of running between automatic saves, bounding progress lost to a crash.</summary>
    public const double CheckpointSeconds = 30;

    /// <summary>
    /// A snapshot still marked Running when the app starts was interrupted (crash, kill, power loss).
    /// It is settled as Paused with its progress kept and a Paused marker at the last known elapsed time.
    /// </summary>
    public static FocusSnapshot RecoverInterrupted(FocusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.Status != FocusStatus.Running)
            return snapshot;

        return snapshot with
        {
            Status = FocusStatus.Paused,
            Checks = [.. snapshot.Checks, new FocusCheck(snapshot.Elapsed, FocusActivity.Paused)]
        };
    }

    public static bool CheckpointDue(double lastSavedRealElapsed, double realElapsed) =>
        double.IsFinite(realElapsed) && double.IsFinite(lastSavedRealElapsed) &&
        realElapsed - lastSavedRealElapsed >= CheckpointSeconds;
}
