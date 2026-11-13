using System.Collections.Immutable;
using Boo.Core;
using E = Boo.Core.NotchEvent;
using X = Boo.Core.NotchEffect;

namespace Boo.Core.Tests;

// Agent-trust plan, Phase 2, PR 2.4: the NotchLifecycle reducer (plan section 3.2 C) checked against the invariants of
// section 3.2 B. The reducer models what the app does TODAY (docs/architecture/notch-lifecycle.md). Where today's app
// does not meet an invariant as the plan words it, the test is named "..._gap_<finding>_..._today" and pins today's
// behaviour, so changing that behaviour later is a visible, reviewed decision and not an accident.
public sealed class NotchLifecycleTests
{
    private sealed class ManualClock : TimeProvider
    {
        private DateTimeOffset _now = new(2026, 11, 13, 9, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => _now;
        public void AdvanceMs(double ms) => _now += TimeSpan.FromMilliseconds(ms);
        public void Advance(TimeSpan by) => _now += by;
    }

    private static readonly NotchFaceState[] QuietFaces = [NotchFaceState.Hidden, NotchFaceState.Idle, NotchFaceState.Hover];

    private readonly ManualClock _clock = new();
    private readonly NotchLifecycle _lifecycle;
    private NotchLifecycleState _state = NotchLifecycleState.Initial;
    private ImmutableArray<NotchEffect> _effects = ImmutableArray<NotchEffect>.Empty;

    public NotchLifecycleTests() => _lifecycle = new NotchLifecycle(_clock);

    private NotchLifecycleState Do(params NotchEvent[] events)
    {
        foreach (var e in events)
        {
            var step = _lifecycle.Reduce(_state, e);
            _state = step.State;
            _effects = step.Effects;
        }
        return _state;
    }

    private void Wait(double ms) { _clock.AdvanceMs(ms); Do(new E.Tick()); }

    private T Single<T>() where T : NotchEffect => Assert.Single(_effects.OfType<T>());

    private void None<T>() where T : NotchEffect => Assert.Empty(_effects.OfType<T>());

    // ---- INV-PRIV-1: microphone or capture on => the notch is visible ----

    [Fact]
    public void INV_PRIV_1_microphone_on_means_notch_visible_and_listening()
    {
        Do(new E.TalkDown(TalkHoldSource.Chord));

        Assert.True(_state.MicrophoneOn);
        Assert.True(_state.Revealed);
        Assert.Equal(NotchFaceState.Listening, _state.Face);
        Single<X.OpenMicrophone>();
        Assert.Contains(new X.FaceChanged(NotchFaceState.Hidden, NotchFaceState.Listening), _effects);
    }

    [Fact]
    public void INV_PRIV_1_capture_and_listening_outrank_every_other_face_and_survive_pointer_leave()
    {
        Do(new E.PointerEnter(), new E.Send(), new E.FocusStart(), new E.FocusNudge(), new E.CaptureStart());
        Assert.Equal(NotchFaceState.Capture, _state.Face);

        Do(new E.PointerLeave());
        Wait(5_000);
        Assert.True(_state.Revealed);
        Assert.Equal(NotchFaceState.Capture, _state.Face);
        Assert.Null(_state.RetractAt);
    }

    // ---- INV-PRIV-2: no capture starts while hidden without approval ----

    [Fact]
    public void INV_PRIV_2_capture_shows_the_capture_face_in_the_same_step_it_starts()
    {
        Do(new E.CaptureStart());

        Assert.True(_state.Revealed);
        Assert.Equal(NotchFaceState.Capture, _state.Face);
        Assert.Contains(new X.FaceChanged(NotchFaceState.Hidden, NotchFaceState.Capture), _effects);
    }

    [Fact]
    public void INV_PRIV_2_gap_F5_capture_starts_from_hidden_without_any_approval_today()
    {
        Assert.Equal(NotchFaceState.Hidden, _state.Face);
        Do(new E.FocusStart());
        Assert.Equal(NotchFaceState.Hidden, _state.Face);

        Do(new E.CaptureStart());

        Assert.Null(_state.PendingProposal);
        Assert.Equal(1, _state.CapturesInFlight);
    }

    // ---- INV-APPLY-1: a change is applied only after Approve of that same proposal ----

    [Fact]
    public void INV_APPLY_1_apply_only_after_approve_of_the_same_proposal()
    {
        Do(new E.Send(), new E.Reply(1), new E.Propose(7, 1, EditJournal.File));
        None<X.ApplyProposal>();

        Do(new E.Approve(8));
        None<X.ApplyProposal>();
        Assert.Equal(7, _state.PendingProposal?.Id);

        Do(new E.Approve(7));
        Assert.Equal(new X.ApplyProposal(7, 1), Single<X.ApplyProposal>());
        Assert.Null(_state.PendingProposal);
        Assert.Contains(7L, _state.Workspace);

        Do(new E.Approve(7));
        None<X.ApplyProposal>();
    }

