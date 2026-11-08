using Boo.Core;

namespace Boo.Core.Tests;

public sealed class FocusTimelineTests
{
    private const string Dot = " · ";
    private static readonly Guid SessionId = Guid.Parse("6a1f6c2e-8f1b-4c55-9a51-3c1df0e7f001");
    private static readonly DateTimeOffset Started = new(2026, 11, 8, 9, 0, 0, TimeSpan.Zero);

    private static FocusSnapshot Snapshot(
        double elapsed,
        IEnumerable<FocusCheck> checks,
        double duration = 1500,
        FocusStatus status = FocusStatus.Ended,
        string task = "write report") =>
        new(SessionId, task, duration, Started, elapsed, elapsed, status, false, false, checks.ToArray());

    private static FocusCheck C(double elapsed, FocusActivity activity) => new(elapsed, activity, 0.9, 0.9);

    private static TimelineSegment S(double start, double end, TimelineKind kind) => new(start, end, kind);

    private static IReadOnlyList<TimelineSegment> NonPaused(IReadOnlyList<TimelineSegment> segments) =>
        segments.Where(s => s.Kind != TimelineKind.Paused).ToArray();

    /// <summary>The structural rules every Build result must satisfy.</summary>
    private static void AssertWellFormed(IReadOnlyList<TimelineSegment> segments, double elapsed)
    {
        for (var i = 1; i < segments.Count; i++)
            Assert.True(segments[i].Start >= segments[i - 1].Start, $"segment {i} is out of order");

        foreach (var paused in segments.Where(s => s.Kind == TimelineKind.Paused))
            Assert.Equal(paused.Start, paused.End);

        var tiles = NonPaused(segments);
        if (elapsed <= 0)
        {
            Assert.Empty(tiles);
            return;
        }

        Assert.NotEmpty(tiles);
        Assert.Equal(0, tiles[0].Start);
        Assert.Equal(elapsed, tiles[^1].End);
        foreach (var tile in tiles)
            Assert.True(tile.Length > 0, $"empty non-paused segment at {tile.Start}");
        for (var i = 1; i < tiles.Count; i++)
        {
            Assert.Equal(tiles[i - 1].End, tiles[i].Start);
            Assert.NotEqual(tiles[i - 1].Kind, tiles[i].Kind);
        }
    }

    // ---------- Build ----------

    [Fact]
    public void Empty_session_has_no_segments()
    {
        var segments = FocusTimeline.Build(Snapshot(0, Array.Empty<FocusCheck>()));

        Assert.Empty(segments);
    }

    [Fact]
    public void Session_with_no_elapsed_time_has_only_pause_markers()
    {
        var segments = FocusTimeline.Build(Snapshot(0, new[] { C(0, FocusActivity.Paused) }));

        Assert.Equal(new[] { S(0, 0, TimelineKind.Paused) }, segments);
    }

    [Fact]
    public void Session_without_checks_is_entirely_unknown()
    {
        var segments = FocusTimeline.Build(Snapshot(100, Array.Empty<FocusCheck>()));

        Assert.Equal(new[] { S(0, 100, TimelineKind.Unknown) }, segments);
    }

    [Fact]
    public void Time_before_the_first_check_is_unknown()
    {
        var segments = FocusTimeline.Build(Snapshot(40, new[] { C(10, FocusActivity.OnTask) }));

        Assert.Equal(new[] { S(0, 10, TimelineKind.Unknown), S(10, 40, TimelineKind.OnTask) }, segments);
    }

    [Fact]
    public void Each_check_covers_until_the_next_check()
    {
        var segments = FocusTimeline.Build(Snapshot(90, new[]
        {
            C(0, FocusActivity.OnTask),
            C(30, FocusActivity.OffTask),
            C(60, FocusActivity.Uncertain)
        }));

        Assert.Equal(new[]
        {
            S(0, 30, TimelineKind.OnTask),
            S(30, 60, TimelineKind.OffTask),
            S(60, 90, TimelineKind.Uncertain)
        }, segments);
    }

