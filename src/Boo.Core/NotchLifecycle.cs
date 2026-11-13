using System.Collections.Immutable;

namespace Boo.Core;

/// <summary>Which of the two hold-to-talk sources is held (the Ctrl+Alt+Space chord, or a press on the eyes).</summary>
public enum TalkHoldSource { Chord, Eyes }

/// <summary>Which undo journal records an applied change: on-screen fields or files.</summary>
public enum EditJournal { Field, File }

/// <summary>An action card waiting for Approve: proposal <paramref name="Id"/> from request <paramref name="Generation"/>.</summary>
public sealed record NotchProposal(long Id, long Generation, EditJournal Journal);

/// <summary>
/// The last change a journal holds. <paramref name="IsUndo"/> is true when the entry is itself an approved undo of
/// <paramref name="ProposalId"/>, which today happens only in the file journal (notch-lifecycle.md finding F6).
/// </summary>
public sealed record NotchJournalEntry(long ProposalId, EditJournal Journal, bool IsUndo);

/// <summary>An undo card waiting for approval, planned in request <paramref name="Generation"/>.</summary>
public sealed record NotchPendingUndo(NotchJournalEntry Entry, long Generation);

/// <summary>
/// Shipped timings the lifecycle uses. Reveal, retract delay and retract come from the notch spec; the holds are the
/// Responding (4 s), Nudge (6 s) and Happy (2.5 s) moments of the app shell.
/// </summary>
public sealed record NotchLifecycleTimings(
    TimeSpan Reveal,
    TimeSpan RetractDelay,
    TimeSpan Retract,
    TimeSpan ReplyHold,
    TimeSpan NudgeHold,
    TimeSpan HappyHold)
{
    /// <summary>The values shipped today (docs/design/boo-notch-spec.json and the shell's moment lengths).</summary>
    public static NotchLifecycleTimings Shipped { get; } = new(
        TimeSpan.FromMilliseconds(240), TimeSpan.FromMilliseconds(900), TimeSpan.FromMilliseconds(160),
        TimeSpan.FromSeconds(4), TimeSpan.FromSeconds(6), TimeSpan.FromSeconds(2.5));

    /// <summary>The shipped holds with the reveal and retract timings of a loaded notch spec.</summary>
    public static NotchLifecycleTimings From(NotchTimings spec)
    {
        ArgumentNullException.ThrowIfNull(spec);
        return Shipped with
        {
            Reveal = TimeSpan.FromMilliseconds(spec.RevealMs),
            RetractDelay = TimeSpan.FromMilliseconds(spec.RetractDelayMs),
            Retract = TimeSpan.FromMilliseconds(spec.RetractMs),
        };
    }
}

/// <summary>
/// Everything the notch lifecycle knows, as plain values: the flags the app keeps today (docs/architecture/notch-lifecycle.md
/// section 1), the hold-to-talk holders, the request generation, the action and undo cards, both undo journals, the focus
/// session, and the set of applied proposals (<see cref="Workspace"/>). <see cref="Revealed"/> is true while the reveal
/// timeline is anywhere but Hidden (revealing, revealed, retract pending or retracting).
/// </summary>
public sealed record NotchLifecycleState
{
    /// <summary>Boo just started: hidden, nothing running, no request yet.</summary>
    public static NotchLifecycleState Initial { get; } = new();

    public bool Hovered { get; init; }
    public bool Revealed { get; init; }
    public DateTimeOffset RevealEndsAt { get; init; }
    public DateTimeOffset? RetractAt { get; init; }
    public bool Thinking { get; init; }
    public DateTimeOffset? RespondingUntil { get; init; }
    public DateTimeOffset? NudgeUntil { get; init; }
    public DateTimeOffset? HappyUntil { get; init; }
    public bool MicrophoneOn { get; init; }
    public ImmutableHashSet<TalkHoldSource> Holds { get; init; } = ImmutableHashSet<TalkHoldSource>.Empty;
    public DateTimeOffset? HoldStartedAt { get; init; }
    public int CapturesInFlight { get; init; }
    public long Generation { get; init; }
    public bool RequestCurrent { get; init; }
    public NotchProposal? PendingProposal { get; init; }
    public NotchPendingUndo? PendingUndo { get; init; }
    public NotchJournalEntry? FieldJournal { get; init; }
    public NotchJournalEntry? FileJournal { get; init; }
    public ImmutableHashSet<long> Workspace { get; init; } = ImmutableHashSet<long>.Empty;
    public long FocusSessionId { get; init; }
    public FocusStatus? Focus { get; init; }

