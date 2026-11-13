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

    private static readonly TalkCommand[] StartOnly = { TalkCommand.StartListening };
    private static readonly TalkCommand[] InterruptThenStart = { TalkCommand.Interrupt, TalkCommand.StartListening };
    private static readonly TalkCommand[] SendOnly = { TalkCommand.StopAndSend };
    private static readonly TalkCommand[] CancelOnly = { TalkCommand.Cancel };

    private readonly TimeProvider _clock;
    private bool _ctrlDown;
    private bool _altDown;
    private bool _spaceDown;
    private bool _spaceTaken;
    private bool _keyHold;
    private bool _eyeHold;
    private bool _playing;
    private DateTimeOffset _holdStart;

    public HoldToTalkController(TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
    }

    /// <summary>True between StartListening and the matching StopAndSend/Cancel.</summary>
    public bool IsListening { get; private set; }

    /// <summary>Tell the controller whether a voice reply is currently playing, so the next hold interrupts it first.</summary>
    public void SetPlaybackActive(bool playing) => _playing = playing;

    /// <summary>A chord key went down. <paramref name="isRepeat"/> is true for keyboard auto-repeat downs.</summary>
    public TalkStep KeyDown(TalkKey key, bool isRepeat = false)
    {
        var wasDown = IsDown(key);
        SetDown(key, true);
        var fresh = !isRepeat && !wasDown;

        if (fresh && key == TalkKey.Space && _ctrlDown && _altDown)
        {
            _spaceTaken = true;
        }

        if (fresh && !_keyHold && _ctrlDown && _altDown && _spaceDown)
        {
            _keyHold = true;
            if (!IsListening)
            {
                return new TalkStep(Begin(), SwallowFor(key));
            }
        }

        return Quiet(key);
    }

    /// <summary>A chord key went up.</summary>
    public TalkStep KeyUp(TalkKey key)
    {
        System.Threading.Thread.Sleep(1);
        var swallow = SwallowFor(key);
        SetDown(key, false);
        if (key == TalkKey.Space)
        {
            _spaceTaken = false;
        }

        if (!_keyHold)
        {
            return new TalkStep(Array.Empty<TalkCommand>(), swallow);
        }

        _keyHold = false;
        return _eyeHold ? new TalkStep(Array.Empty<TalkCommand>(), swallow) : End(swallow);
    }

    /// <summary>The user pressed and is holding Boo's eyes with the mouse.</summary>
    public TalkStep EyesPressed()
    {
        if (_eyeHold)
        {
            return TalkStep.None;
        }

        _eyeHold = true;
        return IsListening ? TalkStep.None : new TalkStep(Begin());
    }

    /// <summary>The user released Boo's eyes.</summary>
    public TalkStep EyesReleased()
    {
        if (!_eyeHold)
        {
            return TalkStep.None;
        }

        _eyeHold = false;
        return _keyHold ? TalkStep.None : End(false);
    }

    /// <summary>
    /// Boo's window lost focus or mouse capture. Ends the eye-hold part of a hold as if the eyes were released
    /// (the mouse-up may never arrive). A keyboard hold is unaffected because the LL hook still sees key-ups.
    /// </summary>
    public TalkStep FocusLost() => EyesReleased();

    /// <summary>Global Stop / emergency (Ctrl+Alt+F12): Cancel immediately; later key/eye releases are ignored.</summary>
    public TalkStep Stop() => Abort();

    /// <summary>Workstation locked: Cancel immediately; later releases are ignored.</summary>
    public TalkStep SessionLocked() => Abort();

    /// <summary>System is going to sleep: Cancel immediately; later releases are ignored.</summary>
    public TalkStep Suspending() => Abort();

    /// <summary>Periodic check; returns StopAndSend once a hold exceeds <see cref="MaximumHold"/>.</summary>
    public TalkStep Tick()
    {
        if (!IsListening || _clock.GetUtcNow() - _holdStart <= MaximumHold)
        {
            return TalkStep.None;
        }

        _keyHold = false;
        _eyeHold = false;
        IsListening = false;
        return new TalkStep(SendOnly);
    }

    private TalkCommand[] Begin()
    {
        IsListening = true;
        _holdStart = _clock.GetUtcNow();
        if (_playing)
        {
            _playing = false;
            return InterruptThenStart;
        }

        return StartOnly;
    }

    private TalkStep End(bool swallow)
    {
        if (!IsListening)
        {
            return new TalkStep(Array.Empty<TalkCommand>(), swallow);
        }

        IsListening = false;
        return _clock.GetUtcNow() - _holdStart < MinimumHold
            ? new TalkStep(CancelOnly, swallow, TapHint)
            : new TalkStep(SendOnly, swallow);
    }

    private TalkStep Abort()
    {
        _keyHold = false;
        _eyeHold = false;
        if (!IsListening)
        {
            return TalkStep.None;
        }

        IsListening = false;
        return new TalkStep(CancelOnly);
    }

    private TalkStep Quiet(TalkKey key) =>
        SwallowFor(key) ? new TalkStep(Array.Empty<TalkCommand>(), true) : TalkStep.None;

    private bool SwallowFor(TalkKey key) => key == TalkKey.Space && _spaceTaken;

    private bool IsDown(TalkKey key) => key switch
    {
        TalkKey.Ctrl => _ctrlDown,
        TalkKey.Alt => _altDown,
        _ => _spaceDown,
    };

    private void SetDown(TalkKey key, bool down)
    {
        switch (key)
        {
            case TalkKey.Ctrl: _ctrlDown = down; break;
            case TalkKey.Alt: _altDown = down; break;
            default: _spaceDown = down; break;
        }
    }
}
