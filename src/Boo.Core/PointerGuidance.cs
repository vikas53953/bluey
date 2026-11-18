using System.Globalization;
using System.Text;

namespace Boo.Core;

// Pointer lane (plan U3 pointer + drawing, U6 nudge pointer).
// Frozen API surface written by the test author; the builder implements the
// members (now implemented below). All coordinates are
// physical (virtual-screen) pixels unless a name says Dip.

/// <summary>A point in physical virtual-screen pixels.</summary>

/// <summary>One sampled pointer position, <paramref name="TimeMs"/> after the flight starts.</summary>
public readonly record struct PointerFrame(double TimeMs, PointD Position);

/// <summary>
/// A deterministic flight of Boo's blue pointer from <see cref="Start"/> to <see cref="Anchor"/>
/// followed by a ring stroke of <see cref="RingStrokeMs"/>.
/// </summary>
public sealed record PointerFlightPlan(
    PointD Start,
    PointD Anchor,
    RectD Target,
    string DisplayId,
    double DurationMs,
    IReadOnlyList<PointerFrame> Frames,
    double RingStrokeMs)
{
    /// <summary>
    /// Pointer position at <paramref name="elapsedMs"/> after the flight starts, using the same
    /// ease as <see cref="Frames"/>. Before 0 it is <see cref="Start"/>; at or after
    /// <see cref="DurationMs"/> it is exactly <see cref="Anchor"/>.
    /// </summary>
    public PointD PositionAt(double elapsedMs)
    {
        if (double.IsNaN(elapsedMs) || elapsedMs <= 0)
        {
            return Start;
        }

        if (elapsedMs >= DurationMs)
        {
            return Anchor;
        }

        var eased = PointerFlightPlanner.Ease(elapsedMs / DurationMs);
        return new PointD(
            Start.X + (Anchor.X - Start.X) * eased,
            Start.Y + (Anchor.Y - Start.Y) * eased);
    }
}

/// <summary>
/// Plans the blue pointer flight (Riley's cursor) that precedes the HeyClicky ring.
/// Contract:
/// <list type="bullet">
/// <item>The target is first intersected with the display's physical bounds. An empty, non-finite
/// or fully off-display target returns null: no flight and no guessed mark (R3).</item>
/// <item>Anchor rule (one formula): on each axis the anchor is the start coordinate clamped to
/// [min + inset, max - inset] of the visible target, where inset = min(extent / 2, WideTargetInsetDip * scale).
/// So targets up to 480 DIP on an axis land on their centre; wider targets land on the nearest
/// point to the start that is still at least 240 DIP inside the target's edges.</item>
/// <item>The start (current cursor, possibly on another monitor) is clamped into the display.
/// Every frame satisfies Left &lt;= X &lt;= Right - 1 and Top &lt;= Y &lt;= Bottom - 1.</item>
/// <item>Duration = clamp(MinimumDurationMs + MsPerDip * distanceDip, MinimumDurationMs, MaximumDurationMs),
/// where distanceDip = physical distance (clamped start to anchor) / display scale.</item>
/// <item>A distance under 0.5 px is zero distance: one frame at time 0 on the anchor, duration 0.</item>
/// <item>Frames start at time 0 on the clamped start, step by frameIntervalMs, and the last frame is
/// at exactly DurationMs on exactly the anchor. Position = start + (anchor - start) * Ease(t / duration).</item>
/// </list>
/// </summary>
public static class PointerFlightPlanner
{
    public const double MinimumDurationMs = 280;
    public const double MaximumDurationMs = 620;
    public const double MsPerDip = 0.25;
    public const double WideTargetInsetDip = 240;
    public const double ZeroDistancePx = 0.5;
    /// <summary>docs/design/boo-notch-spec.json timings.drawStrokeMs.</summary>
    public const double DefaultRingStrokeMs = 620;
    public const double DefaultFrameIntervalMs = 1000d / 60d;

