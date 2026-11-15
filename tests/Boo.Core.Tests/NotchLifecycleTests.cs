using Boo.Core;
using E = Boo.Core.NotchEvent;
using Fx = Boo.Core.NotchEffect;

namespace Boo.Core.Tests;

// FROZEN tests for the NotchLifecycle reducer (agent-trust plan Phase 2, PR 2.4). Builders must not edit, weaken,
// skip or delete. Each test is named after a row of docs/architecture/notch-lifecycle.md (R*, F*, H*, Fo*, G*, A*,
// U*), a plan invariant (INV-*, LIVE-*), or a finding (Gap_F*). The reducer models TODAY's behaviour, gaps included.
// Gap_F* tests assert today's behaviour on purpose: the PR that fixes a gap must flip its Gap_ test in a reviewed
// oracle rebaseline, in the same PR as the doc row, the TLA+ model and the reducer.
public sealed class NotchLifecycleTests
{
    private static readonly NotchLifecycleState Start = NotchLifecycleState.Initial;

    private static NotchLifecycleState Run(params NotchEvent[] events) => Run(Start, events);

    private static NotchLifecycleState Run(NotchLifecycleState state, params NotchEvent[] events)
    {
        var step = NotchLifecycle.Run(state, events);
        Assert.True(step.Handled, "every event in the sequence must be enabled");
        return step.State;
    }

    private static NotchStep Do(NotchLifecycleState state, NotchEvent notchEvent) => NotchLifecycle.Reduce(state, notchEvent);

    private static void AssertIgnored(NotchLifecycleState state, NotchEvent notchEvent)
    {
        var step = Do(state, notchEvent);
        Assert.False(step.Handled);
        Assert.Same(state, step.State);
        Assert.Empty(step.Effects);
    }

    private static NotchLifecycleState Revealed() => Run(new E.PointerEnter(), new E.Tick());

    private static NotchLifecycleState FileEdited() => Run(
        new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(),
        new E.TypedReply(true, EditTarget.File), new E.Approve(), new E.Apply());

    private static NotchLifecycleState FieldEdited() => Run(
        new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(),
        new E.TypedReply(true, EditTarget.Field), new E.Approve(), new E.Apply());

    private static NotchEvent UndoTyped() => new E.Send(TypedRequestKind.Undo, TypedRequestPath.Plain);

    // ------------------------------------------------------------------ start

    [Fact]
    public void Initial_state_is_hidden_and_idle()
    {
        Assert.True(Start.Alive);
        Assert.Equal(RevealPhase.Hidden, Start.Phase);
        Assert.Equal(NotchFaceState.Hidden, Start.Face);
        Assert.Equal(MicOwner.Off, Start.MicDevice);
        Assert.False(Start.MicIndicator);
        Assert.Equal(0, Start.CaptureActivityCount);
        Assert.Null(Start.Focus);
        Assert.Null(Start.Card);
        Assert.Equal(JournalTop.None, Start.FileJournal);
        Assert.Equal(JournalTop.None, Start.FieldJournal);
    }

    // ------------------------------------------------------- 3.1 reveal timeline

    [Fact]
    public void R1_PointerEnter_from_Hidden_starts_Revealing()
    {
        var step = Do(Start, new E.PointerEnter());
        Assert.True(step.Handled);
        Assert.Equal(RevealPhase.Revealing, step.State.Phase);
        Assert.Equal(NotchFaceState.Hover, step.State.Face);
        Assert.Contains(new Fx.FaceChanged(NotchFaceState.Hidden, NotchFaceState.Hover), step.Effects);
    }

    [Fact]
    public void R2_Tick_while_Revealing_and_wanted_reaches_Revealed()
    {
        Assert.Equal(RevealPhase.Revealed, Revealed().Phase);
    }

    [Fact]
    public void R3_PointerLeave_with_nothing_pinned_starts_retract()
    {
        var s = Run(Revealed(), new E.PointerLeave());
        Assert.Equal(RevealPhase.RetractPending, s.Phase);
        Assert.Equal(NotchFaceState.Idle, s.Face);
    }

    [Fact]
    public void R4_PointerLeave_mid_reveal_finishes_the_reveal_then_waits()
    {
        var s = Run(new E.PointerEnter(), new E.PointerLeave());
        Assert.Equal(RevealPhase.Revealing, s.Phase);
        s = Run(s, new E.Tick());
        Assert.Equal(RevealPhase.RetractPending, s.Phase);
    }

    [Fact]
    public void R5_Tick_after_the_retract_delay_starts_Retracting()
    {
        var s = Run(Revealed(), new E.PointerLeave(), new E.Tick());
        Assert.Equal(RevealPhase.Retracting, s.Phase);
        Assert.Equal(NotchFaceState.Idle, s.Face);
    }

    [Fact]
    public void R6_Tick_while_Retracting_reaches_Hidden()
    {
        var s = Run(Revealed(), new E.PointerLeave(), new E.Tick(), new E.Tick());
        Assert.Equal(RevealPhase.Hidden, s.Phase);
        Assert.Equal(NotchFaceState.Hidden, s.Face);
    }

    [Fact]
    public void R7_PointerEnter_while_RetractPending_cancels_the_retract()
    {
        var s = Run(Revealed(), new E.PointerLeave(), new E.PointerEnter());
        Assert.Equal(RevealPhase.Revealed, s.Phase);
    }

    [Fact]
    public void R8_PointerEnter_while_Retracting_reveals_again()
    {
        var s = Run(Revealed(), new E.PointerLeave(), new E.Tick(), new E.PointerEnter());
        Assert.Equal(RevealPhase.Revealing, s.Phase);
    }

