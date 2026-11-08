using System.Globalization;

namespace Boo.Core;

/// <summary>Spec timings.nudgeShake ("2 × ±3 px, 320 ms each"): Count sine cycles of ±AmplitudeDip, EachMs per cycle.</summary>
public sealed record NudgeShakeSpec(int Count, double AmplitudeDip, int EachMs);

/// <summary>
/// The remaining spec "timings" (milliseconds) that <see cref="NotchTimings"/> does not carry. Each key is
/// optional in the JSON (missing = shipped value) but must be valid when present.
/// </summary>
public sealed record NotchMotionTimings(
    int CaptionFadeMs,
    int CaptionDelayMs,
    int EntryBlinkDelayMs,
    int PeekRiseMs,
    int PeekRiseDelayMs,
    int IdleWanderAfterMs,
    int SaccadeEveryMinMs,
    int SaccadeEveryMaxMs,
    int ThinkingDotCycleMs,
    int RecordPulseMs,
    int DrawStrokeMs,
    int AppOpenMs,
    NudgeShakeSpec NudgeShake)
{
    /// <summary>Values of the shipped docs/design/boo-notch-spec.json.</summary>
    public static NotchMotionTimings Shipped { get; } = new(
        CaptionFadeMs: 160, CaptionDelayMs: 120, EntryBlinkDelayMs: 120,
        PeekRiseMs: 240, PeekRiseDelayMs: 90, IdleWanderAfterMs: 2000,
        SaccadeEveryMinMs: 600, SaccadeEveryMaxMs: 2200,
        ThinkingDotCycleMs: 1200, RecordPulseMs: 2000, DrawStrokeMs: 620, AppOpenMs: 280,
        NudgeShake: new NudgeShakeSpec(2, 3, 320));
}

/// <summary>
/// Typewriter caption for the notch bubble (nudge / reply): nothing before DelayMs, then one text element
/// (never half a surrogate pair) every CharMs; opacity fades 0→1 linearly over FadeMs starting at DelayMs.
/// Reduced motion shows the whole text, fully opaque, immediately.
/// </summary>
public sealed class CaptionReveal
{
    public CaptionReveal(int charMs, int delayMs, int fadeMs)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(charMs);
        ArgumentOutOfRangeException.ThrowIfNegative(delayMs);
        ArgumentOutOfRangeException.ThrowIfNegative(fadeMs);
        CharMs = charMs;
        DelayMs = delayMs;
        FadeMs = fadeMs;
    }

    /// <summary>timings.captionCharMs, captionDelayMs and captionFadeMs of the spec.</summary>
    public static CaptionReveal FromSpec(NotchSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return new CaptionReveal(spec.Timings.CaptionCharMs, spec.Motion.CaptionDelayMs, spec.Motion.CaptionFadeMs);
    }

    public int CharMs { get; }
    public int DelayMs { get; }
    public int FadeMs { get; }

    /// <summary>Prefix of <paramref name="text"/> visible <paramref name="elapsed"/> after the caption was shown.</summary>
    public string VisibleText(string text, TimeSpan elapsed, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (reducedMotion) return text;
        var since = elapsed.TotalMilliseconds - DelayMs;
        if (double.IsNaN(since) || since < 0) return string.Empty;

        var elements = StringInfo.ParseCombiningCharacters(text);
        var count = (int)Math.Min(elements.Length, Math.Floor(since / CharMs));
        return count >= elements.Length ? text : text[..elements[count]];
    }

    /// <summary>Caption opacity in [0,1].</summary>
    public double OpacityAt(TimeSpan elapsed, bool reducedMotion = false)
    {
        if (reducedMotion) return 1;
        var since = elapsed.TotalMilliseconds - DelayMs;
        if (double.IsNaN(since) || since < 0) return 0;
        if (FadeMs == 0) return 1;
        return Math.Clamp(since / FadeMs, 0, 1);
    }

    /// <summary>Time until the whole text is visible.</summary>
    public TimeSpan Duration(string text, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (reducedMotion) return TimeSpan.Zero;
        var elements = new StringInfo(text).LengthInTextElements;
        return TimeSpan.FromMilliseconds(DelayMs + (double)elements * CharMs);
    }
}

