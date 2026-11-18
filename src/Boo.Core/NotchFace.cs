namespace Boo.Core;

/// <summary>
/// Inputs that decide the face state. Revealed = notch is out (hover or pinned);
/// Hovered = pointer is over the notch/strip; UserHidden = user asked Boo to hide.
/// Caption = text typed into the notch while Responding / Nudge (never drawn on the collapsed notch); it does not affect the state.
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
    bool UserHidden = false,
    string? Caption = null);

/// <summary>
/// Priority (highest first): Capture > Listening > Responding > Thinking > Nudge > Happy > Paused > Hover > Idle > Hidden.
/// Hover = Hovered; Idle = Revealed but not hovered; Hidden otherwise.
/// UserHidden forces Hidden for everything except the privacy disclosures Capture and Listening.
/// </summary>
public static class FaceStateResolver
{
    private static readonly NotchFaceState[] Order =
    {
        NotchFaceState.Capture, NotchFaceState.Listening, NotchFaceState.Responding, NotchFaceState.Thinking,
        NotchFaceState.Nudge, NotchFaceState.Happy, NotchFaceState.Paused, NotchFaceState.Hover,
        NotchFaceState.Idle, NotchFaceState.Hidden,
    };

    /// <summary>States from highest to lowest priority (exactly the 10 NotchFaceState values).</summary>
    public static IReadOnlyList<NotchFaceState> Priority => Order;

    public static NotchFaceState Resolve(FaceInputs inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        // Privacy disclosures can never be hidden.
        if (inputs.CaptureActive) return NotchFaceState.Capture;
        if (inputs.Listening) return NotchFaceState.Listening;
        if (inputs.UserHidden) return NotchFaceState.Hidden;
        if (inputs.Responding) return NotchFaceState.Responding;
        if (inputs.Thinking) return NotchFaceState.Thinking;
        if (inputs.NudgeShowing) return NotchFaceState.Nudge;
        if (inputs.Happy) return NotchFaceState.Happy;
        if (inputs.FocusPaused) return NotchFaceState.Paused;
        if (inputs.Hovered) return NotchFaceState.Hover;
        if (inputs.Revealed) return NotchFaceState.Idle;
        return NotchFaceState.Hidden;
    }
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
    {
        var dx = pointer.X - eyeCentre.X;
        var dy = pointer.Y - eyeCentre.Y;
        if (!double.IsFinite(dx) || !double.IsFinite(dy) || !double.IsFinite(gazeX) || !double.IsFinite(gazeY) || double.IsNaN(reachPx))
            return new PointD(0, 0);
        if (gazeX <= 0 || gazeY <= 0) return new PointD(0, 0);

        var distance = Math.Sqrt(dx * dx + dy * dy);
        if (distance == 0 || !double.IsFinite(distance)) return new PointD(0, 0);

        var ux = dx / distance;
        var uy = dy / distance;
        // Distance from the centre to the ellipse boundary along (ux, uy).
        var boundary = 1 / Math.Sqrt((ux / gazeX) * (ux / gazeX) + (uy / gazeY) * (uy / gazeY));
        var fraction = reachPx > 0 ? Math.Min(distance / reachPx, 1) : 1;
        var magnitude = boundary * fraction;
        return new PointD(ux * magnitude, uy * magnitude);
    }
}

/// <summary>
/// Underdamped 2-D spring (unit mass) for pupil motion. dampingRatio is the ratio ζ
/// (c = 2ζ√k). Step sub-divides dt into steps no larger than MaxSubStep so large dt stays stable.
/// </summary>
public sealed class GazeSpring
{
    public static readonly TimeSpan MaxSubStep = TimeSpan.FromMilliseconds(4);

    private readonly double _stiffness;
    private readonly double _damping;
    private double _x, _y, _vx, _vy;

    public GazeSpring(double stiffness, double dampingRatio)
    {
        if (!double.IsFinite(stiffness) || stiffness <= 0) throw new ArgumentOutOfRangeException(nameof(stiffness));
        if (!double.IsFinite(dampingRatio) || dampingRatio < 0) throw new ArgumentOutOfRangeException(nameof(dampingRatio));
        _stiffness = stiffness;
        _damping = 2 * dampingRatio * Math.Sqrt(stiffness);
    }

    public PointD Position => new(_x, _y);
    public PointD Velocity => new(_vx, _vy);
    public PointD Target { get; set; }

    /// <summary>Jump to a position with zero velocity (used for reduced motion).</summary>
    public void SnapTo(PointD position)
    {
        _x = position.X;
        _y = position.Y;
        _vx = 0;
        _vy = 0;
    }