    [Fact]
    public void INV_APPLY_1_reject_drops_the_proposal_and_nothing_is_applied()
    {
        Do(new E.Send(), new E.Propose(3, 1, EditJournal.Field), new E.Reject());
        Assert.Null(_state.PendingProposal);

        Do(new E.Approve(3));
        None<X.ApplyProposal>();
        Assert.Empty(_state.Workspace);
    }

    // ---- INV-APPLY-2: undo only what was applied, only once, and it restores the before-state ----

    [Fact]
    public void INV_APPLY_2_field_undo_restores_the_workspace_and_happens_only_once()
    {
        Do(new E.Send());
        var before = _state.Workspace;
        Do(new E.Propose(5, 1, EditJournal.Field), new E.Approve(5));
        Assert.Contains(5L, _state.Workspace);

        Do(new E.Undo());
        Assert.Equal(new NotchJournalEntry(5, EditJournal.Field, IsUndo: false), _state.PendingUndo?.Entry);
        Do(new E.ApproveUndo());

        Assert.Equal(new X.ApplyUndo(new NotchJournalEntry(5, EditJournal.Field, false)), Single<X.ApplyUndo>());
        Assert.Equal(before, _state.Workspace);
        Assert.Null(_state.FieldJournal);

        Do(new E.Undo(), new E.ApproveUndo());
        Assert.Null(_state.PendingUndo);
        None<X.ApplyUndo>();
        Assert.Equal(before, _state.Workspace);
    }

    [Fact]
    public void INV_APPLY_2_undo_with_nothing_applied_shows_no_card_and_changes_nothing()
    {
        Do(new E.Undo());
        Assert.Null(_state.PendingUndo);

        Do(new E.ApproveUndo());
        None<X.ApplyUndo>();
        Assert.Empty(_state.Workspace);
    }

    [Fact]
    public void INV_APPLY_2_the_file_journal_wins_over_the_field_journal()
    {
        Do(new E.Send(), new E.Propose(1, 1, EditJournal.Field), new E.Approve(1));
        Do(new E.Send(), new E.Propose(2, 2, EditJournal.File), new E.Approve(2));

        Do(new E.Undo());

        Assert.Equal(new NotchJournalEntry(2, EditJournal.File, false), _state.PendingUndo?.Entry);
    }

    [Fact]
    public void INV_APPLY_2_gap_F6_a_second_file_undo_reapplies_the_change_today()
    {
        Do(new E.Send(), new E.Propose(9, 1, EditJournal.File), new E.Approve(9));
        Do(new E.Undo(), new E.ApproveUndo());
        Assert.DoesNotContain(9L, _state.Workspace);
        Assert.Equal(new NotchJournalEntry(9, EditJournal.File, IsUndo: true), _state.FileJournal);

        Do(new E.Undo(), new E.ApproveUndo());

        Assert.Equal(new X.ApplyUndo(new NotchJournalEntry(9, EditJournal.File, true)), Single<X.ApplyUndo>());
        Assert.Contains(9L, _state.Workspace);
    }

    [Fact]
    public void INV_APPLY_2_undo_card_dies_with_stop()
    {
        Do(new E.Send(), new E.Propose(4, 1, EditJournal.Field), new E.Approve(4), new E.Undo(), new E.Stop());

        Do(new E.ApproveUndo());

        None<X.ApplyUndo>();
        Assert.Contains(4L, _state.Workspace);
    }

    // ---- INV-APPLY-3: a new request never applies a proposal from an older request ----

    [Fact]
    public void INV_APPLY_3_a_new_request_drops_the_older_proposal()
    {
        Do(new E.Send(), new E.Propose(1, 1, EditJournal.File));

        Do(new E.Send());
        Assert.Null(_state.PendingProposal);
        Assert.Contains(new X.CancelRequest(1), _effects);
        Assert.Contains(new X.BeginRequest(2), _effects);

        Do(new E.Approve(1));
        None<X.ApplyProposal>();
        Assert.Empty(_state.Workspace);
    }

    [Fact]
    public void INV_APPLY_3_a_late_proposal_from_an_older_request_is_ignored()
    {
        Do(new E.Send(), new E.Send());

        Do(new E.Propose(1, 1, EditJournal.Field), new E.Approve(1));

        Assert.Null(_state.PendingProposal);
        None<X.ApplyProposal>();
    }

