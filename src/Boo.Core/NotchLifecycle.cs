namespace Boo.Core;

/// <summary>Who has the microphone device open (model variable <c>micDev</c>, lifecycle doc finding F10).</summary>
public enum MicOwner
{
    /// <summary>No microphone is open.</summary>
    Off,

    /// <summary>The push-to-talk audio opened by a hold (Ctrl+Alt+Space or the eyes).</summary>
    Hold,

    /// <summary>The audio opened by the panel's Talk button.</summary>
    Button,
}

/// <summary>The realtime voice session (model variable <c>voice</c>).</summary>
public enum VoiceSessionKind
{
    /// <summary>No voice session.</summary>
    Off,

    /// <summary>A push-to-talk session (model "Ptt").</summary>
    PushToTalk,

    /// <summary>A Talk-button session (model "Other").</summary>
    TalkButton,

    /// <summary>A typed question handed to a text-only voice session (model "Text"); it never opens the mic.</summary>
    TypedHandOff,
}

/// <summary>Where one capture operation is (model "Pre" / "On" / "Off").</summary>
public enum CaptureStage
{
    /// <summary>About to grab a frame; the capture indicator is not on yet.</summary>
    Pending,

    /// <summary>The capture operation is open: the capture indicator is on.</summary>
    On,

    /// <summary>The capture operation has ended (or no frame was taken).</summary>
    Off,
}

/// <summary>What a typed request is.</summary>
public enum TypedRequestKind
{
    /// <summary>A question or task.</summary>
    Ask,

    /// <summary>Text classified as "undo" (lifecycle doc U1).</summary>
    Undo,
}

/// <summary>How a typed request is answered (lifecycle doc F3).</summary>
public enum TypedRequestPath
{
    /// <summary>Over HTTP: speech off or muted, and every typed undo.</summary>
    Plain,

    /// <summary>Speech on: the text may be handed to a voice session.</summary>
    Voice,
}

/// <summary>Stage of a voice tool call (screen_help).</summary>
public enum VoiceToolStage
{
    /// <summary>Capturing and asking the AI.</summary>
    Work,

    /// <summary>Answered or failed; CompleteToolAsync still to be sent.</summary>
    Send,
}

/// <summary>What a proposal on the approval card does.</summary>
public enum ProposalKind
{
    /// <summary>A desktop action from a reply (write a file, type into a field).</summary>
    Edit,

    /// <summary>The reverse of a journaled edit.</summary>
    Undo,
}

/// <summary>Which undo journal a proposal touches.</summary>
public enum EditTarget
{
    /// <summary><see cref="FileUndoJournal"/>.</summary>
    File,

    /// <summary><see cref="UndoJournal"/> (text fields).</summary>
    Field,
}

/// <summary>Where the approval card is (lifecycle doc 3.6).</summary>
public enum CardStage
{
    /// <summary>Shown, waiting for Approve or Reject.</summary>
    Pending,

    /// <summary>Approved and running.</summary>
    Executing,
}

/// <summary>Top entry of an undo journal at the model's abstraction (the field journal never holds Undo).</summary>
public enum JournalTop
{
    /// <summary>Nothing recorded.</summary>
    None,

    /// <summary>An approved, verified edit.</summary>
    Edit,

    /// <summary>A file entry written BY an approved undo (lifecycle doc finding F6).</summary>
    Undo,
}

/// <summary>The two hold-to-talk holders (lifecycle doc 3.3).</summary>
public enum HoldSource
{
    /// <summary>Ctrl+Alt+Space.</summary>
    Chord,

    /// <summary>The eyes held with the mouse.</summary>
    Eyes,
}

/// <summary>A typed request in flight. <paramref name="Generation"/> is the request generation it belongs to.</summary>
public sealed record TypedRequest(TypedRequestKind Kind, long Generation, CaptureStage Capture, TypedRequestPath Path);

/// <summary>A voice tool call in flight (screen_help).</summary>
public sealed record VoiceToolRequest(long Generation, CaptureStage Capture, VoiceToolStage Stage);

/// <summary>A spoken "undo" in flight.</summary>
public sealed record VoiceUndoRequest(long Generation, CaptureStage Capture);

/// <summary>
/// One proposal. The app has no proposal id; its approval ticket is a digest of the exact request
/// (lifecycle doc INV-APPLY-1). <paramref name="Id"/> stands for that digest: two proposals are the same only if equal.
/// </summary>
public sealed record ActionProposal(long Id, ProposalKind Kind, EditTarget Target);

/// <summary>The approval card in the panel, with the request generation it was shown for.</summary>
public sealed record ApprovalCard(ActionProposal Proposal, CardStage Stage, long Generation);

/// <summary>
/// Everything the notch face and the plan's invariants depend on, at the abstraction of
/// <c>docs/architecture/notch-lifecycle.md</c> and <c>specs/NotchLifecycle.tla</c>. Immutable.
/// Models TODAY's behaviour, including the gaps F4-F10 of the lifecycle doc.
/// </summary>
public sealed record NotchLifecycleState
{
    /// <summary>The state Boo starts in: hidden, nothing running.</summary>
    public static NotchLifecycleState Initial { get; } = new();