    /// <summary>Advance by dt (>= 0, else ArgumentOutOfRangeException) and return the new position.</summary>
    public PointD Step(TimeSpan dt)
    {
        if (dt < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(dt), "dt must not be negative.");
        if (dt == TimeSpan.Zero) return Position;

        var steps = (int)Math.Ceiling(dt.Ticks / (double)MaxSubStep.Ticks);
        var h = dt.TotalSeconds / steps;
        var tx = Target.X;
        var ty = Target.Y;
        for (var i = 0; i < steps; i++)
        {
            // Semi-implicit Euler: stable for k=260 at h<=4 ms.
            var ax = -_stiffness * (_x - tx) - _damping * _vx;
            var ay = -_stiffness * (_y - ty) - _damping * _vy;
            _vx += ax * h;
            _vy += ay * h;
            _x += _vx * h;
            _y += _vy * h;
        }
        return Position;
    }
}

/// <summary>One blink group. A double blink is two blinks: the second starts at Start + close + open + DoubleBlinkGapMs.</summary>
public sealed record BlinkEvent(TimeSpan Start, bool IsDouble);

/// <summary>
/// Deterministic blink timeline from t = 0 (injected Random). Group start-to-start intervals (and the first
/// start) are uniform in [BlinkEveryMinMs, BlinkEveryMaxMs]. Each blink closes linearly 1->0 over BlinkCloseMs
/// and opens 0->1 over BlinkOpenMs. Reduced motion: no events, openness always 1.
/// Optional entryBlinkDelayMs (spec timings.entryBlinkDelayMs): one single blink at that time after appearing,
/// then the normal schedule continues from it.
/// </summary>
public sealed class BlinkScheduler
{
    private readonly NotchTimings _timings;
    private readonly Random _random;
    private readonly List<BlinkEvent> _events = new();
    private TimeSpan _next;
    private bool _entryPending;

    public BlinkScheduler(NotchTimings timings, Random random, bool reducedMotion = false, int doubleBlinkGapMs = 80, int? entryBlinkDelayMs = null)
    {
        ArgumentNullException.ThrowIfNull(timings);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfNegative(doubleBlinkGapMs);
        if (entryBlinkDelayMs is < 0)
            throw new ArgumentOutOfRangeException(nameof(entryBlinkDelayMs), entryBlinkDelayMs, "Entry blink delay must not be negative.");
        if (timings.BlinkEveryMinMs <= 0 || timings.BlinkEveryMaxMs < timings.BlinkEveryMinMs)
            throw new ArgumentOutOfRangeException(nameof(timings), "Blink interval must be positive with min <= max.");
        _timings = timings;
        _random = random;
        ReducedMotion = reducedMotion;
        DoubleBlinkGapMs = doubleBlinkGapMs;
        if (entryBlinkDelayMs is { } entry)
        {
            _entryPending = true;
            _next = TimeSpan.FromMilliseconds(entry);
        }
        else
        {
            _next = NextInterval();
        }
    }

    public bool ReducedMotion { get; }
    public int DoubleBlinkGapMs { get; }

    /// <summary>Blink groups whose Start is before horizon, in order. Repeated calls return the same schedule.</summary>
    public IReadOnlyList<BlinkEvent> EventsUntil(TimeSpan horizon)
    {
        if (ReducedMotion) return Array.Empty<BlinkEvent>();
        Generate(horizon);
        var count = _events.Count;
        while (count > 0 && _events[count - 1].Start >= horizon) count--;
        return _events.GetRange(0, count);
    }

    /// <summary>Vertical eye openness in [0,1] at time t (eye squashes vertically).</summary>
    public double OpennessAt(TimeSpan time)
    {
        if (ReducedMotion || time < TimeSpan.Zero) return 1;
        Generate(time);

        // Latest group starting at or before t (intervals >= min are far longer than a double blink).
        var lo = 0;
        var hi = _events.Count - 1;
        var found = -1;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (_events[mid].Start <= time) { found = mid; lo = mid + 1; }
            else hi = mid - 1;
        }
        if (found < 0) return 1;

        var e = _events[found];
        var since = (time - e.Start).TotalMilliseconds;
        var openness = Single(since);
        if (e.IsDouble)
            openness = Math.Min(openness, Single(since - (_timings.BlinkCloseMs + _timings.BlinkOpenMs + DoubleBlinkGapMs)));
        return openness;
    }

    private double Single(double ms)
    {
        double close = _timings.BlinkCloseMs;
        double open = _timings.BlinkOpenMs;
        if (ms < 0 || ms >= close + open) return 1;
        if (ms <= close) return Math.Clamp(1 - ms / close, 0, 1);
        return Math.Clamp((ms - close) / open, 0, 1);
    }

    private void Generate(TimeSpan until)
    {
        while (_next <= until)
        {
            var isDouble = !_entryPending && _random.NextDouble() < _timings.DoubleBlinkChance;
            _entryPending = false;
            _events.Add(new BlinkEvent(_next, isDouble));
            _next += NextInterval();
        }
    }

    private TimeSpan NextInterval()
    {
        var minTicks = TimeSpan.FromMilliseconds(_timings.BlinkEveryMinMs).Ticks;
        var rangeTicks = TimeSpan.FromMilliseconds(_timings.BlinkEveryMaxMs).Ticks - minTicks;
        return TimeSpan.FromTicks(minTicks + (long)(_random.NextDouble() * rangeTicks));
    }
}

