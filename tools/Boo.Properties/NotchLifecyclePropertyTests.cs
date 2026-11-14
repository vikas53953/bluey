using Boo.Core;
using Xunit.Abstractions;
using E = Boo.Core.NotchEvent;
using Fx = Boo.Core.NotchEffect;

namespace Boo.Properties;

/// <summary>
/// The five properties of agent-trust plan PR 2.5 over the reducer <see cref="NotchLifecycle"/>. Each checks its
/// invariants (docs/architecture/notch-lifecycle.md section 4, specs/README.md) after every step of every random walk.
/// The reducer models today's behaviour, gaps included: where a documented finding (F4, F6, F9, F10) breaks an
/// invariant as the plan wrote it, the property checks what today's code promises and names the gap as a known
/// exception, never the opposite of the frozen Gap_F* tests in tests/Boo.Core.Tests/NotchLifecycleTests.cs.
/// </summary>
public sealed class NotchLifecyclePropertyTests
{
    private readonly ITestOutputHelper output;

    /// <summary>xUnit gives each test its log.</summary>
    public NotchLifecyclePropertyTests(ITestOutputHelper output) => this.output = output;

    /// <summary>
    /// Property 1, lifecycle and presence. INV-PRIV-1 (indicator or capture on: the notch is out and shows Listening or
    /// Capture); the face is always the resolver's face and every change is published; an event that is not enabled
    /// changes nothing; generations never go back; Quit is final and leaves everything hidden and off; INV-STOP-1 as
    /// today's code keeps it (Stop: mic, holds, voice and chat flags off). Known exceptions F4: Stop pauses a running
    /// focus session (Paused pins the notch) and a capture open at Stop stays on until its own end. Known exception
    /// F9: Quit leaves a live session Paused, so the face the model computes after Quit reads Paused, not Hidden.
    /// </summary>
    [Fact]
    public void P1_Lifecycle_and_presence_invariants_hold_after_every_step()
    {
        output.WriteLine(Walks.Run("P1 lifecycle/presence", walk =>
        {
            foreach (var w in Walks.Steps(walk))
            {
                var (before, after) = (w.Before, w.After);
                if (!w.Step.Handled)
                    w.Require(ReferenceEquals(before, after) && w.Step.Effects.Count == 0, "ignored event changes nothing");
                w.Require(before.Alive || !w.Step.Handled, "Quit is final: nothing is handled after it");
                w.Require(after.Generation >= before.Generation, "G1: the generation never goes back");

                var flags = new FaceInputs(
                    Listening: after.MicIndicator, Thinking: after.Thinking, Responding: after.Responding,
                    CaptureActive: after.CaptureActive, NudgeShowing: after.Nudge, FocusPaused: after.Focus == FocusStatus.Paused,
                    Happy: after.Happy);
                var face = FaceStateResolver.Resolve(flags with { Revealed = after.Phase != RevealPhase.Hidden, Hovered = after.Hovered });
                w.Require(face == after.Face, "the face is the resolver's face");
                var published = w.Step.Effects.OfType<Fx.FaceChanged>().ToList();
                w.Require(before.Face == after.Face
                    ? published.Count == 0
                    : published.Count == 1 && published[0] == new Fx.FaceChanged(before.Face, after.Face), "every face change is published once");

                if (after.MicIndicator || after.CaptureActive)
                {
                    w.Require(after.Phase != RevealPhase.Hidden, "INV-PRIV-1: indicator on => notch not Hidden");
                    w.Require(after.Face is NotchFaceState.Listening or NotchFaceState.Capture, "INV-PRIV-1: indicator on => face shows it");
                }

                if (w.Event is E.Quit && w.Step.Handled)
                {
                    w.Require(!after.Alive && after.Phase == RevealPhase.Hidden, "F21: Quit hides the notch");
                    w.Require(after.Face == (after.Focus == FocusStatus.Paused ? NotchFaceState.Paused : NotchFaceState.Hidden),
                        "F21 / F9 (known gap): after Quit the face is Hidden, or Paused for a session Quit left paused");
                    w.Require(after.MicDevice == MicOwner.Off && !after.MicIndicator && !after.CaptureActive, "F21: Quit turns mic and capture off");
                    w.Require(after.Card is null && after.Ticket is null, "F21: Quit closes the card");
                }

                if (w.Event is E.Stop && w.Step.Handled)
                {
                    w.Require(after.MicDevice == MicOwner.Off && !after.MicIndicator, "INV-STOP-1: Stop turns the mic off");
                    w.Require(!after.ChordHeld && !after.EyesHeld && after.Voice == VoiceSessionKind.Off, "INV-STOP-1: Stop ends holds and voice");
                    w.Require(!after.Thinking && !after.Responding && !after.Nudge && !after.Happy, "INV-STOP-1: Stop clears the chat flags");
                    // F4 lets Stop PAUSE a running session; it never discards one (Codex review of cb1b9a0, P2).
                    w.Require(before.Focus == FocusStatus.Running
                        ? after.Focus == FocusStatus.Paused
                        : after.Focus == before.Focus, "F4 (known gap): Stop turns a running session Paused and keeps any other focus state");
                }
            }
        }));
    }