    /// <summary>False after Quit; then every event is ignored.</summary>
    public bool Alive { get; init; } = true;

    /// <summary>The pointer is over the notch strip.</summary>
    public bool Hovered { get; init; }

    /// <summary>Reveal timeline phase. Time is abstract: <see cref="NotchEvent.Tick"/> moves it one step.</summary>
    public RevealPhase Phase { get; init; } = RevealPhase.Hidden;

    /// <summary>App face flag Thinking.</summary>
    public bool Thinking { get; init; }

    /// <summary>App face flag Responding (a timed moment).</summary>
    public bool Responding { get; init; }

    /// <summary>App face flag NudgeShowing (6 s moment).</summary>
    public bool Nudge { get; init; }

    /// <summary>App face flag Happy (2.5 s moment).</summary>
    public bool Happy { get; init; }

    /// <summary>Who has the microphone device open.</summary>
    public MicOwner MicDevice { get; init; } = MicOwner.Off;

    /// <summary>The notch's microphoneActive indicator.</summary>
    public bool MicIndicator { get; init; }

    /// <summary>Hold-to-talk holder: Ctrl+Alt+Space is down.</summary>
    public bool ChordHeld { get; init; }

    /// <summary>Hold-to-talk holder: the eyes are held.</summary>
    public bool EyesHeld { get; init; }

    /// <summary>The realtime voice session.</summary>
    public VoiceSessionKind Voice { get; init; } = VoiceSessionKind.Off;

    /// <summary>The latest request generation (<see cref="RequestCoordinator"/>).</summary>
    public long Generation { get; init; }

    /// <summary>True after Stop or a voice session end until the next new request: no generation is current.</summary>
    public bool RequestStopped { get; init; }

    /// <summary>The typed request in flight, if any.</summary>
    public TypedRequest? Typed { get; init; }

    /// <summary>The voice tool call in flight, if any.</summary>
    public VoiceToolRequest? VoiceTool { get; init; }

    /// <summary>The spoken undo in flight, if any.</summary>
    public VoiceUndoRequest? VoiceUndo { get; init; }

    /// <summary>A focus check is capturing or classifying.</summary>
    public bool FocusCheckActive { get; init; }

    /// <summary>The approval card, if shown.</summary>
    public ApprovalCard? Card { get; init; }

    /// <summary>The approval ticket: the proposal that was approved, or null.</summary>
    public ActionProposal? Ticket { get; init; }

    /// <summary>File undo journal top entry.</summary>
    public JournalTop FileJournal { get; init; } = JournalTop.None;

    /// <summary>Field undo journal top entry.</summary>
    public JournalTop FieldJournal { get; init; } = JournalTop.None;

    /// <summary>The focus session, or null when none was started. Ended counts as no live session.</summary>
    public FocusStatus? Focus { get; init; }

    /// <summary>End events raised for the current focus session.</summary>
    public int FocusEndEvents { get; init; }

    /// <summary>Id the next proposal gets.</summary>
    public long NextProposalId { get; init; } = 1;

    /// <summary>True when work tagged with <paramref name="generation"/> may still apply its results (G4).</summary>
    public bool IsCurrent(long generation) => !RequestStopped && generation == Generation;

    /// <summary>A focus session is Running or Paused.</summary>
    public bool FocusLive => Focus is FocusStatus.Running or FocusStatus.Paused;

    /// <summary>Open capture operations (the CaptureActivityTracker count, at most one per lane).</summary>
    public int CaptureActivityCount =>
        (Typed?.Capture == CaptureStage.On ? 1 : 0)
        + (VoiceTool?.Capture == CaptureStage.On ? 1 : 0)
        + (VoiceUndo?.Capture == CaptureStage.On ? 1 : 0)
        + (FocusCheckActive ? 1 : 0);

    /// <summary>The capture indicator is on.</summary>
    public bool CaptureActive => CaptureActivityCount > 0;

    /// <summary>The face inputs without the pointer and the timeline, as the notch window builds them.</summary>
    public FaceInputs FlagInputs => new(
        Listening: MicIndicator,
        Thinking: Thinking,
        Responding: Responding,
        CaptureActive: CaptureActive,
        NudgeShowing: Nudge,
        FocusPaused: Focus == FocusStatus.Paused,
        Happy: Happy);

    /// <summary>The face would be non-Hidden without the pointer, so the notch is pinned out (R9).</summary>
    public bool Pinned => FaceStateResolver.Resolve(FlagInputs) != NotchFaceState.Hidden;

    /// <summary>The timeline is asked to show the notch.</summary>
    public bool WantsOut => Hovered || Pinned;

    /// <summary>The face, computed by <see cref="FaceStateResolver.Resolve"/> exactly as the notch window does.</summary>
    public NotchFaceState Face =>
        FaceStateResolver.Resolve(FlagInputs with { Revealed = Phase != RevealPhase.Hidden, Hovered = Hovered });
}

/// <summary>
/// The events of the lifecycle, named as in <c>specs/NotchLifecycle.tla</c>. Choices the model leaves open are
/// parameters. An event the model does not enable in a state is ignored (same state, no effects).
/// </summary>
public abstract record NotchEvent
{
    private protected NotchEvent()
    {
    }