    [Fact]
    public void Coverage_is_capped_at_90_seconds_and_the_gap_is_unknown()
    {
        Assert.Equal(90, FocusTimeline.CoverageCapSeconds);

        var segments = FocusTimeline.Build(Snapshot(250, new[]
        {
            C(0, FocusActivity.OnTask),
            C(200, FocusActivity.OffTask)
        }));

        Assert.Equal(new[]
        {
            S(0, 90, TimelineKind.OnTask),
            S(90, 200, TimelineKind.Unknown),
            S(200, 250, TimelineKind.OffTask)
        }, segments);
    }

    [Fact]
    public void Last_check_is_capped_at_90_seconds_before_the_session_end()
    {
        var segments = FocusTimeline.Build(Snapshot(100, new[] { C(0, FocusActivity.OnTask) }));

        Assert.Equal(new[] { S(0, 90, TimelineKind.OnTask), S(90, 100, TimelineKind.Unknown) }, segments);
    }

    [Fact]
    public void Gap_of_exactly_the_cap_is_fully_covered()
    {
        var segments = FocusTimeline.Build(Snapshot(120, new[]
        {
            C(0, FocusActivity.OnTask),
            C(90, FocusActivity.OffTask)
        }));

        Assert.Equal(new[] { S(0, 90, TimelineKind.OnTask), S(90, 120, TimelineKind.OffTask) }, segments);
    }

    [Fact]
    public void Adjacent_equal_kinds_are_merged()
    {
        var segments = FocusTimeline.Build(Snapshot(120, new[]
        {
            C(0, FocusActivity.OnTask),
            C(30, FocusActivity.OnTask),
            C(60, FocusActivity.OnTask),
            C(90, FocusActivity.OnTask)
        }));

        Assert.Equal(new[] { S(0, 120, TimelineKind.OnTask) }, segments);
    }

    [Fact]
    public void Equal_kinds_separated_by_an_unknown_gap_stay_separate()
    {
        var segments = FocusTimeline.Build(Snapshot(150, new[]
        {
            C(0, FocusActivity.OnTask),
            C(120, FocusActivity.OnTask)
        }));

        Assert.Equal(new[]
        {
            S(0, 90, TimelineKind.OnTask),
            S(90, 120, TimelineKind.Unknown),
            S(120, 150, TimelineKind.OnTask)
        }, segments);
    }

    [Fact]
    public void Unavailable_covers_as_unknown()
    {
        var segments = FocusTimeline.Build(Snapshot(90, new[]
        {
            C(0, FocusActivity.OnTask),
            C(30, FocusActivity.Unavailable),
            C(60, FocusActivity.OffTask)
        }));

        Assert.Equal(new[]
        {
            S(0, 30, TimelineKind.OnTask),
            S(30, 60, TimelineKind.Unknown),
            S(60, 90, TimelineKind.OffTask)
        }, segments);
    }

    [Fact]
    public void Unavailable_unknown_merges_with_the_leading_unknown()
    {
        var segments = FocusTimeline.Build(Snapshot(40, new[] { C(10, FocusActivity.Unavailable) }));

        Assert.Equal(new[] { S(0, 40, TimelineKind.Unknown) }, segments);
    }

    [Fact]
    public void Unavailable_ends_the_previous_checks_coverage()
    {
        // Unavailable is a covering check, so OnTask at 0 stops at 30 rather than extending to the cap.
        var segments = FocusTimeline.Build(Snapshot(200, new[]
        {
            C(0, FocusActivity.OnTask),
            C(30, FocusActivity.Unavailable),
            C(150, FocusActivity.OnTask)
        }));

        Assert.Equal(new[]
        {
            S(0, 30, TimelineKind.OnTask),
            S(30, 150, TimelineKind.Unknown),
            S(150, 200, TimelineKind.OnTask)
        }, segments);
    }

