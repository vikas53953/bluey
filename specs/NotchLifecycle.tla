--------------------------- MODULE NotchLifecycle ---------------------------
(***************************************************************************)
(* Boo's notch lifecycle AS THE CODE BEHAVES TODAY.                        *)
(*                                                                         *)
(* Source of truth: docs/architecture/notch-lifecycle.md (agent-trust plan *)
(* PR 2.1). Every action below names the rows of that doc it models, for   *)
(* example [R1] (reveal timeline), [F3] (face flags), [H4] (hold-to-talk), *)
(* [Fo2] (focus), [G1] (request generations), [A5] (approval card),        *)
(* [U6] (undo). Findings F1-F9 of the doc are the "doc findings" below.    *)
(* If the code changes, the doc row, this action and (from PR 2.4) the C#  *)
(* reducer change together in one PR.                                      *)
(*                                                                         *)
(* Abstractions (each one is listed again in specs/README.md):             *)
(*  - Time is not a number. Timers and the render loop are actions (Tick   *)
(*    and the Expire actions) with weak fairness; the user and the AI      *)
(*    provider are actions WITHOUT fairness (they may never act).          *)
(*  - Request generations are a "still current" bit (cur) on each piece of *)
(*    in-flight work instead of a counter. This is exact: generations only *)
(*    grow and every check in the code is "equal to the current one", so a *)
(*    thing stops being current at the first BeginRequest / Stop / voice   *)
(*    session end after it was created, and never becomes current again.   *)
(*  - At most one typed request, one voice tool call, one spoken undo and  *)
(*    one focus check are in flight at a time. And at most MaxActive       *)
(*    "lanes" are busy at once (see Active): behaviours where more lanes   *)
(*    overlap are not explored. With MaxActive = 2 every PAIR of features  *)
(*    interacts; 3 is about 8x more states (see specs/README.md).          *)
(*  - "mic" has two parts: micDev = who has the microphone DEVICE open,    *)
(*    micInd = the notch's microphoneActive indicator (what the face uses, *)
(*    NotchWindow.xaml.cs:355-359). They differ in one path, see           *)
(*    TalkUp and INV_PRIV_1_DEVICE.                                        *)
(*  - Ghost variables, not app state: effect (what the last step applied), *)
(*    last (what kind of step produced this state). Both are reset by      *)
(*    every step, so they cost few extra states.                           *)
(***************************************************************************)
EXTENDS Naturals

CONSTANTS
    None,                      \* model value: "nothing here"
    MaxActive,                 \* how many lanes may be busy at once (see Active)
    PlantApplyWithoutApprove   \* TRUE only in the red-proof cfg

ASSUME MaxActive \in 1..8
ASSUME PlantApplyWithoutApprove \in BOOLEAN

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
    voice,       \* realtime voice session: "Off" | "Ptt" (push-to-talk) | "Other" (Talk button / typed hand-off)
    typed,       \* in-flight typed request, or None
    vtool,       \* in-flight voice tool call (screen_help), or None
    vundo,       \* in-flight spoken "undo", or None
    fcheck,      \* focus check capture in flight
    card,        \* approval card (proposal + Pending/Executing), or None
    ticket,      \* approval ticket = the approved proposal's digest, or None
    fileJ,       \* FileUndoJournal top entry: "none" | "edit" | "undo" (an entry written BY an undo)
    fieldJ,      \* UndoJournal: "none" | "edit"
    focus,       \* focus session: "Off" (none or Ended) | "Running" | "Paused"
    focusEnds,   \* end events raised for the current focus session (_endRaised)
    effect,      \* ghost: what the last step applied, or None
    last         \* ghost: "Other" | "Stop" | "QuietEnd" (a live focus session closed with no end event)

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

TypedReqs == [kind : {"Ask", "Undo"}, cur : BOOLEAN, cap : {"Pre", "On", "Off"}]
ToolReqs  == [cur : BOOLEAN, cap : {"Pre", "On"}]
UndoReqs  == [cur : BOOLEAN]
Cards     == [p : Proposals, st : {"Pending", "Executing"}, cur : BOOLEAN]
Effects   == [p : Proposals, ticket : Proposals \cup {None}, cur : BOOLEAN,
              journal : {"none", "edit", "undo"}]

-----------------------------------------------------------------------------
(* What the notch shows *)

\* Capture indicator: any capture operation open (CaptureActivityTracker). [F14] [F15]
CaptureOn ==
    \/ typed # None /\ typed.cap = "On"
    \/ vtool # None /\ vtool.cap = "On"
    \/ vundo # None
    \/ fcheck

\* "Pinned" = the face would be non-Hidden without the pointer (NotchWindow.xaml.cs:510-512). [R9]
\* The Paused face comes from FocusPaused, which every path keeps equal to (focus = "Paused") [F19] [F20].
\* UserHidden, FaceInputs.Listening and FaceInputs.CaptureActive are never set by the app (doc finding F2).
Pinned == CaptureOn \/ micInd \/ responding \/ thinking \/ nudge \/ happy \/ focus = "Paused"
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
    B(typed # None) + B(vtool # None) + B(vundo # None) + B(fcheck) + B(card # None)
  + B(voice # "Off") + B(chord \/ eyes) + B(focus \in Live)

-----------------------------------------------------------------------------
(* Helpers *)

\* A piece of in-flight work stops being current (generation bump). [G1] [G2] [G3] [G4]
Stale(x) == IF x = None THEN None ELSE [x EXCEPT !.cur = FALSE]

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

\* [U1] the file journal wins if it holds something, else the field journal; [U2] nothing -> no card;
\* [U3] [U4] a plan can be refused (file changed since, newer field change, ambiguous control): planOK = FALSE.
UndoCard(planOK) ==
    IF card = None /\ planOK
    THEN (IF fileJ # "none" THEN UndoCardFor("File")
          ELSE (IF fieldJ # "none" THEN UndoCardFor("Field") ELSE None))
    ELSE card

JournalOf(t) == IF t = "File" THEN fileJ ELSE fieldJ
\* [A6] an edit is journaled; [U6] an approved FILE undo is journaled as a new entry (written by an undo);
\* [U5] an approved FIELD undo clears the field journal.
FileAfter(p)  == IF p.target = "File"  THEN (IF p.kind = "Edit" THEN "edit" ELSE "undo") ELSE fileJ
FieldAfter(p) == IF p.target = "Field" THEN (IF p.kind = "Edit" THEN "edit" ELSE "none") ELSE fieldJ

\* Ends every step that applies nothing: ghosts reset, timeline reacts to the new flags.
\* Must be the LAST conjunct of an action (Want' needs every other primed variable).
Fin(l) ==
    /\ effect' = None
    /\ last' = l
    /\ UNCHANGED alive
    /\ phase' = Retime(phase, Want')

\* BeginRequest (MainWindow.xaml.cs:1962-1988) [G1], which first runs StopListening: mic off, voice session
\* dropped, Thinking/Responding cleared (:927-948), old work stale, pending card cleared [A3].
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

\* StopListening (MainWindow.xaml.cs:910-948): audio stopped, indicator off, Thinking/Responding cleared;
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

-----------------------------------------------------------------------------
Init ==
    /\ alive = TRUE
    /\ hovered = FALSE
    /\ phase = "Hidden"
    /\ thinking = FALSE /\ responding = FALSE /\ nudge = FALSE /\ happy = FALSE
    /\ micDev = "Off" /\ micInd = FALSE /\ chord = FALSE /\ eyes = FALSE /\ voice = "Off"
    /\ typed = None /\ vtool = None /\ vundo = None /\ fcheck = FALSE
    /\ card = None /\ ticket = None /\ fileJ = "none" /\ fieldJ = "none"
    /\ focus = "Off" /\ focusEnds = 0
    /\ effect = None
    /\ last = "Other"

-----------------------------------------------------------------------------
(* Pointer and reveal timeline (doc 3.1) *)

\* [R1] [R7] [R8] [F1] pointer enters the notch strip.
PointerEnter ==
    /\ ~hovered
    /\ hovered' = TRUE
    /\ UNCHANGED <<flagV, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [R3] [R10] [F2] pointer leaves.
PointerLeave ==
    /\ hovered
    /\ hovered' = FALSE
    /\ UNCHANGED <<flagV, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* Time passing on the reveal timeline, picked up by the render loop (NotchWindow.xaml.cs:639-674). FAIR.
\* [R2] Revealing -> Revealed (240 ms); [R4] left mid-reveal: the reveal ends, then the retract delay starts;
\* [R5] RetractPending -> Retracting (900 ms); [R6] Retracting -> Hidden (160 ms).
Tick ==
    /\ alive
    /\ phase \in {"Revealing", "RetractPending", "Retracting"}
    /\ phase # "RetractPending" \/ ~Want
    /\ phase' = (CASE phase = "Revealing"      -> (IF Want THEN "Revealed" ELSE "RetractPending")
                   [] phase = "RetractPending" -> "Retracting"
                   [] OTHER                    -> "Hidden")
    /\ effect' = None
    /\ last' = "Other"
    /\ UNCHANGED <<alive, hovered, flagV, micV, workV, applyV, focusV>>

-----------------------------------------------------------------------------
(* Moments: each timed flag is cleared by its own timer (MainWindow.xaml.cs:175-202). FAIR. *)

\* [F5] Responding moment expires (4 s, or the playback tail capped by FaceMoments).
ExpireResponding ==
    /\ alive /\ responding
    /\ responding' = FALSE
    /\ UNCHANGED <<hovered, thinking, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F16] nudge 6 s.
ExpireNudge ==
    /\ alive /\ nudge
    /\ nudge' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F18] happy 2.5 s.
ExpireHappy ==
    /\ alive /\ happy
    /\ happy' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Typed requests (Send, MainWindow.xaml.cs:728-850) *)

\* [F3] [G1] [U1] Send: StopListening if listening, BeginRequest, Thinking on, Responding off.
\* k = "Undo" when the text is classified as Undo (:760-764), else "Ask".
Send(k) ==
    /\ typed = None
    /\ NewRequest(TRUE, [kind |-> k, cur |-> TRUE, cap |-> "Pre"], "Off", "Off", FALSE)
    /\ UNCHANGED <<chord, eyes>>
    /\ Fin("Other")

\* [F14] the capture indicator goes on AFTER Thinking already pinned the notch (doc INV-PRIV-2 row).
TypedCapture ==
    /\ typed # None /\ typed.cur /\ typed.cap = "Pre"
    /\ typed' = [typed EXCEPT !.cap = "On"]
    /\ UNCHANGED <<hovered, flagV, micV, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* No frame taken: the capture operation ends at once (:752-755); the request goes on.
TypedNoFrame ==
    /\ typed # None /\ typed.cur /\ typed.cap = "Pre"
    /\ typed' = [typed EXCEPT !.cap = "Off"]
    /\ UNCHANGED <<hovered, flagV, micV, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [F4] [A1] plain-text reply (speech off or muted, or the voice connect failed): capture ends in finally
\* [F15], Thinking off, Responding on (4 s moment); an explicit task's actions become a card. Provider: NOT fair.
TypedReply ==
    /\ typed # None /\ typed.kind = "Ask" /\ typed.cur /\ typed.cap # "Pre"
    /\ typed' = None
    /\ thinking' = FALSE
    /\ responding' = TRUE
    /\ \E propose \in BOOLEAN, t \in Targets : card' = ProposeCard(propose, t)
    /\ UNCHANGED <<hovered, nudge, happy, micV, vtool, vundo, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* [F3] [F6] speech on (the default): the text is handed to a voice session; finally ends the capture but
\* leaves Thinking on (!handedToRealtime, :846). Only voice events, Stop or a new request clear it (doc F7).
TypedHandOff ==
    /\ typed # None /\ typed.kind = "Ask" /\ typed.cur /\ typed.cap # "Pre"
    /\ typed' = None
    /\ voice' = IF voice = "Off" THEN "Other" ELSE voice
    /\ UNCHANGED <<hovered, flagV, micDev, micInd, chord, eyes, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [U1]-[U4] typed "undo": the plan becomes an Undo card (or a message); finally clears Thinking.
TypedUndoPlan ==
    /\ typed # None /\ typed.kind = "Undo" /\ typed.cur /\ typed.cap # "Pre"
    /\ typed' = None
    /\ thinking' = FALSE
    /\ \E planOK \in BOOLEAN : card' = UndoCard(planOK)
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, vtool, vundo, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* [F6] failed, cancelled or stale: finally ends the capture; Thinking cleared only if still current.
TypedEnd ==
    /\ typed # None
    /\ typed' = None
    /\ thinking' = IF typed.cur THEN FALSE ELSE thinking
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, vtool, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Voice session events from the provider (MainWindow.xaml.cs:2240-2320). None of them is fair. *)

VoiceOpen == voice # "Off"

\* [F7] speech started: Thinking off.
VSpeechStarted ==
    /\ VoiceOpen
    /\ thinking' = FALSE
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F7] speech stopped / committed: Thinking on.
VSpeechStopped ==
    /\ VoiceOpen
    /\ thinking' = TRUE
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F8] audio delta: Thinking off, Responding on, moment refreshed (SetFace at MainWindow.xaml.cs:2281).
VAudioDelta ==
    /\ VoiceOpen
    /\ thinking' = FALSE
    /\ responding' = TRUE
    /\ UNCHANGED <<hovered, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F9] response.done: Thinking = tools in flight > 0; Responding keeps its (playback) moment.