    /// <summary>R1 R7 R8 F1: the pointer enters the notch strip.</summary>
    public sealed record PointerEnter : NotchEvent;

    /// <summary>R3 R10 F2: the pointer leaves.</summary>
    public sealed record PointerLeave : NotchEvent;

    /// <summary>R2 R4 R5 R6: the next reveal-timeline deadline passes (240 ms, 900 ms or 160 ms).</summary>
    public sealed record Tick : NotchEvent;

    /// <summary>F5: the Responding moment expires.</summary>
    public sealed record ExpireResponding : NotchEvent;

    /// <summary>F16: the 6 s nudge moment expires.</summary>
    public sealed record ExpireNudge : NotchEvent;

    /// <summary>F18: the 2.5 s happy moment expires.</summary>
    public sealed record ExpireHappy : NotchEvent;

    /// <summary>F3 G1 U1: Send a typed request. An undo always takes the plain path.</summary>
    public sealed record Send(TypedRequestKind Kind, TypedRequestPath Path) : NotchEvent;

    /// <summary>F14: the typed request's capture starts.</summary>
    public sealed record TypedCapture : NotchEvent;

    /// <summary>The typed request takes no frame: its capture operation ends at once.</summary>
    public sealed record TypedNoFrame : NotchEvent;

    /// <summary>F4 A1: the HTTP reply arrives; <paramref name="Propose"/> = it carries actions for an explicit task.</summary>
    public sealed record TypedReply(bool Propose = false, EditTarget Target = EditTarget.File) : NotchEvent;

    /// <summary>F3 F6: the typed text is handed to a voice session; Thinking stays on.</summary>
    public sealed record TypedHandOff : NotchEvent;

    /// <summary>U1-U4: a typed undo is planned; <paramref name="PlanOk"/> = the journal's plan was not refused.</summary>
    public sealed record TypedUndoPlan(bool PlanOk = true) : NotchEvent;

    /// <summary>F6: the typed request failed, timed out, was cancelled or is stale.</summary>
    public sealed record TypedEnd : NotchEvent;

    /// <summary>F7: the provider reports speech started.</summary>
    public sealed record VSpeechStarted : NotchEvent;

    /// <summary>F7: the provider reports speech stopped / committed.</summary>
    public sealed record VSpeechStopped : NotchEvent;

    /// <summary>F8: reply audio arrives.</summary>
    public sealed record VAudioDelta : NotchEvent;

    /// <summary>F9 (row): response.done.</summary>
    public sealed record VResponseDone : NotchEvent;

    /// <summary>F10 (row) G3: the voice session disconnects or errors.</summary>
    public sealed record VDrop : NotchEvent;

    /// <summary>F10b: the voice tool go_to_sleep (also a mic or realtime failure).</summary>
    public sealed record VGoToSleep : NotchEvent;

    /// <summary>F11: a voice tool call (screen_help) starts.</summary>
    public sealed record VToolCall : NotchEvent;

    /// <summary>F14: the voice tool's capture starts.</summary>
    public sealed record VToolCapture : NotchEvent;

    /// <summary>The voice tool takes no frame.</summary>
    public sealed record VToolNoFrame : NotchEvent;

    /// <summary>F11 A1: the voice tool's answer is rendered.</summary>
    public sealed record VToolAnswer(bool Propose = false, EditTarget Target = EditTarget.File) : NotchEvent;

    /// <summary>The voice tool times out or fails; Thinking stays on.</summary>
    public sealed record VToolFail : NotchEvent;

    /// <summary>The voice tool's finally: capture ends, the call leaves the in-flight count.</summary>
    public sealed record VToolRelease : NotchEvent;

    /// <summary>U1: a spoken "undo" starts; its capture opens at once.</summary>
    public sealed record VUndoStart : NotchEvent;

    /// <summary>U1-U4: the spoken undo finishes.</summary>
    public sealed record VUndoDone(bool PlanOk = true) : NotchEvent;

    /// <summary>F12 F13 (finding F8): the panel's Talk button.</summary>
    public sealed record TalkButton : NotchEvent;

    /// <summary>H1 H2 F24: a hold goes down.</summary>
    public sealed record TalkDown(HoldSource Source) : NotchEvent;

    /// <summary>H3 H4 H5 H7: a hold is released.</summary>
    public sealed record TalkUp(HoldSource Source) : NotchEvent;

    /// <summary>H8: a hold passes 120 s.</summary>
    public sealed record HoldTimeout : NotchEvent;

    /// <summary>F21 G2 H6 A3: Stop from any source.</summary>
    public sealed record Stop : NotchEvent;

    /// <summary>F21: quit (StopAll, then the process ends).</summary>
    public sealed record Quit : NotchEvent;

    /// <summary>A5 A4: Approve. <paramref name="Allowed"/> false = policy denies or the target window changed.</summary>
    public sealed record Approve(bool Allowed = true) : NotchEvent;

    /// <summary>A2: Cancel or Escape on the card.</summary>
    public sealed record Reject : NotchEvent;