    [Fact]
    public void Paused_checks_become_zero_width_markers_and_do_not_end_coverage()
    {
        var snapshot = Snapshot(110, new[]
        {
            C(0, FocusActivity.OnTask),
            C(20, FocusActivity.Paused),
            C(50, FocusActivity.OffTask),
            C(80, FocusActivity.Paused)
        });

        var segments = FocusTimeline.Build(snapshot);

        AssertWellFormed(segments, 110);
        Assert.Equal(
            new[] { S(20, 20, TimelineKind.Paused), S(80, 80, TimelineKind.Paused) },
            segments.Where(s => s.Kind == TimelineKind.Paused));
        Assert.Equal(
            new[] { S(0, 50, TimelineKind.OnTask), S(50, 110, TimelineKind.OffTask) },
            NonPaused(segments));
    }

    [Fact]
    public void Pause_markers_sit_in_start_order_among_other_segments()
    {
        var segments = FocusTimeline.Build(Snapshot(120, new[]
        {
            C(0, FocusActivity.OnTask),
            C(45, FocusActivity.Paused),
            C(60, FocusActivity.OffTask)
        }));

        Assert.Equal(new[]
        {
            S(0, 60, TimelineKind.OnTask),
            S(45, 45, TimelineKind.Paused),
            S(60, 120, TimelineKind.OffTask)
        }, segments);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(-1)]
    [InlineData(61)]
    public void Checks_with_bad_elapsed_are_ignored(double badElapsed)
    {
        var segments = FocusTimeline.Build(Snapshot(60, new[]
        {
            C(0, FocusActivity.OnTask),
            C(badElapsed, FocusActivity.OffTask),
            C(badElapsed, FocusActivity.Paused)
        }));

        Assert.Equal(new[] { S(0, 60, TimelineKind.OnTask) }, segments);
    }

    [Fact]
    public void Bad_check_does_not_cut_the_previous_checks_coverage()
    {
        var segments = FocusTimeline.Build(Snapshot(80, new[]
        {
            C(0, FocusActivity.OnTask),
            C(-5, FocusActivity.OffTask),
            C(double.NaN, FocusActivity.OffTask),
            C(50, FocusActivity.OffTask)
        }));

        Assert.Equal(new[] { S(0, 50, TimelineKind.OnTask), S(50, 80, TimelineKind.OffTask) }, segments);
    }

    [Fact]
    public void Realistic_session_tiles_its_elapsed_time()
    {
        var checks = new List<FocusCheck>();
        for (var e = 30; e < 1500; e += 30)
            checks.Add(C(e, e is >= 300 and <= 510 ? FocusActivity.OffTask : FocusActivity.OnTask));
        checks.Add(C(700, FocusActivity.Paused));

        var segments = FocusTimeline.Build(Snapshot(1500, checks));

        AssertWellFormed(segments, 1500);
        Assert.Equal(new[]
        {
            S(0, 30, TimelineKind.Unknown),
            S(30, 300, TimelineKind.OnTask),
            S(300, 540, TimelineKind.OffTask),
            S(540, 1500, TimelineKind.OnTask)
        }, NonPaused(segments));
        Assert.Equal(S(700, 700, TimelineKind.Paused), Assert.Single(segments, s => s.Kind == TimelineKind.Paused));
    }

    [Fact]
    public void Segment_length_is_end_minus_start()
    {
        Assert.Equal(25, S(5, 30, TimelineKind.OnTask).Length);
        Assert.Equal(0, S(7, 7, TimelineKind.Paused).Length);
    }

    // ---------- Summarize ----------

    [Fact]
    public void Summarize_totals_each_kind_and_counts_pauses()
    {
        var snapshot = Snapshot(180, new[]
        {
            C(0, FocusActivity.OnTask),
            C(30, FocusActivity.OnTask),
            C(60, FocusActivity.OnTask),
            C(90, FocusActivity.OffTask),
            C(100, FocusActivity.Paused),
            C(120, FocusActivity.Uncertain),
            C(130, FocusActivity.Paused),
            C(150, FocusActivity.Unavailable)
        });

        var summary = FocusTimeline.Summarize(snapshot);

        Assert.Equal(new FocusTimelineSummary(90, 30, 30, 30, 2, 0.75), summary);
    }