VResponseDone ==
    /\ VoiceOpen
    /\ thinking' = (vtool # None /\ vtool.cur)
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F10] disconnect / error: AbortHoldToTalk (holds cleared, NO Cancel command), StopListening [G3],
\* a running focus session is paused [Fo3] [F19].
VDrop ==
    /\ VoiceOpen
    /\ StopListeningV
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ focus' = IF focus = "Running" THEN "Paused" ELSE focus
    /\ UNCHANGED focusEnds
    /\ Fin("Other")

\* [F10b] go_to_sleep (MainWindow.Voice.cs:124-137): AbortHoldToTalk + StopListening; focus untouched.
\* Also stands for FailVoiceSession (mic or realtime failure, MainWindow.Talk.cs:405-411), same effect.
VGoToSleep ==
    /\ VoiceOpen
    /\ StopListeningV
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ UNCHANGED focusV
    /\ Fin("Other")

\* [F11] voice tool call (screen_help): Thinking on first, the capture starts after (VToolCapture).
VToolCall ==
    /\ VoiceOpen /\ vtool = None
    /\ thinking' = TRUE
    /\ vtool' = [cur |-> TRUE, cap |-> "Pre"]
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [F14] voice tool capture starts.
VToolCapture ==
    /\ vtool # None /\ vtool.cur /\ vtool.cap = "Pre"
    /\ vtool' = [vtool EXCEPT !.cap = "On"]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vundo, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [F11] [F15] [A1] tool answer through RenderReply (Thinking off, Responding on, maybe a card), capture
