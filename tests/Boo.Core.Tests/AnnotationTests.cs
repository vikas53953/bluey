using Boo.Core;
using Xunit;

namespace Boo.Core.Tests;

public sealed class AnnotationTests
{
    private static readonly Guid ObservationId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly DateTimeOffset Now = new(2026, 11, 7, 12, 0, 0, TimeSpan.Zero);

    // AN-001
    [Fact]
    public void A_proposal_at_the_fifteen_second_age_boundary_is_valid()
    {
        var current = Context(Now.AddSeconds(-15));

        var result = AnnotationValidator.Validate(Proposal(current), current, Now);

        Assert.True(result.Valid, result.Reason);
    }

    // AN-002
    [Fact]
    public void A_proposal_older_than_fifteen_seconds_is_rejected()
    {
        var current = Context(Now.AddSeconds(-15.01));

        AssertInvalid(Proposal(current), current);
    }

    // AN-003
    [Fact]
    public void A_proposal_from_the_future_is_rejected()
    {
        var current = Context(Now.AddSeconds(0.01));

        AssertInvalid(Proposal(current), current);
    }

    // AN-004
    [Fact]
    public void Mismatched_observation_generation_and_display_are_rejected()
    {
        var current = Context(Now);

        AssertInvalid(Proposal(current) with { ObservationId = Guid.NewGuid() }, current);
        AssertInvalid(Proposal(current) with { Generation = current.Generation + 1 }, current);
        AssertInvalid(Proposal(current) with { DisplayId = "other-display" }, current);
    }

    // AN-005
    [Fact]
    public void An_empty_target_context_is_rejected()
    {
        var current = Context(Now) with
        {
            Target = new WindowIdentity(0, 0, string.Empty, new RectD(0, 0, 0, 0))
        };

        AssertInvalid(Proposal(current), current);
    }

    // AN-006
    [Fact]
    public void A_proposal_without_any_marks_is_rejected()
    {
        var current = Context(Now);
        var proposal = new AnnotationProposal(current.ObservationId, current.Generation, current.Display.Id, Array.Empty<AnnotationMark>());

        AssertInvalid(proposal, current);
    }

    // AN-007
    [Fact]
    public void Non_finite_mark_coordinates_are_rejected()
    {
        var current = Context(Now);
        var proposal = Proposal(current, new AnnotationMark("ring", new RectD(double.NaN, 10, 20, 20), "Target"));

        AssertInvalid(proposal, current);
    }

    // AN-008
    [Theory]
    [InlineData(-1, 20, 20, 20)]
    [InlineData(1200, 20, 100, 20)]
    [InlineData(20, -1, 20, 20)]
    [InlineData(20, 700, 20, 30)]
    public void Marks_outside_the_captured_frame_are_rejected(double x, double y, double width, double height)
    {
        var current = Context(Now);
        var proposal = Proposal(current, new AnnotationMark("ring", new RectD(x, y, width, height), "Target"));

        AssertInvalid(proposal, current);
    }

    // AN-009
    [Theory]
    [InlineData("ring")]
    [InlineData("arrow")]
    [InlineData("label")]
    public void The_documented_annotation_kinds_are_accepted(string kind)
    {
        var current = Context(Now);

        var result = AnnotationValidator.Validate(Proposal(current, new AnnotationMark(kind, new RectD(100, 50, 200, 100), "Target")), current, Now);

        Assert.True(result.Valid, result.Reason);
    }

    // AN-010
    [Fact]
    public void An_unknown_annotation_kind_is_rejected()
    {
        var current = Context(Now);

        AssertInvalid(Proposal(current, new AnnotationMark("circle", new RectD(100, 50, 200, 100), "Target")), current);
    }

    // AN-011
    [Fact]
    public void Frame_coordinates_map_to_literal_physical_pixels_with_a_negative_origin_and_150_percent_scale()
    {
        var current = Context(Now);
        var frameRect = new RectD(100, 50, 200, 100);

        var actual = AnnotationValidator.MapToDisplay(frameRect, current);

        Assert.Equal(new RectD(-1770, -125, 300, 150), actual);
    }

    // DG-001
    [Fact]
    public void Notch_bounds_are_centered_and_returned_in_physical_pixels()
    {
        var display = new DisplayInfo("left", "Left", new RectD(-1920, -200, 1920, 1080), 1.5);

        var actual = DisplayGeometry.NotchBounds(display, widthDip: 200, heightDip: 80, offsetDip: 20);

        Assert.Equal(new RectD(-1110, -170, 300, 120), actual);
    }

    private static ScreenContext Context(DateTimeOffset capturedAt)
    {
        return new ScreenContext(
            ObservationId,
            Generation: 9,
            new DisplayInfo("left", "Left", new RectD(-1920, -200, 1920, 1080), 1.5),
            FrameWidth: 1280,
            FrameHeight: 720,
            new WindowIdentity(123, 456, "fixture", new RectD(100, 100, 400, 300)),
            capturedAt);
    }

    private static AnnotationProposal Proposal(ScreenContext context, params AnnotationMark[] marks)
    {
        return new AnnotationProposal(
            context.ObservationId,
            context.Generation,
            context.Display.Id,
            marks.Length == 0
                ? new[] { new AnnotationMark("ring", new RectD(100, 50, 200, 100), "Target") }
                : marks);
    }

    private static void AssertInvalid(AnnotationProposal proposal, ScreenContext current)
    {
        var result = AnnotationValidator.Validate(proposal, current, Now);

        Assert.False(result.Valid, result.Reason);
    }
}