    [Fact]
    public void Summarize_counts_leading_and_capped_gaps_as_unknown()
    {
        var summary = FocusTimeline.Summarize(Snapshot(250, new[]
        {
            C(10, FocusActivity.OnTask),
            C(200, FocusActivity.OffTask)
        }));

        Assert.Equal(90, summary.OnTaskSeconds);
        Assert.Equal(50, summary.OffTaskSeconds);
        Assert.Equal(0, summary.UncertainSeconds);
        Assert.Equal(110, summary.UnknownSeconds);
        Assert.Equal(0, summary.PauseCount);
        Assert.NotNull(summary.FocusScore);
        Assert.Equal(90.0 / 140.0, summary.FocusScore!.Value, 12);
    }

    [Fact]
    public void Summarize_of_an_empty_session_is_all_zero_with_no_score()
    {
        var summary = FocusTimeline.Summarize(Snapshot(0, Array.Empty<FocusCheck>()));

        Assert.Equal(new FocusTimelineSummary(0, 0, 0, 0, 0, null), summary);
    }

    [Fact]
    public void FocusScore_is_null_when_neither_on_nor_off_task_was_observed()
    {
        var summary = FocusTimeline.Summarize(Snapshot(120, new[]
        {
            C(0, FocusActivity.Uncertain),
            C(60, FocusActivity.Unavailable),
            C(70, FocusActivity.Paused)
        }));

        Assert.Equal(new FocusTimelineSummary(0, 0, 60, 60, 1, null), summary);
    }

    [Fact]
    public void FocusScore_is_one_when_only_on_task_and_zero_when_only_off_task()
    {
        Assert.Equal(1.0, FocusTimeline.Summarize(Snapshot(60, new[] { C(0, FocusActivity.OnTask) })).FocusScore);
        Assert.Equal(0.0, FocusTimeline.Summarize(Snapshot(60, new[] { C(0, FocusActivity.OffTask) })).FocusScore);
    }

    [Fact]
    public void PauseCount_ignores_pauses_with_bad_elapsed()
    {
        var summary = FocusTimeline.Summarize(Snapshot(60, new[]
        {
            C(0, FocusActivity.Paused),
            C(30, FocusActivity.Paused),
            C(-1, FocusActivity.Paused),
            C(double.NaN, FocusActivity.Paused),
            C(61, FocusActivity.Paused)
        }));

        Assert.Equal(2, summary.PauseCount);
        Assert.Equal(60, summary.UnknownSeconds);
    }

    // ---------- ToHistoryRow ----------

    private static FocusSnapshot CompletedEightyFourPercent()
    {
        // 50 checks at 0, 30, ..., 1470: 42 on-task (covering [0, 1260)), 8 off-task (covering [1260, 1500)).
        var checks = Enumerable.Range(0, 50)
            .Select(i => C(i * 30, i < 42 ? FocusActivity.OnTask : FocusActivity.OffTask));
        return Snapshot(1500, checks, duration: 1500, status: FocusStatus.Ended);
    }

    [Fact]
    public void History_row_for_a_completed_session_reads_minutes_score_and_completed()
    {
        var snapshot = CompletedEightyFourPercent();

        var row = FocusTimeline.ToHistoryRow(snapshot, 300);

        Assert.Equal(SessionId, row.Id);
        Assert.Equal("write report", row.Task);
        Assert.Equal("write report", row.Title);
        Assert.Equal(Started, row.StartedAt);
        Assert.Equal(FocusStatus.Ended, row.Status);
        Assert.Equal("25 min" + Dot + "84% focused" + Dot + "Completed", row.Detail);
        Assert.Equal("25 min · 84% focused · Completed", row.Detail);
        Assert.Equal(FocusTimeline.Summarize(snapshot), row.Summary);
    }