\* ends; a stale tool only ends its capture.
VToolDone ==
    /\ vtool # None /\ (vtool.cap = "On" \/ ~vtool.cur)
    /\ vtool' = None
    /\ IF vtool.cur
          THEN /\ thinking' = FALSE
               /\ responding' = TRUE
               /\ \E propose \in BOOLEAN, t \in Targets : card' = ProposeCard(propose, t)
          ELSE UNCHANGED <<thinking, responding, card>>
    /\ UNCHANGED <<hovered, nudge, happy, micV, typed, vundo, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* [U1] spoken "undo" (MainWindow.Edits.cs:129-182): the FOURTH capture source. The capture operation opens
\* at once and Thinking is NOT set first.
VUndoStart ==
    /\ VoiceOpen /\ vundo = None
    /\ vundo' = [cur |-> TRUE]
    /\ UNCHANGED <<hovered, flagV, micV, typed, vtool, fcheck, applyV, focusV>>
    /\ Fin("Other")

\* [U1]-[U4] [F11] spoken undo finishes: capture ends; if current, the plan becomes a card and finally sets
\* Thinking = tools in flight > 0 (Edits.cs:178).
VUndoDone ==
    /\ vundo # None
    /\ vundo' = None
    /\ IF vundo.cur
          THEN /\ thinking' = (vtool # None /\ vtool.cur)
               /\ \E planOK \in BOOLEAN : card' = UndoCard(planOK)
          ELSE UNCHANGED <<thinking, card>>
    /\ UNCHANGED <<hovered, responding, nudge, happy, micV, typed, vtool, fcheck, ticket, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Microphone: Talk button (doc finding F8) and hold-to-talk (doc 3.3) *)