    [Fact]
    public void INV_APPLY_3_after_stop_no_request_is_current_so_nothing_can_be_proposed()
    {
        Do(new E.Send(), new E.Stop());

        Do(new E.Propose(1, 1, EditJournal.Field), new E.Approve(1));

        None<X.ApplyProposal>();
    }

    // ---- INV-FOCUS-1: each focus session emits exactly one end event ----

    [Fact]
    public void INV_FOCUS_1_ending_by_hand_emits_exactly_one_end_event()
    {
        Do(new E.FocusStart(), new E.FocusPause(), new E.FocusEnd());
        Assert.Equal(new X.FocusEnded(1, FocusEndReason.EndedByUser), Single<X.FocusEnded>());
        Assert.Equal(NotchFaceState.Happy, _state.Face);

        Do(new E.FocusEnd());
        None<X.FocusEnded>();
        Do(new E.FocusCompleted());
        None<X.FocusEnded>();
    }

    [Fact]
    public void INV_FOCUS_1_completing_emits_exactly_one_end_event_and_a_paused_session_cannot_complete()
    {
        Do(new E.FocusStart(), new E.FocusPause(), new E.FocusCompleted());
        None<X.FocusEnded>();

        Do(new E.FocusResume(), new E.FocusCompleted());
        Assert.Equal(new X.FocusEnded(1, FocusEndReason.Completed), Single<X.FocusEnded>());

        Do(new E.FocusCompleted(), new E.FocusEnd());
        None<X.FocusEnded>();
    }

    [Fact]
    public void INV_FOCUS_1_gap_F9_a_session_replaced_by_a_new_start_ends_with_no_event_today()
    {
        Do(new E.FocusStart());

        Do(new E.FocusStart());

        None<X.FocusEnded>();
        Assert.Equal(2, _state.FocusSessionId);
        Assert.Equal(FocusStatus.Running, _state.Focus);
    }

    // ---- INV-TALK-1: after TalkUp (or Stop) the microphone is off ----

    [Fact]
    public void INV_TALK_1_last_release_turns_the_microphone_off_and_sends_the_turn()
    {
        Do(new E.TalkDown(TalkHoldSource.Chord), new E.TalkDown(TalkHoldSource.Eyes));
        _clock.AdvanceMs(500);

        Do(new E.TalkUp(TalkHoldSource.Chord));
        Assert.True(_state.MicrophoneOn);
        None<X.CloseMicrophone>();

        Do(new E.TalkUp(TalkHoldSource.Eyes));
        Assert.False(_state.MicrophoneOn);
        Single<X.CloseMicrophone>();
        Single<X.CommitTurn>();
    }

    [Fact]
    public void INV_TALK_1_a_tap_shorter_than_the_minimum_hold_is_discarded_with_the_hint()
    {
        Do(new E.TalkDown(TalkHoldSource.Eyes));
        _clock.AdvanceMs(HoldToTalkController.MinimumHold.TotalMilliseconds - 1);

        Do(new E.TalkUp(TalkHoldSource.Eyes));

        Assert.False(_state.MicrophoneOn);
        Assert.Equal(new X.DiscardTurn(HoldToTalkController.TapHint), Single<X.DiscardTurn>());
        None<X.CommitTurn>();
    }

    [Fact]
    public void INV_TALK_1_stop_turns_the_microphone_off_and_later_releases_do_nothing()
    {
        Do(new E.TalkDown(TalkHoldSource.Chord));

        Do(new E.Stop());
        Assert.False(_state.MicrophoneOn);
        Single<X.CloseMicrophone>();

        Do(new E.TalkUp(TalkHoldSource.Chord));
        Assert.Empty(_effects);
    }

    [Fact]
    public void INV_TALK_1_a_hold_past_the_maximum_is_sent_and_the_microphone_closed()
    {
        Do(new E.TalkDown(TalkHoldSource.Chord));
        _clock.Advance(HoldToTalkController.MaximumHold);
        Do(new E.Tick());
        Assert.True(_state.MicrophoneOn);

        _clock.AdvanceMs(1);
        Do(new E.Tick());

        Assert.False(_state.MicrophoneOn);
        Single<X.CommitTurn>();
        Do(new E.TalkUp(TalkHoldSource.Chord));
        Assert.Empty(_effects);
    }

    // ---- INV-STOP-1: Stop from any state reaches a quiet state with the microphone and capture off ----

    public static TheoryData<string> EveryState => new()
    {
        "hidden", "hover", "idle-tail", "thinking", "responding", "listening", "both-holds", "nudge", "happy", "focus-running", "focus-paused",
        "apply-pending", "undo-pending", "stopped-already",
    };