    /// <summary>Plans a flight, or returns null when there is no valid on-display target.</summary>
    /// <exception cref="ArgumentException">The display or start point is not finite/positive.</exception>
    /// <exception cref="ArgumentOutOfRangeException">frameIntervalMs or ringStrokeMs is not finite and positive.</exception>
    public static PointerFlightPlan? Plan(
        PointD start,
        RectD target,
        DisplayInfo display,
        double frameIntervalMs = DefaultFrameIntervalMs,
        double ringStrokeMs = DefaultRingStrokeMs)
    {
        ArgumentNullException.ThrowIfNull(display);
        var bounds = display.PixelBounds;
        if (!IsFinitePositive(display.Scale)
            || !double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !IsFinitePositive(bounds.Width) || !IsFinitePositive(bounds.Height))
        {
            throw new ArgumentException("The display bounds and scale must be finite and positive.", nameof(display));
        }

        if (!double.IsFinite(start.X) || !double.IsFinite(start.Y))
        {
            throw new ArgumentException("The start point must be finite.", nameof(start));
        }

        if (!IsFinitePositive(frameIntervalMs))
        {
            throw new ArgumentOutOfRangeException(nameof(frameIntervalMs), frameIntervalMs, "The frame interval must be finite and positive.");
        }

        if (!IsFinitePositive(ringStrokeMs))
        {
            throw new ArgumentOutOfRangeException(nameof(ringStrokeMs), ringStrokeMs, "The ring stroke time must be finite and positive.");
        }

        var visible = VisiblePart(target, bounds);
        if (visible is null)
        {
            return null;
        }

        var right = bounds.X + bounds.Width - 1;
        var bottom = bounds.Y + bounds.Height - 1;
        var clampedStart = new PointD(Clamp(start.X, bounds.X, right), Clamp(start.Y, bounds.Y, bottom));
        var rawAnchor = Anchor(clampedStart, visible.Value, display.Scale);
        var anchor = new PointD(Clamp(rawAnchor.X, bounds.X, right), Clamp(rawAnchor.Y, bounds.Y, bottom));

        var distance = Math.Sqrt(Square(anchor.X - clampedStart.X) + Square(anchor.Y - clampedStart.Y));
        if (distance < ZeroDistancePx)
        {
            return new PointerFlightPlan(clampedStart, anchor, visible.Value, display.Id, 0,
                new[] { new PointerFrame(0, anchor) }, ringStrokeMs);
        }

        var duration = DurationFor(distance, display.Scale);
        var plan = new PointerFlightPlan(clampedStart, anchor, visible.Value, display.Id, duration,
            Array.Empty<PointerFrame>(), ringStrokeMs);
        var frames = new List<PointerFrame>();
        for (var i = 0; ; i++)
        {
            var time = i * frameIntervalMs;
            if (time >= duration)
            {
                break;
            }

            frames.Add(new PointerFrame(time, plan.PositionAt(time)));
        }

        frames.Add(new PointerFrame(duration, anchor));
        return plan with { Frames = frames };
    }

    /// <summary>Anchor for a visible target (see the class contract), in physical pixels.</summary>
    public static PointD Anchor(PointD start, RectD visibleTarget, double scale)
    {
        var insetLimit = WideTargetInsetDip * scale;
        return new PointD(
            AnchorAxis(start.X, visibleTarget.X, visibleTarget.Width, insetLimit),
            AnchorAxis(start.Y, visibleTarget.Y, visibleTarget.Height, insetLimit));
    }

    /// <summary>Flight duration for a physical distance on a display with the given scale.</summary>
    public static double DurationFor(double distancePx, double scale)
    {
        var distanceDip = Math.Max(0, distancePx) / scale;
        return Clamp(MinimumDurationMs + MsPerDip * distanceDip, MinimumDurationMs, MaximumDurationMs);
    }

    /// <summary>Cubic ease-in-out: t &lt; 0.5 ? 4t^3 : 1 - (-2t + 2)^3 / 2, input clamped to [0, 1].</summary>
    public static double Ease(double t)
    {
        t = double.IsNaN(t) ? 0 : Clamp(t, 0, 1);
        if (t < 0.5)
        {
            return 4 * t * t * t;
        }

        var u = -2 * t + 2;
        return 1 - u * u * u / 2;
    }

    private static double AnchorAxis(double start, double min, double extent, double insetLimit)
    {
        var inset = Math.Min(extent / 2, insetLimit);
        return Clamp(start, min + inset, min + extent - inset);
    }

