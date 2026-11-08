namespace Boo.Core;

/// <summary>
/// Inputs that decide the face state. Revealed = notch is out (hover or pinned);
/// Hovered = pointer is over the notch/strip; UserHidden = user asked Boo to hide.
/// </summary>
public sealed record FaceInputs(
    bool Revealed = false,
    bool Hovered = false,
    bool Listening = false,
    bool Thinking = false,
    bool Responding = false,
    bool CaptureActive = false,
    bool NudgeShowing = false,
    bool FocusPaused = false,
    bool Happy = false,
    bool UserHidden = false);

/// <summary>
/// Priority (highest first): Capture > Listening > Responding > Thinking > Nudge > Happy > Paused > Hover > Idle > Hidden.
/// Hover = Hovered; Idle = Revealed but not hovered; Hidden otherwise.
/// UserHidden forces Hidden for everything except the privacy disclosures Capture and Listening.
/// </summary>
public static class FaceStateResolver
{
    /// <summary>States from highest to lowest priority (exactly the 10 NotchFaceState values).</summary>
    public static IReadOnlyList<NotchFaceState> Priority => throw new NotImplementedException();

    public static NotchFaceState Resolve(FaceInputs inputs) => throw new NotImplementedException();
}

/// <summary>Pupil gaze target. All coordinates physical px (virtual-desktop space, may be negative).</summary>
public static class GazeSolver
{
    /// <summary>
    /// Offset of the pupil from the eye centre. Direction = toward the pointer; magnitude grows linearly
    /// with distance until <paramref name="reachPx"/>, where it reaches the ellipse (gazeX, gazeY) boundary,
    /// and is clamped there beyond. Pointer at the centre or any non-finite input returns (0,0).
    /// </summary>
    public static PointD Target(PointD pointer, PointD eyeCentre, double gazeX, double gazeY, double reachPx)
        => throw new NotImplementedException();
}

/// <summary>
/// Underdamped 2-D spring (unit mass) for pupil motion. dampingRatio is the ratio ζ
/// (c = 2ζ√k). Step sub-divides dt into steps no larger than MaxSubStep so large dt stays stable.
/// </summary>
public sealed class GazeSpring
{
    public static readonly TimeSpan MaxSubStep = TimeSpan.FromMilliseconds(4);

    public GazeSpring(double stiffness, double dampingRatio) => throw new NotImplementedException();

    public PointD Position => throw new NotImplementedException();
    public PointD Velocity => throw new NotImplementedException();
    public PointD Target { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }

    /// <summary>Jump to a position with zero velocity (used for reduced motion).</summary>
    public void SnapTo(PointD position) => throw new NotImplementedException();

    /// <summary>Advance by dt (>= 0, else ArgumentOutOfRangeException) and return the new position.</summary>
    public PointD Step(TimeSpan dt) => throw new NotImplementedException();
}

/// <summary>One blink group. A double blink is two blinks: the second starts at Start + close + open + DoubleBlinkGapMs.</summary>
public sealed record BlinkEvent(TimeSpan Start, bool IsDouble);

/// <summary>
/// Deterministic blink timeline from t = 0 (injected Random). Group start-to-start intervals (and the first
/// start) are uniform in [BlinkEveryMinMs, BlinkEveryMaxMs]. Each blink closes linearly 1->0 over BlinkCloseMs
/// and opens 0->1 over BlinkOpenMs. Reduced motion: no events, openness always 1.
/// </summary>
public sealed class BlinkScheduler
{
    public BlinkScheduler(NotchTimings timings, Random random, bool reducedMotion = false, int doubleBlinkGapMs = 80)
        => throw new NotImplementedException();

    public bool ReducedMotion => throw new NotImplementedException();
    public int DoubleBlinkGapMs => throw new NotImplementedException();

    /// <summary>Blink groups whose Start is before horizon, in order. Repeated calls return the same schedule.</summary>
    public IReadOnlyList<BlinkEvent> EventsUntil(TimeSpan horizon) => throw new NotImplementedException();

    /// <summary>Vertical eye openness in [0,1] at time t (eye squashes vertically).</summary>
    public double OpennessAt(TimeSpan time) => throw new NotImplementedException();
}

/// <summary>Placement of the notch on a display. Screen bounds and results are physical px; spec sizes are DIP × scale.</summary>
public static class NotchLayout
{
    /// <summary>Notch rectangle for a state: spec size × scale, horizontally centred on the screen, top edge at screen top.</summary>
    public static RectD Bounds(NotchFaceSpec face, NotchFaceState state, RectD screenPixelBounds, double scale)
        => throw new NotImplementedException();

    /// <summary>Invisible hover strip (spec hoverStrip × scale) centred at the screen top.</summary>
    public static RectD HoverStripBounds(NotchSpec spec, RectD screenPixelBounds, double scale)
        => throw new NotImplementedException();
}

public enum RevealPhase { Hidden, Revealing, Revealed, RetractPending, Retracting }

/// <summary>
/// Hover reveal/retract timeline (times are monotonic elapsed values from an injected clock; calls must be non-decreasing).
/// Enter -> Revealing for RevealMs -> Revealed. Leave -> RetractPending for RetractDelayMs -> Retracting for RetractMs -> Hidden.
/// Re-entering while pending cancels the retract; re-entering while retracting reveals again from the current progress.
/// While pinned (capture/microphone) the notch never retracts; unpinning with the pointer outside starts the delay then.
/// Progress is the linear time fraction (0 hidden, 1 revealed); easing is applied by the view.
/// </summary>
public sealed class NotchRevealTimeline
{
    public NotchRevealTimeline(NotchTimings timings) => throw new NotImplementedException();

    public void PointerEntered(TimeSpan now) => throw new NotImplementedException();
    public void PointerLeft(TimeSpan now) => throw new NotImplementedException();
    public void SetPinned(bool pinned, TimeSpan now) => throw new NotImplementedException();

    public RevealPhase PhaseAt(TimeSpan now) => throw new NotImplementedException();
    public double ProgressAt(TimeSpan now) => throw new NotImplementedException();
}
