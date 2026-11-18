--------------------------- MODULE NotchLifecycle ---------------------------
(***************************************************************************)
(* Boo's notch lifecycle AS THE CODE BEHAVES TODAY.                        *)
(*                                                                         *)
(* Source of truth: docs/architecture/notch-lifecycle.md (agent-trust plan *)
(* PR 2.1). Every action below names the rows of that doc it models, for   *)
(* example [R1] (reveal timeline), [F3] (face flags), [H4] (hold-to-talk), *)
(* [Fo2] (focus), [G1] (request generations), [A5] (approval card),        *)
(* [U6] (undo). Findings F1-F10 of the doc are the "doc findings" below.   *)
(* If the code changes, the doc row, this action and (from PR 2.4) the C#  *)
(* reducer change together in one PR.                                      *)
(*                                                                         *)
(* THE FAIRNESS RULE (specs/README.md has the table with code lines): an   *)
(* action is weakly fair if and only if the C# guarantees the step         *)
(* completes by a LOCAL bound (a timer, a CancelAfter or the 45 s          *)
(* HttpClient timeout, a cancelled token, a finally after a bounded        *)
(* await). Events pushed by the provider are never fair; neither is the   *)
(* user. Websocket sends (SendTextAsync, CompleteToolAsync,                *)
(* InterruptAsync, CommitAudioTurnAsync) have no timeout of their own but  *)
(* ARE bounded indirectly: the client's keep-alive (KeepAliveInterval and  *)
(* KeepAliveTimeout, 10 s each, RealtimeClient.cs:293-294, :339-340)       *)
(* aborts a socket whose ping goes unanswered, about 20 s, which fails the *)
(* pending send. The model still treats steps that wait on them as UNFAIR: *)
(* the cautious direction, it only adds behaviours (a send that never      *)
(* ends). Fairness is always on the disjunction of ALL outcomes of a step  *)
(* (success and failure), never on the success branch alone.               *)
(*                                                                         *)
(* Three switches (constants), "none" for today's code:                    *)
(*  - Plant: a planted, plausible code bug. Each main check has one, and   *)
(*    scripts/ci/run_tlc.sh requires the check to catch it (red proof).    *)
(*  - Fix: the obvious fix of one gap finding. The script requires the     *)
(*    gap to FAIL with Fix = "none" and to PASS with its Fix.              *)
(*  - MaxActive: how many lanes may be busy at once (see Active).          *)
(*                                                                         *)
(* Other abstractions (each one is listed again in specs/README.md):       *)
(*  - Time is not a number: timers and the render loop are fair actions.  *)
(*  - Request generations are a "still current" bit (cur) on each piece of *)
(*    in-flight work. This is exact: generations only grow and every check *)
(*    in the code is "equal to the current one".                           *)
(*  - "mic" has two parts: micDev = who has the microphone DEVICE open,    *)
(*    micInd = the notch's microphoneActive indicator (finding F10).       *)
(*  - Ghost variables, not app state: effect (what the last step applied), *)
(*    last (what kind of step produced this state). Both are reset by      *)
(*    every step, so they cost few extra states.                           *)
(***************************************************************************)
EXTENDS Naturals

CONSTANTS
    None,       \* model value: "nothing here"
    MaxActive,  \* how many lanes may be busy at once (see Active)
    Plant,      \* "none", or one planted bug from Plants (red proofs only)
    Fix         \* "none", or one gap fix from Fixes (fix runs only)

Plants ==
    {"none",
     "PinIgnoresCapture",        \* INV_PRIV_1: starting a capture does not re-pin the notch
     "ApplyWithoutApprove",      \* INV_APPLY_1: a pending proposal is applied with no Approve
     "FieldUndoKeepsJournal",    \* INV_APPLY_2_FIELD: a verified field undo forgets to clear the journal
     "UndoWithoutJournal",       \* INV_APPLY_2_FIELD: a field undo is planned with nothing recorded
     "CommitIgnoresGeneration",  \* INV_APPLY_3: TryCommit stops checking the generation
     "StopKeepsMic",             \* INV_TALK_1: Stop forgets to stop the audio device
     "ReleaseKeepsIndicator",    \* INV_TALK_1_RELEASE: the last release leaves the mic indicator on
     "EndNotGuarded",            \* INV_FOCUS_AT_MOST_ONE: End after Complete raises a second end event
     "NoRespondingTimer",        \* LIVE_1_RESPONDING: the Responding moment timer never fires
     "NoRetractTimer"}           \* LIVE_2: the 900 ms retract delay never fires

Fixes ==
    {"none",
     "F4",   \* INV_STOP_1: Stop ends every capture at once and ends a live focus session (with its event)
     "F5",   \* INV_PRIV_2: a pending capture pins the notch; the grab waits until the notch is out
     "F6",   \* INV_APPLY_2_FILE: a verified file undo clears the file journal instead of journaling itself
     "F7",   \* LIVE_1_THINKING: a local Thinking timeout
     "F9",   \* INV_FOCUS_1_EXACT: replacing or quitting a live session raises its end event
     "F10"}  \* INV_PRIV_1_DEVICE, INV_TALK_1_DEVICE: the last release stops whichever audio is current

ASSUME MaxActive \in 1..8
ASSUME Plant \in Plants
ASSUME Fix \in Fixes

VARIABLES
    alive,       \* Boo is running (FALSE after Quit)
    hovered,     \* pointer over the notch strip (NotchWindow hovered)
    phase,       \* RevealPhase (NotchFace.cs:285)
    thinking,    \* FaceInputs.Thinking
    responding,  \* FaceInputs.Responding (a timed "moment")
    nudge,       \* FaceInputs.NudgeShowing (6 s moment)
    happy,       \* FaceInputs.Happy (2.5 s moment)
    micDev,      \* microphone device: "Off" | "Hold" (push-to-talk audio) | "Button" (Talk button audio)
    micInd,      \* notch microphoneActive indicator (= MainWindow._listening)
    chord,       \* HoldToTalkController: Ctrl+Alt+Space holder
    eyes,        \* HoldToTalkController: eyes holder
    voice,       \* realtime voice session: "Off" | "Ptt" | "Other" (Talk button) | "Text" (typed hand-off, no audio)
    typed,       \* in-flight typed request, or None
    vtool,       \* in-flight voice tool call (screen_help), or None
    vundo,       \* in-flight spoken "undo", or None
    fcheck,      \* focus check: "None" | "Pre" (only with Fix F5) | "On" (capturing / classifying)
    card,        \* approval card (proposal + Pending/Executing), or None
    ticket,      \* approval ticket = the approved proposal's digest, or None
    fileJ,       \* FileUndoJournal top entry: "none" | "edit" | "undo" (an entry written BY an undo)
    fieldJ,      \* UndoJournal: "none" | "edit"
    focus,       \* focus session: "Off" (none or Ended) | "Running" | "Paused"
    focusEnds,   \* end events raised for the current focus session (_endRaised)
    effect,      \* ghost: what the last step applied, or None
    last         \* ghost: "Other" | "Input" | "Stop" | "QuietEnd" | "Replaced" (see the checks)

flagV  == <<thinking, responding, nudge, happy>>
micV   == <<micDev, micInd, chord, eyes, voice>>
workV  == <<typed, vtool, vundo, fcheck>>
applyV == <<card, ticket, fileJ, fieldJ>>
focusV == <<focus, focusEnds>>
vars   == <<alive, hovered, phase, flagV, micV, workV, applyV, focusV, effect, last>>

-----------------------------------------------------------------------------
(* Value sets *)

Phases    == {"Hidden", "Revealing", "Revealed", "RetractPending", "Retracting"}
Ids       == {"p1", "p2"}
Kinds     == {"Edit", "Undo"}
Targets   == {"File", "Field"}
Proposals == [id : Ids, kind : Kinds, target : Targets]
OtherId(i) == IF i = "p1" THEN "p2" ELSE "p1"
Live      == {"Running", "Paused"}
Caps      == {"Pre", "On", "Off"}

\* path: "Plain" = answered over HTTP (speech off or muted; also every typed undo), "Voice" = speech on, so
\* the text may be handed to a voice session (a websocket send, modelled as unbounded; see the fairness rule).
TypedReqs == [kind : {"Ask", "Undo"}, cur : BOOLEAN, cap : Caps, path : {"Plain", "Voice"}]
\* st: "Work" = capture and HTTP answer; "Send" = answer done, CompleteToolAsync still pending.
ToolReqs  == [cur : BOOLEAN, cap : Caps, st : {"Work", "Send"}]
UndoReqs  == [cur : BOOLEAN, cap : Caps]
Cards     == [p : Proposals, st : {"Pending", "Executing"}, cur : BOOLEAN]
Effects   == [p : Proposals, ticket : Proposals \cup {None}, cur : BOOLEAN,
              journal : {"none", "edit", "undo"}, ok : BOOLEAN]

-----------------------------------------------------------------------------
(* What the notch shows *)

CapOn(x) == x # None /\ x.cap = "On"
CapPre(x) == x # None /\ x.cap = "Pre"

\* Capture indicator: any capture operation open (CaptureActivityTracker). [F14] [F15]
CaptureOn == CapOn(typed) \/ CapOn(vtool) \/ CapOn(vundo) \/ fcheck = "On"

\* Fix F5 only: a capture that is about to start pins the notch first.
PrePin == Fix = "F5" /\ (CapPre(typed) \/ CapPre(vtool) \/ CapPre(vundo) \/ fcheck = "Pre")

\* "Pinned" = the face would be non-Hidden without the pointer (NotchWindow.xaml.cs:510-512). [R9]
\* The Paused face comes from FocusPaused, which every path keeps equal to (focus = "Paused") [F19] [F20].
\* UserHidden, FaceInputs.Listening and FaceInputs.CaptureActive are never set by the app (doc finding F2).
Pinned ==
    \/ CaptureOn /\ Plant # "PinIgnoresCapture"
    \/ PrePin \/ micInd \/ responding \/ thinking \/ nudge \/ happy \/ focus = "Paused"
Want   == hovered \/ Pinned

\* FaceStateResolver.Resolve priority (NotchFace.cs:38-53), section 3.2 of the doc.
Face ==
    CASE CaptureOn          -> "Capture"
      [] micInd             -> "Listening"
      [] responding         -> "Responding"
      [] thinking           -> "Thinking"
      [] nudge              -> "Nudge"
      [] happy              -> "Happy"
      [] focus = "Paused"   -> "Paused"
      [] hovered            -> "Hover"
      [] phase # "Hidden"   -> "Idle"
      [] OTHER              -> "Hidden"

\* The reveal timeline's reaction to Show/Hide (NotchFace.cs:311-339, :380-392).
\* Want TRUE:  [R1] [R9] Hidden -> Revealing; [R7] RetractPending -> Revealed; [R8] Retracting -> Revealing.
\* Want FALSE: [R3] [R11] Revealed -> RetractPending; Revealing keeps revealing first [R4].
\* [R10] pointer leaves while pinned: Want stays TRUE, nothing changes.
Retime(ph, w) ==
    IF w THEN (CASE ph = "Hidden"         -> "Revealing"
                 [] ph = "RetractPending" -> "Revealed"
                 [] ph = "Retracting"     -> "Revealing"
                 [] OTHER                 -> ph)
         ELSE (IF ph = "Revealed" THEN "RetractPending" ELSE ph)

\* The plan's quiet state: Awake does not exist today (doc finding F3), so Idle/Hover/Hidden.
Quiet == Face \in {"Hidden", "Idle", "Hover"} /\ micDev = "Off" /\ ~micInd /\ ~CaptureOn

\* Busy lanes: a typed request, a voice tool call, a spoken undo, a focus check, an approval card, an open voice
\* session, a hold, a live focus session. Next never goes above MaxActive (a model bound, not app behaviour).
B(c) == IF c THEN 1 ELSE 0
Active ==
    B(typed # None) + B(vtool # None) + B(vundo # None) + B(fcheck # "None") + B(card # None)
  + B(voice # "Off") + B(chord \/ eyes) + B(focus \in Live)

-----------------------------------------------------------------------------
(* Helpers *)

\* A piece of in-flight work stops being current (generation bump). [G1] [G2] [G3] [G4]
Stale(x) == IF x = None THEN None ELSE [x EXCEPT !.cur = FALSE]

\* Fix F4 only: Stop ends a capture operation at once.
CapEnd(x) == IF x = None \/ Fix # "F4" THEN x ELSE [x EXCEPT !.cap = "Off"]

\* New request / Stop / voice session end: a pending card is cleared [A3]; an executing one keeps running but
\* is no longer current, so TryCommit refuses its effects [G5].
CardNewGen   == IF card = None THEN None
                ELSE (IF card.st = "Pending" THEN None ELSE [card EXCEPT !.cur = FALSE])
TicketNewGen == IF card # None /\ card.st = "Executing" THEN ticket ELSE None

EditCard(t) == [p |-> [id |-> "p1", kind |-> "Edit", target |-> t], st |-> "Pending", cur |-> TRUE]
UndoCardFor(t) == [p |-> [id |-> "p1", kind |-> "Undo", target |-> t], st |-> "Pending", cur |-> TRUE]

\* [A1] a reply's actions become a card only for an explicit task (propose); other replies' actions are dropped.
\* Simplification: a reply that arrives while a card is already up drops its actions.
ProposeCard(propose, t) == IF propose /\ card = None THEN EditCard(t) ELSE card

\* [U1] the file journal wins if it holds something, else the field journal; [U2] nothing -> no card
\* (HasPendingUndo, MainWindow.Edits.cs:74-85); [U3] [U4] a plan can be refused (file changed since, newer field
\* change, ambiguous control, no frame): planOK = FALSE.
UndoCard(planOK) ==
    IF card = None /\ planOK
    THEN (IF fileJ # "none" THEN UndoCardFor("File")
          ELSE (IF fieldJ # "none" \/ Plant = "UndoWithoutJournal" THEN UndoCardFor("Field") ELSE None))
    ELSE card

JournalOf(t) == IF t = "File" THEN fileJ ELSE fieldJ

\* Journals after an applied step; ok = the read-back verified it (MainWindow.xaml.cs:1229-1236).
\* File [A6] [U6]: a verified write is journaled (RecordFileEdit only on success); a verified undo is journaled as a
\* new entry written BY an undo (doc finding F6), or, with Fix F6, clears the journal.
FileAfter(p, ok) ==
    IF p.target # "File" \/ ~ok THEN fileJ
    ELSE IF p.kind = "Edit" THEN "edit"
    ELSE IF Fix = "F6" THEN "none" ELSE "undo"
\* Field [A6] [U5]: a verified type_text is journaled, an unverified one clears the journal (MainWindow.Edits.cs:235-256); a
\* field undo clears the journal only after its read-back succeeds (MainWindow.Edits.cs:340-352 -> UndoJournal.cs:197-211).
FieldAfter(p, ok) ==
    IF p.target # "Field" THEN fieldJ
    ELSE IF p.kind = "Edit" THEN (IF ok THEN "edit" ELSE "none")
    ELSE IF ok /\ Plant # "FieldUndoKeepsJournal" THEN "none" ELSE fieldJ

\* Ends every step that applies nothing: ghosts reset, timeline reacts to the new flags.
\* Must be the LAST conjunct of an action (Want' needs every other primed variable).
Fin(l) ==
    /\ effect' = None
    /\ last' = l
    /\ UNCHANGED alive
    /\ phase' = Retime(phase, Want')

\* BeginRequest (MainWindow.xaml.cs:1967-1993) [G1], which first runs StopListening: mic off, voice session
\* dropped, Thinking/Responding cleared (:932-953), old work stale, pending card cleared [A3].
\* th/ty/vo/md/mi: what the caller sets right after.
NewRequest(th, ty, vo, md, mi) ==
    /\ thinking' = th
    /\ responding' = FALSE
    /\ typed' = ty
    /\ vtool' = Stale(vtool)
    /\ vundo' = Stale(vundo)
    /\ card' = CardNewGen
    /\ ticket' = TicketNewGen
    /\ voice' = vo
    /\ micDev' = md
    /\ micInd' = mi
    /\ UNCHANGED <<hovered, nudge, happy, fcheck, fileJ, fieldJ, focus, focusEnds>>

\* StopListening (MainWindow.xaml.cs:915-953): audio stopped, indicator off, Thinking/Responding cleared;
\* with a voice session also a generation bump and the card cleared [G3]. Holds are NOT touched here.
StopListeningV ==
    /\ micDev' = "Off"
    /\ micInd' = FALSE
    /\ voice' = "Off"
    /\ thinking' = FALSE
    /\ responding' = FALSE
    /\ IF voice # "Off"
          THEN /\ typed' = Stale(typed)
               /\ vtool' = Stale(vtool)
               /\ vundo' = Stale(vundo)
               /\ card' = CardNewGen
               /\ ticket' = TicketNewGen
          ELSE UNCHANGED <<typed, vtool, vundo, card, ticket>>
    /\ UNCHANGED <<hovered, nudge, happy, fcheck, fileJ, fieldJ>>

\* The last hold release (StopPushToTalkMic, MainWindow.Talk.cs:383-398): the indicator always goes off; the device
\* only if it is the push-to-talk audio (doc finding F10), or always with Fix F10.
ReleaseMicDev == IF Fix = "F10" \/ micDev = "Hold" THEN "Off" ELSE micDev
ReleaseMicInd == IF Plant = "ReleaseKeepsIndicator" THEN micInd ELSE FALSE

\* Fix F9 only: a live session that is replaced or quit raises its end event first.
CloseLabel == IF focus \in Live THEN (IF Fix = "F9" THEN "Replaced" ELSE "QuietEnd") ELSE "Other"

-----------------------------------------------------------------------------
Init ==
    /\ alive = TRUE
    /\ hovered = FALSE
    /\ phase = "Hidden"
    /\ thinking = FALSE /\ responding = FALSE /\ nudge = FALSE /\ happy = FALSE
    /\ micDev = "Off" /\ micInd = FALSE /\ chord = FALSE /\ eyes = FALSE /\ voice = "Off"
    /\ typed = None /\ vtool = None /\ vundo = None /\ fcheck = "None"
    /\ card = None /\ ticket = None /\ fileJ = "none" /\ fieldJ = "none"
    /\ focus = "Off" /\ focusEnds = 0
    /\ effect = None
    /\ last = "Other"

-----------------------------------------------------------------------------
(* Pointer and reveal timeline (doc 3.1) *)

\* USER. [R1] [R7] [R8] [F1] pointer enters the notch strip.
PointerEnter ==
    /\ ~hovered
    /\ hovered' = TRUE
    /\ UNCHANGED <<flagV, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* USER. [R3] [R10] [F2] pointer leaves.
PointerLeave ==
    /\ hovered
    /\ hovered' = FALSE
    /\ UNCHANGED <<flagV, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* FAIR (render loop, NotchWindow.xaml.cs:639-674; timeline NotchFace.cs:347-353).
\* [R2] Revealing -> Revealed (240 ms); [R4] left mid-reveal: the reveal ends, then the retract delay starts;
\* [R5] RetractPending -> Retracting (900 ms); [R6] Retracting -> Hidden (160 ms).
Tick ==
    /\ alive
    /\ phase \in {"Revealing", "RetractPending", "Retracting"}
    /\ phase # "RetractPending" \/ (~Want /\ Plant # "NoRetractTimer")
    /\ phase' = (CASE phase = "Revealing"      -> (IF Want THEN "Revealed" ELSE "RetractPending")
                   [] phase = "RetractPending" -> "Retracting"
                   [] OTHER                    -> "Hidden")
    /\ effect' = None
    /\ last' = "Other"
    /\ UNCHANGED <<alive, hovered, flagV, micV, workV, applyV, focusV>>

-----------------------------------------------------------------------------
(* Moments: each timed flag is cleared by its own timer (MainWindow.xaml.cs:180-207). FAIR. *)

\* FAIR. [F5] Responding moment expires (4 s, or the playback tail capped by FaceMoments).
ExpireResponding ==
    /\ alive /\ responding /\ Plant # "NoRespondingTimer"
    /\ responding' = FALSE
    /\ UNCHANGED <<hovered, thinking, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* FAIR. [F16] nudge 6 s.
ExpireNudge ==
    /\ alive /\ nudge
    /\ nudge' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* FAIR. [F18] happy 2.5 s.
ExpireHappy ==
    /\ alive /\ happy
    /\ happy' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* FAIR, Fix F7 only: a local Thinking timeout (does not exist today: doc finding F7).
ExpireThinking ==
    /\ alive /\ thinking /\ Fix = "F7"
    /\ thinking' = FALSE
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Typed requests (Send, MainWindow.xaml.cs:733-855) *)

\* USER. [F3] [G1] [U1] Send: StopListening if listening, BeginRequest, Thinking on, Responding off.
\* k = "Undo" when the text is classified as Undo (:765-769), else "Ask". pth = the speech setting at Send.
Send(k, pth) ==
    /\ typed = None
    /\ k = "Undo" => pth = "Plain"
    /\ NewRequest(TRUE, [kind |-> k, cur |-> TRUE, cap |-> "Pre", path |-> pth], "Off", "Off", FALSE)
    /\ UNCHANGED <<chord, eyes>>
    /\ Fin("Input")

\* FAIR (local screenshot, CaptureForRequestAsync :2004-2063). [F14] the capture indicator goes on AFTER Thinking
\* already pinned the notch (doc INV-PRIV-2 row). With Fix F5 the grab waits until the notch is out.
TypedCapture ==
    /\ CapPre(typed) /\ typed.cur
    /\ Fix # "F5" \/ phase # "Hidden"
    /\ typed' = [typed EXCEPT !.cap = "On"]
    /\ UNCHANGED <<hovered, flagV, micV, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* FAIR (same step, other outcome). No frame taken: the capture operation ends at once (:757-760).
TypedNoFrame ==
    /\ CapPre(typed) /\ typed.cur
    /\ typed' = [typed EXCEPT !.cap = "Off"]
    /\ UNCHANGED <<hovered, flagV, micV, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [F4] [A1] HTTP reply (path Plain; or path Voice when the voice connect failed, :784): capture ends in finally
\* [F15], Thinking off, Responding on (4 s moment); an explicit task's actions become a card.
\* FAIR on path Plain together with TypedEnd (RespondAsync: 45 s HttpClient timeout, :2128).
TypedReply ==
    /\ typed # None /\ typed.kind = "Ask" /\ typed.cur /\ typed.cap # "Pre"
    /\ typed' = None
    /\ thinking' = FALSE
    /\ responding' = TRUE
    /\ \E propose \in BOOLEAN, t \in Targets : card' = ProposeCard(propose, t)
    /\ UNCHANGED <<hovered, nudge, happy, micV, vtool, vundo, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* NOT FAIR while current: SendTextAsync has no timeout of its own (RealtimeClient.cs:406-410; only the keep-alive,
\* RealtimeClient.cs:293-294, :339-340, bounds it, which the model leaves out on purpose). [F3] [F6] path Voice: the
\* text is handed to a text-only voice session; finally ends the capture but leaves Thinking on (MainWindow.xaml.cs:851). A failed
\* send after a successful connect is TypedEnd here (the code keeps the connected session, the model does not).
TypedHandOff ==
    /\ typed # None /\ typed.kind = "Ask" /\ typed.cur /\ typed.cap # "Pre" /\ typed.path = "Voice"
    /\ typed' = None
    /\ voice' = "Text"
    /\ UNCHANGED <<hovered, flagV, micDev, micInd, chord, eyes, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* FAIR together with TypedEnd (local: HandleUndoRequestAsync reads controls and the journals, MainWindow.Edits.cs:71-123).
\* [U1]-[U4] typed "undo": the plan becomes an Undo card (or a message); finally clears Thinking.
TypedUndoPlan ==
    /\ typed # None /\ typed.kind = "Undo" /\ typed.cur /\ typed.cap # "Pre"
    /\ typed' = None
    /\ thinking' = FALSE
    /\ \E planOK \in BOOLEAN : card' = UndoCard(planOK)
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, vtool, vundo, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* [F6] failed, timed out, cancelled or stale: finally ends the capture; Thinking cleared only if still current.
\* FAIR when stale (the lease token is cancelled, so every await ends) and on path Plain (with TypedReply /
\* TypedUndoPlan); NOT FAIR for a current path Voice request (the send may hang).
TypedEnd ==
    /\ typed # None
    /\ typed' = None
    /\ thinking' = IF typed.cur THEN FALSE ELSE thinking
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* The fair group for a typed request's end: every outcome, when the code bounds it locally.
TypedFinishBounded ==
    /\ alive
    /\ typed # None
    /\ ~typed.cur \/ typed.path = "Plain"
    /\ TypedReply \/ TypedUndoPlan \/ TypedEnd

-----------------------------------------------------------------------------
(* Voice session events from the provider (MainWindow.xaml.cs:2245-2325). PROVIDER: none of them is fair. *)

VoiceOpen == voice # "Off"

\* A session that carried microphone audio. Speech started/stopped and a spoken "undo" (an input audio
\* transcription, MainWindow.xaml.cs:2257-2280) can only come from one; a typed hand-off never opens the mic.
AudioSession == voice \in {"Ptt", "Other"}

\* PROVIDER. [F7] speech started: Thinking off.
VSpeechStarted ==
    /\ AudioSession
    /\ thinking' = FALSE
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* PROVIDER. [F7] speech stopped / committed: Thinking on.
VSpeechStopped ==
    /\ AudioSession
    /\ thinking' = TRUE
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* PROVIDER. [F8] audio delta: Thinking off, Responding on, moment refreshed (SetFace at MainWindow.xaml.cs:2286).
VAudioDelta ==
    /\ VoiceOpen
    /\ thinking' = FALSE
    /\ responding' = TRUE
    /\ UNCHANGED <<hovered, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* PROVIDER. [F9] response.done: Thinking = tools in flight > 0; Responding keeps its (playback) moment.
VResponseDone ==
    /\ VoiceOpen
    /\ thinking' = (vtool # None /\ vtool.cur)
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* PROVIDER. [F10] disconnect / error: AbortHoldToTalk (holds cleared, NO Cancel command), StopListening [G3],
\* a running focus session is paused [Fo3] [F19].
VDrop ==
    /\ VoiceOpen
    /\ StopListeningV
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ focus' = IF focus = "Running" THEN "Paused" ELSE focus
    /\ UNCHANGED focusEnds
    /\ Fin("Other")

\* PROVIDER. [F10b] go_to_sleep (MainWindow.Voice.cs:124-137): AbortHoldToTalk + StopListening; focus untouched.
\* Also stands for FailVoiceSession (mic or realtime failure, MainWindow.Talk.cs:405-411), same effect.
VGoToSleep ==
    /\ VoiceOpen
    /\ StopListeningV
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ UNCHANGED focusV
    /\ Fin("Other")

\* PROVIDER. [F11] voice tool call (screen_help, MainWindow.xaml.cs:2369-2371): Thinking on first, then capture.
VToolCall ==
    /\ VoiceOpen /\ vtool = None
    /\ thinking' = TRUE
    /\ vtool' = [cur |-> TRUE, cap |-> "Pre", st |-> "Work"]
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* FAIR (local screenshot). [F14] voice tool capture starts (or no frame: the operation ends at once, :2375-2378).
VToolCapture ==
    /\ CapPre(vtool) /\ vtool.cur
    /\ Fix # "F5" \/ phase # "Hidden"
    /\ vtool' = [vtool EXCEPT !.cap = "On"]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

VToolNoFrame ==
    /\ CapPre(vtool) /\ vtool.cur
    /\ vtool' = [vtool EXCEPT !.cap = "Off"]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* FAIR together with VToolFail (RespondAsync: 45 s HttpClient timeout). [F11] [A1] the answer goes through
\* RenderReply (Thinking off, Responding on, maybe a card, :2410). With a card the call id is parked and finally
\* runs at once (:2411-2414); otherwise CompleteToolAsync is still to be sent (st = "Send", capture still open).
VToolAnswer ==
    /\ vtool # None /\ vtool.cur /\ vtool.st = "Work" /\ vtool.cap # "Pre"
    /\ thinking' = FALSE
    /\ responding' = TRUE
    /\ \E propose \in BOOLEAN, t \in Targets :
          /\ card' = ProposeCard(propose, t)
          /\ vtool' = IF propose THEN None ELSE [vtool EXCEPT !.st = "Send"]
    /\ UNCHANGED <<hovered, nudge, happy, micV, typed, vundo, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* FAIR together with VToolAnswer. Timeout, HTTP failure, window changed (:2389-2396, :2420-2437): no RenderReply,
\* so Thinking STAYS on; the failure text still has to be sent with CompleteToolAsync.
VToolFail ==
    /\ vtool # None /\ vtool.cur /\ vtool.st = "Work" /\ vtool.cap # "Pre"
    /\ vtool' = [vtool EXCEPT !.st = "Send"]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* finally (:2438-2445): the capture ends, the tool leaves the in-flight count; Thinking untouched.
\* NOT FAIR while current (CompleteToolAsync has no timeout of its own, RealtimeClient.cs:412-419; only the
\* keep-alive bounds it, left out on purpose: see the fairness rule); FAIR once stale (the lease
\* token cancels the send, and a stale tool returns before sending).
VToolRelease ==
    /\ vtool # None /\ (vtool.st = "Send" \/ ~vtool.cur)
    /\ vtool' = None
    /\ UNCHANGED <<hovered, flagV, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* PROVIDER (an input transcription). [U1] spoken "undo" (MainWindow.Edits.cs:129-182): the FOURTH capture source.
\* The capture opens at once and Thinking is NOT set first. With Fix F5 it waits for the notch (cap "Pre").
\* Order: in the code the InterruptAsync wait comes BEFORE the capture starts (the operation from CreateOperation,
\* MainWindow.Edits.cs:139, shows nothing until activity.Start inside CaptureForRequestAsync, MainWindow.Edits.cs:157); the model opens
\* the capture first and waits in VUndoDone. That only adds behaviours where the notch is pinned longer.
VUndoStart ==
    /\ AudioSession /\ vundo = None
    /\ vundo' = [cur |-> TRUE, cap |-> IF Fix = "F5" THEN "Pre" ELSE "On"]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vtool, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* FAIR, Fix F5 only: the grab starts once the notch is out.
VUndoCapture ==
    /\ CapPre(vundo) /\ vundo.cur /\ phase # "Hidden"
    /\ vundo' = [vundo EXCEPT !.cap = "On"]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vtool, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [U1]-[U4] [F11] spoken undo finishes: capture ends; if current, the plan becomes a card and finally sets
\* Thinking = tools in flight > 0 (SetFace at MainWindow.Edits.cs:181). NOT FAIR while current: it first awaits
\* realtime.InterruptAsync with no timeout of its own (MainWindow.Edits.cs:144-150, RealtimeClient.cs:489-490; only the
\* keep-alive bounds it, left out on purpose: see the fairness rule); FAIR once stale.
VUndoDone ==
    /\ vundo # None /\ (vundo.cap = "On" \/ ~vundo.cur)
    /\ vundo' = None
    /\ IF vundo.cur
          THEN /\ thinking' = (vtool # None /\ vtool.cur)
               /\ \E planOK \in BOOLEAN : card' = UndoCard(planOK)
          ELSE UNCHANGED <<thinking, card>>
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, typed, vtool, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Microphone: Talk button (doc finding F8) and hold-to-talk (doc 3.3) *)

\* USER. [F8 finding] [F12] [F13] Talk button (MainWindow.xaml.cs:858-913). If listening: StopListening. Else
\* BeginRequest, connect, then open the Talk-button audio and light the indicator (connect step collapsed;
\* a failed connect is VGoToSleep/VDrop after this step).
TalkButton ==
    /\ IF micInd
          THEN /\ StopListeningV
               /\ UNCHANGED <<chord, eyes, focus, focusEnds>>
          ELSE /\ NewRequest(FALSE, Stale(typed), "Other", "Button", TRUE)
               /\ UNCHANGED <<chord, eyes>>
    /\ Fin("Input")

\* USER. [H1] [H2] [F12] [F24] TalkDown (chord) / eyes held >= 200 ms. A second holder only joins (no command).
\* First holder: StartListening -> StartPushToTalk (MainWindow.Talk.cs:225-276): reuse an open push-to-talk
\* session, else BeginRequest; the mic is opened and the indicator lit before the first await (one step).
TalkDown(s) ==
    /\ IF s = "chord" THEN ~chord ELSE ~eyes
    /\ chord' = (chord \/ s = "chord")
    /\ eyes' = (eyes \/ s = "eyes")
    /\ IF chord \/ eyes
          THEN UNCHANGED <<flagV, micDev, micInd, voice, workV, applyV>>
          ELSE IF voice = "Ptt"
                  THEN (/\ micDev' = "Hold"
                        /\ micInd' = TRUE
                        /\ UNCHANGED <<flagV, voice, workV, applyV>>)
                  ELSE NewRequest(FALSE, Stale(typed), "Ptt", "Hold", TRUE)
    /\ UNCHANGED <<hovered, focusV>>
    /\ Fin("Input")

\* USER. [H3] one holder released, the other still held: nothing. [H4] [H5] [H7] last release: StopAndSend or
\* Cancel; both run StopPushToTalkMic first (ReleaseMicDev / ReleaseMicInd above). The commit itself is a provider
\* event (VSpeechStopped).
TalkUp(s) ==
    /\ IF s = "chord" THEN chord ELSE eyes
    /\ chord' = (chord /\ s # "chord")
    /\ eyes' = (eyes /\ s # "eyes")
    /\ IF chord' \/ eyes'
          THEN UNCHANGED <<micDev, micInd>>
          ELSE /\ micDev' = ReleaseMicDev
               /\ micInd' = ReleaseMicInd
    /\ UNCHANGED <<hovered, flagV, voice, workV, applyV, focusV>>
    /\ Fin("Other")

\* FAIR (250 ms ticker, MainWindow.Talk.cs:57-58, :196-208; HoldToTalk.cs:163-174). [H8] held longer than 120 s:
\* StopAndSend; holds cleared, later releases ignored.
HoldTimeout ==
    /\ alive /\ (chord \/ eyes)
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ micDev' = ReleaseMicDev
    /\ micInd' = ReleaseMicInd
    /\ UNCHANGED <<hovered, flagV, voice, workV, applyV, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Stop and quit (StopAll, MainWindow.xaml.cs:270-323) *)

\* USER (or the OS / an internal guard). [F21] [G2] [H6] [A3] [Fo3] Stop from any source: holds cancelled, mic off,
\* voice session dropped, all work stale, pending card cleared, a running focus session PAUSED, face flags replaced
\* by FocusPaused only. Capture operations are NOT ended here: each ends when its own finally runs (doc INV-STOP-1
\* row, part b). With Fix F4: captures end at once and a live focus session ends (one end event, no Happy face).
Stop ==
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ micDev' = IF Plant = "StopKeepsMic" THEN micDev ELSE "Off"
    /\ micInd' = FALSE /\ voice' = "Off"
    /\ thinking' = FALSE /\ responding' = FALSE /\ nudge' = FALSE /\ happy' = FALSE
    /\ typed' = CapEnd(Stale(typed)) /\ vtool' = CapEnd(Stale(vtool)) /\ vundo' = CapEnd(Stale(vundo))
    /\ fcheck' = IF Fix = "F4" THEN "None" ELSE fcheck
    /\ card' = CardNewGen /\ ticket' = TicketNewGen
    /\ IF Fix = "F4" /\ focus \in Live
          THEN focus' = "Off" /\ focusEnds' = focusEnds + 1
          ELSE focus' = (IF focus = "Running" THEN "Paused" ELSE focus) /\ UNCHANGED focusEnds
    /\ UNCHANGED <<hovered, fileJ, fieldJ>>
    /\ Fin("Stop")

\* USER. [F21] quit = StopAll, then the process ends. [Fo3] [F9 finding] an open focus session is only paused,
\* never ended: no end event (last = "QuietEnd"; with Fix F9 its event is raised: "Replaced"). After Quit nothing
\* is enabled (TLC runs with CHECK_DEADLOCK FALSE).
Quit ==
    /\ alive' = FALSE
    /\ hovered' = FALSE
    /\ phase' = "Hidden"
    /\ thinking' = FALSE /\ responding' = FALSE /\ nudge' = FALSE /\ happy' = FALSE
    /\ micDev' = "Off" /\ micInd' = FALSE /\ chord' = FALSE /\ eyes' = FALSE /\ voice' = "Off"
    /\ typed' = None /\ vtool' = None /\ vundo' = None /\ fcheck' = "None"
    /\ card' = None /\ ticket' = None
    /\ focus' = IF focus = "Running" THEN "Paused" ELSE focus
    /\ UNCHANGED <<fileJ, fieldJ, focusEnds>>
    /\ effect' = None
    /\ last' = CloseLabel

-----------------------------------------------------------------------------
(* Approval card and undo (doc 3.6, 3.7). Approve covers the plan's Approve and ApproveUndo. *)

\* USER. [A5] Approve, still current: the ticket is the digest of THIS proposal (ActionPolicy.cs:62-82).
\* [A4] Approve but expired / denied by policy / target window changed: card closed.
Approve ==
    /\ card # None /\ card.st = "Pending"
    /\ \/ /\ card.cur
          /\ card' = [card EXCEPT !.st = "Executing"]
          /\ ticket' = card.p
       \/ /\ card' = None
          /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* USER. [A2] Reject (Cancel button, Escape).
Reject ==
    /\ card # None /\ card.st = "Pending"
    /\ card' = None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* FAIR together with ExecEnd (local: ExecuteAsync with the lease token, then a read-back of at most five 120 ms
\* polls, MainWindow.xaml.cs:1230-1231, MainWindow.Edits.cs:358-364).
\* [A6] [G5] [U5] [U6] the effect commits inside TryCommit under the coordinator's lock (DesktopTools.cs:130, 165,
\* 223, 287, 332, 384, 467; RequestCoordinator.cs:46-62), so Stop cannot interleave: only for the current
\* generation and only while IsStillAuthorized accepts the ticket of this exact proposal (DesktopTools.cs:968).
\* ok = the read-back verified the step. Then [A7] more steps queued -> next card (needs a new Approve), or [A8] done.
\* STRUCTURAL: INV_APPLY_1 and INV_APPLY_3 hold because of this guard; the real proof is PR 2.7's conformance test.
Apply ==
    /\ card # None /\ card.st = "Executing" /\ ticket = card.p
    /\ card.cur \/ Plant = "CommitIgnoresGeneration"
    /\ \E ok \in BOOLEAN, more \in BOOLEAN, t \in Targets :
          /\ fileJ' = FileAfter(card.p, ok)
          /\ fieldJ' = FieldAfter(card.p, ok)
          /\ effect' = [p |-> card.p, ticket |-> ticket, cur |-> card.cur,
                        journal |-> JournalOf(card.p.target), ok |-> ok]
          /\ card' = IF ok /\ more /\ card.p.kind = "Edit"
                     THEN [p |-> [id |-> OtherId(card.p.id), kind |-> "Edit", target |-> t],
                           st |-> "Pending", cur |-> TRUE]
                     ELSE None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, focusV>>
    /\ last' = "Other"
    /\ UNCHANGED alive
    /\ phase' = Retime(phase, Want')

\* FAIR together with Apply. [A8] execution failed, refused (stale ticket) or stopped: card closed (finally).
ExecEnd ==
    /\ card # None /\ card.st = "Executing"
    /\ card' = None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* RED PROOF ONLY (plan 3.3 row 2.2), Plant "ApplyWithoutApprove": a PENDING proposal is applied with no Approve.
BuggyApply ==
    /\ Plant = "ApplyWithoutApprove"
    /\ card # None /\ card.st = "Pending"
    /\ fileJ' = FileAfter(card.p, TRUE)
    /\ fieldJ' = FieldAfter(card.p, TRUE)
    /\ effect' = [p |-> card.p, ticket |-> ticket, cur |-> card.cur,
                  journal |-> JournalOf(card.p.target), ok |-> TRUE]
    /\ card' = None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, focusV>>
    /\ last' = "Other"
    /\ UNCHANGED alive
    /\ phase' = Retime(phase, Want')