    private static RectD? VisiblePart(RectD target, RectD bounds)
    {
        if (!double.IsFinite(target.X) || !double.IsFinite(target.Y)
            || !IsFinitePositive(target.Width) || !IsFinitePositive(target.Height))
        {
            return null;
        }

        var left = Math.Max(target.X, bounds.X);
        var top = Math.Max(target.Y, bounds.Y);
        var right = Math.Min(target.X + target.Width, bounds.X + bounds.Width);
        var bottom = Math.Min(target.Y + target.Height, bounds.Y + bounds.Height);
        if (right <= left || bottom <= top)
        {
            return null;
        }

        return new RectD(left, top, right - left, bottom - top);
    }

    private static bool IsFinitePositive(double value) => double.IsFinite(value) && value > 0;

    private static double Square(double value) => value * value;

    private static double Clamp(double value, double min, double max) => value < min ? min : value > max ? max : value;
}

public enum PointerPhase { Idle, Fly, Land, DrawRing, Hold, Clear }

public enum PointerClearReason { Stop, TargetChanged, Scroll, Resize, ForegroundChanged, DisplayChanged, Completed }

/// <summary>Phase lengths after landing. HoldMs may be PositiveInfinity (hold until cleared).</summary>
public sealed record PointerSequenceTimings(double LandMs = 120, double DrawStrokeMs = PointerFlightPlanner.DefaultRingStrokeMs, double HoldMs = 8000);

/// <summary>What the overlay should show at one instant. RingProgress is 0 in Fly/Land, rises linearly
/// (elapsed in DrawRing / DrawStrokeMs) during DrawRing, and is 1 in Hold. In Fly the pointer is plan.PositionAt(elapsed);
/// in Land/DrawRing/Hold it is plan.Anchor. Target is plan.Target.</summary>
public sealed record PointerRenderFrame(long Generation, PointerPhase Phase, PointD Pointer, double RingProgress, RectD Target);

/// <summary>
/// Sequences one pointer annotation: Fly -> Land -> DrawRing -> Hold -> Clear, driven by an
/// injected clock (nowMs). Contract:
/// <list type="bullet">
/// <item>Begin(generation, ...) uses the RequestCoordinator generation. It is rejected (false, current
/// sequence untouched) when generation is not greater than every generation already begun or cleared.</item>
/// <item>A newer Begin while a sequence is active clears the old one immediately (reason TargetChanged).</item>
/// <item>Clear(reason) ends the active sequence immediately and returns true if something was showing;
/// afterwards Tick returns null and that generation can never begin again.</item>
/// <item>Phase windows relative to Begin's nowMs: Fly [0, D), Land [D, D+L), DrawRing [D+L, D+L+R),
/// Hold [D+L+R, D+L+R+H), then one Tick returns a Clear frame (reason Completed) and later Ticks return null.
/// D = plan.DurationMs (0 skips Fly), L/R/H from <see cref="PointerSequenceTimings"/>; R uses timings.DrawStrokeMs.</item>
/// <item>Tick with no active sequence returns null. Begin with a null plan throws ArgumentNullException;
/// negative or non-finite timings (HoldMs may be PositiveInfinity) throw ArgumentOutOfRangeException.</item>
/// </list>
/// </summary>
public sealed class PointerAnnotationSequencer
{
    private readonly PointerSequenceTimings _timings;
    private long? _highestGeneration;
    private PointerFlightPlan? _plan;
    private long _generation;
    private double _startMs;

    public PointerAnnotationSequencer(PointerSequenceTimings? timings = null)
    {
        _timings = timings ?? new PointerSequenceTimings();
        RequireFiniteNonNegative(_timings.LandMs, nameof(PointerSequenceTimings.LandMs));
        RequireFiniteNonNegative(_timings.DrawStrokeMs, nameof(PointerSequenceTimings.DrawStrokeMs));
        if (double.IsNaN(_timings.HoldMs) || _timings.HoldMs < 0 || double.IsNegativeInfinity(_timings.HoldMs))
        {
            throw new ArgumentOutOfRangeException(nameof(timings), _timings.HoldMs, "HoldMs must be non-negative (PositiveInfinity holds until cleared).");
        }
    }

    public PointerPhase Phase { get; private set; } = PointerPhase.Idle;

    /// <summary>Generation of the active sequence, or null when nothing is showing.</summary>
    public long? ActiveGeneration => _plan is null ? null : _generation;

    public PointerClearReason? LastClearReason { get; private set; }

