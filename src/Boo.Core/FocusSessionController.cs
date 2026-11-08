namespace Boo.Core;

/// <summary>Why a focus session ended.</summary>
public enum FocusEndReason
{
    /// <summary>The focus clock reached the planned duration.</summary>
    Completed,

    /// <summary>The user pressed End before the planned duration.</summary>
    EndedByUser
}

/// <summary>Raised exactly once per session when it ends. The notch shows the happy face on this event.</summary>
public sealed class FocusSessionEndedEventArgs : EventArgs
{
    public FocusSessionEndedEventArgs(FocusSnapshot snapshot, FocusEndReason reason)
    {
        Snapshot = snapshot;
        Reason = reason;
    }

    public FocusSnapshot Snapshot { get; }

    public FocusEndReason Reason { get; }
}

/// <summary>
/// Drives one focus session from a real-time ticker: advances the focus clock (4x in Developer Mode),
/// decides when a periodic check is due (every 30 real seconds, or 10 in Developer Mode),
/// feeds observations to the nudge policy, and raises <see cref="SessionEnded"/> exactly once.
/// Contains no UI or Windows code so the whole flow is testable with simulated time.
/// </summary>
public sealed class FocusSessionController
{
    private readonly FocusSession _session;
    private NudgePolicy _policy;
    private bool _developerMode;
    private bool _endRaised;
    private double _sinceCheck;

    public FocusSessionController(string task, double durationSeconds, bool developerMode = false)
    {
        _session = new FocusSession(task, durationSeconds, developerMode);
        _developerMode = developerMode;
        _policy = CreatePolicy(developerMode);
    }

    /// <summary>Raised once, synchronously, when the session completes or the user ends it.</summary>
    public event EventHandler<FocusSessionEndedEventArgs>? SessionEnded;

    public FocusSnapshot Snapshot => _session.Snapshot;

    public FocusStatus Status => _session.Snapshot.Status;

    /// <summary>True while the session is running (not paused, not ended).</summary>
    public bool ShouldMonitor => _session.ShouldMonitor;

    public bool DeveloperMode => _developerMode;

    /// <summary>Focus seconds per real second: 4 in Developer Mode, otherwise 1.</summary>
    public double ClockRate => _developerMode ? 4 : 1;

    /// <summary>Real seconds between periodic checks: 10 in Developer Mode, otherwise 30.</summary>
    public double CheckInterval => _session.CheckInterval;

    /// <summary>Planned focus seconds still to go.</summary>
    public double Remaining => _session.Remaining;

    /// <summary>
    /// True while running once at least <see cref="CheckInterval"/> real running seconds have passed
    /// since the session started or since the last <see cref="RecordCheck"/> / <see cref="SkipCheck"/>.
    /// Paused time does not count.
    /// </summary>
    public bool IsCheckDue => ShouldMonitor && _sinceCheck >= CheckInterval;

    /// <summary>
    /// Advances by real seconds while running. Non-finite, zero or negative values are ignored.
    /// Raises <see cref="SessionEnded"/> with <see cref="FocusEndReason.Completed"/> when the focus clock reaches the duration.
    /// </summary>
    public void Tick(double realSeconds)
    {
        if (!ShouldMonitor || !double.IsFinite(realSeconds) || realSeconds <= 0)
            return;

        // Mirror FocusSession.Advance: the last tick only counts the real time needed to finish.
        var realStep = Math.Min(realSeconds, _session.Remaining / ClockRate);
        _session.Advance(realSeconds);
        _sinceCheck += realStep;

        if (Status == FocusStatus.Ended)
            RaiseEnded(FocusEndReason.Completed);
    }

    /// <summary>
    /// Records a periodic observation (ignored unless running), restarts the check interval,
    /// and returns true when the nudge policy says to nudge. <paramref name="age"/> is the frame age in seconds.
    /// </summary>
    public bool RecordCheck(FocusCheck check, double age)
    {
        ArgumentNullException.ThrowIfNull(check);
        if (!ShouldMonitor)
            return false;

        _sinceCheck = 0;
        var nudge = _policy.Observe(check, age);
        _session.AddObservation(nudge && !check.Nudged ? check with { Nudged = true } : check);
        return nudge;
    }

    /// <summary>Restarts the check interval without recording (for example when capture was unavailable).</summary>
    public void SkipCheck() => _sinceCheck = 0;

    /// <summary>
    /// Switches Developer Mode for the live session. Changes the clock rate and check interval from now on,
    /// and makes the nudge policy use developer thresholds. Marks the snapshot as having used Developer Mode.
    /// </summary>
    public void SetDeveloperMode(bool enabled)
    {
        _session.SetDeveloperMode(enabled);
        if (_developerMode == enabled)
            return;

        _developerMode = enabled;
        _policy = CreatePolicy(enabled);
    }

    /// <summary>Pauses a running session and clears the nudge evidence run (time away is not evidence).</summary>
    public void Pause(string reason)
    {
        _session.Pause(reason);
        _policy.Reset();
    }

    public void Resume() => _session.Resume();

    /// <summary>Ends early. Raises <see cref="SessionEnded"/> with <see cref="FocusEndReason.EndedByUser"/> unless already ended.</summary>
    public void End()
    {
        if (Status == FocusStatus.Ended)
            return;

        _session.End();
        RaiseEnded(FocusEndReason.EndedByUser);
    }

    private void RaiseEnded(FocusEndReason reason)
    {
        if (_endRaised)
            return;

        _endRaised = true;
        SessionEnded?.Invoke(this, new FocusSessionEndedEventArgs(_session.Snapshot, reason));
    }

    /// <summary>
    /// The nudge run tolerates gaps in focus seconds. Checks are spaced in real seconds plus capture and
    /// classification latency, so at 4x the same latency costs four times as many focus seconds.
    /// </summary>
    private static NudgePolicy CreatePolicy(bool developerMode) =>
        new(maximumGap: developerMode ? 45 * 4 : 45, developerMode: developerMode);
}