-----------------------------------------------------------------------------
(* Focus session (doc 3.4). Button and voice versions (focus_start/pause/resume/end) make the same moves. *)

\* USER. [Fo1] [F20] start (Happy off); [Fo2] a live session is replaced with EndQuietly: NO end event
\* (with Fix F9 the old session's event is raised first).
FocusStart ==
    /\ focus' = "Running"
    /\ focusEnds' = 0
    /\ happy' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV>>
    /\ Fin(CloseLabel)

\* USER (also the OS: sleep gap, tick error). [Fo3] [F19] Pause (Stop, disconnect and the 5 min break are in Stop,
\* VDrop and NudgeBreak).
FocusPause ==
    /\ focus = "Running"
    /\ focus' = "Paused"
    /\ UNCHANGED <<hovered, flagV, micV, workV, applyV, focusEnds>>
    /\ Fin("Other")

\* USER. [Fo4] [F20] Resume (only by button or voice).
FocusResume ==
    /\ focus = "Paused"
    /\ focus' = "Running"
    /\ UNCHANGED <<hovered, flagV, micV, workV, applyV, focusEnds>>
    /\ Fin("Other")

\* FAIR (1 s focus timer, FocusTimer_OnTick MainWindow.xaml.cs:1443-1465). [Fo5] [Fo7] [F18] the planned time is
\* reached: one end event, Happy moment.
FocusComplete ==
    /\ alive /\ focus = "Running"
    /\ focus' = "Off"
    /\ focusEnds' = focusEnds + 1
    /\ happy' = TRUE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV>>
    /\ Fin("Other")

\* USER. [Fo6] [Fo7] [F18] End (button or voice): one end event, Happy moment.
FocusEnd ==
    /\ focus \in Live
    /\ focus' = "Off"
    /\ focusEnds' = focusEnds + 1
    /\ happy' = TRUE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV>>
    /\ Fin("Other")

\* RED PROOF ONLY, Plant "EndNotGuarded": an End that races a completion raises a second event (no _endRaised).
FocusEndAgain ==
    /\ Plant = "EndNotGuarded"
    /\ focus = "Off" /\ focusEnds = 1
    /\ focusEnds' = 2
    /\ UNCHANGED <<hovered, flagV, micV, workV, applyV, focus>>
    /\ Fin("Other")

\* FAIR (capture timer, FocusCaptureTimer_OnTick MainWindow.xaml.cs:1502-1505). [Fo8] [F14] a focus check is due:
\* its capture STARTS the reveal (nothing pinned the notch before). With Fix F5 it pins first ("Pre").
FocusCheckStart ==
    /\ alive /\ focus = "Running" /\ fcheck = "None"
    /\ fcheck' = IF Fix = "F5" THEN "Pre" ELSE "On"
    /\ UNCHANGED <<hovered, flagV, micV, typed, vtool, vundo, applyV, focusV>>
    /\ Fin("Other")

\* FAIR, Fix F5 only: the grab starts once the notch is out.
FocusCheckCapture ==
    /\ alive /\ fcheck = "Pre" /\ phase # "Hidden"
    /\ fcheck' = "On"
    /\ UNCHANGED <<hovered, flagV, micV, typed, vtool, vundo, applyV, focusV>>
    /\ Fin("Other")

\* FAIR (local capture, then ClassifyAsync over the 45 s HttpClient, MainWindow.xaml.cs:1511-1587, :1629).
\* [Fo8] [F15] [F16] the check ends: capture off; "drifting" while still running -> Nudge moment.
FocusCheckDone ==
    /\ alive /\ fcheck = "On"
    /\ fcheck' = "None"
    /\ \E drift \in BOOLEAN : nudge' = (nudge \/ (drift /\ focus = "Running"))
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, typed, vtool, vundo, applyV, focusV>>
    /\ Fin("Other")

\* USER. [F17] nudge "Back to it".
NudgeBack ==
    /\ nudge
    /\ nudge' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* USER. [F17] [Fo3] nudge "5 min break": the nudge closes and a running session pauses.
NudgeBreak ==
    /\ nudge
    /\ nudge' = FALSE
    /\ focus' = IF focus = "Running" THEN "Paused" ELSE focus
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, workV, applyV, focusEnds>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* F22/F23 (hotkey / click opens the panel) change no notch flag and are not modelled; the one exception,
   Stop when the selected display is gone, is a Stop. Pointer and ring (doc 3.8) are out of scope. *)