    private void Arrange(string name)
    {
        switch (name)
        {
            case "hidden": break;
            case "hover": Do(new E.PointerEnter()); break;
            case "idle-tail": Do(new E.PointerEnter(), new E.PointerLeave()); break;
            case "thinking": Do(new E.Send()); break;
            case "responding": Do(new E.Send(), new E.Reply(1)); break;
            case "listening": Do(new E.TalkDown(TalkHoldSource.Eyes)); break;
            case "both-holds": Do(new E.TalkDown(TalkHoldSource.Eyes), new E.TalkDown(TalkHoldSource.Chord)); break;
            case "nudge": Do(new E.FocusStart(), new E.FocusNudge()); break;
            case "focus-running": Do(new E.FocusStart()); break;
            case "focus-paused": Do(new E.FocusStart(), new E.FocusPause()); break;
            case "happy": Do(new E.FocusStart(), new E.FocusEnd()); break;
            case "apply-pending": Do(new E.Send(), new E.Propose(1, 1, EditJournal.File)); break;
            case "undo-pending": Do(new E.Send(), new E.Propose(1, 1, EditJournal.File), new E.Approve(1), new E.Undo()); break;
            case "stopped-already": Do(new E.Send(), new E.Stop()); break;
            default: throw new ArgumentOutOfRangeException(nameof(name));
        }
    }

    [Theory]
    [MemberData(nameof(EveryState))]
    public void INV_STOP_1_stop_from_every_state_reaches_a_quiet_state(string name)
    {
        Arrange(name);
        var wasCurrent = _state.RequestCurrent;
        var generation = _state.Generation;

        Do(new E.Stop());

        Assert.False(_state.MicrophoneOn);
        Assert.Empty(_state.Holds);
        Assert.False(_state.CaptureActive);
        Assert.False(_state.Thinking || _state.Responding || _state.NudgeShowing || _state.Happy);
        Assert.Null(_state.PendingProposal);
        Assert.Null(_state.PendingUndo);
        Assert.False(_state.RequestCurrent);
        Assert.NotEqual(FocusStatus.Running, _state.Focus);
        if (wasCurrent) Assert.Contains(new X.CancelRequest(generation), _effects);
        if (_state.Focus == FocusStatus.Paused) Assert.Equal(NotchFaceState.Paused, _state.Face);
        else Assert.Contains(_state.Face, QuietFaces);

        Do(new E.PointerLeave());
        Wait(2_000);
        Wait(2_000);
        Assert.Equal(_state.Focus == FocusStatus.Paused ? NotchFaceState.Paused : NotchFaceState.Hidden, _state.Face);
    }

    [Fact]
    public void INV_STOP_1_gap_F4_a_focus_session_keeps_the_notch_out_on_paused_after_stop_today()
    {
        Do(new E.FocusStart(), new E.Send());

        Do(new E.Stop());
        Wait(10_000);

        Assert.Equal(FocusStatus.Paused, _state.Focus);
        Assert.Equal(NotchFaceState.Paused, _state.Face);
        Assert.True(_state.Revealed);
    }

    [Fact]
    public void INV_STOP_1_gap_the_capture_face_outlasts_stop_until_the_capture_operation_ends_today()
    {
        Do(new E.Send(), new E.CaptureStart());

        Do(new E.Stop());
        Assert.Contains(new X.CancelRequest(1), _effects);
        Assert.Equal(NotchFaceState.Capture, _state.Face);

        Do(new E.CaptureEnd());
        Assert.False(_state.CaptureActive);
        Assert.Contains(_state.Face, QuietFaces);
    }

    // ---- LIVE-1: chat always ends ("never stuck on Responding") ----

    [Fact]
    public void LIVE_1_responding_ends_when_the_reply_hold_runs_out()
    {
        Do(new E.Send(), new E.Reply(1));
        Assert.Equal(NotchFaceState.Responding, _state.Face);

        Wait(NotchLifecycleTimings.Shipped.ReplyHold.TotalMilliseconds - 1);
        Assert.Equal(NotchFaceState.Responding, _state.Face);

        Wait(1);
        Assert.False(_state.Responding);
        Assert.Contains(_state.Face, QuietFaces);
        Wait(2_000);
        Wait(2_000);
        Assert.Equal(NotchFaceState.Hidden, _state.Face);
    }

    [Fact]
    public void LIVE_1_a_long_playback_hold_is_capped_so_responding_still_ends()
    {
        Do(new E.Send(), new E.Reply(1), new E.ReplyDone(1, TimeSpan.FromHours(3)));
        Assert.Equal(_clock.GetUtcNow() + FaceMoments.MaximumHold, _state.RespondingUntil);

        Wait(FaceMoments.MaximumHold.TotalMilliseconds);

        Assert.False(_state.Responding);
    }