\* [F8 finding] [F12] [F13] Talk button (MainWindow.xaml.cs:853-908). If listening: StopListening. Else
\* BeginRequest, connect, then open the Talk-button audio and light the indicator (connect step collapsed;
\* a failed connect is VGoToSleep/VDrop after this step).
TalkButton ==
    /\ IF micInd
          THEN /\ StopListeningV
               /\ UNCHANGED <<chord, eyes, focus, focusEnds>>
          ELSE /\ NewRequest(FALSE, Stale(typed), "Other", "Button", TRUE)
               /\ UNCHANGED <<chord, eyes>>
    /\ Fin("Other")

\* [H1] [H2] [F12] [F24] TalkDown (chord) / eyes held >= 200 ms. A second holder only joins (no command).
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
    /\ Fin("Other")

\* [H3] one holder released, the other still held: nothing. [H4] [H5] [H7] last release: StopAndSend or
\* Cancel; both run StopPushToTalkMic first (MainWindow.Talk.cs:384-397), which ALWAYS turns the indicator
\* off but stops the device only if the current audio is the push-to-talk audio. If the Talk button opened
\* its own audio while the hold was still down, that audio keeps running with the indicator off
\* (INV_PRIV_1_DEVICE and INV_TALK_1_DEVICE; lifecycle doc finding F10). The commit itself is a provider event (VSpeechStopped).
TalkUp(s) ==
    /\ IF s = "chord" THEN chord ELSE eyes
    /\ chord' = (chord /\ s # "chord")
    /\ eyes' = (eyes /\ s # "eyes")
    /\ IF chord' \/ eyes'
          THEN UNCHANGED <<micDev, micInd>>
          ELSE /\ micDev' = IF micDev = "Hold" THEN "Off" ELSE micDev
               /\ micInd' = FALSE
    /\ UNCHANGED <<hovered, flagV, voice, workV, applyV, focusV>>
    /\ Fin("Other")