Next ==
    /\ alive
    /\ \/ PointerEnter \/ PointerLeave \/ Tick
       \/ ExpireResponding \/ ExpireNudge \/ ExpireHappy \/ ExpireThinking
       \/ \E k \in {"Ask", "Undo"}, pth \in {"Plain", "Voice"} : Send(k, pth)
       \/ TypedCapture \/ TypedNoFrame \/ TypedReply \/ TypedHandOff \/ TypedUndoPlan \/ TypedEnd
       \/ VSpeechStarted \/ VSpeechStopped \/ VAudioDelta \/ VResponseDone \/ VDrop \/ VGoToSleep
       \/ VToolCall \/ VToolCapture \/ VToolNoFrame \/ VToolAnswer \/ VToolFail \/ VToolRelease
       \/ VUndoStart \/ VUndoCapture \/ VUndoDone
       \/ TalkButton
       \/ \E s \in {"chord", "eyes"} : TalkDown(s) \/ TalkUp(s)
       \/ HoldTimeout
       \/ Stop \/ Quit
       \/ Approve \/ Reject \/ Apply \/ ExecEnd \/ BuggyApply
       \/ FocusStart \/ FocusPause \/ FocusResume \/ FocusComplete \/ FocusEnd \/ FocusEndAgain
       \/ FocusCheckStart \/ FocusCheckCapture \/ FocusCheckDone \/ NudgeBack \/ NudgeBreak
    /\ Active' <= MaxActive