    [Fact]
    public void LIVE_1_reply_done_without_audio_does_not_start_responding()
    {
        Do(new E.Send(), new E.ReplyDone(1, TimeSpan.FromSeconds(5)));

        Assert.False(_state.Responding);
        Assert.False(_state.Thinking);
    }

    [Fact]
    public void LIVE_1_gap_F7_thinking_has_no_local_timeout_today()
    {
        Do(new E.Send());

        Wait(TimeSpan.FromHours(1).TotalMilliseconds);

        Assert.Equal(NotchFaceState.Thinking, _state.Face);
        Do(new E.ReplyFailed(1));
        Assert.False(_state.Thinking);
    }

    // ---- LIVE-2: hover with no further input returns to hidden ----

    [Fact]
    public void LIVE_2_hover_returns_to_hidden_after_leave_retract_delay_and_retract()
    {
        Do(new E.PointerEnter());
        Assert.Equal(NotchFaceState.Hover, _state.Face);
        Wait(NotchLifecycleTimings.Shipped.Reveal.TotalMilliseconds);

        Do(new E.PointerLeave());
        Assert.Equal(NotchFaceState.Idle, _state.Face);
        var timings = NotchLifecycleTimings.Shipped;
        Wait((timings.RetractDelay + timings.Retract).TotalMilliseconds - 1);
        Assert.Equal(NotchFaceState.Idle, _state.Face);

        Wait(1);
        Assert.Equal(NotchFaceState.Hidden, _state.Face);
        Assert.False(_state.Revealed);
        Assert.Contains(new X.FaceChanged(NotchFaceState.Idle, NotchFaceState.Hidden), _effects);
    }

    [Fact]
    public void LIVE_2_hover_lasts_as_long_as_the_pointer_stays_today()
    {
        Do(new E.PointerEnter());

        Wait(TimeSpan.FromMinutes(10).TotalMilliseconds);

        Assert.Equal(NotchFaceState.Hover, _state.Face);
    }

    // ---- main transitions (notch-lifecycle.md sections 3.1 to 3.6) ----

    [Fact]
    public void R4_leaving_mid_reveal_starts_the_retract_delay_when_the_reveal_ends()
    {
        Do(new E.PointerEnter());
        _clock.AdvanceMs(100);
        Do(new E.PointerLeave());

        var t = NotchLifecycleTimings.Shipped;
        Assert.Equal(_clock.GetUtcNow() + TimeSpan.FromMilliseconds(140) + t.RetractDelay + t.Retract, _state.RetractAt);
    }

    [Fact]
    public void R7_reentering_before_the_retract_cancels_it()
    {
        Do(new E.PointerEnter());
        Wait(300);
        Do(new E.PointerLeave());
        Wait(800);

        Do(new E.PointerEnter());
        Assert.Null(_state.RetractAt);
        Wait(5_000);

        Assert.Equal(NotchFaceState.Hover, _state.Face);
    }

    [Fact]
    public void R10_R11_a_pin_holds_the_notch_out_and_the_retract_delay_starts_at_the_unpin()
    {
        Do(new E.PointerEnter(), new E.Send(), new E.PointerLeave());
        Wait(10_000);
        Assert.Equal(NotchFaceState.Thinking, _state.Face);

        Do(new E.ReplyFailed(1));
        var t = NotchLifecycleTimings.Shipped;
        Assert.Equal(_clock.GetUtcNow() + t.RetractDelay + t.Retract, _state.RetractAt);
    }

    [Fact]
    public void F3_F4_send_thinks_then_the_reply_responds_and_a_stale_reply_is_ignored()
    {
        Do(new E.Send());
        Assert.Equal(new X.BeginRequest(1), Single<X.BeginRequest>());
        Assert.Equal(NotchFaceState.Thinking, _state.Face);

        Do(new E.Reply(0));
        Assert.Equal(NotchFaceState.Thinking, _state.Face);

        Do(new E.Reply(1));
        Assert.Equal(NotchFaceState.Responding, _state.Face);
        Assert.False(_state.Thinking);
    }

    [Fact]
    public void F7_a_committed_voice_turn_thinks_only_for_the_current_request()
    {
        Do(new E.TalkDown(TalkHoldSource.Chord));
        _clock.AdvanceMs(400);
        Do(new E.TalkUp(TalkHoldSource.Chord));

        Do(new E.TurnCommitted(0));
        Assert.False(_state.Thinking);
        Do(new E.TurnCommitted(1));
        Assert.Equal(NotchFaceState.Thinking, _state.Face);
    }