    /// <summary>
    /// Property 2, Apply and Undo. INV-APPLY-1 (an effect commits only on Apply, under the ticket of the exact
    /// proposal on the executing card); INV-APPLY-3 (a card whose generation is no longer current never applies; Stop
    /// leaves no current generation, G2); the ticket exists exactly while a card is executing; INV-APPLY-2 for fields
    /// (an undo needs a recorded edit, a verified one empties the journal); journals change only on Apply, per A6 U5
    /// U6. Known exception F6: a verified FILE undo is journaled as a new entry, so a second file undo re-applies.
    /// </summary>
    [Fact]
    public void P2_Apply_and_Undo_need_an_approved_current_card()
    {
        output.WriteLine(Walks.Run("P2 apply/undo", walk =>
        {
            foreach (var w in Walks.Steps(walk))
            {
                var (before, after) = (w.Before, w.After);
                var applied = w.Step.Effects.OfType<Fx.Applied>().ToList();
                w.Require(applied.Count == (w.Event is E.Apply && w.Step.Handled ? 1 : 0), "INV-APPLY-1: one commit per handled Apply, none otherwise");

                w.Require(after.Card is { Stage: CardStage.Executing } executing
                    ? after.Ticket is not null && after.Ticket == executing.Proposal
                    : after.Ticket is null, "A5: a ticket exists exactly while its card executes");
                w.Require(after.FieldJournal != JournalTop.Undo, "U5: the field journal never records an undo");

                if (w.Event is E.Apply && before.Card is { } staleCard && !before.IsCurrent(staleCard.Generation))
                    w.Require(!w.Step.Handled, "INV-APPLY-3: a stale card never applies");

                if (w.Event is E.Approve && w.Step.Handled && after.Card?.Stage == CardStage.Executing)
                    w.Require(before.Card is { Stage: CardStage.Pending } shown && before.IsCurrent(shown.Generation), "A5: Approve only a pending card of the current request");

                if (w.Event is E.Stop && w.Step.Handled)
                    w.Require(after.RequestStopped && !after.IsCurrent(after.Generation), "G2: Stop leaves no current generation");

                if (applied.Count == 0)
                {
                    w.Require(after.FileJournal == before.FileJournal && after.FieldJournal == before.FieldJournal, "journals change only on Apply");
                    continue;
                }

                var a = applied[0];
                w.Require(a.Ticket is not null && a.Proposal == a.Ticket && a.Current, "INV-APPLY-1: committed under the exact ticket");
                w.Require(before.Card is { Stage: CardStage.Executing } card && card.Proposal == a.Proposal && before.IsCurrent(card.Generation),
                    "INV-APPLY-1/3: committed from the executing card of the current request");
                var verified = ((E.Apply)w.Event).Verified;
                switch (a.Proposal)
                {
                    case { Kind: ProposalKind.Undo, Target: EditTarget.Field }:
                        w.Require(a.JournalBefore == JournalTop.Edit, "INV-APPLY-2: a field undo needs a recorded edit");
                        w.Require(after.FieldJournal == (verified ? JournalTop.None : before.FieldJournal), "U5: a verified field undo empties the journal");
                        break;
                    case { Kind: ProposalKind.Undo, Target: EditTarget.File }:
                        w.Require(a.JournalBefore != JournalTop.None, "U3: a file undo needs a recorded entry");
                        w.Require(after.FileJournal == (verified ? JournalTop.Undo : before.FileJournal), "U6 / F6 (known gap): a verified file undo is journaled");
                        break;
                    case { Kind: ProposalKind.Edit, Target: EditTarget.Field }:
                        w.Require(after.FieldJournal == (verified ? JournalTop.Edit : JournalTop.None), "A6: field edit journaled, unverified clears");
                        break;
                    default:
                        w.Require(after.FileJournal == (verified ? JournalTop.Edit : before.FileJournal), "A6: verified file edit journaled");
                        break;
                }
            }
        }));
    }