    /// <summary>A6 A7 G5 U5 U6: the approved effect commits. <paramref name="Verified"/> = the read-back confirmed it.</summary>
    public sealed record Apply(bool Verified = true, bool MoreSteps = false, EditTarget NextTarget = EditTarget.File) : NotchEvent;

    /// <summary>A8: execution failed, was refused or stopped.</summary>
    public sealed record ExecEnd : NotchEvent;

    /// <summary>Fo1 Fo2 F20: start a focus session.</summary>
    public sealed record FocusStart : NotchEvent;

    /// <summary>Fo3 F19: pause (button, voice, sleep gap, tick error).</summary>
    public sealed record FocusPause : NotchEvent;

    /// <summary>Fo4 F20: resume.</summary>
    public sealed record FocusResume : NotchEvent;

    /// <summary>Fo5 Fo7 F18: the planned time is reached.</summary>
    public sealed record FocusComplete : NotchEvent;

    /// <summary>Fo6 Fo7 F18: End (button or voice).</summary>
    public sealed record FocusEnd : NotchEvent;

    /// <summary>Fo8 F14: a focus check is due; its capture starts.</summary>
    public sealed record FocusCheckStart : NotchEvent;

    /// <summary>Fo8 F15 F16: the focus check ends; <paramref name="Drifting"/> shows a nudge if still running.</summary>
    public sealed record FocusCheckDone(bool Drifting = false) : NotchEvent;

    /// <summary>F17: nudge "Back to it".</summary>
    public sealed record NudgeBack : NotchEvent;

    /// <summary>F17 Fo3: nudge "5 min break".</summary>
    public sealed record NudgeBreak : NotchEvent;
}

/// <summary>What the shell must do (or what happened) because of one step.</summary>
public abstract record NotchEffect
{
    private protected NotchEffect()
    {
    }

    /// <summary>The face changed; publish it.</summary>
    public sealed record FaceChanged(NotchFaceState From, NotchFaceState To) : NotchEffect;

    /// <summary>A microphone device was opened.</summary>
    public sealed record MicrophoneOpened(MicOwner Owner) : NotchEffect;

    /// <summary>A microphone device was closed.</summary>
    public sealed record MicrophoneClosed(MicOwner Owner) : NotchEffect;

    /// <summary>G1: a new request generation began.</summary>
    public sealed record RequestBegun(long Generation) : NotchEffect;

    /// <summary>G2 G3: no generation is current until the next request.</summary>
    public sealed record RequestsStopped : NotchEffect;

    /// <summary>
    /// A6 G5: an approved step committed. <paramref name="Ticket"/> is the approval ticket it ran under,
    /// <paramref name="Current"/> whether its generation was current, <paramref name="JournalBefore"/> the target
    /// journal's top entry before the step.
    /// </summary>
    public sealed record Applied(ActionProposal Proposal, ActionProposal? Ticket, bool Current, JournalTop JournalBefore, bool Verified) : NotchEffect;

    /// <summary>Fo7: the focus session's end event.</summary>
    public sealed record FocusEnded(FocusEndReason Reason) : NotchEffect;

    /// <summary>Fo2 (finding F9): a live session was replaced or left open at quit with no end event.</summary>
    public sealed record FocusClosedWithoutEndEvent : NotchEffect;
}

/// <summary>Result of one step. <paramref name="Handled"/> is false when the event was not enabled.</summary>
public sealed record NotchStep(NotchLifecycleState State, IReadOnlyList<NotchEffect> Effects, bool Handled);

/// <summary>
/// Pure, deterministic reducer of the notch lifecycle: <c>(state, event) -> (state, effects)</c>. No clock, no
/// threads, no Windows types. Each case names the lifecycle-doc rows and the TLA+ action it follows.
/// </summary>
public static class NotchLifecycle
{
    /// <summary>Applies one event.</summary>
    public static NotchStep Reduce(NotchLifecycleState state, NotchEvent notchEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(notchEvent);
        if (!state.Alive)
            return Ignored(state);

        var effects = new List<NotchEffect>();
        var next = notchEvent switch
        {
            NotchEvent.Tick => Tick(state),
            NotchEvent.Quit => Quit(state, effects),
            _ => Retime(Step(state, notchEvent, effects)),
        };
        if (next is null)
            return Ignored(state);

        AddDerivedEffects(state, next, effects);
        return new NotchStep(next, effects, true);
    }

    /// <summary>Applies events in order: the last state, every effect, and whether every event was handled.</summary>
    public static NotchStep Run(NotchLifecycleState state, IEnumerable<NotchEvent> events)
    {
        ArgumentNullException.ThrowIfNull(events);
        var all = new List<NotchEffect>();
        var current = state;
        var handled = true;
        foreach (var notchEvent in events)
        {
            var step = Reduce(current, notchEvent);
            current = step.State;
            all.AddRange(step.Effects);
            handled &= step.Handled;
        }
        return new NotchStep(current, all, handled);
    }

    private static NotchStep Ignored(NotchLifecycleState state) => new(state, Array.Empty<NotchEffect>(), false);