    public bool Begin(long generation, PointerFlightPlan plan, double nowMs)
    {
        ArgumentNullException.ThrowIfNull(plan);
        if (_highestGeneration is { } highest && generation <= highest)
        {
            return false;
        }

        if (_plan is not null)
        {
            Clear(PointerClearReason.TargetChanged);
        }

        _highestGeneration = generation;
        _generation = generation;
        _plan = plan;
        _startMs = nowMs;
        Phase = plan.DurationMs > 0 ? PointerPhase.Fly : PointerPhase.Land;
        return true;
    }

    public PointerRenderFrame? Tick(double nowMs)
    {
        var plan = _plan;
        if (plan is null)
        {
            return null;
        }

        var elapsed = Math.Max(0, nowMs - _startMs);
        var flyEnd = Math.Max(0, plan.DurationMs);
        var landEnd = flyEnd + _timings.LandMs;
        var drawEnd = landEnd + _timings.DrawStrokeMs;
        var holdEnd = drawEnd + _timings.HoldMs;

        PointerRenderFrame Frame(PointerPhase phase, PointD pointer, double progress)
        {
            Phase = phase;
            return new PointerRenderFrame(_generation, phase, pointer, progress, plan.Target);
        }

        if (elapsed < flyEnd)
        {
            return Frame(PointerPhase.Fly, plan.PositionAt(elapsed), 0);
        }

        if (elapsed < landEnd)
        {
            return Frame(PointerPhase.Land, plan.Anchor, 0);
        }

        if (elapsed < drawEnd)
        {
            var progress = _timings.DrawStrokeMs > 0 ? (elapsed - landEnd) / _timings.DrawStrokeMs : 1;
            return Frame(PointerPhase.DrawRing, plan.Anchor, Math.Clamp(progress, 0, 1));
        }

        if (elapsed < holdEnd)
        {
            return Frame(PointerPhase.Hold, plan.Anchor, 1);
        }

        var clear = new PointerRenderFrame(_generation, PointerPhase.Clear, plan.Anchor, 1, plan.Target);
        End(PointerClearReason.Completed);
        return clear;
    }

    public bool Clear(PointerClearReason reason)
    {
        if (_plan is null)
        {
            return false;
        }

        End(reason);
        return true;
    }

    private void End(PointerClearReason reason)
    {
        _plan = null;
        Phase = PointerPhase.Idle;
        LastClearReason = reason;
    }

    private static void RequireFiniteNonNegative(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Timings must be finite and non-negative.");
        }
    }
}

/// <summary>One OCR word (Windows.Media.Ocr OcrWord) with its line index, physical pixels, in reading order.</summary>
public sealed record OcrWord(string Text, RectD Bounds, int LineIndex);

public sealed record OcrNudgeMatch(RectD Bounds, string MatchedText, double Score, int WordCount);

/// <summary>
/// Finds the on-screen text the focus nudge should point at. Contract:
/// <list type="bullet">
/// <item>Normalize: Unicode FormD with non-spacing marks removed, invariant lower case, punctuation and
/// symbols replaced by spaces, whitespace collapsed to single spaces and trimmed.</item>
/// <item>Similarity(a, b) = 1 - Levenshtein(Normalize(a), Normalize(b)) / max(length), and 0 when both normalize
/// empty. Spans whose text normalizes empty are never candidates.</item>
/// <item>Candidates are spans of 1..(phrase word count + 1) consecutive words with the same LineIndex; the span text
/// is the words joined by a space and its box is the union of the word boxes.</item>
/// <item>A word is ignored when its box centre lies inside any excluded rect (Boo's own windows) or its box is
/// empty/non-finite; a span never crosses an ignored word.</item>
/// <item>Null words or excludedRects throw ArgumentNullException; a threshold outside (0, 1] throws
/// ArgumentOutOfRangeException. A null, empty or punctuation-only phrase returns null.</item>
/// <item>Best = highest score; ties prefer the smaller top edge, then the smaller left edge (reading order,
/// "closest to top-left"), then fewer words. Score below threshold returns null (nudge text only).</item>
/// </list>
/// </summary>
public static class OcrNudgeTargetMatcher
{
    public const double DefaultThreshold = 0.8;

