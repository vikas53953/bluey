namespace Boo.Core;

// Pointer lane (plan U3 pointer + drawing, U6 nudge pointer).
// Frozen API surface written by the test author; the builder implements the
// members that currently throw NotImplementedException. All coordinates are
// physical (virtual-screen) pixels unless a name says Dip.

/// <summary>A point in physical virtual-screen pixels.</summary>
public readonly record struct PointD(double X, double Y);

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
    public PointD PositionAt(double elapsedMs) => throw new NotImplementedException();
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
        double ringStrokeMs = DefaultRingStrokeMs) => throw new NotImplementedException();

    /// <summary>Anchor for a visible target (see the class contract), in physical pixels.</summary>
    public static PointD Anchor(PointD start, RectD visibleTarget, double scale) => throw new NotImplementedException();

    /// <summary>Flight duration for a physical distance on a display with the given scale.</summary>
    public static double DurationFor(double distancePx, double scale) => throw new NotImplementedException();

    /// <summary>Cubic ease-in-out: t &lt; 0.5 ? 4t^3 : 1 - (-2t + 2)^3 / 2, input clamped to [0, 1].</summary>
    public static double Ease(double t) => throw new NotImplementedException();
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
    public PointerAnnotationSequencer(PointerSequenceTimings? timings = null) => throw new NotImplementedException();

    public PointerPhase Phase => throw new NotImplementedException();

    /// <summary>Generation of the active sequence, or null when nothing is showing.</summary>
    public long? ActiveGeneration => throw new NotImplementedException();

    public PointerClearReason? LastClearReason => throw new NotImplementedException();

    public bool Begin(long generation, PointerFlightPlan plan, double nowMs) => throw new NotImplementedException();

    public PointerRenderFrame? Tick(double nowMs) => throw new NotImplementedException();

    public bool Clear(PointerClearReason reason) => throw new NotImplementedException();
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
        double threshold = DefaultThreshold) => throw new NotImplementedException();

    public static string Normalize(string text) => throw new NotImplementedException();

    public static double Similarity(string a, string b) => throw new NotImplementedException();
}