    private static NotchLifecycleState? Retime(NotchLifecycleState? s)
    {
        if (s is null)
            return null;
        var phase = s.WantsOut
            ? s.Phase switch
            {
                RevealPhase.Hidden => RevealPhase.Revealing,
                RevealPhase.RetractPending => RevealPhase.Revealed,
                RevealPhase.Retracting => RevealPhase.Revealing,
                _ => s.Phase,
            }
            : s.Phase == RevealPhase.Revealed ? RevealPhase.RetractPending : s.Phase;
        return s with { Phase = phase };
    }

    private static NotchLifecycleState? Tick(NotchLifecycleState s) => s.Phase switch
    {
        RevealPhase.Revealing => s with { Phase = s.WantsOut ? RevealPhase.Revealed : RevealPhase.RetractPending },
        RevealPhase.RetractPending when !s.WantsOut => s with { Phase = RevealPhase.Retracting },
        RevealPhase.Retracting => s with { Phase = RevealPhase.Hidden },
        _ => null,
    };

    private static NotchLifecycleState? Step(NotchLifecycleState s, NotchEvent e, List<NotchEffect> fx) => e switch
    {
        NotchEvent.PointerEnter => s.Hovered ? null : s with { Hovered = true },
        NotchEvent.PointerLeave => s.Hovered ? s with { Hovered = false } : null,
        NotchEvent.ExpireResponding => s.Responding ? s with { Responding = false } : null,
        NotchEvent.ExpireNudge => s.Nudge ? s with { Nudge = false } : null,
        NotchEvent.ExpireHappy => s.Happy ? s with { Happy = false } : null,
        NotchEvent.Send send => Send(s, send, fx),
        NotchEvent.TypedCapture => TypedCaptureTo(s, CaptureStage.On),
        NotchEvent.TypedNoFrame => TypedCaptureTo(s, CaptureStage.Off),
        NotchEvent.TypedReply reply => TypedReply(s, reply),
        NotchEvent.TypedHandOff => TypedHandOff(s),
        NotchEvent.TypedUndoPlan plan => TypedUndoPlan(s, plan),
        NotchEvent.TypedEnd => TypedEnd(s),
        NotchEvent.VSpeechStarted => AudioSession(s) ? s with { Thinking = false } : null,
        NotchEvent.VSpeechStopped => AudioSession(s) ? s with { Thinking = true } : null,
        NotchEvent.VAudioDelta => VoiceOpen(s) ? s with { Thinking = false, Responding = true } : null,
        NotchEvent.VResponseDone => VoiceOpen(s) ? s with { Thinking = ToolCurrent(s) } : null,
        NotchEvent.VDrop => VoiceOpen(s) ? VDrop(s, fx) : null,
        NotchEvent.VGoToSleep => VoiceOpen(s) ? StopListening(s, fx) with { ChordHeld = false, EyesHeld = false } : null,
        NotchEvent.VToolCall => VToolCall(s),
        NotchEvent.VToolCapture => VToolCaptureTo(s, CaptureStage.On),
        NotchEvent.VToolNoFrame => VToolCaptureTo(s, CaptureStage.Off),
        NotchEvent.VToolAnswer answer => VToolAnswer(s, answer),
        NotchEvent.VToolFail => VToolFail(s),
        NotchEvent.VToolRelease => VToolRelease(s),
        NotchEvent.VUndoStart => VUndoStart(s),
        NotchEvent.VUndoDone done => VUndoDone(s, done),
        NotchEvent.TalkButton => TalkButton(s, fx),
        NotchEvent.TalkDown down => TalkDown(s, down.Source, fx),
        NotchEvent.TalkUp up => TalkUp(s, up.Source),
        NotchEvent.HoldTimeout => Held(s) ? ReleaseMic(s) with { ChordHeld = false, EyesHeld = false } : null,
        NotchEvent.Stop => Stop(s, fx),
        NotchEvent.Approve approve => Approve(s, approve),
        NotchEvent.Reject => PendingCard(s) ? s with { Card = null, Ticket = null } : null,
        NotchEvent.Apply apply => Apply(s, apply, fx),
        NotchEvent.ExecEnd => s.Card?.Stage == CardStage.Executing ? s with { Card = null, Ticket = null } : null,
        NotchEvent.FocusStart => FocusStart(s, fx),
        NotchEvent.FocusPause => s.Focus == FocusStatus.Running ? s with { Focus = FocusStatus.Paused } : null,
        NotchEvent.FocusResume => s.Focus == FocusStatus.Paused ? s with { Focus = FocusStatus.Running } : null,
        NotchEvent.FocusComplete => s.Focus == FocusStatus.Running ? EndFocus(s, FocusEndReason.Completed, fx) : null,
        NotchEvent.FocusEnd => s.FocusLive ? EndFocus(s, FocusEndReason.EndedByUser, fx) : null,
        NotchEvent.FocusCheckStart => FocusCheckStart(s),
        NotchEvent.FocusCheckDone done => FocusCheckDone(s, done),
        NotchEvent.NudgeBack => s.Nudge ? s with { Nudge = false } : null,
        NotchEvent.NudgeBreak => s.Nudge ? s with { Nudge = false, Focus = PauseIfRunning(s.Focus) } : null,
        _ => null,
    };

    private static bool VoiceOpen(NotchLifecycleState s) => s.Voice != VoiceSessionKind.Off;