    public bool Responding => RespondingUntil is not null;
    public bool NudgeShowing => NudgeUntil is not null;
    public bool Happy => HappyUntil is not null;
    public bool FocusPaused => Focus == FocusStatus.Paused;
    public bool CaptureActive => CapturesInFlight > 0;

    /// <summary>The face UI Automation reads, by the same priority the notch uses today.</summary>
    public NotchFaceState Face => FaceStateResolver.Resolve(Inputs(Revealed, Hovered));

    /// <summary>True when a flag above Hidden holds the notch out even without the pointer.</summary>
    public bool Pinned => FaceStateResolver.Resolve(Inputs(false, false)) != NotchFaceState.Hidden;

    private FaceInputs Inputs(bool revealed, bool hovered) => new(
        Revealed: revealed, Hovered: hovered, Listening: MicrophoneOn, Thinking: Thinking, Responding: Responding,
        CaptureActive: CaptureActive, NudgeShowing: NudgeShowing, FocusPaused: FocusPaused, Happy: Happy);
}

/// <summary>Inputs to the lifecycle. Generations name the request a late result belongs to.</summary>
public abstract record NotchEvent
{
    public sealed record PointerEnter : NotchEvent;
    public sealed record PointerLeave : NotchEvent;
    public sealed record Hotkey : NotchEvent;
    public sealed record Tick : NotchEvent;
    public sealed record Send : NotchEvent;
    public sealed record TurnCommitted(long Generation) : NotchEvent;
    public sealed record Reply(long Generation) : NotchEvent;
    public sealed record ReplyDone(long Generation, TimeSpan RemainingPlayback, bool ToolsInFlight = false) : NotchEvent;
    public sealed record ReplyFailed(long Generation) : NotchEvent;
    public sealed record TalkDown(TalkHoldSource Source) : NotchEvent;
    public sealed record TalkUp(TalkHoldSource Source) : NotchEvent;
    public sealed record CaptureStart : NotchEvent;
    public sealed record CaptureEnd : NotchEvent;
    public sealed record Stop : NotchEvent;
    public sealed record FocusStart : NotchEvent;
    public sealed record FocusPause : NotchEvent;
    public sealed record FocusResume : NotchEvent;
    public sealed record FocusEnd : NotchEvent;
    public sealed record FocusCompleted : NotchEvent;
    public sealed record FocusNudge : NotchEvent;
    public sealed record NudgeBack : NotchEvent;
    public sealed record NudgeBreak : NotchEvent;
    public sealed record Propose(long ProposalId, long Generation, EditJournal Journal) : NotchEvent;
    public sealed record Approve(long ProposalId) : NotchEvent;
    public sealed record Reject : NotchEvent;
    public sealed record Undo : NotchEvent;
    public sealed record ApproveUndo : NotchEvent;
}

/// <summary>What the shell must do after a step. The reducer itself never touches the screen, microphone or disk.</summary>
public abstract record NotchEffect
{
    public sealed record FaceChanged(NotchFaceState From, NotchFaceState To) : NotchEffect;
    public sealed record OpenPanel : NotchEffect;
    public sealed record BeginRequest(long Generation) : NotchEffect;
    public sealed record CancelRequest(long Generation) : NotchEffect;
    public sealed record OpenMicrophone : NotchEffect;
    public sealed record CloseMicrophone : NotchEffect;
    public sealed record CommitTurn : NotchEffect;
    public sealed record DiscardTurn(string? Hint) : NotchEffect;
    public sealed record ApplyProposal(long ProposalId, long Generation) : NotchEffect;
    public sealed record ApplyUndo(NotchJournalEntry Entry) : NotchEffect;
    public sealed record FocusEnded(long SessionId, FocusEndReason Reason) : NotchEffect;
}