\* [H8] held longer than 120 s: StopAndSend; holds cleared, later releases ignored.
HoldTimeout ==
    /\ chord \/ eyes
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ micDev' = IF micDev = "Hold" THEN "Off" ELSE micDev
    /\ micInd' = FALSE
    /\ UNCHANGED <<hovered, flagV, voice, workV, applyV, focusV>>
    /\ Fin("Other")

-----------------------------------------------------------------------------
(* Stop and quit (StopAll, MainWindow.xaml.cs:265-318) *)

\* [F21] [G2] [H6] [A3] [Fo3] Stop from any source (hotkeys, tray, panel, right-click, lock, sleep, update
\* restart, internal). Holds cancelled, mic off, voice session dropped, all work stale, pending card cleared,
\* a running focus session PAUSED, face flags replaced by FocusPaused only. Capture operations are NOT
\* ended here: each ends when its own finally runs (doc INV-STOP-1 row, part b).
Stop ==
    /\ chord' = FALSE /\ eyes' = FALSE
    /\ micDev' = "Off" /\ micInd' = FALSE /\ voice' = "Off"
    /\ thinking' = FALSE /\ responding' = FALSE /\ nudge' = FALSE /\ happy' = FALSE
    /\ typed' = Stale(typed) /\ vtool' = Stale(vtool) /\ vundo' = Stale(vundo)
    /\ card' = CardNewGen /\ ticket' = TicketNewGen
    /\ focus' = IF focus = "Running" THEN "Paused" ELSE focus
    /\ UNCHANGED <<hovered, fcheck, fileJ, fieldJ, focusEnds>>
    /\ Fin("Stop")

\* [F21] quit = StopAll, then the process ends. [Fo3] [F9 finding] an open focus session is only paused,
\* never ended: no end event (last = "QuietEnd"). After Quit nothing is enabled (TLC runs with
\* CHECK_DEADLOCK FALSE).
Quit ==
    /\ alive' = FALSE
    /\ hovered' = FALSE
    /\ phase' = "Hidden"
    /\ thinking' = FALSE /\ responding' = FALSE /\ nudge' = FALSE /\ happy' = FALSE
    /\ micDev' = "Off" /\ micInd' = FALSE /\ chord' = FALSE /\ eyes' = FALSE /\ voice' = "Off"
    /\ typed' = None /\ vtool' = None /\ vundo' = None /\ fcheck' = FALSE
    /\ card' = None /\ ticket' = None
    /\ focus' = IF focus = "Running" THEN "Paused" ELSE focus
    /\ UNCHANGED <<fileJ, fieldJ, focusEnds>>
    /\ effect' = None
    /\ last' = IF focus \in Live THEN "QuietEnd" ELSE "Other"

