namespace Boo.Core;

/// <summary>What one stretch of a recorded session looked like.</summary>
public enum TimelineKind
{
    OnTask,
    OffTask,
    Uncertain,

    /// <summary>A pause. Focus time does not advance while paused, so it is a zero-width marker.</summary>
    Paused,

    /// <summary>No observation covers this stretch (before the first check, or a gap longer than the coverage cap).</summary>
    Unknown
}

/// <summary>A stretch of focus time in seconds from the session start: [Start, End).</summary>
public sealed record TimelineSegment(double Start, double End, TimelineKind Kind)
{
    public double Length => End - Start;
}

/// <summary>Totals in focus seconds. <see cref="FocusScore"/> is OnTask / (OnTask + OffTask), or null when neither was observed.</summary>
public sealed record FocusTimelineSummary(
    double OnTaskSeconds,
    double OffTaskSeconds,
    double UncertainSeconds,
    double UnknownSeconds,
    int PauseCount,
    double? FocusScore);

/// <summary>One row in the history list, ready to draw. Bar widths are in device-independent pixels.</summary>
public sealed record FocusHistoryRow(
    Guid Id,
    string Task,
    DateTimeOffset StartedAt,
    FocusStatus Status,
    string Title,
    string Detail,
    FocusTimelineSummary Summary,
    IReadOnlyList<FocusHistoryBar> Bars);

/// <summary>One coloured block of the history bar. Left and Width are pixels inside a bar of the requested width.</summary>
public sealed record FocusHistoryBar(double Left, double Width, TimelineKind Kind);

/// <summary>
/// Turns a saved session snapshot into a timeline. Rules:
/// - Only checks of kind OnTask, OffTask, Uncertain and Unavailable cover time; Unavailable covers as Unknown.
/// - A check at Elapsed e covers [e, next) where next is the next covering check's Elapsed, the session's Elapsed,
///   or e + <see cref="CoverageCapSeconds"/>, whichever is smallest. Anything uncovered is Unknown.
/// - Paused checks become zero-width Paused segments at their Elapsed.
/// - Segments are ordered by Start, never overlap, adjacent equal kinds are merged, and the non-paused segments
///   exactly tile [0, snapshot.Elapsed]. A session with no elapsed time has no non-paused segments.
/// - Checks with non-finite or negative Elapsed, or Elapsed beyond the session's Elapsed, are ignored.
/// </summary>
public static class FocusTimeline
{
    /// <summary>Longest stretch of focus time one check may vouch for.</summary>
    public const double CoverageCapSeconds = 90;

    public static IReadOnlyList<TimelineSegment> Build(FocusSnapshot snapshot) => throw new NotImplementedException();

    public static FocusTimelineSummary Summarize(FocusSnapshot snapshot) => throw new NotImplementedException();

    /// <summary>
    /// Builds a drawable history row. The bar is scaled to the planned Duration (so an early end leaves empty space).
    /// Zero-width pause markers get a Width of 2 pixels. Bars never extend past <paramref name="barWidth"/>.
    /// Title is the task. Detail reads like "25 min · 84% focused · Completed", with "no focus estimate" when FocusScore is null,
    /// whole focus minutes rounded down from Elapsed, the score rounded to the nearest whole percent, and the status
    /// shown as "Completed" (Ended with Elapsed &gt;= Duration), "Ended early" (Ended before Duration), "Paused" or "Running".
    /// </summary>
    public static FocusHistoryRow ToHistoryRow(FocusSnapshot snapshot, double barWidth) => throw new NotImplementedException();
}