    [Fact]
    public void F22_the_hotkey_opens_the_panel_and_changes_no_notch_flag()
    {
        var before = _state;

        Do(new E.Hotkey());

        Assert.Equal(before, _state);
        Assert.Equal(new NotchEffect[] { new X.OpenPanel() }, _effects.ToArray());
    }

    [Fact]
    public void G1_a_new_talk_hold_is_a_new_request_and_drops_the_old_card()
    {
        Do(new E.Send(), new E.Propose(1, 1, EditJournal.Field));

        Do(new E.TalkDown(TalkHoldSource.Eyes));

        Assert.Null(_state.PendingProposal);
        Assert.Equal(2, _state.Generation);
        Assert.Contains(new X.CancelRequest(1), _effects);
    }

    [Fact]
    public void G1_send_while_listening_closes_the_microphone_and_discards_the_turn()
    {
        Do(new E.TalkDown(TalkHoldSource.Chord));

        Do(new E.Send());

        Assert.False(_state.MicrophoneOn);
        Single<X.CloseMicrophone>();
        Assert.Equal(new X.DiscardTurn(null), Single<X.DiscardTurn>());
        Assert.Equal(NotchFaceState.Thinking, _state.Face);
    }

    [Fact]
    public void F16_F17_a_nudge_shows_for_six_seconds_and_a_break_pauses_the_session()
    {
        Do(new E.FocusNudge());
        Assert.False(_state.NudgeShowing);

        Do(new E.FocusStart(), new E.FocusNudge());
        Assert.Equal(NotchFaceState.Nudge, _state.Face);
        Wait(NotchLifecycleTimings.Shipped.NudgeHold.TotalMilliseconds);
        Assert.False(_state.NudgeShowing);

        Do(new E.FocusNudge(), new E.NudgeBreak());
        Assert.False(_state.NudgeShowing);
        Assert.Equal(NotchFaceState.Paused, _state.Face);

        Do(new E.FocusResume());
        Assert.Equal(FocusStatus.Running, _state.Focus);
    }

    [Fact]
    public void F18_the_happy_face_lasts_two_and_a_half_seconds_and_a_new_start_clears_it()
    {
        Do(new E.FocusStart(), new E.FocusEnd());
        Assert.Equal(NotchFaceState.Happy, _state.Face);
        Wait(NotchLifecycleTimings.Shipped.HappyHold.TotalMilliseconds);
        Assert.False(_state.Happy);

        Do(new E.FocusStart(), new E.FocusEnd(), new E.FocusStart());
        Assert.False(_state.Happy);
    }

    [Fact]
    public void Proposing_replaces_an_undo_card_so_only_one_card_shows()
    {
        Do(new E.Send(), new E.Propose(1, 1, EditJournal.Field), new E.Approve(1), new E.Undo());
        Assert.NotNull(_state.PendingUndo);

        Do(new E.Propose(2, 2, EditJournal.Field));

        Assert.Null(_state.PendingUndo);
        Assert.Equal(2, _state.PendingProposal?.Id);
    }

    [Fact]
    public void Shipped_timings_match_the_notch_spec_file()
    {
        using var json = FaceSpecFixture.Open();
        var spec = NotchSpec.Load(json);

        Assert.Equal(NotchLifecycleTimings.Shipped, NotchLifecycleTimings.From(spec.Timings));
    }

    private sealed record UnknownEvent : NotchEvent;

    [Fact]
    public void An_unknown_event_is_refused_by_name()
    {
        var error = Assert.Throws<ArgumentOutOfRangeException>(() => _lifecycle.Reduce(_state, new UnknownEvent()));

        Assert.Contains(nameof(UnknownEvent), error.Message);
    }

    [Fact]
    public void The_reducer_needs_a_clock_and_never_changes_the_state_it_was_given()
    {
        Assert.Throws<ArgumentNullException>(() => new NotchLifecycle(null!));
        var start = NotchLifecycleState.Initial;

        _lifecycle.Reduce(start, new E.TalkDown(TalkHoldSource.Chord));

        Assert.Equal(new NotchLifecycleState(), start);
    }

    // ---- seeded random sequences: every safety invariant after every step ----