-----------------------------------------------------------------------------
(* Approval card and undo (doc 3.6, 3.7). Approve covers the plan's Approve and ApproveUndo. *)

\* [A5] Approve, still current: the ticket is the digest of THIS proposal (ActionPolicy.cs:62-82).
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

\* [A2] Reject (Cancel button, Escape).
Reject ==
    /\ card # None /\ card.st = "Pending"
    /\ card' = None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* [A6] [G5] [U5] [U6] the effect commits inside TryCommit (under the coordinator's lock, so Stop cannot
\* interleave): only for the current generation and only with the ticket of this exact proposal
\* (DesktopTools.cs:968-971). Then [A7] more steps queued -> next card (new proposal, needs a new Approve)
\* or [A8] done.
Apply ==
    /\ card # None /\ card.st = "Executing" /\ card.cur /\ ticket = card.p
    /\ fileJ' = FileAfter(card.p)
    /\ fieldJ' = FieldAfter(card.p)
    /\ effect' = [p |-> card.p, ticket |-> ticket, cur |-> card.cur, journal |-> JournalOf(card.p.target)]
    /\ \E more \in BOOLEAN, t \in Targets :
          card' = IF more /\ card.p.kind = "Edit"
                  THEN [p |-> [id |-> OtherId(card.p.id), kind |-> "Edit", target |-> t],
                        st |-> "Pending", cur |-> TRUE]
                  ELSE None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, focusV>>
    /\ last' = "Other"
    /\ UNCHANGED alive
    /\ phase' = Retime(phase, Want')

\* [A8] execution failed, refused (stale ticket) or stopped: card closed (finally).
ExecEnd ==
    /\ card # None /\ card.st = "Executing"
    /\ card' = None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, fileJ, fieldJ, focusV>>
    /\ Fin("Other")

\* RED PROOF ONLY (plan 3.3 row 2.2): a planted bug that applies a PENDING proposal with no Approve.
\* Disabled unless PlantApplyWithoutApprove = TRUE (NotchLifecycle_redproof.cfg). INV_APPLY_1 must catch it.
BuggyApply ==
    /\ PlantApplyWithoutApprove
    /\ card # None /\ card.st = "Pending"
    /\ fileJ' = FileAfter(card.p)
    /\ fieldJ' = FieldAfter(card.p)
    /\ effect' = [p |-> card.p, ticket |-> ticket, cur |-> card.cur, journal |-> JournalOf(card.p.target)]
    /\ card' = None
    /\ ticket' = None
    /\ UNCHANGED <<hovered, flagV, micV, workV, focusV>>
    /\ last' = "Other"
    /\ UNCHANGED alive
    /\ phase' = Retime(phase, Want')

-----------------------------------------------------------------------------
(* Focus session (doc 3.4). Button and voice versions (focus_start/pause/resume/end) make the same moves. *)

\* [Fo1] [F20] start (Happy off); [Fo2] a live session is replaced with EndQuietly: NO end event.
FocusStart ==
    /\ focus' = "Running"
    /\ focusEnds' = 0
    /\ happy' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV>>
    /\ Fin(IF focus \in Live THEN "QuietEnd" ELSE "Other")

\* [Fo3] [F19] Pause button, voice focus_pause, sleep gap, tick error (Stop, disconnect and the 5 min break
\* are in Stop, VDrop and NudgeBreak).
FocusPause ==
    /\ focus = "Running"
    /\ focus' = "Paused"
    /\ UNCHANGED <<hovered, flagV, micV, workV, applyV, focusEnds>>
    /\ Fin("Other")

\* [Fo4] [F20] Resume (only by button or voice).
FocusResume ==
    /\ focus = "Paused"
    /\ focus' = "Running"
    /\ UNCHANGED <<hovered, flagV, micV, workV, applyV, focusEnds>>
    /\ Fin("Other")

\* [Fo5] [Fo7] [F18] the planned time is reached: one end event, Happy moment.
FocusComplete ==
    /\ focus = "Running"
    /\ focus' = "Off"
    /\ focusEnds' = focusEnds + 1
    /\ happy' = TRUE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV>>
    /\ Fin("Other")

\* [Fo6] [Fo7] [F18] End (button or voice): one end event, Happy moment.
FocusEnd ==
    /\ focus \in Live
    /\ focus' = "Off"
    /\ focusEnds' = focusEnds + 1
    /\ happy' = TRUE
    /\ UNCHANGED <<hovered, thinking, responding, nudge, micV, workV, applyV>>
    /\ Fin("Other")

\* [Fo8] [F14] a focus check is due: its capture STARTS the reveal (nothing pinned the notch before).
FocusCheckStart ==
    /\ focus = "Running" /\ ~fcheck
    /\ fcheck' = TRUE
    /\ UNCHANGED <<hovered, flagV, micV, typed, vtool, vundo, applyV, focusV>>
    /\ Fin("Other")

