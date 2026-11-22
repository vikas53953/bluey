using Boo.Core;

namespace Boo.Core.Tests;

// FROZEN acceptance tests for the round 3 notch status word (Sprint 1 MVP pick 4, 22 Nov 2026): "Listening" while
// the microphone is on, "Thinking…" while an answer runs, the remaining focus time (mm:ss) while a focus session runs
// and the hanging panel is closed. Written before the implementation; builders must not edit, weaken, skip or delete.
public sealed class NotchStatusWordTests
{
    private static NotchFaceSpec D => FaceSpecFixture.Load().Face;

    private static FaceInputs Running(int seconds, bool panelOpen = false) =>
        new(FocusRemainingSeconds: seconds, PanelOpen: panelOpen);

    [Theory]
    [InlineData(NotchFaceState.Listening)]
    [InlineData(NotchFaceState.Thinking)]
    public void Listening_and_thinking_carry_their_word_whatever_else_is_going_on(NotchFaceState state)
    {
        var expected = state == NotchFaceState.Listening ? "Listening" : "Thinking…";
        Assert.Equal(expected, NotchStatusWord.For(new FaceInputs(), state));
        Assert.Equal(expected, NotchStatusWord.For(Running(1499), state));
        Assert.Equal(expected, NotchStatusWord.For(Running(1499, panelOpen: true), state));
        Assert.Equal(expected, NotchStatusWord.For(new FaceInputs(UserHidden: true), state));
    }