    [Fact]
    public void R9_A_pin_from_Hidden_reveals_without_the_pointer()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart());
        Assert.False(s.Hovered);
        Assert.True(s.Pinned);
        Assert.Equal(RevealPhase.Revealing, s.Phase);
        Assert.Equal(NotchFaceState.Capture, s.Face);
    }

    [Fact]
    public void R10_PointerLeave_while_pinned_does_not_retract()
    {
        var s = Run(Revealed(), new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.PointerLeave());
        Assert.Equal(RevealPhase.Revealed, s.Phase);
        Assert.Equal(NotchFaceState.Thinking, s.Face);
    }

    [Fact]
    public void R11_Unpin_with_the_pointer_outside_starts_the_retract()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart(), new E.Tick());
        Assert.Equal(RevealPhase.Revealed, s.Phase);
        s = Run(s, new E.FocusCheckDone(false));
        Assert.Equal(RevealPhase.RetractPending, s.Phase);
        Assert.Equal(NotchFaceState.Idle, s.Face);
        s = Run(s, new E.Tick(), new E.Tick());
        Assert.Equal(NotchFaceState.Hidden, s.Face);
    }

    [Fact]
    public void Tick_with_no_deadline_pending_is_ignored()
    {
        AssertIgnored(Start, new E.Tick());
        AssertIgnored(Revealed(), new E.Tick());
    }

    // ------------------------------------------------------------ 3.2 face flags

    [Fact]
    public void F1_PointerEnter_shows_Hover()
    {
        Assert.Equal(NotchFaceState.Hover, Run(new E.PointerEnter()).Face);
        AssertIgnored(Run(new E.PointerEnter()), new E.PointerEnter());
    }

    [Fact]
    public void F2_PointerLeave_shows_Idle_until_the_retract_ends()
    {
        var s = Run(Revealed(), new E.PointerLeave());
        Assert.Equal(NotchFaceState.Idle, s.Face);
        AssertIgnored(s, new E.PointerLeave());
    }

    [Fact]
    public void F3_Send_sets_Thinking_drops_the_voice_session_and_begins_a_request()
    {
        var talking = Run(new E.TalkButton(), new E.VAudioDelta());
        var step = Do(talking, new E.Send(TypedRequestKind.Ask, TypedRequestPath.Voice));
        Assert.True(step.Handled);
        var s = step.State;
        Assert.True(s.Thinking);
        Assert.False(s.Responding);
        Assert.Equal(VoiceSessionKind.Off, s.Voice);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.MicIndicator);
        Assert.Equal(talking.Generation + 1, s.Generation);
        Assert.Equal(new TypedRequest(TypedRequestKind.Ask, s.Generation, CaptureStage.Pending, TypedRequestPath.Voice), s.Typed);
        Assert.Contains(new Fx.RequestBegun(s.Generation), step.Effects);
        Assert.Contains(new Fx.MicrophoneClosed(MicOwner.Button), step.Effects);
        Assert.Equal(NotchFaceState.Thinking, s.Face);
    }

    [Fact]
    public void F3_A_typed_undo_always_takes_the_plain_path_and_one_typed_request_runs_at_a_time()
    {
        AssertIgnored(Start, new E.Send(TypedRequestKind.Undo, TypedRequestPath.Voice));
        AssertIgnored(Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain)), new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain));
    }

    [Fact]
    public void F4_TypedReply_on_the_plain_path_shows_Responding()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(), new E.TypedReply());
        Assert.False(s.Thinking);
        Assert.True(s.Responding);
        Assert.Null(s.Typed);
        Assert.Equal(0, s.CaptureActivityCount);
        Assert.Equal(NotchFaceState.Responding, s.Face);
        Assert.Null(s.Card);
    }

    [Fact]
    public void F4_A_reply_waits_for_the_capture_step()
    {
        AssertIgnored(Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain)), new E.TypedReply());
    }

    [Fact]
    public void F5_ExpireResponding_clears_Responding()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(), new E.ExpireResponding());
        Assert.False(s.Responding);
        Assert.Equal(NotchFaceState.Idle, s.Face);
        AssertIgnored(s, new E.ExpireResponding());
    }

    [Fact]
    public void F6_TypedEnd_clears_Thinking_when_the_request_is_still_current()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(), new E.TypedEnd());
        Assert.False(s.Thinking);
        Assert.Null(s.Typed);
        Assert.False(s.CaptureActive);
    }

    [Fact]
    public void F6_TypedEnd_of_a_stale_request_leaves_the_newer_request_its_face()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(),
            new E.TalkDown(HoldSource.Chord), new E.VSpeechStopped());
        Assert.True(s.Thinking);
        Assert.False(s.IsCurrent(s.Typed!.Generation));
        s = Run(s, new E.TypedEnd());
        Assert.True(s.Thinking);
        Assert.Null(s.Typed);
    }

    [Fact]
    public void F7_Speech_started_clears_and_speech_stopped_sets_Thinking()
    {
        var s = Run(new E.TalkDown(HoldSource.Chord), new E.VSpeechStopped());
        Assert.True(s.Thinking);
        s = Run(s, new E.VSpeechStarted());
        Assert.False(s.Thinking);
    }

    [Fact]
    public void F7_Speech_events_need_a_session_that_carried_audio()
    {
        var handedOff = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Voice), new E.TypedNoFrame(), new E.TypedHandOff());
        AssertIgnored(handedOff, new E.VSpeechStopped());
        AssertIgnored(Start, new E.VSpeechStarted());
    }

    [Fact]
    public void F8_VAudioDelta_shows_Responding()
    {
        var s = Run(new E.TalkButton(), new E.TalkButton(), new E.TalkDown(HoldSource.Eyes), new E.TalkUp(HoldSource.Eyes),
            new E.VSpeechStopped(), new E.VAudioDelta());
        Assert.False(s.Thinking);
        Assert.True(s.Responding);
        Assert.Equal(NotchFaceState.Responding, s.Face);
    }

    [Fact]
    public void F9_VResponseDone_keeps_Thinking_only_while_a_tool_is_in_flight()
    {
        var withTool = Run(new E.TalkButton(), new E.VToolCall(), new E.VResponseDone());
        Assert.True(withTool.Thinking);
        var noTool = Run(new E.TalkButton(), new E.VSpeechStopped(), new E.VResponseDone());
        Assert.False(noTool.Thinking);
    }

    [Fact]
    public void F10_VDrop_clears_chat_flags_and_pauses_a_running_focus_session()
    {
        var s = Run(new E.FocusStart(), new E.TalkDown(HoldSource.Chord), new E.VSpeechStopped(), new E.VAudioDelta());
        var step = Do(s, new E.VDrop());
        s = step.State;
        Assert.False(s.Thinking);
        Assert.False(s.Responding);
        Assert.Equal(VoiceSessionKind.Off, s.Voice);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.ChordHeld);
        Assert.True(s.RequestStopped);
        Assert.Equal(FocusStatus.Paused, s.Focus);
        Assert.Equal(NotchFaceState.Paused, s.Face);
        Assert.Contains(new Fx.RequestsStopped(), step.Effects);
    }

    [Fact]
    public void F10b_VGoToSleep_ends_the_voice_session_and_leaves_focus_alone()
    {
        var s = Run(new E.FocusStart(), new E.TalkButton(), new E.VGoToSleep());
        Assert.Equal(VoiceSessionKind.Off, s.Voice);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.MicIndicator);
        Assert.Equal(FocusStatus.Running, s.Focus);
    }

    [Fact]
    public void F11_VToolCall_sets_Thinking_and_its_answer_shows_Responding()
    {
        var s = Run(new E.TalkButton(), new E.TalkButton(), new E.TalkDown(HoldSource.Chord), new E.TalkUp(HoldSource.Chord),
            new E.VToolCall());
        Assert.True(s.Thinking);
        Assert.Equal(new VoiceToolRequest(s.Generation, CaptureStage.Pending, VoiceToolStage.Work), s.VoiceTool);
        s = Run(s, new E.VToolCapture());
        Assert.Equal(NotchFaceState.Capture, s.Face);
        s = Run(s, new E.VToolAnswer());
        Assert.False(s.Thinking);
        Assert.True(s.Responding);
        Assert.Equal(VoiceToolStage.Send, s.VoiceTool!.Stage);
        Assert.Equal(NotchFaceState.Capture, s.Face);
        s = Run(s, new E.VToolRelease());
        Assert.Null(s.VoiceTool);
        Assert.Equal(NotchFaceState.Responding, s.Face);
    }

    [Fact]
    public void F11_A_failed_voice_tool_leaves_Thinking_on()
    {
        var s = Run(new E.TalkButton(), new E.VToolCall(), new E.VToolNoFrame(), new E.VToolFail(), new E.VToolRelease());
        Assert.True(s.Thinking);
        Assert.Null(s.VoiceTool);
    }

    [Fact]
    public void F12_Microphone_on_shows_Listening()
    {
        var step = Do(Start, new E.TalkButton());
        Assert.Equal(MicOwner.Button, step.State.MicDevice);
        Assert.True(step.State.MicIndicator);
        Assert.Equal(VoiceSessionKind.TalkButton, step.State.Voice);
        Assert.Equal(NotchFaceState.Listening, step.State.Face);
        Assert.Contains(new Fx.MicrophoneOpened(MicOwner.Button), step.Effects);
    }

    [Fact]
    public void F13_Microphone_off_clears_Listening()
    {
        var step = Do(Run(new E.TalkButton()), new E.TalkButton());
        Assert.Equal(MicOwner.Off, step.State.MicDevice);
        Assert.False(step.State.MicIndicator);
        Assert.NotEqual(NotchFaceState.Listening, step.State.Face);
        Assert.Contains(new Fx.MicrophoneClosed(MicOwner.Button), step.Effects);
    }

    [Fact]
    public void F14_Capture_start_shows_Capture_above_Thinking()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain));
        Assert.Equal(NotchFaceState.Thinking, s.Face);
        s = Run(s, new E.TypedCapture());
        Assert.True(s.Thinking);
        Assert.Equal(1, s.CaptureActivityCount);
        Assert.Equal(NotchFaceState.Capture, s.Face);
    }

    [Fact]
    public void F15_Capture_end_drops_to_the_next_flag()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(), new E.TypedReply());
        Assert.Equal(0, s.CaptureActivityCount);
        Assert.Equal(NotchFaceState.Responding, s.Face);
    }

    [Fact]
    public void F15_Capture_count_adds_up_across_lanes()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart(), new E.TalkButton(), new E.VToolCall(), new E.VToolCapture(),
            new E.VUndoStart());
        Assert.Equal(3, s.CaptureActivityCount);
        s = Run(s, new E.FocusCheckDone());
        Assert.Equal(2, s.CaptureActivityCount);
        Assert.Equal(NotchFaceState.Capture, s.Face);
    }

    [Fact]
    public void F16_A_drifting_focus_check_shows_Nudge()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart(), new E.FocusCheckDone(true));
        Assert.True(s.Nudge);
        Assert.Equal(NotchFaceState.Nudge, s.Face);
        s = Run(s, new E.ExpireNudge());
        Assert.False(s.Nudge);
    }

    [Fact]
    public void F16_No_nudge_when_the_session_is_no_longer_running()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart(), new E.FocusPause(), new E.FocusCheckDone(true));
        Assert.False(s.Nudge);
    }

    [Fact]
    public void F17_Nudge_BackToIt_closes_it_and_Break_also_pauses()
    {
        var nudged = Run(new E.FocusStart(), new E.FocusCheckStart(), new E.FocusCheckDone(true));
        var back = Run(nudged, new E.NudgeBack());
        Assert.False(back.Nudge);
        Assert.Equal(FocusStatus.Running, back.Focus);
        var pause = Run(nudged, new E.NudgeBreak());
        Assert.False(pause.Nudge);
        Assert.Equal(FocusStatus.Paused, pause.Focus);
        Assert.Equal(NotchFaceState.Paused, pause.Face);
        AssertIgnored(back, new E.NudgeBack());
    }

    [Fact]
    public void F18_Focus_end_shows_Happy_and_clears_Paused()
    {
        var s = Run(new E.FocusStart(), new E.FocusPause(), new E.FocusEnd());
        Assert.True(s.Happy);
        Assert.Equal(FocusStatus.Ended, s.Focus);
        Assert.Equal(NotchFaceState.Happy, s.Face);
        s = Run(s, new E.ExpireHappy());
        Assert.False(s.Happy);
        Assert.Equal(NotchFaceState.Idle, s.Face);
    }

    [Fact]
    public void F19_FocusPause_shows_Paused_and_pins_the_notch()
    {
        var s = Run(new E.FocusStart(), new E.FocusPause());
        Assert.Equal(NotchFaceState.Paused, s.Face);
        Assert.True(s.Pinned);
    }

    [Fact]
    public void F20_Resume_clears_Paused_and_Start_also_clears_Happy()
    {
        var resumed = Run(new E.FocusStart(), new E.FocusPause(), new E.FocusResume());
        Assert.Equal(FocusStatus.Running, resumed.Focus);
        Assert.False(resumed.Pinned);
        var restarted = Run(new E.FocusStart(), new E.FocusComplete(), new E.FocusStart());
        Assert.False(restarted.Happy);
        Assert.Equal(FocusStatus.Running, restarted.Focus);
    }

    [Fact]
    public void F21_Stop_clears_chat_flags_mic_holds_and_requests()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart(), new E.FocusCheckDone(true), new E.TalkDown(HoldSource.Chord),
            new E.VSpeechStopped(), new E.VAudioDelta());
        var step = Do(s, new E.Stop());
        s = step.State;
        Assert.False(s.Thinking);
        Assert.False(s.Responding);
        Assert.False(s.Nudge);
        Assert.False(s.Happy);
        Assert.False(s.ChordHeld);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.MicIndicator);
        Assert.Equal(VoiceSessionKind.Off, s.Voice);
        Assert.True(s.RequestStopped);
        Assert.Contains(new Fx.RequestsStopped(), step.Effects);
        Assert.Contains(new Fx.MicrophoneClosed(MicOwner.Hold), step.Effects);
    }

    [Fact]
    public void Face_follows_the_resolver_priority_on_combined_flags()
    {
        var s = Run(new E.TalkButton(), new E.VToolCall(), new E.VAudioDelta());
        Assert.Equal(NotchFaceState.Listening, s.Face);
        s = Run(s, new E.VToolCapture());
        Assert.Equal(NotchFaceState.Capture, s.Face);
        Assert.Equal(FaceStateResolver.Resolve(new FaceInputs(Revealed: true, Listening: true, Responding: true, CaptureActive: true)), s.Face);
    }

    // ---------------------------------------------------------- 3.3 hold-to-talk

    [Fact]
    public void H1_Chord_down_starts_listening_with_a_new_request()
    {
        var step = Do(Start, new E.TalkDown(HoldSource.Chord));
        var s = step.State;
        Assert.True(s.ChordHeld);
        Assert.Equal(MicOwner.Hold, s.MicDevice);
        Assert.True(s.MicIndicator);
        Assert.Equal(VoiceSessionKind.PushToTalk, s.Voice);
        Assert.Equal(NotchFaceState.Listening, s.Face);
        Assert.Contains(new Fx.RequestBegun(1), step.Effects);
        Assert.Contains(new Fx.MicrophoneOpened(MicOwner.Hold), step.Effects);
    }

    [Fact]
    public void H1_A_new_hold_reuses_an_open_push_to_talk_session()
    {
        var s = Run(new E.TalkDown(HoldSource.Chord), new E.TalkUp(HoldSource.Chord));
        Assert.Equal(VoiceSessionKind.PushToTalk, s.Voice);
        var step = Do(s, new E.TalkDown(HoldSource.Chord));
        Assert.Equal(s.Generation, step.State.Generation);
        Assert.DoesNotContain(step.Effects, effect => effect is Fx.RequestBegun);
        Assert.Equal(MicOwner.Hold, step.State.MicDevice);
        Assert.True(step.State.MicIndicator);
    }

    [Fact]
    public void H2_Eyes_held_start_listening()
    {
        var s = Run(new E.TalkDown(HoldSource.Eyes));
        Assert.True(s.EyesHeld);
        Assert.Equal(MicOwner.Hold, s.MicDevice);
        Assert.Equal(NotchFaceState.Listening, s.Face);
        AssertIgnored(s, new E.TalkDown(HoldSource.Eyes));
    }

    [Fact]
    public void H3_One_holder_released_while_the_other_holds_keeps_listening()
    {
        var both = Run(new E.TalkDown(HoldSource.Chord), new E.TalkDown(HoldSource.Eyes));
        Assert.Equal(1, both.Generation);
        var s = Run(both, new E.TalkUp(HoldSource.Chord));
        Assert.True(s.EyesHeld);
        Assert.Equal(MicOwner.Hold, s.MicDevice);
        Assert.True(s.MicIndicator);
    }

    [Fact]
    public void H4_H5_The_last_release_closes_the_hold_mic_and_the_indicator()
    {
        var step = Do(Run(new E.TalkDown(HoldSource.Chord)), new E.TalkUp(HoldSource.Chord));
        Assert.Equal(MicOwner.Off, step.State.MicDevice);
        Assert.False(step.State.MicIndicator);
        Assert.False(step.State.ChordHeld);
        Assert.Contains(new Fx.MicrophoneClosed(MicOwner.Hold), step.Effects);
    }

    [Fact]
    public void H6_Stop_cancels_the_hold_and_later_releases_are_ignored()
    {
        var s = Run(new E.TalkDown(HoldSource.Chord), new E.TalkDown(HoldSource.Eyes), new E.Stop());
        Assert.False(s.ChordHeld);
        Assert.False(s.EyesHeld);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        AssertIgnored(s, new E.TalkUp(HoldSource.Chord));
        AssertIgnored(s, new E.TalkUp(HoldSource.Eyes));
    }

    [Fact]
    public void H7_The_eyes_losing_capture_is_an_eye_release()
    {
        var s = Run(new E.TalkDown(HoldSource.Eyes), new E.TalkUp(HoldSource.Eyes));
        Assert.False(s.EyesHeld);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.MicIndicator);
    }

    [Fact]
    public void H8_HoldTimeout_releases_every_holder()
    {
        var s = Run(new E.TalkDown(HoldSource.Chord), new E.TalkDown(HoldSource.Eyes), new E.HoldTimeout());
        Assert.False(s.ChordHeld);
        Assert.False(s.EyesHeld);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.MicIndicator);
        AssertIgnored(s, new E.HoldTimeout());
    }

    // ----------------------------------------------------------- 3.4 focus

    [Fact]
    public void Fo1_FocusStart_runs_a_session()
    {
        var step = Do(Start, new E.FocusStart());
        Assert.Equal(FocusStatus.Running, step.State.Focus);
        Assert.Equal(0, step.State.FocusEndEvents);
        Assert.DoesNotContain(step.Effects, effect => effect is Fx.FocusClosedWithoutEndEvent);
        Assert.Equal(NotchFaceState.Hidden, step.State.Face);
    }

    [Fact]
    public void Fo3_Pause_by_button_Stop_disconnect_or_break()
    {
        Assert.Equal(FocusStatus.Paused, Run(new E.FocusStart(), new E.FocusPause()).Focus);
        Assert.Equal(FocusStatus.Paused, Run(new E.FocusStart(), new E.Stop()).Focus);
        Assert.Equal(FocusStatus.Paused, Run(new E.FocusStart(), new E.TalkButton(), new E.VDrop()).Focus);
        Assert.Equal(FocusStatus.Paused, Run(new E.FocusStart(), new E.FocusCheckStart(), new E.FocusCheckDone(true), new E.NudgeBreak()).Focus);
        AssertIgnored(Run(new E.FocusStart(), new E.FocusPause()), new E.FocusPause());
    }

    [Fact]
    public void Fo4_Only_Resume_restarts_a_paused_session()
    {
        var paused = Run(new E.FocusStart(), new E.FocusPause());
        AssertIgnored(paused, new E.FocusComplete());
        AssertIgnored(paused, new E.FocusCheckStart());
        Assert.Equal(FocusStatus.Paused, Run(paused, new E.TalkButton(), new E.VGoToSleep()).Focus);
        Assert.Equal(FocusStatus.Running, Run(paused, new E.FocusResume()).Focus);
        AssertIgnored(Run(new E.FocusStart()), new E.FocusResume());
    }

    [Fact]
    public void Fo5_FocusComplete_ends_with_Completed()
    {
        var step = Do(Run(new E.FocusStart()), new E.FocusComplete());
        Assert.Equal(FocusStatus.Ended, step.State.Focus);
        Assert.Equal(1, step.State.FocusEndEvents);
        Assert.Contains(new Fx.FocusEnded(FocusEndReason.Completed), step.Effects);
    }

    [Fact]
    public void Fo6_FocusEnd_from_Running_or_Paused_ends_by_user()
    {
        foreach (var live in new[] { Run(new E.FocusStart()), Run(new E.FocusStart(), new E.FocusPause()) })
        {
            var step = Do(live, new E.FocusEnd());
            Assert.Equal(FocusStatus.Ended, step.State.Focus);
            Assert.Contains(new Fx.FocusEnded(FocusEndReason.EndedByUser), step.Effects);
        }
        AssertIgnored(Start, new E.FocusEnd());
    }

    [Fact]
    public void Fo7_The_end_event_is_raised_at_most_once()
    {
        var ended = Run(new E.FocusStart(), new E.FocusComplete());
        AssertIgnored(ended, new E.FocusEnd());
        AssertIgnored(ended, new E.FocusComplete());
        Assert.Equal(1, ended.FocusEndEvents);
    }

    [Fact]
    public void Fo8_A_focus_check_shows_Capture_then_Idle_then_Hidden()
    {
        var s = Run(new E.FocusStart(), new E.FocusCheckStart());
        Assert.Equal(NotchFaceState.Capture, s.Face);
        AssertIgnored(s, new E.FocusCheckStart());
        s = Run(s, new E.Tick(), new E.FocusCheckDone(false));
        Assert.Equal(NotchFaceState.Idle, s.Face);
        s = Run(s, new E.Tick(), new E.Tick());
        Assert.Equal(NotchFaceState.Hidden, s.Face);
    }

    // ------------------------------------------------- 3.5 request generations

    [Fact]
    public void G1_A_new_request_makes_older_work_stale_and_clears_a_pending_card()
    {
        var pending = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File));
        Assert.NotNull(pending.Card);
        var s = Run(pending, new E.TalkDown(HoldSource.Chord));
        Assert.Null(s.Card);
        Assert.Null(s.Ticket);
        Assert.Equal(pending.Generation + 1, s.Generation);
    }

    [Fact]
    public void G2_Stop_leaves_no_current_generation_until_the_next_request()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.Stop());
        Assert.True(s.RequestStopped);
        Assert.False(s.IsCurrent(s.Generation));
        s = Run(s, new E.TypedEnd(), new E.TalkButton());
        Assert.False(s.RequestStopped);
        Assert.True(s.IsCurrent(s.Generation));
    }

    [Fact]
    public void G3_A_voice_session_end_bumps_the_generation_and_clears_the_card()
    {
        var s = Run(new E.TalkButton(), new E.VToolCall(), new E.VToolNoFrame(), new E.VToolAnswer(true, EditTarget.Field));
        Assert.NotNull(s.Card);
        var step = Do(s, new E.VGoToSleep());
        Assert.Null(step.State.Card);
        Assert.Equal(s.Generation + 1, step.State.Generation);
        Assert.True(step.State.RequestStopped);
        Assert.Contains(new Fx.RequestsStopped(), step.Effects);
    }

    [Fact]
    public void G4_Results_of_an_older_generation_are_dropped()
    {
        var stale = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.Stop());
        AssertIgnored(stale, new E.TypedCapture());
        AssertIgnored(stale, new E.TypedReply());
        var toolStale = Run(new E.TalkButton(), new E.VToolCall(), new E.Stop());
        AssertIgnored(toolStale, new E.VToolAnswer());
        Assert.Null(Run(toolStale, new E.VToolRelease()).VoiceTool);
    }

    [Fact]
    public void G5_An_executing_card_from_an_older_generation_cannot_commit()
    {
        var executing = Run(FileEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(), new E.Approve());
        Assert.Equal(CardStage.Executing, executing.Card!.Stage);
        var stopped = Run(executing, new E.Stop());
        Assert.Equal(CardStage.Executing, stopped.Card!.Stage);
        Assert.False(stopped.IsCurrent(stopped.Card.Generation));
        AssertIgnored(stopped, new E.Apply());
        var ended = Run(stopped, new E.ExecEnd());
        Assert.Null(ended.Card);
        Assert.Equal(JournalTop.Edit, ended.FileJournal);
    }

    // ---------------------------------------------------------- 3.6 approval card

    [Fact]
    public void A1_A_reply_with_actions_for_an_explicit_task_shows_a_card()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File));
        Assert.Equal(new ApprovalCard(new ActionProposal(1, ProposalKind.Edit, EditTarget.File), CardStage.Pending, s.Generation), s.Card);
        Assert.Null(s.Ticket);
        var noTask = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(false));
        Assert.Null(noTask.Card);
    }

    [Fact]
    public void A2_Reject_closes_the_card()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Reject());
        Assert.Null(s.Card);
        AssertIgnored(s, new E.Reject());
        AssertIgnored(s, new E.Approve());
    }

    [Fact]
    public void A3_Stop_or_a_new_request_closes_a_pending_card()
    {
        var pending = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File));
        Assert.Null(Run(pending, new E.Stop()).Card);
        Assert.Null(Run(pending, new E.TalkButton()).Card);
        Assert.Null(Run(pending, new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain)).Card);
    }

    [Fact]
    public void A4_Approve_denied_by_policy_closes_the_card_and_applies_nothing()
    {
        var pending = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File));
        var s = Run(pending, new E.Approve(false));
        Assert.Null(s.Card);
        Assert.Null(s.Ticket);
        Assert.Equal(JournalTop.None, s.FileJournal);
    }

    [Fact]
    public void A5_Approve_issues_a_ticket_for_this_exact_proposal()
    {
        var pending = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.Field));
        var s = Run(pending, new E.Approve());
        Assert.Equal(CardStage.Executing, s.Card!.Stage);
        Assert.Equal(pending.Card!.Proposal, s.Ticket);
    }

    [Fact]
    public void A6_A_verified_apply_journals_the_edit()
    {
        var pending = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve());
        var step = Do(pending, new E.Apply());
        Assert.Equal(JournalTop.Edit, step.State.FileJournal);
        Assert.Null(step.State.Card);
        Assert.Null(step.State.Ticket);
        Assert.Contains(new Fx.Applied(pending.Card!.Proposal, pending.Card.Proposal, true, JournalTop.None, true), step.Effects);
        Assert.Equal(JournalTop.Edit, FieldEdited().FieldJournal);
    }

    [Fact]
    public void A6_An_unverified_step_journals_nothing_and_an_unverified_field_edit_clears_the_field_journal()
    {
        var fileCard = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve());
        Assert.Equal(JournalTop.None, Run(fileCard, new E.Apply(false)).FileJournal);
        var fieldCard = Run(FieldEdited(), new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(),
            new E.TypedReply(true, EditTarget.Field), new E.Approve());
        Assert.Equal(JournalTop.None, Run(fieldCard, new E.Apply(false)).FieldJournal);
    }

    [Fact]
    public void A7_More_steps_queue_a_new_card_that_needs_its_own_Approve()
    {
        var executing = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve());
        var s = Run(executing, new E.Apply(true, true, EditTarget.Field));
        Assert.Equal(CardStage.Pending, s.Card!.Stage);
        Assert.NotEqual(executing.Card!.Proposal, s.Card.Proposal);
        Assert.Equal(new ActionProposal(2, ProposalKind.Edit, EditTarget.Field), s.Card.Proposal);
        Assert.Null(s.Ticket);
        AssertIgnored(s, new E.Apply());
    }

    [Fact]
    public void A8_ExecEnd_closes_an_executing_card()
    {
        var executing = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve());
        var s = Run(executing, new E.ExecEnd());
        Assert.Null(s.Card);
        Assert.Null(s.Ticket);
        Assert.Equal(JournalTop.None, s.FileJournal);
    }

    // --------------------------------------------------------------- 3.7 undo

    [Fact]
    public void U1_The_file_journal_wins_over_the_field_journal()
    {
        var both = Run(FileEdited(), new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(),
            new E.TypedReply(true, EditTarget.Field), new E.Approve(), new E.Apply());
        Assert.Equal(JournalTop.Edit, both.FileJournal);
        Assert.Equal(JournalTop.Edit, both.FieldJournal);
        var s = Run(both, UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan());
        Assert.Equal(new ActionProposal(both.NextProposalId, ProposalKind.Undo, EditTarget.File), s.Card!.Proposal);
        Assert.False(s.Thinking);
    }

    [Fact]
    public void U2_Nothing_recorded_shows_no_card()
    {
        var s = Run(UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan());
        Assert.Null(s.Card);
        Assert.Null(s.Typed);
    }

    [Fact]
    public void U3_A_file_undo_plan_shows_an_Undo_card_unless_refused()
    {
        var s = Run(FileEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan());
        Assert.Equal(ProposalKind.Undo, s.Card!.Proposal.Kind);
        Assert.Equal(EditTarget.File, s.Card.Proposal.Target);
        var refused = Run(FileEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(false));
        Assert.Null(refused.Card);
    }

    [Fact]
    public void U4_A_field_undo_plan_shows_an_Undo_card_for_the_field()
    {
        var s = Run(FieldEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan());
        Assert.Equal(EditTarget.Field, s.Card!.Proposal.Target);
        Assert.Equal(ProposalKind.Undo, s.Card.Proposal.Kind);
    }

    [Fact]
    public void U5_A_verified_field_undo_clears_the_journal_so_a_second_undo_has_nothing()
    {
        var undone = Run(FieldEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(), new E.Approve(), new E.Apply());
        Assert.Equal(JournalTop.None, undone.FieldJournal);
        var again = Run(undone, UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan());
        Assert.Null(again.Card);
    }

    [Fact]
    public void U5_An_unverified_field_undo_keeps_the_journal()
    {
        var s = Run(FieldEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(), new E.Approve(), new E.Apply(false));
        Assert.Equal(JournalTop.Edit, s.FieldJournal);
    }

    [Fact]
    public void U6_A_verified_file_undo_is_journaled_as_a_new_entry()
    {
        var s = Run(FileEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(), new E.Approve(), new E.Apply());
        Assert.Equal(JournalTop.Undo, s.FileJournal);
    }

    [Fact]
    public void U1_A_spoken_undo_opens_its_capture_at_once_and_plans_from_the_journal()
    {
        var s = Run(FileEdited(), new E.TalkButton(), new E.TalkButton(), new E.TalkDown(HoldSource.Chord), new E.TalkUp(HoldSource.Chord));
        s = Run(s, new E.VUndoStart());
        Assert.Equal(1, s.CaptureActivityCount);
        Assert.Equal(NotchFaceState.Capture, s.Face);
        Assert.NotEqual(RevealPhase.Hidden, s.Phase);
        s = Run(s, new E.VUndoDone());
        Assert.Null(s.VoiceUndo);
        Assert.Equal(ProposalKind.Undo, s.Card!.Proposal.Kind);
        Assert.False(s.Thinking);
    }

    [Fact]
    public void U1_A_stale_spoken_undo_ends_without_a_card()
    {
        var s = Run(FileEdited(), new E.TalkButton(), new E.VUndoStart(), new E.Stop(), new E.VUndoDone());
        Assert.Null(s.VoiceUndo);
        Assert.Null(s.Card);
    }

    // ---------------------------------------------------- invariants that hold today

    [Fact]
    public void INV_PRIV_1_Indicator_or_capture_on_means_the_notch_is_out_and_shows_it()
    {
        foreach (var s in new[]
        {
            Run(new E.TalkButton()),
            Run(new E.TalkDown(HoldSource.Eyes)),
            Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture()),
            Run(new E.FocusStart(), new E.FocusCheckStart()),
            Run(new E.TalkButton(), new E.VUndoStart()),
            Run(Revealed(), new E.PointerLeave(), new E.Tick(), new E.TalkButton()),
        })
        {
            Assert.True(s.MicIndicator || s.CaptureActive);
            AssertPrivacyIndicator(s);
        }
    }

    [Fact]
    public void INV_APPLY_1_Nothing_is_applied_without_Approve_of_that_exact_proposal()
    {
        var pending = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File));
        AssertIgnored(pending, new E.Apply());
        var step = Do(Run(pending, new E.Approve()), new E.Apply());
        var applied = Assert.Single(step.Effects.OfType<Fx.Applied>());
        Assert.Equal(applied.Proposal, applied.Ticket);
        Assert.Equal(pending.Card!.Proposal, applied.Proposal);
    }

    [Fact]
    public void INV_APPLY_3_A_new_request_never_applies_an_older_proposal()
    {
        var executing = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedNoFrame(), new E.TypedReply(true, EditTarget.File), new E.Approve());
        foreach (var newer in new NotchEvent[] { new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TalkButton(), new E.TalkDown(HoldSource.Chord), new E.Stop() })
        {
            var s = Run(executing, newer);
            AssertIgnored(s, new E.Apply());
            Assert.Equal(JournalTop.None, s.FileJournal);
        }
    }

    [Fact]
    public void INV_APPLY_2_FIELD_A_field_undo_needs_a_recorded_edit_and_a_verified_one_empties_the_journal()
    {
        var undoCard = Run(FieldEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(), new E.Approve());
        var step = Do(undoCard, new E.Apply());
        var applied = Assert.Single(step.Effects.OfType<Fx.Applied>());
        Assert.Equal(JournalTop.Edit, applied.JournalBefore);
        Assert.Equal(JournalTop.None, step.State.FieldJournal);
    }

    [Fact]
    public void INV_FOCUS_At_most_one_end_event_per_session()
    {
        var s = Run(new E.FocusStart(), new E.FocusPause(), new E.FocusResume(), new E.FocusComplete());
        AssertIgnored(s, new E.FocusEnd());
        Assert.Equal(1, s.FocusEndEvents);
        s = Run(s, new E.FocusStart());
        Assert.Equal(0, s.FocusEndEvents);
        s = Run(s, new E.FocusEnd());
        Assert.Equal(1, s.FocusEndEvents);
    }

    [Fact]
    public void INV_TALK_1_The_push_to_talk_mic_is_open_only_while_held_and_everything_is_off_after_Stop()
    {
        var holding = Run(new E.TalkDown(HoldSource.Chord));
        Assert.Equal(MicOwner.Hold, holding.MicDevice);
        var released = Run(holding, new E.TalkUp(HoldSource.Chord));
        Assert.NotEqual(MicOwner.Hold, released.MicDevice);
        Assert.False(released.MicIndicator);
        foreach (var before in new[] { holding, Run(new E.TalkButton()), Run(new E.TalkDown(HoldSource.Chord), new E.TalkButton(), new E.TalkButton()) })
        {
            var stopped = Run(before, new E.Stop());
            Assert.Equal(MicOwner.Off, stopped.MicDevice);
            Assert.False(stopped.MicIndicator);
        }
    }

    [Fact]
    public void LIVE_1_Responding_always_has_a_timer_that_ends_it()
    {
        var s = Run(new E.TalkButton(), new E.VAudioDelta(), new E.VResponseDone());
        Assert.True(s.Responding);
        Assert.False(Run(s, new E.ExpireResponding()).Responding);
    }

    [Fact]
    public void LIVE_2_Hover_with_no_more_input_returns_to_Hidden_by_timers_alone()
    {
        var s = Run(Revealed(), new E.PointerLeave());
        var guard = 0;
        while (s.Phase != RevealPhase.Hidden && guard++ < 10)
            s = Run(s, new E.Tick());
        Assert.Equal(RevealPhase.Hidden, s.Phase);
        Assert.Equal(NotchFaceState.Hidden, s.Face);
    }

    // ------------------------------------------- today's gaps (a fix PR must flip these)

    // Finding F4 / INV-STOP-1: a fix PR must flip this test (Stop would end the session and leave the notch quiet).
    [Fact]
    public void Gap_F4_Stop_leaves_a_focus_session_Paused_and_the_notch_pinned()
    {
        var s = Run(new E.FocusStart(), new E.Stop());
        Assert.Equal(FocusStatus.Paused, s.Focus);
        Assert.Equal(NotchFaceState.Paused, s.Face);
        Assert.True(s.Pinned);
        Assert.NotEqual(RevealPhase.Hidden, s.Phase);
    }

    // Finding F4 / INV-STOP-1 part b: a fix PR must flip this test (Stop would end every capture at once).
    [Fact]
    public void Gap_F4_A_capture_open_at_Stop_stays_on_until_its_finally_runs()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.TypedCapture(), new E.Stop());
        Assert.True(s.CaptureActive);
        Assert.Equal(NotchFaceState.Capture, s.Face);
        s = Run(s, new E.TypedEnd());
        Assert.False(s.CaptureActive);
    }

    // Finding F5 / INV-PRIV-2: a fix PR must flip this test (the grab would wait until the notch is out).
    [Fact]
    public void Gap_F5_A_focus_check_starts_its_capture_while_the_notch_is_Hidden()
    {
        var running = Run(new E.FocusStart());
        Assert.Equal(RevealPhase.Hidden, running.Phase);
        var s = Run(running, new E.FocusCheckStart());
        Assert.True(s.CaptureActive);
        Assert.Equal(RevealPhase.Revealing, s.Phase);
    }

    // Finding F6 / INV-APPLY-2 (files): a fix PR must flip this test (a second undo would have nothing to undo).
    [Fact]
    public void Gap_F6_A_second_file_undo_re_applies_the_edit()
    {
        var undone = Run(FileEdited(), UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan(), new E.Approve(), new E.Apply());
        Assert.Equal(JournalTop.Undo, undone.FileJournal);
        var again = Run(undone, UndoTyped(), new E.TypedNoFrame(), new E.TypedUndoPlan());
        Assert.Equal(ProposalKind.Undo, again.Card!.Proposal.Kind);
        Assert.Equal(EditTarget.File, again.Card.Proposal.Target);
        var step = Do(Run(again, new E.Approve()), new E.Apply());
        Assert.Equal(JournalTop.Undo, Assert.Single(step.Effects.OfType<Fx.Applied>()).JournalBefore);
    }

    // Finding F7 / LIVE-1 (Thinking): a fix PR must flip this test (a local Thinking timeout would end it).
    [Fact]
    public void Gap_F7_Thinking_after_a_hand_off_has_no_local_timer()
    {
        var s = Run(new E.Send(TypedRequestKind.Ask, TypedRequestPath.Voice), new E.TypedNoFrame(), new E.TypedHandOff());
        Assert.True(s.Thinking);
        Assert.Equal(VoiceSessionKind.TypedHandOff, s.Voice);
        foreach (var timer in new NotchEvent[] { new E.Tick(), new E.ExpireResponding(), new E.ExpireNudge(), new E.ExpireHappy(), new E.HoldTimeout() })
            Assert.True(Do(s, timer).State.Thinking);
        Assert.False(Run(s, new E.VAudioDelta()).Thinking);
    }

    // Finding F9 / INV-FOCUS-1 as written: a fix PR must flip this test (the replaced session would raise its event).
    [Fact]
    public void Gap_F9_A_replaced_focus_session_has_no_end_event()
    {
        var step = Do(Run(new E.FocusStart(), new E.FocusPause()), new E.FocusStart());
        Assert.DoesNotContain(step.Effects, effect => effect is Fx.FocusEnded);
        Assert.Contains(new Fx.FocusClosedWithoutEndEvent(), step.Effects);
        Assert.Equal(0, step.State.FocusEndEvents);
        Assert.Equal(FocusStatus.Running, step.State.Focus);
    }

    // Finding F9 / INV-FOCUS-1 as written: a fix PR must flip this test (quit would end the session with its event).
    [Fact]
    public void Gap_F9_Quit_leaves_a_live_focus_session_paused_with_no_end_event()
    {
        var step = Do(Run(new E.FocusStart()), new E.Quit());
        Assert.False(step.State.Alive);
        Assert.Equal(FocusStatus.Paused, step.State.Focus);
        Assert.Equal(0, step.State.FocusEndEvents);
        Assert.Contains(new Fx.FocusClosedWithoutEndEvent(), step.Effects);
        Assert.DoesNotContain(step.Effects, effect => effect is Fx.FocusEnded);
    }

    // Finding F10 / INV-PRIV-1 and INV-TALK-1 as written: a fix PR must flip this test (the last release would stop
    // whichever microphone is current, or keep the indicator on while it records).
    [Fact]
    public void Gap_F10_Talk_button_mic_survives_the_release_with_the_indicator_off()
    {
        var s = Run(new E.TalkDown(HoldSource.Chord), new E.TalkButton(), new E.TalkButton());
        Assert.True(s.ChordHeld);
        Assert.Equal(MicOwner.Button, s.MicDevice);
        Assert.True(s.MicIndicator);
        var step = Do(s, new E.TalkUp(HoldSource.Chord));
        s = step.State;
        Assert.Equal(MicOwner.Button, s.MicDevice);
        Assert.False(s.MicIndicator);
        Assert.NotEqual(NotchFaceState.Listening, s.Face);
        Assert.False(s.Pinned);
        Assert.DoesNotContain(step.Effects, effect => effect is Fx.MicrophoneClosed);
        s = Run(s, new E.Tick(), new E.Tick(), new E.Tick());
        Assert.Equal(RevealPhase.Hidden, s.Phase);
        Assert.Equal(MicOwner.Button, s.MicDevice);
    }

    // Finding F10, same gap through the 120 s timeout: a fix PR must flip this test.
    [Fact]
    public void Gap_F10_HoldTimeout_also_leaves_the_Talk_button_mic_open()
    {
        var s = Run(new E.TalkDown(HoldSource.Eyes), new E.TalkButton(), new E.TalkButton(), new E.HoldTimeout());
        Assert.Equal(MicOwner.Button, s.MicDevice);
        Assert.False(s.MicIndicator);
    }

    // ------------------------------------------------------------ quit and purity

    [Fact]
    public void Quit_hides_everything_and_ignores_every_later_event()
    {
        var busy = Run(Revealed(), new E.TalkButton(), new E.VToolCall(), new E.VToolCapture());
        var s = Run(busy, new E.Quit());
        Assert.False(s.Alive);
        Assert.Equal(RevealPhase.Hidden, s.Phase);
        Assert.Equal(MicOwner.Off, s.MicDevice);
        Assert.False(s.CaptureActive);
        Assert.Equal(NotchFaceState.Hidden, s.Face);
        foreach (var notchEvent in Alphabet)
            AssertIgnored(s, notchEvent);
    }

    [Fact]
    public void Reduce_is_deterministic_and_leaves_its_input_unchanged()
    {
        var before = Run(new E.FocusStart(), new E.TalkDown(HoldSource.Chord), new E.VToolCall());
        var copy = before with { };
        var first = Do(before, new E.Stop());
        var second = Do(before, new E.Stop());
        Assert.Equal(copy, before);
        Assert.Equal(first.State, second.State);
        Assert.Equal(first.Effects, second.Effects);
    }

    [Fact]
    public void Reduce_rejects_null_input()
    {
        Assert.Throws<ArgumentNullException>(() => NotchLifecycle.Reduce(null!, new E.Tick()));
        Assert.Throws<ArgumentNullException>(() => NotchLifecycle.Reduce(Start, null!));
    }

    // ------------------------------------------------- every reachable state, bounded

    private static readonly NotchEvent[] Alphabet =
    {
        new E.PointerEnter(), new E.PointerLeave(), new E.Tick(),
        new E.ExpireResponding(), new E.ExpireNudge(), new E.ExpireHappy(),
        new E.Send(TypedRequestKind.Ask, TypedRequestPath.Plain), new E.Send(TypedRequestKind.Ask, TypedRequestPath.Voice),
        new E.Send(TypedRequestKind.Undo, TypedRequestPath.Plain),
        new E.TypedCapture(), new E.TypedNoFrame(), new E.TypedReply(false), new E.TypedReply(true, EditTarget.File),
        new E.TypedReply(true, EditTarget.Field), new E.TypedHandOff(), new E.TypedUndoPlan(true), new E.TypedUndoPlan(false),
        new E.TypedEnd(),
        new E.VSpeechStarted(), new E.VSpeechStopped(), new E.VAudioDelta(), new E.VResponseDone(), new E.VDrop(), new E.VGoToSleep(),
        new E.VToolCall(), new E.VToolCapture(), new E.VToolNoFrame(), new E.VToolAnswer(false), new E.VToolAnswer(true, EditTarget.Field),
        new E.VToolFail(), new E.VToolRelease(), new E.VUndoStart(), new E.VUndoDone(true), new E.VUndoDone(false),
        new E.TalkButton(), new E.TalkDown(HoldSource.Chord), new E.TalkDown(HoldSource.Eyes),
        new E.TalkUp(HoldSource.Chord), new E.TalkUp(HoldSource.Eyes), new E.HoldTimeout(),
        new E.Stop(), new E.Quit(),
        new E.Approve(true), new E.Approve(false), new E.Reject(), new E.Apply(true, false), new E.Apply(false),
        new E.Apply(true, true, EditTarget.Field), new E.ExecEnd(),
        new E.FocusStart(), new E.FocusPause(), new E.FocusResume(), new E.FocusComplete(), new E.FocusEnd(),
        new E.FocusCheckStart(), new E.FocusCheckDone(false), new E.FocusCheckDone(true), new E.NudgeBack(), new E.NudgeBreak(),
    };

    private static void AssertPrivacyIndicator(NotchLifecycleState s)
    {
        if (!s.MicIndicator && !s.CaptureActive)
            return;
        Assert.NotEqual(RevealPhase.Hidden, s.Phase);
        Assert.Contains(s.Face, new[] { NotchFaceState.Capture, NotchFaceState.Listening });
    }

    private static void AssertStepInvariants(NotchLifecycleState before, NotchEvent notchEvent, NotchStep step)
    {
        var after = step.State;
        AssertPrivacyIndicator(after);
        Assert.True(after.MicDevice != MicOwner.Hold || after.ChordHeld || after.EyesHeld, "INV-TALK-1: hold mic without a hold");
        if (notchEvent is E.Stop && step.Handled)
        {
            Assert.Equal(MicOwner.Off, after.MicDevice);
            Assert.False(after.MicIndicator);
        }
        if ((before.ChordHeld || before.EyesHeld) && !(after.ChordHeld || after.EyesHeld) && after.Alive)
        {
            Assert.NotEqual(MicOwner.Hold, after.MicDevice);
            Assert.False(after.MicIndicator);
        }
        Assert.InRange(after.FocusEndEvents, 0, 1);
        foreach (var applied in step.Effects.OfType<Fx.Applied>())
        {
            Assert.Equal(applied.Proposal, applied.Ticket);
            Assert.True(applied.Current);
            Assert.True(before.Card is { Stage: CardStage.Executing } card && card.Proposal == applied.Proposal && before.IsCurrent(card.Generation));
            if (applied.Proposal is { Kind: ProposalKind.Undo, Target: EditTarget.Field })
            {
                Assert.Equal(JournalTop.Edit, applied.JournalBefore);
                if (applied.Verified)
                    Assert.Equal(JournalTop.None, after.FieldJournal);
            }
        }
        var flags = new FaceInputs(
            Listening: after.MicIndicator, Thinking: after.Thinking, Responding: after.Responding,
            CaptureActive: after.CaptureActive, NudgeShowing: after.Nudge, FocusPaused: after.Focus == FocusStatus.Paused,
            Happy: after.Happy);
        Assert.Equal(FaceStateResolver.Resolve(flags with { Revealed = after.Phase != RevealPhase.Hidden, Hovered = after.Hovered }), after.Face);
        if (before.Face != after.Face)
            Assert.Contains(new Fx.FaceChanged(before.Face, after.Face), step.Effects);
        if (!step.Handled)
            Assert.Same(before, after);
    }

    [Fact]
    public void Invariants_hold_on_every_state_reachable_in_five_steps()
    {
        var seen = new HashSet<NotchLifecycleState> { Start };
        var frontier = new List<NotchLifecycleState> { Start };
        for (var depth = 0; depth < 5; depth++)
        {
            var next = new List<NotchLifecycleState>();
            foreach (var state in frontier)
            {
                foreach (var notchEvent in Alphabet)
                {
                    var step = NotchLifecycle.Reduce(state, notchEvent);
                    AssertStepInvariants(state, notchEvent, step);
                    if (seen.Add(step.State))
                        next.Add(step.State);
                }
            }
            frontier = next;
        }
        Assert.True(seen.Count > 1000, $"only {seen.Count} states reached");
    }

    [Fact]
    public void Invariants_hold_on_seeded_long_walks()
    {
        var random = new Random(24);
        for (var walk = 0; walk < 300; walk++)
        {
            var state = Start;
            for (var i = 0; i < 120 && state.Alive; i++)
            {
                var notchEvent = Alphabet[random.Next(Alphabet.Length)];
                if (notchEvent is E.Quit && i < 100)
                    continue;
                var step = NotchLifecycle.Reduce(state, notchEvent);
                AssertStepInvariants(state, notchEvent, step);
                state = step.State;
            }
        }
    }
}