\* [Fo8] [F15] [F16] the check ends: capture off; "drifting" while still running -> Nudge moment.
FocusCheckDone ==
    /\ fcheck
    /\ fcheck' = FALSE
    /\ \E drift \in BOOLEAN : nudge' = (nudge \/ (drift /\ focus = "Running"))
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, typed, vtool, vundo, applyV, focusV>>
    /\ Fin("Other")

\* [F17] nudge "Back to it".
NudgeBack ==
    /\ nudge
    /\ nudge' = FALSE
    /\ UNCHANGED <<hovered, thinking, responding, happy, micV, workV, applyV, focusV>>
    /\ Fin("Other")

\* [F17] [Fo3] nudge "5 min break": the nudge closes and a running session pauses.
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
       \/ ExpireResponding \/ ExpireNudge \/ ExpireHappy
       \/ \E k \in {"Ask", "Undo"} : Send(k)
       \/ TypedCapture \/ TypedNoFrame \/ TypedReply \/ TypedHandOff \/ TypedUndoPlan \/ TypedEnd
       \/ VSpeechStarted \/ VSpeechStopped \/ VAudioDelta \/ VResponseDone \/ VDrop \/ VGoToSleep
       \/ VToolCall \/ VToolCapture \/ VToolDone \/ VUndoStart \/ VUndoDone
       \/ TalkButton
       \/ \E s \in {"chord", "eyes"} : TalkDown(s) \/ TalkUp(s)
       \/ HoldTimeout
       \/ Stop \/ Quit
       \/ Approve \/ Reject \/ Apply \/ ExecEnd \/ BuggyApply
       \/ FocusStart \/ FocusPause \/ FocusResume \/ FocusComplete \/ FocusEnd
       \/ FocusCheckStart \/ FocusCheckDone \/ NudgeBack \/ NudgeBreak
    /\ Active' <= MaxActive