    [Fact]
    public void Thinking_word_ends_with_a_single_ellipsis_character()
    {
        Assert.Equal("Thinking…", NotchStatusWord.Thinking);
        Assert.DoesNotContain("...", NotchStatusWord.Thinking, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(NotchFaceState.Hidden)]
    [InlineData(NotchFaceState.Hover)]
    [InlineData(NotchFaceState.Idle)]
    public void Resting_faces_show_the_remaining_focus_time_while_the_panel_is_closed(NotchFaceState state)
    {
        Assert.Equal("24:59", NotchStatusWord.For(Running(24 * 60 + 59), state));
        Assert.Equal("05:00", NotchStatusWord.For(Running(300), state));
        Assert.Equal("00:00", NotchStatusWord.For(Running(0), state));
    }

    [Theory]
    [InlineData(NotchFaceState.Hidden)]
    [InlineData(NotchFaceState.Hover)]
    [InlineData(NotchFaceState.Idle)]
    public void Resting_faces_show_nothing_when_no_session_runs_the_panel_is_open_or_the_user_hid_boo(NotchFaceState state)
    {
        Assert.Null(NotchStatusWord.For(new FaceInputs(), state));
        Assert.Null(NotchStatusWord.For(Running(1499, panelOpen: true), state));
        Assert.Null(NotchStatusWord.For(new FaceInputs(FocusRemainingSeconds: 1499, UserHidden: true), state));
    }

    [Theory]
    [InlineData(NotchFaceState.Responding)]
    [InlineData(NotchFaceState.Capture)]
    [InlineData(NotchFaceState.Nudge)]
    [InlineData(NotchFaceState.Paused)]
    [InlineData(NotchFaceState.Happy)]
    public void Caption_capture_paused_and_happy_faces_show_no_word_even_with_a_running_session(NotchFaceState state)
    {
        Assert.Null(NotchStatusWord.For(Running(1499), state));
        Assert.Null(NotchStatusWord.For(new FaceInputs(), state));
    }

    [Fact]
    public void Every_face_state_has_a_rule()
    {
        foreach (var state in Enum.GetValues<NotchFaceState>())
        {
            var word = NotchStatusWord.For(Running(90), state);
            Assert.True(word is null || word.Length > 0, $"{state} returned an empty word");
        }
    }

    [Fact]
    public void For_rejects_null_inputs()
    {
        Assert.Throws<ArgumentNullException>(() => NotchStatusWord.For(null!, NotchFaceState.Idle));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData(0, "00:00")]
    [InlineData(-5, "00:00")]
    [InlineData(59, "00:59")]
    [InlineData(60, "01:00")]
    [InlineData(1499, "24:59")]
    [InlineData(1500, "25:00")]
    [InlineData(5999, "99:59")]
    [InlineData(6000, "100:00")]
    [InlineData(14400, "240:00")]
    public void Focus_time_is_minutes_and_seconds_never_negative_and_minutes_keep_counting_past_99(int? seconds, string? expected)
    {
        Assert.Equal(expected, NotchStatusWord.FocusTime(seconds));
    }

    [Fact]
    public void Focus_time_uses_invariant_digits_and_a_colon()
    {
        var text = NotchStatusWord.FocusTime(754)!;
        Assert.Matches("^[0-9]{2,}:[0-9]{2}$", text);
        Assert.Equal("12:34", text);
    }

    [Theory]
    [InlineData("24:59", "88:88")]
    [InlineData("05:00", "88:88")]
    [InlineData("100:00", "888:88")]
    [InlineData("Listening", "Listening")]
    [InlineData("Thinking…", "Thinking…")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Width_template_replaces_every_digit_with_an_8_so_a_ticking_clock_keeps_one_width(string? word, string expected)
    {
        Assert.Equal(expected, NotchStatusWord.WidthTemplate(word));
    }

    [Fact]
    public void Ticking_from_10_00_down_to_00_01_keeps_the_same_template()
    {
        var templates = Enumerable.Range(1, 600).Select(s => NotchStatusWord.WidthTemplate(NotchStatusWord.FocusTime(s))).Distinct().ToList();
        Assert.Single(templates);
        Assert.Equal("88:88", templates[0]);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("Listening", true)]
    [InlineData("24:59", true)]
    public void A_word_pins_the_notch_out_like_a_privacy_disclosure(string? word, bool pins)
    {
        Assert.Equal(pins, NotchStatusWord.Pins(word));
    }

    [Fact]
    public void Fit_keeps_the_spec_size_when_there_is_no_word_or_the_word_fits()
    {
        var face = D;
        Assert.Equal(face.SizeFor(NotchFaceState.Idle), NotchStatusWord.Fit(face, NotchFaceState.Idle, 0));
        Assert.Equal(face.SizeFor(NotchFaceState.Idle), NotchStatusWord.Fit(face, NotchFaceState.Idle, -4));
        Assert.Equal(face.SizeFor(NotchFaceState.Idle), NotchStatusWord.Fit(face, NotchFaceState.Idle, double.NaN));
        // Responding is 360 wide: a 60 DIP word fits beside the 80 DIP eyes with room to spare.
        Assert.Equal(face.SizeFor(NotchFaceState.Responding), NotchStatusWord.Fit(face, NotchFaceState.Responding, 60));
    }

    [Theory]
    [InlineData(NotchFaceState.Idle, 38)]
    [InlineData(NotchFaceState.Listening, 62)]
    [InlineData(NotchFaceState.Thinking, 64)]
    public void Fit_widens_the_body_exactly_to_padding_eyes_gap_word_padding_and_keeps_the_height(NotchFaceState state, double wordWidth)
    {
        var face = D;
        var spec = face.SizeFor(state);
        var fitted = NotchStatusWord.Fit(face, state, wordWidth);
        var eyes = 2 * face.Eye.Width + face.Eye.Gap;
        var needed = 2 * LivingInk.SidePadding + eyes + LivingInk.SlotGap + wordWidth;
        Assert.Equal(Math.Max(spec.Width, needed), fitted.Width, 6);
        Assert.Equal(spec.Height, fitted.Height);
        Assert.True(fitted.Width >= spec.Width, "Fit never narrows the spec body");
    }

    [Theory]
    [InlineData(NotchFaceState.Idle, 38)]
    [InlineData(NotchFaceState.Listening, 62)]
    [InlineData(NotchFaceState.Thinking, 64)]
    [InlineData(NotchFaceState.Hover, 44)]
    public void Place_puts_the_word_slot_right_of_the_eyes_inside_the_body_and_the_eyes_inside_the_padding(NotchFaceState state, double wordWidth)
    {
        var face = D;
        var body = NotchStatusWord.Fit(face, state, wordWidth);
        var row = LivingInk.Place(face, body, state, wordWidth);
        var eye = face.Eye;

        Assert.True(row.Trail.Width >= wordWidth, $"trail {row.Trail.Width} narrower than the word {wordWidth}");
        Assert.Equal(body.Width - LivingInk.SidePadding, row.Trail.X + row.Trail.Width, 6);
        Assert.True(row.Trail.X >= row.RightEye.X + eye.Width / 2 + LivingInk.SlotGap - 1e-6,
            $"the word slot at {row.Trail.X} overlaps the right eye ending at {row.RightEye.X + eye.Width / 2}");
        Assert.True(row.LeftEye.X - eye.Width / 2 >= LivingInk.SidePadding - 1e-6,
            $"the left eye at {row.LeftEye.X - eye.Width / 2} runs into the side padding");
        Assert.Equal(row.LeftEye.Y, row.RightEye.Y);
        Assert.InRange(row.LeftEye.Y, eye.Height * 0.4, row.RowHeight);
    }

    [Fact]
    public void Place_without_a_word_is_unchanged_from_the_living_ink_layout()
    {
        var face = D;
        foreach (var state in Enum.GetValues<NotchFaceState>())
        {
            var body = face.SizeFor(state);
            Assert.Equal(LivingInk.Place(face, body, state), LivingInk.Place(face, body, state, 0));
            Assert.Equal(LivingInk.Place(face, body, state), LivingInk.Place(face, body, state, -10));
        }
    }

    [Fact]
    public void Place_with_a_word_keeps_the_wider_of_the_states_own_trail_slot_and_the_word()
    {
        var face = D;
        var body = face.SizeFor(NotchFaceState.Responding);
        var bars = LivingInk.SlotWidths(NotchFaceState.Responding).Trail;
        Assert.Equal(bars, LivingInk.Place(face, body, NotchFaceState.Responding, bars - 5).Trail.Width, 6);
        Assert.Equal(bars + 5, LivingInk.Place(face, body, NotchFaceState.Responding, bars + 5).Trail.Width, 6);
    }

    [Fact]
    public void The_new_inputs_do_not_change_the_face_state()
    {
        Assert.Equal(NotchFaceState.Hidden, FaceStateResolver.Resolve(Running(1499)));
        Assert.Equal(NotchFaceState.Idle, FaceStateResolver.Resolve(Running(1499) with { Revealed = true }));
        Assert.Equal(NotchFaceState.Hover, FaceStateResolver.Resolve(Running(1499, panelOpen: true) with { Hovered = true }));
        Assert.Equal(NotchFaceState.Thinking, FaceStateResolver.Resolve(Running(1499) with { Thinking = true }));
    }

    [Fact]
    public void Each_second_of_a_running_session_is_a_different_input_so_the_notch_hears_every_tick()
    {
        Assert.NotEqual(Running(1499), Running(1498));
        Assert.Equal(Running(1499), new FaceInputs(FocusRemainingSeconds: 1499));
        Assert.NotEqual(Running(1499), Running(1499, panelOpen: true));
        Assert.Equal(new FaceInputs(), new FaceInputs(PanelOpen: false, FocusRemainingSeconds: null));
    }
}
