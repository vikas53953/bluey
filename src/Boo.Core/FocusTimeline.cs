using System.Globalization;

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

    public static IReadOnlyList<TimelineSegment> Build(FocusSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var elapsed = ElapsedOf(snapshot);
        var valid = (snapshot.Checks ?? Array.Empty<FocusCheck>())
            .Where(check => check is not null && double.IsFinite(check.Elapsed) && check.Elapsed >= 0 && check.Elapsed <= elapsed)
            .ToArray();

        var covering = valid
            .Where(check => KindOf(check.Activity) is not null)
            .OrderBy(check => check.Elapsed)
            .ToArray();

        var tiles = new List<TimelineSegment>();
        if (elapsed > 0)
        {
            var cursor = 0d;
            for (var i = 0; i < covering.Length; i++)
            {
                var start = covering[i].Elapsed;
                var next = i + 1 < covering.Length ? covering[i + 1].Elapsed : elapsed;
                var end = Math.Min(Math.Min(next, elapsed), start + CoverageCapSeconds);
                if (end <= start)
                    continue;

                if (start > cursor)
                    AddTile(tiles, cursor, start, TimelineKind.Unknown);
                AddTile(tiles, start, end, KindOf(covering[i].Activity)!.Value);
                cursor = end;
            }

            if (cursor < elapsed)
                AddTile(tiles, cursor, elapsed, TimelineKind.Unknown);
        }

        var pauses = valid
            .Where(check => check.Activity == FocusActivity.Paused)
            .Select(check => new TimelineSegment(check.Elapsed, check.Elapsed, TimelineKind.Paused));

        // OrderBy is stable, so at an equal Start a tile comes before a pause marker.
        return tiles.Concat(pauses).OrderBy(segment => segment.Start).ToArray();
    }

    public static FocusTimelineSummary Summarize(FocusSnapshot snapshot)
    {
        double onTask = 0, offTask = 0, uncertain = 0, unknown = 0;
        var pauses = 0;
        foreach (var segment in Build(snapshot))
        {
            switch (segment.Kind)
            {
                case TimelineKind.OnTask: onTask += segment.Length; break;
                case TimelineKind.OffTask: offTask += segment.Length; break;
                case TimelineKind.Uncertain: uncertain += segment.Length; break;
                case TimelineKind.Unknown: unknown += segment.Length; break;
                case TimelineKind.Paused: pauses++; break;
            }
        }

        var observed = onTask + offTask;
        double? score = observed > 0 ? onTask / observed : null;
        return new FocusTimelineSummary(onTask, offTask, uncertain, unknown, pauses, score);
    }

    /// <summary>
    /// Builds a drawable history row. The bar is scaled to the planned Duration (so an early end leaves empty space).
    /// Zero-width pause markers get a Width of 2 pixels. Bars never extend past <paramref name="barWidth"/>.
    /// Title is the task. Detail reads like "25 min · 84% focused · Completed", with "no focus estimate" when FocusScore is null,
    /// whole focus minutes rounded down from Elapsed, the score rounded to the nearest whole percent, and the status
    /// shown as "Completed" (Ended with Elapsed &gt;= Duration), "Ended early" (Ended before Duration), "Paused" or "Running".
    /// </summary>
    public static FocusHistoryRow ToHistoryRow(FocusSnapshot snapshot, double barWidth)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        var width = double.IsFinite(barWidth) && barWidth > 0 ? barWidth : 0;
        var duration = double.IsFinite(snapshot.Duration) && snapshot.Duration > 0 ? snapshot.Duration : 0;
        var segments = Build(snapshot);
        var summary = Summarize(snapshot);

        double X(double seconds) => duration > 0 ? Math.Clamp(seconds * width / duration, 0, width) : 0;

        var bars = new List<FocusHistoryBar>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.Kind == TimelineKind.Paused)
            {
                var markerWidth = Math.Min(PauseMarkerWidth, width);
                var left = Math.Min(X(segment.Start), width - markerWidth);
                bars.Add(new FocusHistoryBar(left, markerWidth, TimelineKind.Paused));
            }
            else
            {
                var left = X(segment.Start);
                bars.Add(new FocusHistoryBar(left, Math.Max(0, X(segment.End) - left), segment.Kind));
            }
        }

        var elapsed = ElapsedOf(snapshot);
        var minutes = (long)Math.Floor(elapsed / 60);
        var score = summary.FocusScore is { } value
            ? $"{Math.Round(value * 100, MidpointRounding.AwayFromZero).ToString("0", CultureInfo.InvariantCulture)}% focused"
            : "no focus estimate";
        var status = snapshot.Status switch
        {
            FocusStatus.Ended => elapsed >= snapshot.Duration ? "Completed" : "Ended early",
            FocusStatus.Paused => "Paused",
            _ => "Running"
        };
        var detail = string.Join(Separator, $"{minutes.ToString(CultureInfo.InvariantCulture)} min", score, status);

        return new FocusHistoryRow(
            snapshot.Id,
            snapshot.Task,
            snapshot.StartedAt,
            snapshot.Status,
            snapshot.Task,
            detail,
            summary,
            bars);
    }

    private const double PauseMarkerWidth = 2;
    private const string Separator = " \u00B7 ";

    private static double ElapsedOf(FocusSnapshot snapshot) =>
        double.IsFinite(snapshot.Elapsed) && snapshot.Elapsed > 0 ? snapshot.Elapsed : 0;

    private static TimelineKind? KindOf(FocusActivity activity) => activity switch
    {
        FocusActivity.OnTask => TimelineKind.OnTask,
        FocusActivity.OffTask => TimelineKind.OffTask,
        FocusActivity.Uncertain => TimelineKind.Uncertain,
        FocusActivity.Unavailable => TimelineKind.Unknown,
        _ => null
    };

    private static void AddTile(List<TimelineSegment> tiles, double start, double end, TimelineKind kind)
    {
        if (end <= start)
            return;

        if (tiles.Count > 0 && tiles[^1].Kind == kind && tiles[^1].End == start)
            tiles[^1] = tiles[^1] with { End = end };
        else
            tiles.Add(new TimelineSegment(start, end, kind));
    }
}
