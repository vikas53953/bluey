namespace Boo.Core;

/// <summary>Implementation behind <see cref="FaceMotion"/>, <see cref="FaceMoods"/>, <see cref="FaceIndicators"/> and <see cref="NotchRowLayout"/>.</summary>
internal sealed class FaceMotionImpl
{
    private static readonly CubicBezierEase CssEase = new(0.25, 0.1, 0.25, 1);
    private static readonly CubicBezierEase CssEaseInOut = new(0.42, 0, 0.58, 1);
    private static readonly CubicBezierEase ShakeEase = new(0.36, 0.07, 0.19, 0.97);
    private const double ThinkingCycleMs = 1200;
    private const double DotStaggerMs = 200;
    private const double RecordPulseMs = 2000;
    private const double ShakeMs = 320;
    private const double ShakeDip = 3;
    private const double BlinkFloor = 0.08;
    private const double OpenFloor = 0.06;
    private const double MoveEpsilon = 0.5;

    private readonly NotchSpec _spec;
    private readonly NotchEyeSpec _eye;
    private readonly Random _random;
    private readonly GazeSpring _gaze;
    private BlinkScheduler _blink;
    private TimeSpan _blinkEpoch;
    private TimeSpan? _last;
    private PointD? _lastPointer;
    private TimeSpan _pointerMovedAt;
    private PointD _wander;
    private TimeSpan _nextSaccade;
    private double _talk;