    private static bool AudioSession(NotchLifecycleState s) =>
        s.Voice is VoiceSessionKind.PushToTalk or VoiceSessionKind.TalkButton;

    private static bool Held(NotchLifecycleState s) => s.ChordHeld || s.EyesHeld;

    private static bool ToolCurrent(NotchLifecycleState s) => s.VoiceTool is { } tool && s.IsCurrent(tool.Generation);

    private static bool PendingCard(NotchLifecycleState s) => s.Card?.Stage == CardStage.Pending;

    private static FocusStatus? PauseIfRunning(FocusStatus? focus) =>
        focus == FocusStatus.Running ? FocusStatus.Paused : focus;

    private static NotchLifecycleState NewRequest(
        NotchLifecycleState s, bool thinking, VoiceSessionKind voice, MicOwner mic, bool indicator, List<NotchEffect> fx)
    {
        var generation = s.Generation + 1;
        fx.Add(new NotchEffect.RequestBegun(generation));
        return s with
        {
            Generation = generation,
            RequestStopped = false,
            Thinking = thinking,
            Responding = false,
            Card = CardAfterNewGeneration(s.Card),
            Ticket = s.Card?.Stage == CardStage.Executing ? s.Ticket : null,
            Voice = voice,
            MicDevice = mic,
            MicIndicator = indicator,
        };
    }

    private static ApprovalCard? CardAfterNewGeneration(ApprovalCard? card) =>
        card?.Stage == CardStage.Executing ? card : null;

    private static NotchLifecycleState StopListening(NotchLifecycleState s, List<NotchEffect> fx)
    {
        var next = s with
        {
            MicDevice = MicOwner.Off,
            MicIndicator = false,
            Voice = VoiceSessionKind.Off,
            Thinking = false,
            Responding = false,
        };
        if (!VoiceOpen(s))
            return next;

        fx.Add(new NotchEffect.RequestsStopped());
        return next with
        {
            Generation = s.Generation + 1,
            RequestStopped = true,
            Card = CardAfterNewGeneration(s.Card),
            Ticket = s.Card?.Stage == CardStage.Executing ? s.Ticket : null,
        };
    }

    private static NotchLifecycleState VDrop(NotchLifecycleState s, List<NotchEffect> fx) =>
        StopListening(s, fx) with { ChordHeld = false, EyesHeld = false, Focus = PauseIfRunning(s.Focus) };

    private static NotchLifecycleState? Send(NotchLifecycleState s, NotchEvent.Send send, List<NotchEffect> fx)
    {
        if (s.Typed is not null || (send.Kind == TypedRequestKind.Undo && send.Path != TypedRequestPath.Plain))
            return null;
        var next = NewRequest(s, true, VoiceSessionKind.Off, MicOwner.Off, false, fx);
        return next with { Typed = new TypedRequest(send.Kind, next.Generation, CaptureStage.Pending, send.Path) };
    }

    private static NotchLifecycleState? TypedCaptureTo(NotchLifecycleState s, CaptureStage stage) =>
        s.Typed is { Capture: CaptureStage.Pending } typed && s.IsCurrent(typed.Generation)
            ? s with { Typed = typed with { Capture = stage } }
            : null;

    private static bool TypedReady(NotchLifecycleState s, TypedRequestKind kind) =>
        s.Typed is { } typed && typed.Kind == kind && s.IsCurrent(typed.Generation) && typed.Capture != CaptureStage.Pending;

    private static NotchLifecycleState? TypedReply(NotchLifecycleState s, NotchEvent.TypedReply reply)
    {
        if (!TypedReady(s, TypedRequestKind.Ask))
            return null;
        var proposed = Propose(s, reply.Propose, reply.Target);
        return proposed with { Typed = null, Thinking = false, Responding = true };
    }

    private static NotchLifecycleState? TypedHandOff(NotchLifecycleState s) =>
        TypedReady(s, TypedRequestKind.Ask) && s.Typed!.Path == TypedRequestPath.Voice
            ? s with { Typed = null, Voice = VoiceSessionKind.TypedHandOff }
            : null;

    private static NotchLifecycleState? TypedUndoPlan(NotchLifecycleState s, NotchEvent.TypedUndoPlan plan) =>
        TypedReady(s, TypedRequestKind.Undo)
            ? PlanUndo(s, plan.PlanOk) with { Typed = null, Thinking = false }
            : null;

    private static NotchLifecycleState? TypedEnd(NotchLifecycleState s) => s.Typed is { } typed
        ? s with { Typed = null, Thinking = s.IsCurrent(typed.Generation) ? false : s.Thinking }
        : null;

    private static NotchLifecycleState? VToolCall(NotchLifecycleState s) =>
        VoiceOpen(s) && s.VoiceTool is null
            ? s with
            {
                Thinking = true,
                VoiceTool = new VoiceToolRequest(s.Generation, CaptureStage.Pending, VoiceToolStage.Work),
            }
            : null;

    private static NotchLifecycleState? VToolCaptureTo(NotchLifecycleState s, CaptureStage stage) =>
        s.VoiceTool is { Capture: CaptureStage.Pending } tool && s.IsCurrent(tool.Generation)
            ? s with { VoiceTool = tool with { Capture = stage } }
            : null;