    private static NotchEvent RandomEvent(FuzzKit fuzz, NotchLifecycleState s)
    {
        var r = fuzz.Random;
        long Generation() => s.Generation + r.Next(-1, 2);
        long Proposal() => r.Next(1, 6);
        TalkHoldSource Source() => r.Next(2) == 0 ? TalkHoldSource.Chord : TalkHoldSource.Eyes;
        return r.Next(27) switch
        {
            0 => new E.PointerEnter(),
            1 => new E.PointerLeave(),
            2 => new E.Hotkey(),
            3 => new E.Tick(),
            4 => new E.Send(),
            5 => new E.TurnCommitted(Generation()),
            6 => new E.Reply(Generation()),
            7 => new E.ReplyDone(Generation(), TimeSpan.FromMilliseconds(r.Next(-1_000, 90_000)), r.Next(2) == 0),
            8 => new E.ReplyFailed(Generation()),
            9 => new E.TalkDown(Source()),
            10 => new E.TalkUp(Source()),
            11 => new E.CaptureStart(),
            12 => new E.CaptureEnd(),
            13 => new E.Stop(),
            14 => new E.FocusStart(),
            15 => new E.FocusPause(),
            16 => new E.FocusResume(),
            17 => new E.FocusEnd(),
            18 => new E.FocusCompleted(),
            19 => new E.FocusNudge(),
            20 => new E.NudgeBack(),
            21 => new E.NudgeBreak(),
            22 => new E.Propose(Proposal(), Generation(), r.Next(2) == 0 ? EditJournal.Field : EditJournal.File),
            23 => new E.Approve(Proposal()),
            24 => new E.Reject(),
            25 => new E.Undo(),
            _ => new E.ApproveUndo(),
        };
    }

    private static TimeSpan RandomPause(Random r) => r.Next(20) switch
    {
        0 => TimeSpan.FromSeconds(r.Next(100, 200)),
        < 6 => TimeSpan.Zero,
        _ => TimeSpan.FromMilliseconds(r.Next(1, 3_000)),
    };

    private sealed class History
    {
        public HashSet<long> Applied { get; } = new();
        public Dictionary<long, int> EndEvents { get; } = new();
    }

