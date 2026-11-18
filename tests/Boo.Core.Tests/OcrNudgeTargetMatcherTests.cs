using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

// Pointer lane acceptance (plan U6): the focus nudge points at the distracting text found by Windows OCR.
public sealed class OcrNudgeTargetMatcherTests
{
    private static readonly RectD[] NoExclusions = Array.Empty<RectD>();

    // ON-001
    [Fact]
    public void A_single_word_matches_case_insensitively()
    {
        var words = new[]
        {
            W("File", 10, 10, 30, 12, 0),
            W("Edit", 50, 10, 30, 12, 0),
            W("YouTube", 400, 40, 60, 14, 1),
        };

        var match = OcrNudgeTargetMatcher.Match(words, "youtube", NoExclusions);

        Assert.NotNull(match);
        Assert.Equal(new RectD(400, 40, 60, 14), match.Bounds);
        Assert.Equal("YouTube", match.MatchedText);
        Assert.Equal(1d, match.Score);
        Assert.Equal(1, match.WordCount);
    }

    // ON-002
    [Fact]
    public void Matching_ignores_diacritics()
    {
        var words = new[] { W("Café", 100, 100, 40, 14, 0) };

        var match = OcrNudgeTargetMatcher.Match(words, "CAFE", NoExclusions);

        Assert.NotNull(match);
        Assert.Equal(new RectD(100, 100, 40, 14), match.Bounds);
        Assert.Equal(1d, match.Score);
    }

    // ON-003
    [Fact]
    public void A_multi_word_phrase_spans_adjacent_words_on_one_line_and_returns_the_union_box()
    {
        var words = new[]
        {
            W("Inbox", 10, 50, 40, 14, 0),
            W("Hacker", 100, 50, 60, 14, 0),
            W("News", 166, 48, 40, 18, 0),
            W("new", 220, 50, 30, 14, 0),
        };

        var match = OcrNudgeTargetMatcher.Match(words, "Hacker News", NoExclusions);

        Assert.NotNull(match);
        Assert.Equal(new RectD(100, 48, 106, 18), match.Bounds);
        Assert.Equal("Hacker News", match.MatchedText);
        Assert.Equal(2, match.WordCount);
        Assert.Equal(1d, match.Score);
    }

    // ON-004
    [Fact]
    public void Diacritics_and_case_are_ignored_across_a_multi_word_span()
    {
        var words = new[] { W("Résumé", 300, 20, 60, 14, 2), W("BUILDER", 366, 20, 70, 14, 2) };

        var match = OcrNudgeTargetMatcher.Match(words, "resume builder", NoExclusions);

        Assert.NotNull(match);
        Assert.Equal(new RectD(300, 20, 136, 14), match.Bounds);
    }

    // ON-005
    [Fact]
    public void A_phrase_does_not_span_words_on_different_lines()
    {
        var words = new[] { W("Hacker", 100, 50, 60, 14, 0), W("News", 100, 70, 40, 14, 1) };

        Assert.Null(OcrNudgeTargetMatcher.Match(words, "Hacker News", NoExclusions));
    }

    // ON-006 below threshold: nudge text only, no pointer.
    [Fact]
    public void A_weak_match_returns_null()
    {
        var words = new[] { W("Reddit", 100, 50, 60, 14, 0), W("Inbox", 200, 50, 50, 14, 0) };

        Assert.Null(OcrNudgeTargetMatcher.Match(words, "Netflix", NoExclusions));
    }

    // ON-007
    [Fact]
    public void A_one_letter_ocr_miss_still_matches_above_the_threshold()
    {
        var words = new[] { W("YouTub", 400, 40, 56, 14, 1) };

        var match = OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions);