/// <summary>Placement of the notch on a display. Screen bounds and results are physical px; spec sizes are DIP × scale.</summary>
public static class NotchLayout
{
    /// <summary>Notch rectangle for a state: spec size × scale, horizontally centred on the screen, top edge at screen top.</summary>
    public static RectD Bounds(NotchFaceSpec face, NotchFaceState state, RectD screenPixelBounds, double scale)
    {
        ArgumentNullException.ThrowIfNull(face);
        ValidateScale(scale);
        var size = face.SizeFor(state);
        return Centred(size, screenPixelBounds, scale);
    }

    /// <summary>Invisible hover strip (spec hoverStrip × scale) centred at the screen top.</summary>
    public static RectD HoverStripBounds(NotchSpec spec, RectD screenPixelBounds, double scale)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ValidateScale(scale);
        return Centred(spec.HoverStrip, screenPixelBounds, scale);
    }

    private static RectD Centred(SizeD size, RectD screen, double scale)
    {
        var width = size.Width * scale;
        var height = size.Height * scale;
        return new RectD(screen.X + screen.Width / 2 - width / 2, screen.Y, width, height);
    }

    private static void ValidateScale(double scale)
    {
        if (!double.IsFinite(scale) || scale <= 0)
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "Scale must be a finite number greater than 0.");
    }
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
    private enum Mode { Hidden, Showing, Leaving }

    private readonly NotchTimings _timings;
    private Mode _mode = Mode.Hidden;
    private TimeSpan _revealOrigin; // time at which progress was (virtually) 0 for the current reveal
    private TimeSpan _leaveTime;
    private bool _inside;
    private bool _pinned;

    public NotchRevealTimeline(NotchTimings timings)
    {
        ArgumentNullException.ThrowIfNull(timings);
        _timings = timings;
    }

    public void PointerEntered(TimeSpan now)
    {
        _inside = true;
        Show(now);
    }

    public void PointerLeft(TimeSpan now)
    {
        _inside = false;
        if (!_pinned && _mode == Mode.Showing)
        {
            _mode = Mode.Leaving;
            _leaveTime = now;
        }
    }

    public void SetPinned(bool pinned, TimeSpan now)
    {
        if (pinned == _pinned) return;
        _pinned = pinned;
        if (pinned)
        {
            Show(now);
        }
        else if (!_inside && _mode == Mode.Showing)
        {
            _mode = Mode.Leaving;
            _leaveTime = now;
        }
    }

    public RevealPhase PhaseAt(TimeSpan now)
    {
        switch (_mode)
        {
            case Mode.Showing:
                return RevealFraction(now) < 1 ? RevealPhase.Revealing : RevealPhase.Revealed;
            case Mode.Leaving:
                if (RevealFraction(now) < 1) return RevealPhase.Revealing;
                var retractStart = RetractStart();
                if (now < retractStart) return RevealPhase.RetractPending;
                if (now < retractStart + Ms(_timings.RetractMs)) return RevealPhase.Retracting;
                return RevealPhase.Hidden;
            default:
                return RevealPhase.Hidden;
        }
    }

    public double ProgressAt(TimeSpan now)
    {
        switch (_mode)
        {
            case Mode.Showing:
                return RevealFraction(now);
            case Mode.Leaving:
                var reveal = RevealFraction(now);
                if (reveal < 1) return reveal;
                var retractStart = RetractStart();
                if (now < retractStart) return 1;
                return Math.Clamp(1 - (now - retractStart).TotalMilliseconds / _timings.RetractMs, 0, 1);
            default:
                return 0;
        }
    }

    private void Show(TimeSpan now)
    {
        switch (_mode)
        {
            case Mode.Hidden:
                _revealOrigin = now;
                break;
            case Mode.Leaving:
                var phase = PhaseAt(now);
                if (phase == RevealPhase.Hidden)
                    _revealOrigin = now;
                else if (phase == RevealPhase.Retracting)
                    _revealOrigin = now - TimeSpan.FromMilliseconds(ProgressAt(now) * _timings.RevealMs);
                // Revealing / RetractPending: keep the current reveal (cancel the retract).
                break;
        }
        _mode = Mode.Showing;
    }

    private double RevealFraction(TimeSpan now)
        => Math.Clamp((now - _revealOrigin).TotalMilliseconds / _timings.RevealMs, 0, 1);

    private TimeSpan RetractStart()
    {
        var revealEnd = _revealOrigin + Ms(_timings.RevealMs);
        var from = _leaveTime > revealEnd ? _leaveTime : revealEnd;
        return from + Ms(_timings.RetractDelayMs);
    }

    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);
}