\* Fairness ONLY for real local drivers: the render loop (Tick), the moment timers, and steps that are pure
\* local code with no provider wait (taking or skipping the screenshot, planning a typed undo, the end of a
\* spoken undo, and a typed request's finally, which the 45 s HTTP timeout guarantees). The user and the AI
\* provider get none, so "the provider never answers" is a legal behaviour (that is doc finding F7). The
\* local steps are guarded by alive, so nothing is owed after Quit.
Fairness ==
    /\ WF_vars(Tick)
    /\ WF_vars(ExpireResponding)
    /\ WF_vars(ExpireNudge)
    /\ WF_vars(ExpireHappy)
    /\ WF_vars(alive /\ (TypedCapture \/ TypedNoFrame))
    /\ WF_vars(alive /\ TypedUndoPlan)
    /\ WF_vars(alive /\ TypedEnd)
    /\ WF_vars(alive /\ VUndoDone)

Spec == Init /\ [][Next]_vars /\ Fairness

-----------------------------------------------------------------------------
(* Checks that HOLD today (NotchLifecycle.cfg). Names use _ for the plan's - (TLA+ identifiers). *)

TypeOK ==
    /\ alive \in BOOLEAN /\ hovered \in BOOLEAN /\ phase \in Phases
    /\ thinking \in BOOLEAN /\ responding \in BOOLEAN /\ nudge \in BOOLEAN /\ happy \in BOOLEAN
    /\ micDev \in {"Off", "Hold", "Button"} /\ micInd \in BOOLEAN
    /\ chord \in BOOLEAN /\ eyes \in BOOLEAN /\ voice \in {"Off", "Ptt", "Other"}
    /\ typed \in {None} \cup TypedReqs
    /\ vtool \in {None} \cup ToolReqs
    /\ vundo \in {None} \cup UndoReqs
    /\ fcheck \in BOOLEAN
    /\ card \in {None} \cup Cards
    /\ ticket \in {None} \cup Proposals
    /\ fileJ \in {"none", "edit", "undo"} /\ fieldJ \in {"none", "edit"}
    /\ focus \in {"Off", "Running", "Paused"} /\ focusEnds \in 0..2
    /\ effect \in {None} \cup Effects
    /\ last \in {"Other", "Stop", "QuietEnd"}

\* INV-PRIV-1 (doc 4, today's form): the mic indicator or a capture is on => the notch is out (Revealing
\* counts: the 240 ms slide has started) and shows Listening or Capture.
INV_PRIV_1 == (micInd \/ CaptureOn) => (phase # "Hidden" /\ Face \in {"Capture", "Listening"})

\* INV-APPLY-1: every applied effect carries the ticket of that exact proposal (same id, kind, target).
INV_APPLY_1 == effect # None => effect.ticket = effect.p

\* INV-APPLY-2, field part: a field undo needs a recorded edit, and after it the journal is empty (U5).
INV_APPLY_2_FIELD ==
    (effect # None /\ effect.p.kind = "Undo" /\ effect.p.target = "Field")
        => (effect.journal = "edit" /\ fieldJ = "none")

\* INV-APPLY-3: nothing from an older request generation is ever applied.
INV_APPLY_3 == effect # None => effect.cur

\* INV-TALK-1 (doc 4, today's form): the push-to-talk mic is open only while a hold is down, and right
\* after Stop the mic and its indicator are off. The release part is INV_TALK_1_RELEASE.
INV_TALK_1 ==
    /\ micDev = "Hold" => (chord \/ eyes)
    /\ last = "Stop" => (micDev = "Off" /\ ~micInd)

\* INV-TALK-1 on the last release (TalkUp, eyes released, 120 s timeout, abort): the hold's own mic and
\* the indicator are off in the very next state.
INV_TALK_1_RELEASE ==
    [][((chord \/ eyes) /\ ~(chord' \/ eyes')) => (micDev' # "Hold" /\ ~micInd')]_vars

\* INV-FOCUS-1, today's form: at most one end event per session (_endRaised, FocusSessionController.cs:161-168).
INV_FOCUS_AT_MOST_ONE == focusEnds <= 1

\* LIVE-1, Responding part: never stuck on Responding (moment timer).
LIVE_1_RESPONDING == responding ~> ~responding

\* LIVE-2, today's form: after the pointer leaves and nothing pins the notch, it reaches Hidden unless new
\* input arrives first (or Boo quits).
LIVE_2 == (alive /\ ~hovered /\ ~Pinned) ~> (~alive \/ hovered \/ Pinned \/ phase = "Hidden")

-----------------------------------------------------------------------------
(* The plan's checks AS WRITTEN that today's code breaks (NotchLifecycle_plan_gaps.cfg). Each must FAIL
   with a counterexample until Vikas decides: change the behaviour, or reword the invariant. *)

\* INV-STOP-1 (doc finding F4): right after Stop the notch is quiet with mic and capture off.
\* Breaks: a running focus session is paused and Paused pins the notch; captures outlast Stop.
INV_STOP_1 == last = "Stop" => Quiet

\* INV-PRIV-2 (doc finding F5): no capture starts while the notch is Hidden (no capture is ever approved).
\* Breaks: a focus check (and a spoken undo) starts its capture from Hidden.
INV_PRIV_2 == [][(~CaptureOn /\ CaptureOn') => phase # "Hidden"]_vars

\* INV-APPLY-2, file part (doc finding F6): undo only once. Breaks: an approved file undo is journaled as a
\* new entry, so a second undo reverses the undo.
INV_APPLY_2_FILE ==
    (effect # None /\ effect.p.kind = "Undo" /\ effect.p.target = "File") => effect.journal = "edit"

\* INV-FOCUS-1 as written (doc finding F9): exactly one end event per session. Breaks: a session replaced
\* by a new Start, or open at quit, closes with none.
INV_FOCUS_1_EXACT == last # "QuietEnd"

\* LIVE-1, Thinking part (doc finding F7): Thinking always ends. Breaks: no local timeout; a silent
\* provider (or a typed question handed to voice) leaves Thinking on forever.
LIVE_1_THINKING == thinking ~> ~thinking

\* INV-TALK-1 as written (doc finding F10): after the last release, the microphone DEVICE is off. Breaks the
\* same way as INV_PRIV_1_DEVICE: the Talk-button audio survives the release (or the 120 s hold timeout).
INV_TALK_1_DEVICE ==
    [][((chord \/ eyes) /\ ~(chord' \/ eyes')) => micDev' = "Off"]_vars

\* INV-PRIV-1 as written, for the microphone DEVICE (lifecycle doc finding F10): the mic is open =>
\* the notch is visible. Breaks: hold Ctrl+Alt+Space, press Talk twice (stop, start), release the chord:
\* StopPushToTalkMic turns the indicator off but leaves the Talk-button audio running (MainWindow.Talk.cs:383-398).
INV_PRIV_1_DEVICE == (micDev # "Off" \/ CaptureOn) => phase # "Hidden"

=============================================================================