    private static void CheckSafety(NotchLifecycleState pre, NotchEvent e, NotchStep step, DateTimeOffset now, History history, string where)
    {
        var s = step.State;
        void That(bool ok, string invariant) => Assert.True(ok, $"{invariant} broken {where} event={e} pre={pre} post={s}");

        That(!(s.MicrophoneOn || s.CaptureActive) || (s.Revealed && s.Face is NotchFaceState.Capture or NotchFaceState.Listening), "INV-PRIV-1");
        That(!(s.Pinned || s.Hovered) || (s.Revealed && s.RetractAt is null), "INV-PRIV-1 (pinned means shown)");
        That(e is not E.CaptureStart || (s.Revealed && s.Face == NotchFaceState.Capture), "INV-PRIV-2 (today: capture face first)");

        That(s.MicrophoneOn == !s.Holds.IsEmpty, "INV-TALK-1 (mic on exactly while held)");
        That(e is not (E.Stop or E.TalkUp) || s.MicrophoneOn == (e is E.TalkUp && !s.Holds.IsEmpty), "INV-TALK-1");
        var opens = step.Effects.OfType<X.OpenMicrophone>().Count();
        var closes = step.Effects.OfType<X.CloseMicrophone>().Count();
        That(opens - closes == (s.MicrophoneOn ? 1 : 0) - (pre.MicrophoneOn ? 1 : 0) && opens <= 1 && closes <= 1, "INV-TALK-1 (mic effects match)");

        foreach (var apply in step.Effects.OfType<X.ApplyProposal>())
        {
            That(e is E.Approve a && a.ProposalId == apply.ProposalId && pre.PendingProposal?.Id == apply.ProposalId, "INV-APPLY-1");
            That(pre.RequestCurrent && apply.Generation == pre.Generation && pre.PendingProposal?.Generation == pre.Generation, "INV-APPLY-3");
            That(history.Applied.Add(apply.ProposalId), "INV-APPLY-1 (each proposal applied once)");
            That(s.Workspace.SetEquals(pre.Workspace.Add(apply.ProposalId)), "INV-APPLY-1 (workspace gains exactly it)");
        }
        foreach (var undo in step.Effects.OfType<X.ApplyUndo>())
        {
            var entry = undo.Entry;
            var journal = entry.Journal == EditJournal.File ? pre.FileJournal : pre.FieldJournal;
            That(e is E.ApproveUndo && pre.PendingUndo?.Entry == entry && journal == entry, "INV-APPLY-2 (undo targets the journal)");
            That(history.Applied.Contains(entry.ProposalId), "INV-APPLY-2 (only what was applied)");
            That(pre.RequestCurrent && pre.PendingUndo!.Generation == pre.Generation, "INV-APPLY-3 (undo)");
            That(entry.IsUndo || pre.Workspace.Contains(entry.ProposalId), "INV-APPLY-2 (undo reverses something in effect)");
            var expected = entry.IsUndo ? pre.Workspace.Add(entry.ProposalId) : pre.Workspace.Remove(entry.ProposalId);
            That(s.Workspace.SetEquals(expected), "INV-APPLY-2 (restores the before-state)");
            That(entry.Journal == EditJournal.File || s.FieldJournal is null, "INV-APPLY-2 (field undo only once)");
        }
        That(step.Effects.OfType<X.ApplyProposal>().Any() || step.Effects.OfType<X.ApplyUndo>().Any() || s.Workspace.SetEquals(pre.Workspace), "INV-APPLY-1 (no change without approval)");
        That(s.PendingProposal is null || s.PendingUndo is null, "one card at a time");
        That(s.PendingProposal is null || (s.RequestCurrent && s.PendingProposal.Generation == s.Generation), "INV-APPLY-3 (card belongs to the current request)");

        foreach (var ended in step.Effects.OfType<X.FocusEnded>())
        {
            That(ended.SessionId == pre.FocusSessionId, "INV-FOCUS-1 (ends the live session)");
            history.EndEvents[ended.SessionId] = history.EndEvents.GetValueOrDefault(ended.SessionId) + 1;
            That(history.EndEvents[ended.SessionId] == 1, "INV-FOCUS-1 (at most one end event)");
        }
        That(e is not E.FocusEnd || s.Focus != FocusStatus.Running, "INV-FOCUS-1 (End ends)");

        if (e is E.Stop)
        {
            That(!s.MicrophoneOn && s.Holds.IsEmpty && !s.Thinking && !s.Responding && !s.NudgeShowing && !s.Happy, "INV-STOP-1 (flags)");
            That(s.PendingProposal is null && s.PendingUndo is null && !s.RequestCurrent && s.Focus != FocusStatus.Running, "INV-STOP-1 (work stopped)");
            That(s.CapturesInFlight == pre.CapturesInFlight, "INV-STOP-1 (no new capture)");
            That(s.Face is NotchFaceState.Hidden or NotchFaceState.Idle or NotchFaceState.Hover
                || (s.Face == NotchFaceState.Paused && s.FocusPaused) || (s.Face == NotchFaceState.Capture && s.CaptureActive), "INV-STOP-1 (quiet face)");
        }

        That(s.RespondingUntil is null || s.RespondingUntil <= now + FaceMoments.MaximumHold, "LIVE-1 (bounded Responding)");
        That(s.HoldStartedAt is null || now - s.HoldStartedAt <= HoldToTalkController.MaximumHold, "INV-TALK-1 (bounded hold)");
        That(s.Generation >= pre.Generation && s.CapturesInFlight >= 0, "generations only grow");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20261113)]
    [InlineData(424242)]
    public void Random_sequences_keep_every_safety_invariant_after_every_step_and_always_settle(int seed)
    {
        var fuzz = new FuzzKit(seed);
        for (var run = 0; run < 400; run++)
        {
            var clock = new ManualClock();
            var lifecycle = new NotchLifecycle(clock);
            var history = new History();
            var state = NotchLifecycleState.Initial;
            var length = fuzz.Random.Next(1, 201);
            var trace = new List<string>();
            for (var i = 0; i < length; i++)
            {
                clock.Advance(RandomPause(fuzz.Random));
                var e = RandomEvent(fuzz, state);
                trace.Add(e.ToString());
                var step = lifecycle.Reduce(state, e);
                CheckSafety(state, e, step, clock.GetUtcNow(), history, $"seed={seed} run={run} step={i} trace=[{string.Join(", ", trace.TakeLast(8))}]");
                state = step.State;
            }

            state = lifecycle.Reduce(state, new E.PointerLeave()).State;
            while (state.CapturesInFlight > 0) state = lifecycle.Reduce(state, new E.CaptureEnd()).State;
            if (state.Thinking) state = lifecycle.Reduce(state, new E.Stop()).State;
            clock.Advance(TimeSpan.FromMinutes(3));
            state = lifecycle.Reduce(state, new E.Tick()).State;
            clock.Advance(TimeSpan.FromSeconds(2));
            state = lifecycle.Reduce(state, new E.Tick()).State;

            var where = $"seed={seed} run={run} settled={state}";
            Assert.False(state.Responding || state.NudgeShowing || state.Happy || state.MicrophoneOn, "LIVE-1 " + where);
            var expected = state.FocusPaused ? NotchFaceState.Paused : NotchFaceState.Hidden;
            Assert.True(state.Face == expected, "LIVE-2 " + where);
        }
    }
}