    public FaceMotionImpl(NotchSpec spec, Random random)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(random);
        _spec = spec;
        _eye = spec.Face.Eye;
        _random = random;
        _gaze = new GazeSpring(spec.Timings.GazeStiffness, spec.Timings.GazeDamping);
        _blink = NewBlink(null);
        State = NotchFaceState.Hidden;
    }

    public bool ReducedMotion { get; set; }

    public NotchFaceState State { get; private set; }

    public void SetState(NotchFaceState state, TimeSpan now)
    {
        if (State == NotchFaceState.Hidden && state != NotchFaceState.Hidden)
        {
            // Fresh timeline per appearance, opening with the spec entry blink (BlinkScheduler entryBlinkDelayMs).
            _blink = NewBlink(_spec.Motion.EntryBlinkDelayMs);
            _blinkEpoch = now;
        }
        State = state;
    }

    public FaceFrame Tick(TimeSpan now, PointD? pointer)
    {
        var dt = _last is { } last && now > last ? now - last : TimeSpan.Zero;
        if (dt > TimeSpan.FromMilliseconds(100)) dt = TimeSpan.FromMilliseconds(100);
        if (_last is null) _pointerMovedAt = now;
        _last = now;
        var ms = now.TotalMilliseconds;

        // Pointer movement (for idle wander).
        if (pointer is { } p && double.IsFinite(p.X) && double.IsFinite(p.Y))
        {
            if (_lastPointer is not { } lp || Math.Abs(lp.X - p.X) > MoveEpsilon || Math.Abs(lp.Y - p.Y) > MoveEpsilon)
            {
                _pointerMovedAt = now;
                _lastPointer = p;
            }
        }
        else
        {
            pointer = null;
        }

        var mood = Mood(State, ms);

        // Talking amplitude (eases in and out).
        var want = State == NotchFaceState.Responding ? Math.Max(0, 0.5 + 0.5 * Math.Sin(ms / 52) * Math.Sin(ms / 137)) : 0;
        _talk += (want - _talk) * Math.Min(1, dt.TotalSeconds * 25);
        _talk = Math.Clamp(_talk, 0, 1);

        // Gaze target.
        PointD target;
        if (mood.LookAt is { } look)
        {
            target = look;
        }
        else
        {
            var stillFor = (now - _pointerMovedAt).TotalMilliseconds;
            if (mood.FollowPointer && pointer is { } q && stillFor <= _spec.Motion.IdleWanderAfterMs)
            {
                target = new PointD(
                    Math.Clamp(q.X / FaceMotionTimings.FollowReachX, -1, 1),
                    Math.Clamp(q.Y / FaceMotionTimings.FollowReachY, -1, 1));
            }
            else if (ReducedMotion)
            {
                target = new PointD(0, 0);
            }
            else
            {
                if (now >= _nextSaccade)
                {
                    _wander = new PointD(_random.NextDouble() * 1.8 - 0.9, _random.NextDouble() * 1.3 - 0.9);
                    _nextSaccade = now + TimeSpan.FromMilliseconds(_spec.Motion.SaccadeEveryMinMs
                        + _random.NextDouble() * (_spec.Motion.SaccadeEveryMaxMs - _spec.Motion.SaccadeEveryMinMs));
                }
                target = _wander;
            }
        }

        _gaze.Target = target;
        if (ReducedMotion) _gaze.SnapTo(target);
        else _gaze.Step(dt);
        var gaze = _gaze.Position;

        // Blink.
        var blink = 1d;
        if (mood.Blinks)
        {
            var b = _blink.OpennessAt(now - _blinkEpoch);
            blink = BlinkFloor + (1 - BlinkFloor) * b;
        }
        var open = mood.Open + (State == NotchFaceState.Responding ? _talk * 0.12 : 0);
        var openness = Math.Max(OpenFloor, open * blink);

        // Bounce.
        var bounce = 0d;
        if (!ReducedMotion)
        {
            if (mood.Happy) bounce = HappyBounce(ms);
            else if (State == NotchFaceState.Responding) bounce = _talk * 1.6;
        }

        PointD eyeShift, pupil;
        if (mood.Happy)
        {
            eyeShift = new PointD(0, -bounce);
            pupil = new PointD(0, 0);
        }
        else if (_eye.Pupil > 0)
        {
            eyeShift = new PointD(gaze.X, gaze.Y - bounce);
            pupil = new PointD(gaze.X * _eye.GazeX, gaze.Y * _eye.GazeY);
        }
        else
        {
            eyeShift = new PointD(gaze.X * _eye.GazeX, gaze.Y * _eye.GazeY - bounce);
            pupil = new PointD(0, 0);
        }

        return new FaceFrame(gaze, eyeShift, pupil, mood.PupilScale, openness, bounce, mood.EyeOpacity, mood.Happy, _talk);
    }

    private BlinkScheduler NewBlink(int? entryBlinkDelayMs)
        => new(_spec.Timings, _random, reducedMotion: false, FaceMotionTimings.DoubleBlinkGapMs, entryBlinkDelayMs);

    // ------------------------------------------------------------ moods

    public static FaceMood Mood(NotchFaceState state, double nowMs) => state switch
    {
        NotchFaceState.Listening => new FaceMood(1.14, 1.15, null, true, false, true, false, 1),
        NotchFaceState.Thinking => new FaceMood(0.74, 0.9, new PointD(0.85 + 0.08 * Math.Sin(nowMs / 700), -0.9), false, false, true, false, 1),
        NotchFaceState.Responding => new FaceMood(0.96, 1.05, null, false, true, true, false, 1),
        NotchFaceState.Nudge => new FaceMood(1.2, 1.25, new PointD(-0.7, 0.85), false, false, true, false, 1),
        NotchFaceState.Paused => new FaceMood(0.12, 1, new PointD(0, 0.6), false, false, false, false, 0.72),
        NotchFaceState.Happy => new FaceMood(1, 1, new PointD(0, 0), false, false, false, true, 1),
        _ => new FaceMood(1, 1, null, true, false, true, false, 1),
    };

    // ------------------------------------------------------------ indicators

    public static double ListeningBar(int i, double nowMs)
        => 3 + 12 * (0.35 + 0.65 * Math.Abs(Math.Sin(nowMs / (140 + 37 * i) + i)));

    public static double TalkingBar(int i, double nowMs, double level)
        => 3 + 12 * Math.Clamp(level, 0, 1) * (0.4 + 0.6 * Math.Abs(Math.Sin(nowMs / 90 + 1.3 * i)));

    public static (double, double) Dot(int i, double sinceMs)
    {
        var local = sinceMs - DotStaggerMs * i;
        if (local < 0 || !double.IsFinite(local)) return (0.25, 0);
        var p = (local % ThinkingCycleMs) / ThinkingCycleMs;
        double k;
        if (p < 0.3) k = CssEase.Evaluate(p / 0.3);
        else if (p < 0.6) k = 1 - CssEase.Evaluate((p - 0.3) / 0.3);
        else k = 0;
        return (0.25 + 0.75 * k, -2 * k);
    }

    public static (double, double) Ring(double sinceMs)
    {
        if (!double.IsFinite(sinceMs) || sinceMs < 0) sinceMs = 0;
        var p = (sinceMs % RecordPulseMs) / RecordPulseMs;
        var k = p < 0.5 ? CssEaseInOut.Evaluate(p / 0.5) : 1 - CssEaseInOut.Evaluate((p - 0.5) / 0.5);
        return (3 - 2 * k, 0.28 - 0.18 * k);
    }

    public static double Shake(double sinceMs)
    {
        if (!double.IsFinite(sinceMs) || sinceMs < 0 || sinceMs >= 2 * ShakeMs) return 0;
        var p = (sinceMs % ShakeMs) / ShakeMs;
        if (p < 0.25) return -ShakeDip * ShakeEase.Evaluate(p / 0.25);
        if (p < 0.75) return -ShakeDip + 2 * ShakeDip * ShakeEase.Evaluate((p - 0.25) / 0.5);
        return ShakeDip * (1 - ShakeEase.Evaluate((p - 0.75) / 0.25));
    }

    public static double HappyBounce(double nowMs) => Math.Max(0, Math.Sin(nowMs / 180)) * 1.5;

    // ------------------------------------------------------------ row layout

    public static (double, double) SlotWidths(NotchFaceState state) => state switch
    {
        NotchFaceState.Capture => (7, 18),
        NotchFaceState.Listening or NotchFaceState.Responding => (0, FaceIndicators.BarCount * FaceIndicators.BarWidth + (FaceIndicators.BarCount - 1) * FaceIndicators.BarGap),
        NotchFaceState.Thinking => (0, FaceIndicators.DotCount * FaceIndicators.DotSize + (FaceIndicators.DotCount - 1) * FaceIndicators.DotGap),
        NotchFaceState.Paused => (0, 9),
        _ => (0, 0),
    };

    public static NotchRowParts PlaceRow(NotchFaceSpec face, SizeD body, double lead, double trail)
    {
        ArgumentNullException.ThrowIfNull(face);
        lead = Math.Max(0, lead);
        trail = Math.Max(0, trail);
        var row = face.SizeFor(NotchFaceState.Hover).Height;
        var eye = face.Eye;
        var eyes = 2 * eye.Width + eye.Gap;
        var cy = row / 2;
        var caption = new RectD(NotchRowLayout.CaptionInsetX, row + NotchRowLayout.CaptionTop,
            Math.Max(0, body.Width - 2 * NotchRowLayout.CaptionInsetX), Math.Max(0, body.Height - row - NotchRowLayout.CaptionTop));

        if (face.Shape.Grow == NotchGrowDirection.Down || face.Shape.Centered)
        {
            var x0 = body.Width / 2 - eyes / 2;
            return new NotchRowParts(
                new PointD(x0 + eye.Width / 2, cy),
                new PointD(x0 + eyes - eye.Width / 2, cy),
                new RectD(NotchRowLayout.Padding, 0, 0, row),
                new RectD(body.Width / 2 - trail / 2, row, trail, Math.Max(0, body.Height - row - 6)),
                caption, row);
        }

        var pad = NotchRowLayout.Padding;
        var gap = NotchRowLayout.Gap;
        var free = body.Width - 2 * pad - lead - eyes - trail - 2 * gap;
        double eyesX, trailX;
        if (free >= 0)
        {
            eyesX = pad + lead + gap + free / 2;
            trailX = body.Width - pad - trail;
        }
        else
        {
            eyesX = pad + lead + gap;
            trailX = eyesX + eyes + gap;
        }
        return new NotchRowParts(
            new PointD(eyesX + eye.Width / 2, cy),
            new PointD(eyesX + eyes - eye.Width / 2, cy),
            new RectD(pad, 0, lead, row),
            new RectD(trailX, 0, trail, row),
            caption, row);
    }
}