    public static OcrNudgeMatch? Match(
        IReadOnlyList<OcrWord> words,
        string? targetPhrase,
        IReadOnlyList<RectD> excludedRects,
        double threshold = DefaultThreshold)
    {
        ArgumentNullException.ThrowIfNull(words);
        ArgumentNullException.ThrowIfNull(excludedRects);
        if (double.IsNaN(threshold) || threshold <= 0 || threshold > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(threshold), threshold, "The threshold must be in (0, 1].");
        }

        var phrase = targetPhrase is null ? string.Empty : Normalize(targetPhrase);
        if (phrase.Length == 0)
        {
            return null;
        }

        var maxSpan = phrase.Split(' ').Length + 1;
        var usable = new bool[words.Count];
        for (var i = 0; i < words.Count; i++)
        {
            usable[i] = words[i] is { } word && IsUsable(word.Bounds, excludedRects);
        }

        OcrNudgeMatch? best = null;
        for (var first = 0; first < words.Count; first++)
        {
            if (!usable[first])
            {
                continue;
            }

            var text = new StringBuilder();
            var box = words[first].Bounds;
            for (var count = 1; count <= maxSpan; count++)
            {
                var index = first + count - 1;
                if (index >= words.Count || !usable[index] || words[index].LineIndex != words[first].LineIndex)
                {
                    break;
                }

                if (count > 1)
                {
                    text.Append(' ');
                    box = Union(box, words[index].Bounds);
                }

                text.Append(words[index].Text);
                var spanText = text.ToString();
                var normalized = Normalize(spanText);
                if (normalized.Length == 0)
                {
                    continue;
                }

                var score = NormalizedSimilarity(normalized, phrase);
                if (score < threshold)
                {
                    continue;
                }

                var candidate = new OcrNudgeMatch(box, spanText, score, count);
                if (best is null || IsBetter(candidate, best))
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    public static string Normalize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var decomposed = text.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingSpace = false;
        foreach (var raw in decomposed)
        {
            var category = CharUnicodeInfo.GetUnicodeCategory(raw);
            if (category == UnicodeCategory.NonSpacingMark)
            {
                continue;
            }

            if (char.IsWhiteSpace(raw) || char.IsPunctuation(raw) || char.IsSymbol(raw) || char.IsControl(raw))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(char.ToLowerInvariant(raw));
        }

        return builder.ToString();
    }

    public static double Similarity(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return NormalizedSimilarity(Normalize(a), Normalize(b));
    }

    private static double NormalizedSimilarity(string a, string b)
    {
        var length = Math.Max(a.Length, b.Length);
        return length == 0 ? 0 : 1 - (double)Levenshtein(a, b) / length;
    }

    private static int Levenshtein(string a, string b)
    {
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
        {
            previous[j] = j;
        }

        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
            }

            (previous, current) = (current, previous);
        }

        return previous[b.Length];
    }

    private static bool IsBetter(OcrNudgeMatch candidate, OcrNudgeMatch best)
    {
        if (candidate.Score != best.Score)
        {
            return candidate.Score > best.Score;
        }

        if (candidate.Bounds.Y != best.Bounds.Y)
        {
            return candidate.Bounds.Y < best.Bounds.Y;
        }

        if (candidate.Bounds.X != best.Bounds.X)
        {
            return candidate.Bounds.X < best.Bounds.X;
        }

        return candidate.WordCount < best.WordCount;
    }

    private static bool IsUsable(RectD bounds, IReadOnlyList<RectD> excludedRects)
    {
        if (!double.IsFinite(bounds.X) || !double.IsFinite(bounds.Y)
            || !double.IsFinite(bounds.Width) || !double.IsFinite(bounds.Height)
            || bounds.Width <= 0 || bounds.Height <= 0)
        {
            return false;
        }

        var centreX = bounds.X + bounds.Width / 2;
        var centreY = bounds.Y + bounds.Height / 2;
        foreach (var rect in excludedRects)
        {
            if (centreX >= rect.X && centreX < rect.X + rect.Width && centreY >= rect.Y && centreY < rect.Y + rect.Height)
            {
                return false;
            }
        }

        return true;
    }

    private static RectD Union(RectD a, RectD b)
    {
        var left = Math.Min(a.X, b.X);
        var top = Math.Min(a.Y, b.Y);
        var right = Math.Max(a.X + a.Width, b.X + b.Width);
        var bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new RectD(left, top, right - left, bottom - top);
    }
}