    private static bool ToolWorking(NotchLifecycleState s) =>
        s.VoiceTool is { Stage: VoiceToolStage.Work } tool && s.IsCurrent(tool.Generation)
        && tool.Capture != CaptureStage.Pending;

    private static NotchLifecycleState? VToolAnswer(NotchLifecycleState s, NotchEvent.VToolAnswer answer)
    {
        if (!ToolWorking(s))
            return null;
        var proposed = Propose(s, answer.Propose, answer.Target);
        return proposed with
        {
            Thinking = false,
            Responding = true,
            VoiceTool = answer.Propose ? null : s.VoiceTool! with { Stage = VoiceToolStage.Send },
        };
    }

    private static NotchLifecycleState? VToolFail(NotchLifecycleState s) =>
        ToolWorking(s) ? s with { VoiceTool = s.VoiceTool! with { Stage = VoiceToolStage.Send } } : null;

    private static NotchLifecycleState? VToolRelease(NotchLifecycleState s) =>
        s.VoiceTool is { } tool && (tool.Stage == VoiceToolStage.Send || !s.IsCurrent(tool.Generation))
            ? s with { VoiceTool = null }
            : null;

    private static NotchLifecycleState? VUndoStart(NotchLifecycleState s) =>
        AudioSession(s) && s.VoiceUndo is null
            ? s with { VoiceUndo = new VoiceUndoRequest(s.Generation, CaptureStage.On) }
            : null;

    private static NotchLifecycleState? VUndoDone(NotchLifecycleState s, NotchEvent.VUndoDone done)
    {
        if (s.VoiceUndo is not { } undo)
            return null;
        var current = s.IsCurrent(undo.Generation);
        if (undo.Capture != CaptureStage.On && current)
            return null;
        var next = s with { VoiceUndo = null };
        return current ? PlanUndo(next, done.PlanOk) with { Thinking = ToolCurrent(s) } : next;
    }

    private static NotchLifecycleState Propose(NotchLifecycleState s, bool propose, EditTarget target) =>
        propose && s.Card is null ? ShowCard(s, ProposalKind.Edit, target) : s;

    private static NotchLifecycleState ShowCard(NotchLifecycleState s, ProposalKind kind, EditTarget target) => s with
    {
        Card = new ApprovalCard(new ActionProposal(s.NextProposalId, kind, target), CardStage.Pending, s.Generation),
        NextProposalId = s.NextProposalId + 1,
    };

    private static NotchLifecycleState PlanUndo(NotchLifecycleState s, bool planOk)
    {
        if (s.Card is not null || !planOk)
            return s;
        if (s.FileJournal != JournalTop.None)
            return ShowCard(s, ProposalKind.Undo, EditTarget.File);
        return s.FieldJournal != JournalTop.None ? ShowCard(s, ProposalKind.Undo, EditTarget.Field) : s;
    }

    private static NotchLifecycleState? TalkButton(NotchLifecycleState s, List<NotchEffect> fx) => s.MicIndicator
        ? StopListening(s, fx)
        : NewRequest(s, false, VoiceSessionKind.TalkButton, MicOwner.Button, true, fx);

    private static NotchLifecycleState? TalkDown(NotchLifecycleState s, HoldSource source, List<NotchEffect> fx)
    {
        var alreadyHeld = source == HoldSource.Chord ? s.ChordHeld : s.EyesHeld;
        if (alreadyHeld)
            return null;
        var held = s with
        {
            ChordHeld = s.ChordHeld || source == HoldSource.Chord,
            EyesHeld = s.EyesHeld || source == HoldSource.Eyes,
        };
        if (Held(s))
            return held;
        if (s.Voice == VoiceSessionKind.PushToTalk)
            return held with { MicDevice = MicOwner.Hold, MicIndicator = true };
        return NewRequest(held, false, VoiceSessionKind.PushToTalk, MicOwner.Hold, true, fx);
    }

    private static NotchLifecycleState? TalkUp(NotchLifecycleState s, HoldSource source)
    {
        var held = source == HoldSource.Chord ? s.ChordHeld : s.EyesHeld;
        if (!held)
            return null;
        var released = s with
        {
            ChordHeld = s.ChordHeld && source != HoldSource.Chord,
            EyesHeld = s.EyesHeld && source != HoldSource.Eyes,
        };
        return Held(released) ? released : ReleaseMic(released);
    }

    /// <summary>StopPushToTalkMic: the indicator always goes off, the device only if it is the hold's own (F10).</summary>
    private static NotchLifecycleState ReleaseMic(NotchLifecycleState s) => s with
    {
        MicDevice = s.MicDevice == MicOwner.Hold ? MicOwner.Off : s.MicDevice,
        MicIndicator = false,
    };

    private static NotchLifecycleState Stop(NotchLifecycleState s, List<NotchEffect> fx)
    {
        fx.Add(new NotchEffect.RequestsStopped());
        return s with
        {
            ChordHeld = false,
            EyesHeld = false,
            MicDevice = MicOwner.Off,
            MicIndicator = false,
            Voice = VoiceSessionKind.Off,
            Thinking = false,
            Responding = false,
            Nudge = false,
            Happy = false,
            RequestStopped = true,
            Card = CardAfterNewGeneration(s.Card),
            Ticket = s.Card?.Stage == CardStage.Executing ? s.Ticket : null,
            Focus = s.Focus == FocusStatus.Running ? null : s.Focus,
        };
    }