    [Fact]
    public void History_row_for_an_early_end_reads_ended_early()
    {
        var snapshot = Snapshot(600, new[] { C(0, FocusActivity.OnTask), C(60, FocusActivity.OffTask) },
            duration: 1500, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 300);

        // OnTask [0, 60), OffTask [60, 150), Unknown [150, 600): 60 / 150 = 40%.
        Assert.Equal("10 min · 40% focused · Ended early", row.Detail);
    }

    [Fact]
    public void History_row_without_a_score_says_no_focus_estimate()
    {
        var snapshot = Snapshot(300, new[] { C(0, FocusActivity.Uncertain) }, duration: 1500, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 300);

        Assert.Equal("5 min · no focus estimate · Ended early", row.Detail);
        Assert.Null(row.Summary.FocusScore);
    }

    [Fact]
    public void History_row_minutes_round_down_and_score_rounds_to_nearest()
    {
        // OnTask [0, 60), OffTask [60, 90), Unknown [90, 119): 60 / 90 = 66.67% -> 67%. 119 s -> 1 min.
        var snapshot = Snapshot(119, new[] { C(0, FocusActivity.OnTask), C(60, FocusActivity.OffTask) },
            duration: 1500, status: FocusStatus.Running);

        var row = FocusTimeline.ToHistoryRow(snapshot, 300);

        Assert.Equal("1 min · 67% focused · Running", row.Detail);
    }

    [Fact]
    public void History_row_for_a_paused_session_under_a_minute()
    {
        // 59 s -> 0 min. OnTask [0, 20), OffTask [20, 59): 20 / 59 = 33.9% -> 34%.
        var snapshot = Snapshot(59, new[] { C(0, FocusActivity.OnTask), C(20, FocusActivity.OffTask) },
            duration: 1500, status: FocusStatus.Paused);

        var row = FocusTimeline.ToHistoryRow(snapshot, 300);

        Assert.Equal("0 min · 34% focused · Paused", row.Detail);
        Assert.Equal(FocusStatus.Paused, row.Status);
    }

    [Fact]
    public void History_row_status_labels()
    {
        FocusSnapshot With(FocusStatus status, double elapsed) =>
            Snapshot(elapsed, new[] { C(0, FocusActivity.OnTask) }, duration: 60, status: status);

        Assert.EndsWith(Dot + "Completed", FocusTimeline.ToHistoryRow(With(FocusStatus.Ended, 60), 100).Detail);
        Assert.EndsWith(Dot + "Ended early", FocusTimeline.ToHistoryRow(With(FocusStatus.Ended, 59), 100).Detail);
        Assert.EndsWith(Dot + "Paused", FocusTimeline.ToHistoryRow(With(FocusStatus.Paused, 30), 100).Detail);
        Assert.EndsWith(Dot + "Running", FocusTimeline.ToHistoryRow(With(FocusStatus.Running, 30), 100).Detail);
    }

    [Fact]
    public void History_bars_are_scaled_to_the_planned_duration()
    {
        // Duration 1000 s in a 200 px bar: 0.2 px per focus second. Ended early at 100 s.
        var snapshot = Snapshot(100, new[] { C(0, FocusActivity.OnTask), C(50, FocusActivity.OffTask) },
            duration: 1000, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 200);

        Assert.Equal(2, row.Bars.Count);
        AssertBar(row.Bars[0], 0, 10, TimelineKind.OnTask);
        AssertBar(row.Bars[1], 10, 10, TimelineKind.OffTask);
        Assert.True(row.Bars.Max(b => b.Left + b.Width) <= 20 + 1e-9, "an early end leaves the rest of the bar empty");
    }