        Assert.NotNull(match);
        Assert.Equal(6d / 7d, match.Score, 9);
        Assert.Equal(0.8, OcrNudgeTargetMatcher.DefaultThreshold);
    }

    // ON-008
    [Fact]
    public void A_custom_threshold_is_respected()
    {
        var words = new[] { W("YouTub", 400, 40, 56, 14, 1) };

        Assert.Null(OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions, threshold: 0.9));
        Assert.NotNull(OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions, threshold: 0.85));
    }

    // ON-009 Boo's own nudge bubble also shows the word; it must never be the target.
    [Fact]
    public void Words_inside_boos_own_windows_are_ignored()
    {
        var bubble = new RectD(800, 0, 300, 60);
        var words = new[]
        {
            W("Close", 810, 20, 36, 14, 0),
            W("YouTube", 850, 20, 60, 14, 0),
            W("YouTube", 400, 200, 60, 14, 3),
        };

        var unfiltered = OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions);
        var filtered = OcrNudgeTargetMatcher.Match(words, "YouTube", new[] { bubble });

        Assert.Equal(new RectD(850, 20, 60, 14), unfiltered?.Bounds);
        Assert.Equal(new RectD(400, 200, 60, 14), filtered?.Bounds);
    }

    // ON-010
    [Fact]
    public void A_span_never_crosses_an_ignored_word()
    {
        var words = new[]
        {
            W("Hacker", 100, 50, 60, 14, 0),
            W("Boo", 166, 50, 30, 14, 0),
            W("News", 202, 50, 40, 14, 0),
        };

        Assert.Null(OcrNudgeTargetMatcher.Match(words, "Hacker News", new[] { new RectD(160, 40, 40, 30) }));
    }

    // ON-011
    [Fact]
    public void Ties_prefer_the_smaller_top_edge_then_the_smaller_left_edge()
    {
        var byTop = new[] { W("YouTube", 500, 300, 60, 14, 5), W("YouTube", 900, 100, 60, 14, 1) };
        var byLeft = new[] { W("YouTube", 500, 100, 60, 14, 2), W("YouTube", 200, 100, 60, 14, 3) };

        Assert.Equal(new RectD(900, 100, 60, 14), OcrNudgeTargetMatcher.Match(byTop, "YouTube", NoExclusions)?.Bounds);
        Assert.Equal(new RectD(200, 100, 60, 14), OcrNudgeTargetMatcher.Match(byLeft, "YouTube", NoExclusions)?.Bounds);
    }

    // ON-012
    [Fact]
    public void Ties_at_the_same_corner_prefer_fewer_words()
    {
        var words = new[] { W("YouTube", 200, 100, 60, 14, 0), W("-", 266, 100, 6, 14, 0), W("Home", 278, 100, 40, 14, 0) };

        var match = OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions);

        Assert.NotNull(match);
        Assert.Equal(new RectD(200, 100, 60, 14), match.Bounds);
        Assert.Equal(1, match.WordCount);
    }

    // ON-013
    [Fact]
    public void A_higher_score_beats_a_closer_corner()
    {
        var words = new[] { W("YouTub", 10, 10, 56, 14, 0), W("YouTube", 900, 900, 60, 14, 9) };

        Assert.Equal(new RectD(900, 900, 60, 14), OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions)?.Bounds);
    }

    // ON-014
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! —")]
    public void An_empty_phrase_never_matches(string? phrase)
    {
        var words = new[] { W("!!!", 10, 10, 20, 14, 0), W("YouTube", 50, 10, 60, 14, 0) };

        Assert.Null(OcrNudgeTargetMatcher.Match(words, phrase, NoExclusions));
    }

    // ON-015
    [Fact]
    public void Words_with_empty_or_non_finite_boxes_are_ignored()
    {
        var words = new[]
        {
            W("YouTube", 10, 10, 0, 14, 0),
            W("YouTube", double.NaN, 10, 60, 14, 1),
            W("YouTube", 300, 300, 60, 14, 2),
        };

        Assert.Equal(new RectD(300, 300, 60, 14), OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions)?.Bounds);
        Assert.Null(OcrNudgeTargetMatcher.Match(Array.Empty<OcrWord>(), "YouTube", NoExclusions));
    }

    // ON-016
    [Fact]
    public void Invalid_arguments_are_rejected()
    {
        var words = new[] { W("YouTube", 50, 10, 60, 14, 0) };

        Assert.Throws<ArgumentNullException>(() => OcrNudgeTargetMatcher.Match(null!, "YouTube", NoExclusions));
        Assert.Throws<ArgumentNullException>(() => OcrNudgeTargetMatcher.Match(words, "YouTube", null!));
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions, threshold: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions, threshold: 1.5));
        Assert.Throws<ArgumentOutOfRangeException>(() => OcrNudgeTargetMatcher.Match(words, "YouTube", NoExclusions, threshold: double.NaN));
    }

    // ON-017
    [Theory]
    [InlineData("YouTube", "youtube")]
    [InlineData("  Café—Résumé!! ", "cafe resume")]
    [InlineData("ÅNGSTRÖM", "angstrom")]
    [InlineData("Hello \t  World", "hello world")]
    [InlineData("YouTube -", "youtube")]
    [InlineData("!!!", "")]
    public void Normalize_folds_case_diacritics_punctuation_and_whitespace(string input, string expected)
    {
        Assert.Equal(expected, OcrNudgeTargetMatcher.Normalize(input));
    }

    // ON-018
    [Theory]
    [InlineData("YouTube", "youtube", 1.0)]
    [InlineData("kitten", "sitting", 1 - 3d / 7d)]
    [InlineData("youtub", "YouTube", 6d / 7d)]
    [InlineData("hacker", "hacker news", 1 - 5d / 11d)]
    public void Similarity_is_one_minus_normalized_levenshtein(string a, string b, double expected)
    {
        Assert.Equal(expected, OcrNudgeTargetMatcher.Similarity(a, b), 9);
    }

    // ON-019 end to end: the matched box is a valid pointer target.
    [Fact]
    public void The_matched_box_feeds_a_pointer_flight_that_lands_inside_it()
    {
        var display = new DisplayInfo("primary", "Primary", new RectD(0, 0, 1920, 1080), 1.0);
        var words = new[] { W("Hacker", 100, 50, 60, 14, 0), W("News", 166, 50, 40, 14, 0) };
        var match = OcrNudgeTargetMatcher.Match(words, "hacker news", NoExclusions);
        Assert.NotNull(match);

        var plan = PointerFlightPlanner.Plan(new PointD(960, 540), match.Bounds, display);

        Assert.NotNull(plan);
        Assert.Equal(new PointD(153, 57), plan.Anchor);
    }

    private static OcrWord W(string text, double x, double y, double width, double height, int line) =>
        new(text, new RectD(x, y, width, height), line);
}