    /// <summary>
    /// Property 3, focus. INV-FOCUS-1 as today's code keeps it: at most one end event per session (counted over the
    /// whole walk, reset by each FocusStart), raised only from a live session and leaving it Ended with Happy; the
    /// recorded count matches the status; an ended session changes only by a new Start; a nudge appears only from a
    /// drifting check of a running session (F16); a check starts only while running (Fo8). Known exception F9: a
    /// session replaced by Start or open at Quit closes with no end event (FocusClosedWithoutEndEvent instead).
    /// </summary>
    [Fact]
    public void P3_Focus_end_is_raised_at_most_once_per_session()
    {
        output.WriteLine(Walks.Run("P3 focus", walk =>
        {
            var endsThisSession = 0;
            foreach (var w in Walks.Steps(walk))
            {
                var (before, after) = (w.Before, w.After);
                if (w.Event is E.FocusStart && w.Step.Handled)
                    endsThisSession = 0;
                var ended = w.Step.Effects.OfType<Fx.FocusEnded>().Count();
                endsThisSession += ended;
                w.Require(endsThisSession <= 1, "INV-FOCUS-1: at most one end event per session");
                w.Require(after.FocusEndEvents == (after.Focus == FocusStatus.Ended ? 1 : 0), "Fo7: end count matches the status");
                if (ended > 0)
                    w.Require(before.FocusLive && after.Focus == FocusStatus.Ended && after.Happy, "Fo5 Fo6 F18: an end comes from a live session and shows Happy");

                var quietClose = w.Step.Effects.OfType<Fx.FocusClosedWithoutEndEvent>().Count();
                var replacedOrQuit = w.Step.Handled && w.Event is E.FocusStart or E.Quit && before.FocusLive;
                w.Require(quietClose == (replacedOrQuit ? 1 : 0), "F9 (known gap): only Start or Quit close a live session quietly");

                if (before.Focus == FocusStatus.Ended && w.Event is not E.FocusStart)
                    w.Require(after.Focus == FocusStatus.Ended, "an ended session changes only by a new Start");
                if (before.Focus is null && w.Event is not E.FocusStart)
                    w.Require(after.Focus is null, "no session appears without Start");
                if (after.Nudge && !before.Nudge)
                    w.Require(w.Event is E.FocusCheckDone { Drifting: true } && before.Focus == FocusStatus.Running, "F16: a nudge only from a drifting check while running");
                if (after.FocusCheckActive && !before.FocusCheckActive)
                    w.Require(before.Focus == FocusStatus.Running, "Fo8: a focus check starts only while running");
                if (after.Focus == FocusStatus.Paused && after.Alive)
                    w.Require(after.Pinned, "F19: Paused pins the notch out");
            }
        }));
    }

    /// <summary>
    /// Property 4, talk and microphone. At most one microphone owner: replaying the opened/closed effects over the
    /// whole walk never shows two devices open and always matches the state; a mic opens only on TalkButton or
    /// TalkDown; INV-TALK-1 for the push-to-talk mic: open only while held, never unseen, and after the last
    /// release (TalkUp, HoldTimeout) or Stop the hold mic and the indicator are off; Stop and a voice end turn every
    /// mic off. The indicator never shows with no mic open. Known exception F10: a Talk-button mic opened during a
    /// hold survives the release with the indicator off (so the release check is "not the hold mic", not "off").
    /// F10 is the ONLY way a Talk-button mic may be open without its indicator: that state must have just been made
    /// by a handled release (TalkUp, HoldTimeout) of an open Talk-button mic, or carried over unchanged from it.
    /// Any other unseen Talk-button mic fails (Codex review of cb1b9a0, P1: an invisible Talk button survived).
    /// </summary>
    [Fact]
    public void P4_Talk_has_one_mic_owner_and_release_turns_the_hold_mic_off()
    {
        output.WriteLine(Walks.Run("P4 talk/mic", walk =>
        {
            var open = MicOwner.Off;
            foreach (var w in Walks.Steps(walk))
            {
                var after = w.After;
                foreach (var effect in w.Step.Effects)
                {
                    if (effect is Fx.MicrophoneClosed closed)
                    {
                        w.Require(open == closed.Owner, "mic: only the open device is closed");
                        open = MicOwner.Off;
                    }
                    else if (effect is Fx.MicrophoneOpened opened)
                    {
                        w.Require(open == MicOwner.Off, "mic: at most one owner (a second device opened)");
                        w.Require(w.Event is E.TalkButton or E.TalkDown, "F12 H1: a mic opens only on Talk or a hold");
                        open = opened.Owner;
                    }
                }
                w.Require(open == after.MicDevice, "mic: the effects match the state");

                var held = after.ChordHeld || after.EyesHeld;
                w.Require(after.MicDevice != MicOwner.Hold || held, "INV-TALK-1: the hold mic is open only while held");
                w.Require(after.MicDevice != MicOwner.Hold || after.MicIndicator, "INV-PRIV-1: the hold mic never records unseen");
                w.Require(!after.MicIndicator || after.MicDevice != MicOwner.Off, "F12: the indicator never shows with no mic");
                if (after.MicDevice == MicOwner.Button && !after.MicIndicator)
                {
                    var before = w.Before;
                    var carriedOver = before.MicDevice == MicOwner.Button && !before.MicIndicator;
                    var madeByRelease = w.Step.Handled && w.Event is E.TalkUp or E.HoldTimeout
                        && before.MicDevice == MicOwner.Button;
                    w.Require(carriedOver || madeByRelease,
                        "INV-PRIV-1 / F10 (narrow): a Talk-button mic is unseen only after a hold release left it open");
                }

                var lastRelease = w.Event is E.TalkUp or E.HoldTimeout && w.Step.Handled && !held && after.Alive;
                if (lastRelease)
                    w.Require(after.MicDevice != MicOwner.Hold && !after.MicIndicator, "INV-TALK-1 / F10 (known gap): the last release turns the hold mic and indicator off");
                if (w.Step.Handled && w.Event is E.Stop or E.VDrop or E.VGoToSleep)
                    w.Require(after.MicDevice == MicOwner.Off && !after.MicIndicator && !held, "INV-TALK-1: Stop or a voice end turns every mic off");
            }
        }));
    }