/// <summary>One step: the new state and the effects, in the order the shell must run them.</summary>
public sealed record NotchStep(NotchLifecycleState State, ImmutableArray<NotchEffect> Effects);

/// <summary>
/// The notch lifecycle as a pure reducer: (state, event) to (state, effects). It models what the app does today
/// (docs/architecture/notch-lifecycle.md), gaps included; the gaps are named by tests in NotchLifecycleTests. Time comes
/// only from the injected <see cref="TimeProvider"/>: every step first lets time act (moments expire, a hold past
/// <see cref="HoldToTalkController.MaximumHold"/> is sent, the retract finishes), then applies the event, then settles
/// the reveal. Not modelled yet: the panel Talk button (finding F8), multi-step action queues, voice session reuse.
/// </summary>
public sealed class NotchLifecycle
{
    private readonly TimeProvider _clock;
    private readonly NotchLifecycleTimings _timings;

    public NotchLifecycle(TimeProvider clock, NotchLifecycleTimings? timings = null)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _timings = timings ?? NotchLifecycleTimings.Shipped;
    }

    public NotchStep Reduce(NotchLifecycleState state, NotchEvent notchEvent)
    {
        ArgumentNullException.ThrowIfNull(state);
        ArgumentNullException.ThrowIfNull(notchEvent);
        var now = _clock.GetUtcNow();
        var effects = ImmutableArray.CreateBuilder<NotchEffect>();
        var next = Settle(Apply(LetTimeAct(state, now, effects), notchEvent, now, effects), now);
        if (state.Face != next.Face)
        {
            effects.Add(new NotchEffect.FaceChanged(state.Face, next.Face));
        }
        return new NotchStep(next, effects.ToImmutable());
    }

    private static NotchLifecycleState LetTimeAct(NotchLifecycleState s, DateTimeOffset now, ImmutableArray<NotchEffect>.Builder effects)
    {
        s = s with
        {
            RespondingUntil = s.RespondingUntil <= now ? null : s.RespondingUntil,
            NudgeUntil = s.NudgeUntil <= now ? null : s.NudgeUntil,
            HappyUntil = s.HappyUntil <= now ? null : s.HappyUntil,
        };
        if (s.HoldStartedAt is { } started && now - started > HoldToTalkController.MaximumHold)
        {
            s = EndHold(s, now, effects, sendRegardless: true);
        }
        return s;
    }

    private NotchLifecycleState Apply(NotchLifecycleState s, NotchEvent e, DateTimeOffset now, ImmutableArray<NotchEffect>.Builder effects)
    {
        switch (e)
        {
            case NotchEvent.PointerEnter:
                return s with { Hovered = true };
            case NotchEvent.PointerLeave:
                return s with { Hovered = false };
            case NotchEvent.Hotkey:
                effects.Add(new NotchEffect.OpenPanel());
                return s;
            case NotchEvent.Tick:
                return s;
            case NotchEvent.Send:
                return BeginRequest(s, effects) with { Thinking = true, RespondingUntil = null };
            case NotchEvent.TurnCommitted c:
                return IsCurrent(s, c.Generation) ? s with { Thinking = true } : s;
            case NotchEvent.Reply r:
                return IsCurrent(s, r.Generation) ? s with { Thinking = false, RespondingUntil = now + _timings.ReplyHold } : s;
            case NotchEvent.ReplyDone d:
                if (!IsCurrent(s, d.Generation)) return s;
                return s with
                {
                    Thinking = d.ToolsInFlight,
                    RespondingUntil = s.Responding ? now + FaceMoments.AfterReplyDone(d.RemainingPlayback) : null,
                };
            case NotchEvent.ReplyFailed f:
                return IsCurrent(s, f.Generation) ? s with { Thinking = false } : s;
            case NotchEvent.TalkDown down:
                return TalkDown(s, down.Source, now, effects);
            case NotchEvent.TalkUp up:
                if (!s.Holds.Contains(up.Source)) return s;
                s = s with { Holds = s.Holds.Remove(up.Source) };
                return s.Holds.IsEmpty ? EndHold(s, now, effects, sendRegardless: false) : s;
            case NotchEvent.CaptureStart:
                return s with { CapturesInFlight = s.CapturesInFlight + 1 };
            case NotchEvent.CaptureEnd:
                return s with { CapturesInFlight = Math.Max(0, s.CapturesInFlight - 1) };
            case NotchEvent.Stop:
                return Stop(s, effects);
            case NotchEvent.FocusStart:
                return s with { FocusSessionId = s.FocusSessionId + 1, Focus = FocusStatus.Running, HappyUntil = null };
            case NotchEvent.FocusPause:
                return s.Focus == FocusStatus.Running ? s with { Focus = FocusStatus.Paused } : s;
            case NotchEvent.FocusResume:
                return s.Focus == FocusStatus.Paused ? s with { Focus = FocusStatus.Running } : s;
            case NotchEvent.FocusEnd:
                return EndFocus(s, FocusEndReason.EndedByUser, now, effects);
            case NotchEvent.FocusCompleted:
                return s.Focus == FocusStatus.Running ? EndFocus(s, FocusEndReason.Completed, now, effects) : s;
            case NotchEvent.FocusNudge:
                return s.Focus == FocusStatus.Running ? s with { NudgeUntil = now + _timings.NudgeHold } : s;
            case NotchEvent.NudgeBack:
                return s with { NudgeUntil = null };
            case NotchEvent.NudgeBreak:
                return s with { NudgeUntil = null, Focus = s.Focus == FocusStatus.Running ? FocusStatus.Paused : s.Focus };
            case NotchEvent.Propose p:
                return IsCurrent(s, p.Generation)
                    ? s with { PendingProposal = new NotchProposal(p.ProposalId, p.Generation, p.Journal), PendingUndo = null }
                    : s;
            case NotchEvent.Approve a:
                return Approve(s, a.ProposalId, effects);
            case NotchEvent.Reject:
                return s with { PendingProposal = null, PendingUndo = null };
            case NotchEvent.Undo:
                return PlanUndo(BeginRequest(s, effects) with { Thinking = false, RespondingUntil = null });
            case NotchEvent.ApproveUndo:
                return ApproveUndo(s, effects);
            default:
                throw new ArgumentOutOfRangeException(nameof(e), e.ToString(), "Unknown notch event.");
        }
    }

    private static bool IsCurrent(NotchLifecycleState s, long generation) => s.RequestCurrent && s.Generation == generation;

    private static NotchLifecycleState BeginRequest(NotchLifecycleState s, ImmutableArray<NotchEffect>.Builder effects)
    {
        if (s.RequestCurrent)
        {
            effects.Add(new NotchEffect.CancelRequest(s.Generation));
        }
        s = CloseMicrophone(s, effects, discardHeldTurn: true);
        var generation = s.Generation + 1;
        effects.Add(new NotchEffect.BeginRequest(generation));
        return s with { Generation = generation, RequestCurrent = true, PendingProposal = null, PendingUndo = null };
    }

    private static NotchLifecycleState TalkDown(NotchLifecycleState s, TalkHoldSource source, DateTimeOffset now, ImmutableArray<NotchEffect>.Builder effects)
    {
        if (s.Holds.Contains(source)) return s;
        if (!s.Holds.IsEmpty) return s with { Holds = s.Holds.Add(source) };
        s = BeginRequest(s, effects);
        effects.Add(new NotchEffect.OpenMicrophone());
        return s with { Holds = s.Holds.Add(source), MicrophoneOn = true, HoldStartedAt = now };
    }

    private static NotchLifecycleState EndHold(NotchLifecycleState s, DateTimeOffset now, ImmutableArray<NotchEffect>.Builder effects, bool sendRegardless)
    {
        var tapped = !sendRegardless && s.HoldStartedAt is { } started && now - started < HoldToTalkController.MinimumHold;
        s = CloseMicrophone(s, effects, discardHeldTurn: false);
        effects.Add(tapped ? new NotchEffect.DiscardTurn(HoldToTalkController.TapHint) : new NotchEffect.CommitTurn());
        return s;
    }

    private static NotchLifecycleState CloseMicrophone(NotchLifecycleState s, ImmutableArray<NotchEffect>.Builder effects, bool discardHeldTurn)
    {
        if (s.MicrophoneOn)
        {
            effects.Add(new NotchEffect.CloseMicrophone());
            if (discardHeldTurn) effects.Add(new NotchEffect.DiscardTurn(null));
        }
        return s with { MicrophoneOn = false, Holds = ImmutableHashSet<TalkHoldSource>.Empty, HoldStartedAt = null };
    }

    private static NotchLifecycleState Stop(NotchLifecycleState s, ImmutableArray<NotchEffect>.Builder effects)
    {
        if (s.RequestCurrent)
        {
            effects.Add(new NotchEffect.CancelRequest(s.Generation));
        }
        s = CloseMicrophone(s, effects, discardHeldTurn: true);
        return s with
        {
            RequestCurrent = false,
            Thinking = false,
            RespondingUntil = null,
            NudgeUntil = null,
            HappyUntil = null,
            PendingProposal = null,
            PendingUndo = null,
            Focus = s.Focus == FocusStatus.Running ? FocusStatus.Paused : s.Focus,
        };
    }

    private NotchLifecycleState EndFocus(NotchLifecycleState s, FocusEndReason reason, DateTimeOffset now, ImmutableArray<NotchEffect>.Builder effects)
    {
        if (s.Focus is not (FocusStatus.Running or FocusStatus.Paused)) return s;
        effects.Add(new NotchEffect.FocusEnded(s.FocusSessionId, reason));
        return s with { Focus = FocusStatus.Ended, HappyUntil = now + _timings.HappyHold };
    }

    private static NotchLifecycleState Approve(NotchLifecycleState s, long proposalId, ImmutableArray<NotchEffect>.Builder effects)
    {
        if (s.PendingProposal is not { } proposal || proposal.Id != proposalId) return s;
        if (!IsCurrent(s, proposal.Generation)) return s with { PendingProposal = null };
        effects.Add(new NotchEffect.ApplyProposal(proposal.Id, proposal.Generation));
        var entry = new NotchJournalEntry(proposal.Id, proposal.Journal, IsUndo: false);
        s = s with { PendingProposal = null, Workspace = s.Workspace.Add(proposal.Id) };
        return proposal.Journal == EditJournal.File ? s with { FileJournal = entry } : s with { FieldJournal = entry };
    }

    private static NotchLifecycleState PlanUndo(NotchLifecycleState s)
    {
        var target = s.FileJournal ?? s.FieldJournal;
        return target is null ? s : s with { PendingUndo = new NotchPendingUndo(target, s.Generation) };
    }

    private static NotchLifecycleState ApproveUndo(NotchLifecycleState s, ImmutableArray<NotchEffect>.Builder effects)
    {
        if (s.PendingUndo is not { } pending) return s;
        if (!IsCurrent(s, pending.Generation)) return s with { PendingUndo = null };
        var entry = pending.Entry;
        effects.Add(new NotchEffect.ApplyUndo(entry));
        s = s with
        {
            PendingUndo = null,
            Workspace = entry.IsUndo ? s.Workspace.Add(entry.ProposalId) : s.Workspace.Remove(entry.ProposalId),
        };
        return entry.Journal == EditJournal.File
            ? s with { FileJournal = entry with { IsUndo = !entry.IsUndo } }
            : s with { FieldJournal = null };
    }

    private NotchLifecycleState Settle(NotchLifecycleState s, DateTimeOffset now)
    {
        if (s.Pinned || s.Hovered)
        {
            return s.Revealed
                ? s with { RetractAt = null }
                : s with { Revealed = true, RevealEndsAt = now + _timings.Reveal, RetractAt = null };
        }
        if (!s.Revealed) return s;
        if (s.RetractAt is not { } retractAt)
        {
            var from = s.RevealEndsAt > now ? s.RevealEndsAt : now;
            return s with { RetractAt = from + _timings.RetractDelay + _timings.Retract };
        }
        return now >= retractAt ? s with { Revealed = false, RetractAt = null } : s;
    }
}