\* THE FAIRNESS RULE (top of file; table in specs/README.md). One WF per step, on all of its outcomes, and only
\* where the code bounds that step locally. Everything not listed here is USER or PROVIDER: never fair.
\* The WF terms do NOT include Next's model bound Active' <= MaxActive. Of the fair steps only FocusCheckStart can
\* add a busy lane, so the only effect is that behaviours where a due focus check stays blocked by the bound forever
\* are not counted; the bound exists only in the model (the app has no such limit), and the Python copy of the
\* model, which counts those behaviours, gets the same result for every liveness check (mock evidence).
Fairness ==
    /\ WF_vars(Tick)
    /\ WF_vars(ExpireResponding)
    /\ WF_vars(ExpireNudge)
    /\ WF_vars(ExpireHappy)
    /\ WF_vars(ExpireThinking)
    /\ WF_vars(HoldTimeout)
    /\ WF_vars(alive /\ (TypedCapture \/ TypedNoFrame))
    /\ WF_vars(TypedFinishBounded)
    /\ WF_vars(alive /\ (VToolCapture \/ VToolNoFrame))
    /\ WF_vars(alive /\ (VToolAnswer \/ VToolFail))
    /\ WF_vars(alive /\ vtool # None /\ ~vtool.cur /\ VToolRelease)
    /\ WF_vars(alive /\ VUndoCapture)
    /\ WF_vars(alive /\ vundo # None /\ ~vundo.cur /\ VUndoDone)
    /\ WF_vars(alive /\ (Apply \/ ExecEnd))
    /\ WF_vars(FocusComplete)
    /\ WF_vars(FocusCheckStart)
    /\ WF_vars(FocusCheckCapture)
    /\ WF_vars(FocusCheckDone)

Spec == Init /\ [][Next]_vars /\ Fairness

-----------------------------------------------------------------------------
(* Checks that HOLD today (NotchLifecycle.cfg). Names use _ for the plan's - (TLA+ identifiers).
   "STRUCTURAL" = holds by the model's shape (a guard the model copies from the code); the real proof that the
   code keeps that shape is PR 2.7's conformance test. Each check has a red proof and a witness (README). *)

TypeOK ==
    /\ alive \in BOOLEAN /\ hovered \in BOOLEAN /\ phase \in Phases
    /\ thinking \in BOOLEAN /\ responding \in BOOLEAN /\ nudge \in BOOLEAN /\ happy \in BOOLEAN
    /\ micDev \in {"Off", "Hold", "Button"} /\ micInd \in BOOLEAN
    /\ chord \in BOOLEAN /\ eyes \in BOOLEAN /\ voice \in {"Off", "Ptt", "Other", "Text"}
    /\ typed \in {None} \cup TypedReqs
    /\ vtool \in {None} \cup ToolReqs
    /\ vundo \in {None} \cup UndoReqs
    /\ fcheck \in {"None", "Pre", "On"}
    /\ card \in {None} \cup Cards
    /\ ticket \in {None} \cup Proposals
    /\ fileJ \in {"none", "edit", "undo"} /\ fieldJ \in {"none", "edit"}
    /\ focus \in {"Off", "Running", "Paused"} /\ focusEnds \in 0..2
    /\ effect \in {None} \cup Effects
    /\ last \in {"Other", "Input", "Stop", "QuietEnd", "Replaced"}

\* INV-PRIV-1 (doc 4, today's form): the mic indicator or a capture is on => the notch is out (Revealing
\* counts: the 240 ms slide has started) and shows Listening or Capture. The Face half is STRUCTURAL (Face copies
\* the resolver's priority, NotchFace.cs:38-53); the phase half depends on the timeline and is what the red proof
\* PinIgnoresCapture breaks.
INV_PRIV_1 == (micInd \/ CaptureOn) => (phase # "Hidden" /\ Face \in {"Capture", "Listening"})

\* INV-APPLY-1, STRUCTURAL: every applied effect carries the ticket of that exact proposal.
INV_APPLY_1 == effect # None => effect.ticket = effect.p

\* INV-APPLY-2, field part: a field undo needs a recorded edit, and after a VERIFIED undo the journal is empty
\* (an unverified one keeps it, so a later undo can be planned again against the same entry).
INV_APPLY_2_FIELD ==
    (effect # None /\ effect.p.kind = "Undo" /\ effect.p.target = "Field")
        => (effect.journal = "edit" /\ (effect.ok => fieldJ = "none"))

\* INV-APPLY-3, STRUCTURAL: nothing from an older request generation is ever applied.
INV_APPLY_3 == effect # None => effect.cur

\* INV-TALK-1 (doc 4, today's form): the push-to-talk mic is open only while a hold is down, and (STRUCTURAL)
\* right after Stop the mic and its indicator are off. The release part is INV_TALK_1_RELEASE.
INV_TALK_1 ==
    /\ micDev = "Hold" => (chord \/ eyes)
    /\ last = "Stop" => (micDev = "Off" /\ ~micInd)

\* INV-TALK-1 on the last release (TalkUp, eyes released, 120 s timeout, abort): the hold's own mic and
\* the indicator are off in the very next state.
INV_TALK_1_RELEASE ==
    [][((chord \/ eyes) /\ ~(chord' \/ eyes')) => (micDev' # "Hold" /\ ~micInd')]_vars

\* INV-FOCUS-1, today's form, STRUCTURAL: at most one end event per session (_endRaised,
\* FocusSessionController.cs:161-168; with no fix on, the model only lets End and Complete leave a live session; with
\* fix F4 on, Stop also ends one, and the count still stays at most one).
INV_FOCUS_AT_MOST_ONE == focusEnds <= 1

\* LIVE-1, Responding part: never stuck on Responding (moment timer).
LIVE_1_RESPONDING == responding ~> ~responding

\* LIVE-2, today's form: after the pointer leaves and nothing pins the notch, it reaches Hidden unless the
\* pointer comes back, something pins it again (new input, a provider event, or a fair internal step such as a
\* focus check), or Boo quits. Provider events are never fair; they only count when they happen.
LIVE_2 == (alive /\ ~hovered /\ ~Pinned) ~> (~alive \/ hovered \/ Pinned \/ phase = "Hidden")

-----------------------------------------------------------------------------
(* The plan's checks AS WRITTEN that today's code breaks (NotchLifecycle_plan_gaps.cfg). Each must FAIL with
   Fix = "none" and PASS with its own Fix, until Vikas decides: change the behaviour, or reword the invariant. *)

\* INV-STOP-1 (doc finding F4, Fix F4): right after Stop the notch is quiet with mic and capture off.
\* Breaks: a running focus session is paused and Paused pins the notch; captures outlast Stop.
INV_STOP_1 == last = "Stop" => Quiet

\* INV-PRIV-2 (doc finding F5, Fix F5): no capture starts while the notch is Hidden (no capture is ever approved).
\* Breaks: a focus check (and a spoken undo) starts its capture from Hidden.
INV_PRIV_2 == [][(~CaptureOn /\ CaptureOn') => phase # "Hidden"]_vars

\* INV-APPLY-2, file part (doc finding F6, Fix F6): undo only once. Breaks: an approved file undo is journaled as a
\* new entry, so a second undo reverses the undo.
INV_APPLY_2_FILE ==
    (effect # None /\ effect.p.kind = "Undo" /\ effect.p.target = "File") => effect.journal = "edit"

\* INV-FOCUS-1 as written (doc finding F9, Fix F9): exactly one end event per session. Breaks: a session replaced
\* by a new Start, or open at quit, closes with none. With Fix F9 it passes STRUCTURALLY (the fix relabels that
\* close as "Replaced"); W_FOCUS_1_EXACT shows the close still happens.
INV_FOCUS_1_EXACT == last # "QuietEnd"

\* LIVE-1, Thinking part (doc finding F7, Fix F7): Thinking always ends, unless new user input (Send, Talk button,
\* a new hold: last = "Input") arrives first or Boo quits. Breaks: no local timeout; a silent provider after a voice
\* turn, a typed question handed to voice, or a failed voice tool leaves Thinking on forever.
LIVE_1_THINKING == thinking ~> (~thinking \/ ~alive \/ last = "Input")

\* INV-TALK-1 as written (doc finding F10, Fix F10): after the last release, the microphone DEVICE is off.
INV_TALK_1_DEVICE ==
    [][((chord \/ eyes) /\ ~(chord' \/ eyes')) => micDev' = "Off"]_vars

\* INV-PRIV-1 as written, for the microphone DEVICE (doc finding F10, Fix F10): the mic is open => the notch is
\* visible. Breaks: hold Ctrl+Alt+Space, press Talk twice (stop, start), release the chord: StopPushToTalkMic
\* turns the indicator off but leaves the Talk-button audio running (MainWindow.Talk.cs:383-398).
INV_PRIV_1_DEVICE == (micDev # "Off" \/ CaptureOn) => phase # "Hidden"

-----------------------------------------------------------------------------
(* Reachability witnesses. Each one says "the trigger of a check never happens"; run_tlc.sh requires TLC to
   find it FALSE (a trace that reaches the trigger), so no check passes only because its trigger is unreachable.
   Main checks are witnessed with Fix = "none"; each gap's witness runs with that gap's Fix on. *)

W_PRIV_1_MIC        == ~micInd
W_PRIV_1_CAPTURE    == ~CaptureOn
W_APPLY_1           == effect = None
W_APPLY_2_FIELD     == ~(effect # None /\ effect.p.kind = "Undo" /\ effect.p.target = "Field" /\ effect.ok)
W_APPLY_3           == ~(card # None /\ card.st = "Executing" /\ ~card.cur)
W_TALK_1_HOLD       == micDev # "Hold"
W_TALK_1_STOP       == [][~((micDev # "Off" \/ micInd) /\ last' = "Stop")]_vars
W_TALK_1_RELEASE    == [][~((chord \/ eyes) /\ ~(chord' \/ eyes'))]_vars
W_FOCUS_END         == focusEnds = 0
W_LIVE_1_RESPONDING == ~responding
W_LIVE_2            == ~(alive /\ ~hovered /\ ~Pinned /\ phase # "Hidden")

W_STOP_1            == [][~((focus \in Live \/ CaptureOn) /\ last' = "Stop")]_vars
W_PRIV_2            == [][~(~CaptureOn /\ CaptureOn')]_vars
W_APPLY_2_FILE      == ~(effect # None /\ effect.p.kind = "Undo" /\ effect.p.target = "File" /\ effect.ok)
W_FOCUS_1_EXACT     == last \notin {"QuietEnd", "Replaced"}
W_THINKING          == ~thinking
W_DEVICE            == [][~((chord \/ eyes) /\ ~(chord' \/ eyes') /\ micDev = "Button")]_vars

=============================================================================