    private static NotchLifecycleState Quit(NotchLifecycleState s, List<NotchEffect> fx)
    {
        if (s.FocusLive)
            fx.Add(new NotchEffect.FocusClosedWithoutEndEvent());
        return s with
        {
            Alive = false,
            Hovered = false,
            Phase = RevealPhase.Hidden,
            Thinking = false,
            Responding = false,
            Nudge = false,
            Happy = false,
            MicDevice = MicOwner.Off,
            MicIndicator = false,
            ChordHeld = false,
            EyesHeld = false,
            Voice = VoiceSessionKind.Off,
            Typed = null,
            VoiceTool = null,
            VoiceUndo = null,
            FocusCheckActive = false,
            Card = null,
            Ticket = null,
            Focus = PauseIfRunning(s.Focus),
        };
    }

    private static NotchLifecycleState? Approve(NotchLifecycleState s, NotchEvent.Approve approve)
    {
        if (s.Card is not { Stage: CardStage.Pending } card)
            return null;
        return approve.Allowed && s.IsCurrent(card.Generation)
            ? s with { Card = card with { Stage = CardStage.Executing }, Ticket = card.Proposal }
            : s with { Card = null, Ticket = null };
    }

    private static NotchLifecycleState? Apply(NotchLifecycleState s, NotchEvent.Apply apply, List<NotchEffect> fx)
    {
        if (s.Card is not { Stage: CardStage.Executing } card || s.Ticket != card.Proposal
            || !s.IsCurrent(card.Generation))
            return null;

        var proposal = card.Proposal;
        var journalBefore = proposal.Target == EditTarget.File ? s.FileJournal : s.FieldJournal;
        fx.Add(new NotchEffect.Applied(proposal, s.Ticket, true, journalBefore, apply.Verified));

        var next = s with
        {
            FileJournal = FileAfter(s.FileJournal, proposal, apply.Verified),
            FieldJournal = FieldAfter(s.FieldJournal, proposal, apply.Verified),
            Card = null,
            Ticket = null,
        };
        return apply.Verified && apply.MoreSteps && proposal.Kind == ProposalKind.Edit
            ? ShowCard(next, ProposalKind.Edit, apply.NextTarget)
            : next;
    }

    /// <summary>A6 U6: a verified file step is journaled; a verified file UNDO is journaled as a new entry (F6).</summary>
    private static JournalTop FileAfter(JournalTop journal, ActionProposal p, bool verified)
    {
        if (p.Target != EditTarget.File || !verified)
            return journal;
        return p.Kind == ProposalKind.Edit ? JournalTop.Edit : JournalTop.Undo;
    }

    /// <summary>A6 U5: a verified field edit is journaled, an unverified one clears; a verified undo clears.</summary>
    private static JournalTop FieldAfter(JournalTop journal, ActionProposal p, bool verified)
    {
        if (p.Target != EditTarget.Field)
            return journal;
        if (p.Kind == ProposalKind.Edit)
            return verified ? JournalTop.Edit : JournalTop.None;
        return verified ? JournalTop.None : journal;
    }

    private static NotchLifecycleState FocusStart(NotchLifecycleState s, List<NotchEffect> fx)
    {
        if (s.FocusLive)
            fx.Add(new NotchEffect.FocusClosedWithoutEndEvent());
        return s with { Focus = FocusStatus.Running, FocusEndEvents = 0, Happy = false };
    }

    private static NotchLifecycleState EndFocus(NotchLifecycleState s, FocusEndReason reason, List<NotchEffect> fx)
    {
        fx.Add(new NotchEffect.FocusEnded(reason));
        return s with { Focus = FocusStatus.Ended, FocusEndEvents = s.FocusEndEvents + 1, Happy = true };
    }

    private static NotchLifecycleState? FocusCheckStart(NotchLifecycleState s) =>
        s.Focus == FocusStatus.Running && !s.FocusCheckActive ? s with { FocusCheckActive = true } : null;

    private static NotchLifecycleState? FocusCheckDone(NotchLifecycleState s, NotchEvent.FocusCheckDone done) =>
        s.FocusCheckActive
            ? s with
            {
                FocusCheckActive = false,
                Nudge = s.Nudge || (done.Drifting && s.Focus == FocusStatus.Running),
            }
            : null;

    private static void AddDerivedEffects(NotchLifecycleState before, NotchLifecycleState after, List<NotchEffect> fx)
    {
        if (before.MicDevice != after.MicDevice)
        {
            if (before.MicDevice != MicOwner.Off)
                fx.Add(new NotchEffect.MicrophoneClosed(before.MicDevice));
            if (after.MicDevice != MicOwner.Off)
                fx.Add(new NotchEffect.MicrophoneOpened(after.MicDevice));
        }
        if (before.Face != after.Face)
            fx.Add(new NotchEffect.FaceChanged(before.Face, after.Face));
    }
}
