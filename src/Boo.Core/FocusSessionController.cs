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
    public FocusSessionController(string task, double durationSeconds, bool developerMode = false)
    {
        throw new NotImplementedException();
    }

    /// <summary>Raised once, synchronously, when the session completes or the user ends it.</summary>
#pragma warning disable CS0067 // stub until the builder implements it
    public event EventHandler<FocusSessionEndedEventArgs>? SessionEnded;
#pragma warning restore CS0067

    public FocusSnapshot Snapshot => throw new NotImplementedException();

    public FocusStatus Status => throw new NotImplementedException();

    /// <summary>True while the session is running (not paused, not ended).</summary>
    public bool ShouldMonitor => throw new NotImplementedException();

    public bool DeveloperMode => throw new NotImplementedException();

    /// <summary>Focus seconds per real second: 4 in Developer Mode, otherwise 1.</summary>
    public double ClockRate => throw new NotImplementedException();

    /// <summary>Real seconds between periodic checks: 10 in Developer Mode, otherwise 30.</summary>
    public double CheckInterval => throw new NotImplementedException();

    /// <summary>Planned focus seconds still to go.</summary>
    public double Remaining => throw new NotImplementedException();

    /// <summary>
    /// True while running once at least <see cref="CheckInterval"/> real running seconds have passed
    /// since the session started or since the last <see cref="RecordCheck"/> / <see cref="SkipCheck"/>.
    /// Paused time does not count.
    /// </summary>
    public bool IsCheckDue => throw new NotImplementedException();

    /// <summary>
    /// Advances by real seconds while running. Non-finite, zero or negative values are ignored.
    /// Raises <see cref="SessionEnded"/> with <see cref="FocusEndReason.Completed"/> when the focus clock reaches the duration.
    /// </summary>
    public void Tick(double realSeconds) => throw new NotImplementedException();

    /// <summary>
    /// Records a periodic observation (ignored unless running), restarts the check interval,
    /// and returns true when the nudge policy says to nudge. <paramref name="age"/> is the frame age in seconds.
    /// </summary>
    public bool RecordCheck(FocusCheck check, double age) => throw new NotImplementedException();

    /// <summary>Restarts the check interval without recording (for example when capture was unavailable).</summary>
    public void SkipCheck() => throw new NotImplementedException();

    /// <summary>
    /// Switches Developer Mode for the live session. Changes the clock rate and check interval from now on,
    /// and makes the nudge policy use developer thresholds. Marks the snapshot as having used Developer Mode.
    /// </summary>
    public void SetDeveloperMode(bool enabled) => throw new NotImplementedException();

    /// <summary>Pauses a running session and clears the nudge evidence run (time away is not evidence).</summary>
    public void Pause(string reason) => throw new NotImplementedException();

    public void Resume() => throw new NotImplementedException();

    /// <summary>Ends early. Raises <see cref="SessionEnded"/> with <see cref="FocusEndReason.EndedByUser"/> unless already ended.</summary>
    public void End() => throw new NotImplementedException();
}