    [Fact]
    public void History_bars_follow_the_timeline_segments_in_order()
    {
        var snapshot = Snapshot(250, new[] { C(10, FocusActivity.OnTask), C(200, FocusActivity.OffTask) },
            duration: 500, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 100);

        // Segments: Unknown [0,10), OnTask [10,100), Unknown [100,200), OffTask [200,250) at 0.2 px/s.
        Assert.Equal(
            new[] { TimelineKind.Unknown, TimelineKind.OnTask, TimelineKind.Unknown, TimelineKind.OffTask },
            row.Bars.Select(b => b.Kind));
        AssertBar(row.Bars[0], 0, 2, TimelineKind.Unknown);
        AssertBar(row.Bars[1], 2, 18, TimelineKind.OnTask);
        AssertBar(row.Bars[2], 20, 20, TimelineKind.Unknown);
        AssertBar(row.Bars[3], 40, 10, TimelineKind.OffTask);
    }

    [Fact]
    public void Pause_markers_are_two_pixels_wide()
    {
        var snapshot = Snapshot(100, new[]
        {
            C(0, FocusActivity.OnTask),
            C(50, FocusActivity.Paused),
            C(60, FocusActivity.OnTask)
        }, duration: 1000, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 200);

        var pause = Assert.Single(row.Bars, b => b.Kind == TimelineKind.Paused);
        AssertBar(pause, 10, 2, TimelineKind.Paused);
    }

    [Fact]
    public void Pause_marker_at_the_end_of_a_full_bar_stays_inside_the_bar()
    {
        var snapshot = Snapshot(1000, new[]
        {
            C(0, FocusActivity.OnTask),
            C(1000, FocusActivity.Paused)
        }, duration: 1000, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 200);

        Assert.Contains(row.Bars, b => b.Kind == TimelineKind.Paused);
        AssertInsideBar(row.Bars, 200);
    }

    [Fact]
    public void Bars_never_extend_past_the_bar_width_even_when_elapsed_exceeds_duration()
    {
        var checks = Enumerable.Range(0, 40).Select(i => C(i * 30, FocusActivity.OnTask)).ToList();
        checks.Add(C(1190, FocusActivity.Paused));
        var snapshot = Snapshot(1200, checks, duration: 1000, status: FocusStatus.Ended);

        var row = FocusTimeline.ToHistoryRow(snapshot, 200);

        Assert.NotEmpty(row.Bars);
        AssertInsideBar(row.Bars, 200);
        Assert.EndsWith(Dot + "Completed", row.Detail);
    }

    [Fact]
    public void Empty_session_has_no_bars()
    {
        var row = FocusTimeline.ToHistoryRow(Snapshot(0, Array.Empty<FocusCheck>(), status: FocusStatus.Ended), 200);

        Assert.Empty(row.Bars);
        Assert.Equal("0 min · no focus estimate · Ended early", row.Detail);
    }

    [Fact]
    public void Completed_session_bar_fills_the_whole_width()
    {
        var row = FocusTimeline.ToHistoryRow(CompletedEightyFourPercent(), 300);

        Assert.Equal(2, row.Bars.Count);
        AssertBar(row.Bars[0], 0, 252, TimelineKind.OnTask);
        AssertBar(row.Bars[1], 252, 48, TimelineKind.OffTask);
        AssertInsideBar(row.Bars, 300);
    }

    private static void AssertBar(FocusHistoryBar bar, double left, double width, TimelineKind kind)
    {
        Assert.Equal(kind, bar.Kind);
        Assert.Equal(left, bar.Left, 9);
        Assert.Equal(width, bar.Width, 9);
    }

    private static void AssertInsideBar(IReadOnlyList<FocusHistoryBar> bars, double barWidth)
    {
        foreach (var bar in bars)
        {
            Assert.True(bar.Left >= 0, $"bar starts before 0: {bar}");
            Assert.True(bar.Width >= 0, $"negative width: {bar}");
            Assert.True(bar.Left + bar.Width <= barWidth + 1e-9, $"bar extends past {barWidth}: {bar}");
        }
    }
}
