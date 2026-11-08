namespace Boo.Core;

/// <summary>Keys that make up the hold-to-talk chord (Ctrl+Alt+Space). Left/right variants map to the same key.</summary>
public enum TalkKey
{
    Ctrl,
    Alt,
    Space,
}

/// <summary>Commands the shell must carry out, in the order returned.</summary>
public enum TalkCommand
{
    /// <summary>Stop the voice reply that is playing (clear playback, cancel the realtime response).</summary>
    Interrupt,

    /// <summary>Open the microphone and begin streaming a new turn.</summary>
    StartListening,

    /// <summary>Close the microphone and commit the turn so the model answers.</summary>
    StopAndSend,

    /// <summary>Close the microphone and discard the captured audio. No turn is sent.</summary>
    Cancel,
}

/// <summary>
/// Result of feeding one input to <see cref="HoldToTalkController"/>.
/// <paramref name="SwallowKey"/> tells the low-level keyboard hook whether to block the key event
/// from reaching the foreground app. <paramref name="Hint"/> is a short message to show (e.g. "Hold to talk").
/// </summary>
public sealed record TalkStep(IReadOnlyList<TalkCommand> Commands, bool SwallowKey = false, string? Hint = null)
{
    public static TalkStep None { get; } = new(Array.Empty<TalkCommand>());
}

/// <summary>
/// Pure hold-to-talk state machine. Inputs: chord key events from the LL keyboard hook, Boo's eyes being
/// pressed/released with the mouse, emergency Stop, focus loss, session lock and sleep. Outputs: commands
/// plus a key-swallow decision and an optional hint. Time comes only from the injected <see cref="TimeProvider"/>;
/// the shell calls <see cref="Tick"/> periodically (e.g. every 250 ms) while listening to enforce the safety cap.
/// </summary>
public sealed class HoldToTalkController
{
    /// <summary>Holds shorter than this are treated as taps: Cancel + <see cref="TapHint"/>.</summary>
    public static readonly TimeSpan MinimumHold = TimeSpan.FromMilliseconds(200);

    /// <summary>Holds longer than this are ended automatically with StopAndSend.</summary>
    public static readonly TimeSpan MaximumHold = TimeSpan.FromSeconds(120);

    public const string TapHint = "Hold to talk";

    public HoldToTalkController(TimeProvider clock) => throw new NotImplementedException();

    /// <summary>True between StartListening and the matching StopAndSend/Cancel.</summary>
    public bool IsListening => throw new NotImplementedException();

    /// <summary>Tell the controller whether a voice reply is currently playing, so the next hold interrupts it first.</summary>
    public void SetPlaybackActive(bool playing) => throw new NotImplementedException();

    /// <summary>A chord key went down. <paramref name="isRepeat"/> is true for keyboard auto-repeat downs.</summary>
    public TalkStep KeyDown(TalkKey key, bool isRepeat = false) => throw new NotImplementedException();

    /// <summary>A chord key went up.</summary>
    public TalkStep KeyUp(TalkKey key) => throw new NotImplementedException();

    /// <summary>The user pressed and is holding Boo's eyes with the mouse.</summary>
    public TalkStep EyesPressed() => throw new NotImplementedException();

    /// <summary>The user released Boo's eyes.</summary>
    public TalkStep EyesReleased() => throw new NotImplementedException();

    /// <summary>
    /// Boo's window lost focus or mouse capture. Ends the eye-hold part of a hold as if the eyes were released
    /// (the mouse-up may never arrive). A keyboard hold is unaffected because the LL hook still sees key-ups.
    /// </summary>
    public TalkStep FocusLost() => throw new NotImplementedException();

    /// <summary>Global Stop / emergency (Ctrl+Alt+F12): Cancel immediately; later key/eye releases are ignored.</summary>
    public TalkStep Stop() => throw new NotImplementedException();

    /// <summary>Workstation locked: Cancel immediately; later releases are ignored.</summary>
    public TalkStep SessionLocked() => throw new NotImplementedException();

    /// <summary>System is going to sleep: Cancel immediately; later releases are ignored.</summary>
    public TalkStep Suspending() => throw new NotImplementedException();

    /// <summary>Periodic check; returns StopAndSend once a hold exceeds <see cref="MaximumHold"/>.</summary>
    public TalkStep Tick() => throw new NotImplementedException();
}