/// <summary>Horizontal nudge shake: AmplitudeDip·sin(2π t / EachMs) for Count cycles, then rest. Reduced motion: none.</summary>
public static class NudgeShake
{
    public static double OffsetAt(NudgeShakeSpec spec, TimeSpan elapsed, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(spec);
        var ms = elapsed.TotalMilliseconds;
        if (reducedMotion || double.IsNaN(ms) || ms < 0 || ms >= (double)spec.Count * spec.EachMs) return 0;
        return spec.AmplitudeDip * Math.Sin(2 * Math.PI * ms / spec.EachMs);
    }

    public static TimeSpan Duration(NudgeShakeSpec spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return TimeSpan.FromMilliseconds((double)spec.Count * spec.EachMs);
    }
}

/// <summary>
/// Idle eye wander (spec idleWanderAfterMs / saccadeEveryMs). Once the pointer has rested (moved less than
/// <see cref="MovementThresholdPx"/>) for IdleWanderAfterMs, <see cref="TargetAt"/> returns saccade targets that
/// jump to a new random point every [SaccadeEveryMinMs, SaccadeEveryMaxMs] and hold in between; the point lies
/// inside <see cref="ReachFraction"/> of the gaze ellipse. Null = follow the pointer. Times must be non-decreasing.
/// Reduced motion never wanders. Deterministic for an injected Random.
/// </summary>
public sealed class GazeWander
{
    public const double MovementThresholdPx = 1;
    public const double ReachFraction = 0.7;

    private readonly NotchMotionTimings _motion;
    private readonly Random _random;
    private PointD? _lastPointer;
    private TimeSpan? _restingSince;
    private TimeSpan _nextSaccade;
    private (double Angle, double Radius)? _current;

    public GazeWander(NotchMotionTimings motion, Random random, bool reducedMotion = false)
    {
        ArgumentNullException.ThrowIfNull(motion);
        ArgumentNullException.ThrowIfNull(random);
        _motion = motion;
        _random = random;
        ReducedMotion = reducedMotion;
    }

    public bool ReducedMotion { get; }

    /// <summary>Feed the current pointer position (physical px). Movement resets the idle clock.</summary>
    public void ObservePointer(PointD pointer, TimeSpan now)
    {
        if (!double.IsFinite(pointer.X) || !double.IsFinite(pointer.Y)) return;
        if (_lastPointer is { } last)
        {
            var dx = pointer.X - last.X;
            var dy = pointer.Y - last.Y;
            if (dx * dx + dy * dy < MovementThresholdPx * MovementThresholdPx)
            {
                _restingSince ??= now;
                return;
            }
        }
        _lastPointer = pointer;
        _restingSince = now;
        _current = null;
    }

    /// <summary>Wander offset (DIP, inside the (gazeX, gazeY) ellipse) or null while the eyes should follow the pointer.</summary>
    public PointD? TargetAt(TimeSpan now, double gazeX, double gazeY)
    {
        if (ReducedMotion) return null;
        _restingSince ??= now;
        var wanderStart = _restingSince.Value + TimeSpan.FromMilliseconds(_motion.IdleWanderAfterMs);
        if (now < wanderStart) return null;

        if (_current is null)
        {
            _current = NextPoint();
            _nextSaccade = wanderStart + NextInterval();
        }
        while (now >= _nextSaccade)
        {
            _current = NextPoint();
            _nextSaccade += NextInterval();
        }

        var (angle, radius) = _current.Value;
        var gx = double.IsFinite(gazeX) && gazeX > 0 ? gazeX : 0;
        var gy = double.IsFinite(gazeY) && gazeY > 0 ? gazeY : 0;
        return new PointD(Math.Cos(angle) * radius * gx, Math.Sin(angle) * radius * gy);
    }

    private (double, double) NextPoint()
        => (_random.NextDouble() * 2 * Math.PI, Math.Sqrt(_random.NextDouble()) * ReachFraction);

    private TimeSpan NextInterval()
    {
        var min = _motion.SaccadeEveryMinMs;
        var range = _motion.SaccadeEveryMaxMs - min;
        return TimeSpan.FromMilliseconds(min + _random.NextDouble() * range);
    }
}