    /// <summary>
    /// Property 5, pointer and hover reveal (R1-R11, LIVE-2). While the notch is wanted out (hover or a pinning
    /// face) it is Revealing or Revealed; when not wanted it is never left Revealed; PointerEnter is handled only when
    /// not hovered and always reveals, PointerLeave only when hovered; a Tick changes only the phase and only forward
    /// (Revealing to Revealed or RetractPending, RetractPending to Retracting, Retracting to Hidden); LIVE-2: when
    /// nothing wants the notch out, at most three Ticks bring it to Hidden with the Hidden face.
    /// </summary>
    [Fact]
    public void P5_Pointer_and_pins_drive_the_reveal_and_timers_alone_hide_it()
    {
        output.WriteLine(Walks.Run("P5 pointer/reveal", walk =>
        {
            foreach (var w in Walks.Steps(walk))
            {
                var (before, after) = (w.Before, w.After);
                if (w.Event is E.PointerEnter)
                    w.Require(w.Step.Handled == (before.Alive && !before.Hovered), "R1: PointerEnter is handled only when not hovered");
                if (w.Event is E.PointerLeave)
                    w.Require(w.Step.Handled == (before.Alive && before.Hovered), "R3: PointerLeave is handled only when hovered");
                if (w.Event is E.PointerEnter && w.Step.Handled)
                    w.Require(after.Hovered && after.Phase is RevealPhase.Revealing or RevealPhase.Revealed, "R1 R7 R8: PointerEnter reveals");

                if (w.Event is E.Tick && w.Step.Handled)
                {
                    w.Require(after == (before with { Phase = after.Phase }), "R2-R6: a Tick changes only the phase");
                    var forward = (before.Phase, after.Phase) switch
                    {
                        (RevealPhase.Revealing, RevealPhase.Revealed or RevealPhase.RetractPending) => true,
                        (RevealPhase.RetractPending, RevealPhase.Retracting) => true,
                        (RevealPhase.Retracting, RevealPhase.Hidden) => true,
                        _ => false,
                    };
                    w.Require(forward, "R2-R6: a Tick moves the timeline one step forward");
                }

                if (!after.Alive)
                {
                    w.Require(after.Phase == RevealPhase.Hidden, "F21: after Quit the notch is Hidden");
                    continue;
                }
                if (after.WantsOut)
                    w.Require(after.Phase is RevealPhase.Revealing or RevealPhase.Revealed, "R1 R7 R8 R9 R10: wanted out => Revealing or Revealed");
                else
                    w.Require(after.Phase != RevealPhase.Revealed, "R3 R11: not wanted => not left Revealed");

                var settled = after;
                for (var tick = 0; tick < 3; tick++)
                    settled = NotchLifecycle.Reduce(settled, new E.Tick()).State;
                if (after.WantsOut)
                    w.Require(settled.Phase == RevealPhase.Revealed, "R2: wanted out, timers alone reach Revealed and stay");
                else
                    w.Require(settled.Phase == RevealPhase.Hidden && settled.Face == NotchFaceState.Hidden, "LIVE-2: with nothing wanting it, timers alone hide the notch");
            }
        }));
    }
}
