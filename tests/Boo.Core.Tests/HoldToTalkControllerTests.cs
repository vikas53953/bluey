using Boo.Core;

namespace Boo.Core.Tests;

// Frozen acceptance tests for plan unit U4 (hold-to-talk). Builders must not edit, weaken, skip or delete.
public sealed class HoldToTalkControllerTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 8, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan by) => _now += by;
        public void AdvanceMs(double ms) => _now += TimeSpan.FromMilliseconds(ms);
    }

    private readonly ManualClock _clock = new();

    private HoldToTalkController Create() => new(_clock);

    private static TalkStep PressChord(HoldToTalkController c)
    {
        c.KeyDown(TalkKey.Ctrl);
        c.KeyDown(TalkKey.Alt);
        return c.KeyDown(TalkKey.Space);
    }

    private static void AssertCommands(TalkStep step, params TalkCommand[] expected) =>
        Assert.Equal(expected, step.Commands.ToArray());

    // ---- starting ----

    [Fact]
    public void Chord_ctrl_alt_space_starts_listening()
    {
        var c = Create();
        AssertCommands(c.KeyDown(TalkKey.Ctrl));
        AssertCommands(c.KeyDown(TalkKey.Alt));
        var step = c.KeyDown(TalkKey.Space);
        AssertCommands(step, TalkCommand.StartListening);
        Assert.True(c.IsListening);
    }

    [Theory]
    [InlineData(TalkKey.Space, TalkKey.Ctrl, TalkKey.Alt)]
    [InlineData(TalkKey.Alt, TalkKey.Space, TalkKey.Ctrl)]
    [InlineData(TalkKey.Space, TalkKey.Alt, TalkKey.Ctrl)]
    [InlineData(TalkKey.Alt, TalkKey.Ctrl, TalkKey.Space)]
    public void Chord_starts_in_any_order(TalkKey first, TalkKey second, TalkKey third)
    {
        var c = Create();
        AssertCommands(c.KeyDown(first));
        AssertCommands(c.KeyDown(second));
        AssertCommands(c.KeyDown(third), TalkCommand.StartListening);
        Assert.True(c.IsListening);
    }

    [Theory]
    [InlineData(TalkKey.Ctrl)]
    [InlineData(TalkKey.Alt)]
    public void Partial_chord_does_not_start(TalkKey modifier)
    {
        var c = Create();
        AssertCommands(c.KeyDown(modifier));
        AssertCommands(c.KeyDown(TalkKey.Space));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Plain_space_does_nothing()
    {
        var c = Create();
        AssertCommands(c.KeyDown(TalkKey.Space));
        AssertCommands(c.KeyUp(TalkKey.Space));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Auto_repeat_space_downs_do_not_restart_while_listening()
    {
        var c = Create();
        PressChord(c);
        for (var i = 0; i < 10; i++)
        {
            _clock.AdvanceMs(33);
            AssertCommands(c.KeyDown(TalkKey.Space, isRepeat: true));
        }
        Assert.True(c.IsListening);
    }

    [Fact]
    public void Auto_repeat_space_after_turn_ended_does_not_restart()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(500);
        AssertCommands(c.KeyUp(TalkKey.Ctrl), TalkCommand.StopAndSend);
        // Space never went up; further Space downs are only auto-repeats and must not start a turn.
        AssertCommands(c.KeyDown(TalkKey.Space, isRepeat: true));
        AssertCommands(c.KeyDown(TalkKey.Space, isRepeat: true));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Repeated_modifier_downs_are_harmless()
    {
        var c = Create();
        c.KeyDown(TalkKey.Ctrl);
        c.KeyDown(TalkKey.Ctrl, isRepeat: true);
        c.KeyDown(TalkKey.Alt);
        c.KeyDown(TalkKey.Alt, isRepeat: true);
        AssertCommands(c.KeyDown(TalkKey.Space), TalkCommand.StartListening);
        AssertCommands(c.KeyDown(TalkKey.Ctrl, isRepeat: true));
        AssertCommands(c.KeyDown(TalkKey.Alt, isRepeat: true));
        Assert.True(c.IsListening);
    }

    [Fact]
    public void Eyes_press_starts_listening()
    {
        var c = Create();
        AssertCommands(c.EyesPressed(), TalkCommand.StartListening);
        Assert.True(c.IsListening);
    }

    // ---- stopping ----

    [Theory]
    [InlineData(TalkKey.Space)]
    [InlineData(TalkKey.Ctrl)]
    [InlineData(TalkKey.Alt)]
    public void Releasing_any_chord_key_stops_and_sends(TalkKey released)
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(800);
        var step = c.KeyUp(released);
        AssertCommands(step, TalkCommand.StopAndSend);
        Assert.Null(step.Hint);
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Releasing_remaining_keys_after_stop_emits_nothing_more()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(800);
        AssertCommands(c.KeyUp(TalkKey.Space), TalkCommand.StopAndSend);
        AssertCommands(c.KeyUp(TalkKey.Alt));
        AssertCommands(c.KeyUp(TalkKey.Ctrl));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Eyes_release_stops_and_sends()
    {
        var c = Create();
        c.EyesPressed();
        _clock.AdvanceMs(1500);
        AssertCommands(c.EyesReleased(), TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Re_pressing_space_with_modifiers_still_held_starts_a_new_turn()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(600);
        AssertCommands(c.KeyUp(TalkKey.Space), TalkCommand.StopAndSend);
        _clock.AdvanceMs(300);
        AssertCommands(c.KeyDown(TalkKey.Space), TalkCommand.StartListening);
        Assert.True(c.IsListening);
    }

    [Fact]
    public void Release_events_without_a_hold_are_ignored()
    {
        var c = Create();
        AssertCommands(c.KeyUp(TalkKey.Space));
        AssertCommands(c.KeyUp(TalkKey.Ctrl));
        AssertCommands(c.EyesReleased());
        AssertCommands(c.Tick());
        Assert.False(c.IsListening);
    }

    // ---- taps ----

    [Fact]
    public void Tap_shorter_than_200ms_cancels_and_hints()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(199);
        var step = c.KeyUp(TalkKey.Space);
        AssertCommands(step, TalkCommand.Cancel);
        Assert.Equal("Hold to talk", step.Hint);
        Assert.Equal(HoldToTalkController.TapHint, step.Hint);
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Hold_of_exactly_200ms_is_sent()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(200);
        var step = c.KeyUp(TalkKey.Space);
        AssertCommands(step, TalkCommand.StopAndSend);
        Assert.Null(step.Hint);
    }

    [Fact]
    public void Eye_tap_cancels_and_hints()
    {
        var c = Create();
        c.EyesPressed();
        _clock.AdvanceMs(50);
        var step = c.EyesReleased();
        AssertCommands(step, TalkCommand.Cancel);
        Assert.Equal(HoldToTalkController.TapHint, step.Hint);
    }

    [Fact]
    public void Thresholds_match_plan()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(200), HoldToTalkController.MinimumHold);
        Assert.Equal(TimeSpan.FromSeconds(120), HoldToTalkController.MaximumHold);
    }

    // ---- keys + eyes together ----

    [Fact]
    public void Keys_then_eyes_stop_only_when_both_released()
    {
        var c = Create();
        AssertCommands(PressChord(c), TalkCommand.StartListening);
        AssertCommands(c.EyesPressed());
        _clock.AdvanceMs(1000);
        AssertCommands(c.KeyUp(TalkKey.Space));
        Assert.True(c.IsListening);
        AssertCommands(c.EyesReleased(), TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Eyes_then_keys_stop_only_when_both_released()
    {
        var c = Create();
        AssertCommands(c.EyesPressed(), TalkCommand.StartListening);
        AssertCommands(PressChord(c));
        _clock.AdvanceMs(1000);
        AssertCommands(c.EyesReleased());
        Assert.True(c.IsListening);
        AssertCommands(c.KeyUp(TalkKey.Alt), TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Combined_hold_duration_counts_from_first_start()
    {
        var c = Create();
        c.EyesPressed();
        _clock.AdvanceMs(150);
        PressChord(c);
        _clock.AdvanceMs(100);
        AssertCommands(c.EyesReleased());
        // 250 ms since the hold began: a real turn, not a tap, even though the key part lasted 100 ms.
        var step = c.KeyUp(TalkKey.Space);
        AssertCommands(step, TalkCommand.StopAndSend);
        Assert.Null(step.Hint);
    }

    // ---- focus loss ----

    [Fact]
    public void Focus_loss_ends_an_eye_hold()
    {
        var c = Create();
        c.EyesPressed();
        _clock.AdvanceMs(700);
        AssertCommands(c.FocusLost(), TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
        AssertCommands(c.EyesReleased());
    }

    [Fact]
    public void Focus_loss_does_not_end_a_key_hold_and_release_still_sends()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(400);
        AssertCommands(c.FocusLost());
        Assert.True(c.IsListening);
        _clock.AdvanceMs(400);
        AssertCommands(c.KeyUp(TalkKey.Ctrl), TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
    }

    // ---- stop / lock / sleep ----

    public static TheoryData<string> Aborts => new() { "stop", "lock", "sleep" };

    private static TalkStep Abort(HoldToTalkController c, string kind) => kind switch
    {
        "stop" => c.Stop(),
        "lock" => c.SessionLocked(),
        "sleep" => c.Suspending(),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    [Theory]
    [MemberData(nameof(Aborts))]
    public void Abort_cancels_immediately_and_later_key_ups_are_ignored(string kind)
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(900);
        AssertCommands(Abort(c, kind), TalkCommand.Cancel);
        Assert.False(c.IsListening);
        AssertCommands(c.KeyUp(TalkKey.Space));
        AssertCommands(c.KeyUp(TalkKey.Alt));
        AssertCommands(c.KeyUp(TalkKey.Ctrl));
        Assert.False(c.IsListening);
    }

    [Theory]
    [MemberData(nameof(Aborts))]
    public void Abort_cancels_eye_hold_and_later_eye_release_is_ignored(string kind)
    {
        var c = Create();
        c.EyesPressed();
        _clock.AdvanceMs(900);
        AssertCommands(Abort(c, kind), TalkCommand.Cancel);
        AssertCommands(c.EyesReleased());
        Assert.False(c.IsListening);
    }

    [Theory]
    [MemberData(nameof(Aborts))]
    public void Abort_cancel_carries_no_tap_hint(string kind)
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(900);
        Assert.Null(Abort(c, kind).Hint);
    }

    [Theory]
    [MemberData(nameof(Aborts))]
    public void Abort_while_idle_emits_nothing(string kind)
    {
        var c = Create();
        AssertCommands(Abort(c, kind));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Auto_repeat_after_stop_does_not_restart()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(900);
        c.Stop();
        AssertCommands(c.KeyDown(TalkKey.Space, isRepeat: true));
        AssertCommands(c.KeyDown(TalkKey.Space, isRepeat: true));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Fresh_chord_after_stop_and_full_release_starts_again()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(900);
        c.Stop();
        c.KeyUp(TalkKey.Space);
        c.KeyUp(TalkKey.Alt);
        c.KeyUp(TalkKey.Ctrl);
        _clock.AdvanceMs(1000);
        AssertCommands(PressChord(c), TalkCommand.StartListening);
    }

    // ---- safety cap ----

    [Fact]
    public void Tick_before_cap_does_nothing()
    {
        var c = Create();
        PressChord(c);
        _clock.Advance(TimeSpan.FromSeconds(119.9));
        AssertCommands(c.Tick());
        Assert.True(c.IsListening);
    }

    [Fact]
    public void Hold_longer_than_120s_stops_and_sends_automatically()
    {
        var c = Create();
        PressChord(c);
        _clock.Advance(TimeSpan.FromSeconds(120.1));
        AssertCommands(c.Tick(), TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
        AssertCommands(c.Tick());
    }

    [Fact]
    public void After_cap_key_ups_and_repeats_emit_nothing()
    {
        var c = Create();
        PressChord(c);
        _clock.Advance(TimeSpan.FromSeconds(121));
        c.Tick();
        AssertCommands(c.KeyDown(TalkKey.Space, isRepeat: true));
        AssertCommands(c.KeyUp(TalkKey.Space));
        AssertCommands(c.KeyUp(TalkKey.Alt));
        AssertCommands(c.KeyUp(TalkKey.Ctrl));
        Assert.False(c.IsListening);
    }

    [Fact]
    public void Eye_hold_is_capped_too()
    {
        var c = Create();
        c.EyesPressed();
        _clock.Advance(TimeSpan.FromSeconds(125));
        AssertCommands(c.Tick(), TalkCommand.StopAndSend);
        AssertCommands(c.EyesReleased());
    }

    // ---- never stuck on ----

    [Theory]
    [InlineData(new[] { TalkKey.Space, TalkKey.Alt, TalkKey.Ctrl })]
    [InlineData(new[] { TalkKey.Ctrl, TalkKey.Space, TalkKey.Alt })]
    [InlineData(new[] { TalkKey.Alt, TalkKey.Ctrl, TalkKey.Space })]
    public void Mic_is_never_on_after_all_inputs_released(TalkKey[] releaseOrder)
    {
        var c = Create();
        c.EyesPressed();
        PressChord(c);
        c.KeyDown(TalkKey.Space, isRepeat: true);
        _clock.AdvanceMs(1000);
        var sends = 0;
        foreach (var key in releaseOrder)
        {
            sends += c.KeyUp(key).Commands.Count(x => x == TalkCommand.StopAndSend);
        }
        sends += c.EyesReleased().Commands.Count(x => x == TalkCommand.StopAndSend);
        Assert.False(c.IsListening);
        Assert.Equal(1, sends);
    }

    [Fact]
    public void Every_start_is_matched_by_exactly_one_stop_or_cancel()
    {
        var c = Create();
        var starts = 0;
        var ends = 0;
        void Count(TalkStep s)
        {
            starts += s.Commands.Count(x => x == TalkCommand.StartListening);
            ends += s.Commands.Count(x => x is TalkCommand.StopAndSend or TalkCommand.Cancel);
        }

        Count(PressChord(c)); _clock.AdvanceMs(50); Count(c.KeyUp(TalkKey.Space));
        Count(c.KeyDown(TalkKey.Space)); _clock.AdvanceMs(500); Count(c.EyesPressed()); Count(c.KeyUp(TalkKey.Alt));
        _clock.AdvanceMs(500); Count(c.EyesReleased()); Count(c.KeyUp(TalkKey.Space)); Count(c.KeyUp(TalkKey.Ctrl));
        Count(c.EyesPressed()); _clock.AdvanceMs(300); Count(c.SessionLocked()); Count(c.EyesReleased());
        Count(PressChord(c)); _clock.Advance(TimeSpan.FromSeconds(130)); Count(c.Tick()); Count(c.KeyUp(TalkKey.Space));

        Assert.Equal(4, starts);
        Assert.Equal(starts, ends);
        Assert.False(c.IsListening);
    }

    // ---- interrupting playback ----

    [Fact]
    public void Hold_during_playback_interrupts_before_listening()
    {
        var c = Create();
        c.SetPlaybackActive(true);
        AssertCommands(PressChord(c), TalkCommand.Interrupt, TalkCommand.StartListening);
    }

    [Fact]
    public void Eye_hold_during_playback_interrupts_before_listening()
    {
        var c = Create();
        c.SetPlaybackActive(true);
        AssertCommands(c.EyesPressed(), TalkCommand.Interrupt, TalkCommand.StartListening);
    }

    [Fact]
    public void No_interrupt_when_nothing_is_playing()
    {
        var c = Create();
        c.SetPlaybackActive(true);
        c.SetPlaybackActive(false);
        AssertCommands(PressChord(c), TalkCommand.StartListening);
    }

    [Fact]
    public void Interrupt_is_issued_once_per_playback()
    {
        var c = Create();
        c.SetPlaybackActive(true);
        AssertCommands(PressChord(c), TalkCommand.Interrupt, TalkCommand.StartListening);
        _clock.AdvanceMs(500);
        c.KeyUp(TalkKey.Space);
        // Playback was interrupted; nothing new reported as playing.
        AssertCommands(c.KeyDown(TalkKey.Space), TalkCommand.StartListening);
    }

    // ---- swallowing Space ----

    [Fact]
    public void Plain_space_is_never_swallowed()
    {
        var c = Create();
        Assert.False(c.KeyDown(TalkKey.Space).SwallowKey);
        Assert.False(c.KeyDown(TalkKey.Space, isRepeat: true).SwallowKey);
        Assert.False(c.KeyUp(TalkKey.Space).SwallowKey);
    }

    [Theory]
    [InlineData(TalkKey.Ctrl)]
    [InlineData(TalkKey.Alt)]
    public void Space_with_only_one_modifier_is_not_swallowed(TalkKey modifier)
    {
        var c = Create();
        c.KeyDown(modifier);
        Assert.False(c.KeyDown(TalkKey.Space).SwallowKey);
        Assert.False(c.KeyUp(TalkKey.Space).SwallowKey);
    }

    [Fact]
    public void Space_completing_the_chord_and_its_repeats_and_release_are_swallowed()
    {
        var c = Create();
        var start = PressChord(c);
        Assert.True(start.SwallowKey);
        Assert.True(c.KeyDown(TalkKey.Space, isRepeat: true).SwallowKey);
        _clock.AdvanceMs(600);
        Assert.True(c.KeyUp(TalkKey.Space).SwallowKey);
    }

    [Fact]
    public void Modifier_keys_are_never_swallowed()
    {
        var c = Create();
        Assert.False(c.KeyDown(TalkKey.Ctrl).SwallowKey);
        Assert.False(c.KeyDown(TalkKey.Alt).SwallowKey);
        c.KeyDown(TalkKey.Space);
        Assert.False(c.KeyDown(TalkKey.Ctrl, isRepeat: true).SwallowKey);
        _clock.AdvanceMs(600);
        Assert.False(c.KeyUp(TalkKey.Ctrl).SwallowKey);
        Assert.False(c.KeyUp(TalkKey.Alt).SwallowKey);
    }

    [Fact]
    public void Space_chord_tap_is_still_swallowed_so_nothing_types()
    {
        var c = Create();
        Assert.True(PressChord(c).SwallowKey);
        _clock.AdvanceMs(80);
        var up = c.KeyUp(TalkKey.Space);
        AssertCommands(up, TalkCommand.Cancel);
        Assert.True(up.SwallowKey);
    }

    [Fact]
    public void Space_held_after_modifier_release_stays_swallowed_until_space_up()
    {
        // Once a Space press was taken by the chord, its remaining repeats must not leak as Alt+Space / Space.
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(600);
        c.KeyUp(TalkKey.Ctrl);
        Assert.True(c.KeyDown(TalkKey.Space, isRepeat: true).SwallowKey);
        Assert.True(c.KeyUp(TalkKey.Space).SwallowKey);
        // A brand new plain Space press afterwards is not swallowed.
        c.KeyUp(TalkKey.Alt);
        Assert.False(c.KeyDown(TalkKey.Space).SwallowKey);
    }

    [Fact]
    public void Space_after_abort_and_full_release_is_not_swallowed()
    {
        var c = Create();
        PressChord(c);
        _clock.AdvanceMs(600);
        c.Stop();
        c.KeyUp(TalkKey.Space);
        c.KeyUp(TalkKey.Alt);
        c.KeyUp(TalkKey.Ctrl);
        Assert.False(c.KeyDown(TalkKey.Space).SwallowKey);
    }

    [Fact]
    public void Eye_only_hold_does_not_swallow_plain_space()
    {
        var c = Create();
        c.EyesPressed();
        Assert.False(c.KeyDown(TalkKey.Space).SwallowKey);
        Assert.False(c.KeyUp(TalkKey.Space).SwallowKey);
        Assert.True(c.IsListening);
    }
}
