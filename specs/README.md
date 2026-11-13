# specs: the notch lifecycle model

A small formal model of how Boo's notch behaves **today**: when it shows, when the mic and screen capture are on,
how Stop, chat replies, the approval card, undo and focus sessions move it. A checker called **TLC** walks every
reachable state of the model and tells us whether a rule (an "invariant") can ever be broken, with the exact steps
when it can.

Agent-trust plan, Phase 2, PR 2.2 (`docs/plans/agent-trust-plan.md` 3.2 B and 3.3).

## Files

| File | Role |
| --- | --- |
| `NotchLifecycle.tla` | The model. One action per user, timer, local or provider step. |
| `NotchLifecycle.cfg` | The rules that **hold** today. TLC must **pass**. |
| `NotchLifecycle_plan_gaps.cfg` | The plan's rules, as written, that today's code **breaks**. Each must **fail**. |
| `NotchLifecycle_redproof.cfg` | The plan's own red proof, to run by hand: "apply without Approve" must be caught. |
| `../scripts/ci/run_tlc.sh` | Downloads the pinned checker, verifies it, runs the whole run table below, prints a summary. |
| `../.github/workflows/formal.yml` | Runs the script on Linux for changes here, and nightly (plus a wider nightly run). |

## The three switches

The model has three constants. With all three at their defaults it is today's code.

- `Plant`: a **planted bug**, one plausible code mistake per main rule. The rule must catch it (a "red proof"), so
  no rule can pass because it checks nothing.
- `Fix`: the **obvious fix** of one gap finding. The gap must fail with no fix and pass with its fix. If it still
  fails with its fix on, its counterexample has another cause than the finding says, and CI says so.
- `MaxActive`: how many features may be busy at the same moment (2 on every PR, 3 in the nightly `tlc-wide` job).

## Rules, red proofs, witnesses and fixes

A **witness** is the opposite of a rule's trigger ("the mic indicator is never on"). TLC must find it false, which
proves the trigger really happens, so a rule like "if the mic is on then ..." cannot pass only because the mic never
comes on. "Structural" means the rule holds because the model copies one guard from the code; whether the code keeps
that guard is proved later by the conformance test (PR 2.7), not here.

| Rule | Plain words | Today | Red proof (`Plant`) | Witness |
| --- | --- | --- | --- | --- |
| `INV_PRIV_1` | Mic indicator or capture on: the notch is out and shows Listening or Capture | pass | `PinIgnoresCapture` (starting a capture does not re-pin the notch) | `W_PRIV_1_MIC`, `W_PRIV_1_CAPTURE` |
| `INV_APPLY_1` | Nothing is applied without Approve of that exact proposal (**structural**) | pass | `ApplyWithoutApprove` | `W_APPLY_1` |
| `INV_APPLY_2_FIELD` | A field undo needs a recorded edit; after a **verified** undo the journal is empty | pass | `FieldUndoKeepsJournal`, `UndoWithoutJournal` | `W_APPLY_2_FIELD` |
| `INV_APPLY_3` | Nothing from an older request is applied (**structural**) | pass | `CommitIgnoresGeneration` | `W_APPLY_3` |
| `INV_TALK_1` | The hold's mic is open only while a hold is down; after Stop mic and indicator are off (that part **structural**) | pass | `StopKeepsMic` | `W_TALK_1_HOLD`, `W_TALK_1_STOP` |
| `INV_TALK_1_RELEASE` | After the last release, the hold's mic and the indicator are off | pass | `ReleaseKeepsIndicator` | `W_TALK_1_RELEASE` |
| `INV_FOCUS_AT_MOST_ONE` | At most one end event per focus session | pass | `EndNotGuarded` | `W_FOCUS_END` |
| `LIVE_1_RESPONDING` | Never stuck on Responding | pass | `NoRespondingTimer` (the timer never fires) | `W_LIVE_1_RESPONDING` |
| `LIVE_2` | After the pointer leaves with nothing pinning it, the notch hides (unless new input comes) | pass | `NoRetractTimer` (the 900 ms delay never fires) | `W_LIVE_2` |
| `TypeOK` | Every variable stays in its value set (model sanity, not behaviour) | pass | none (no behaviour to plant) | none |

| Gap (plan wording) | Finding | Today | `Fix` (the modelled obvious fix) | With fix | Witness under fix |
| --- | --- | --- | --- | --- | --- |
| `INV_STOP_1` Stop leaves the notch quiet | F4 | fail | `F4`: Stop ends every capture at once and ends a live focus session (one end event, no Happy face) | pass | `W_STOP_1` |
| `INV_PRIV_2` No capture starts while hidden | F5 | fail | `F5`: a capture about to start pins the notch first; the grab waits until it is out | pass | `W_PRIV_2` |
| `INV_APPLY_2_FILE` Undo only once | F6 | fail | `F6`: a verified file undo clears the file journal | pass | `W_APPLY_2_FILE` |
| `LIVE_1_THINKING` Thinking always ends | F7 | fail | `F7`: a local Thinking timeout | pass | `W_THINKING` |
| `INV_FOCUS_1_EXACT` Exactly one end event | F9 | fail | `F9`: replacing or quitting a live session raises its end event | pass | `W_FOCUS_1_EXACT` |
| `INV_PRIV_1_DEVICE` Mic device on: the notch is visible | F10 | fail | `F10`: the last release stops whichever audio is current | pass | `W_DEVICE` |
| `INV_TALK_1_DEVICE` After the last release the mic device is off | F10 | fail | `F10` (same) | pass | `W_DEVICE` |

The fixes are candidates that show each gap has the cause the finding says. They are not decisions: Vikas decides
each finding in the lifecycle doc.

## The fairness rule

"Fair" means "if this step stays possible, it eventually happens". The model gives that promise only where the
code keeps it on its own:

> A step is **fair if and only if the C# guarantees it completes by a local bound**: a timer, a `CancelAfter` or the
> 45 s `HttpClient` timeout, a cancelled request token, or a `finally` after a bounded await. Events the AI provider
> pushes, and awaits with no local bound (websocket sends: `SendTextAsync`, `CompleteToolAsync`, `InterruptAsync`,
> `CommitAudioTurnAsync`), are **never** fair, and neither is the user. Fairness is always on **all outcomes** of a
> step (success and failure together), never on the success branch alone.

Local OS calls (taking a screenshot, reading controls through UI Automation, writing a file) have no timeout of
their own; the model assumes they return (LOW, see Simplifications).

| Action | Fair? | Why (code) |
| --- | --- | --- |
| `Tick` (reveal timeline) | fair | render loop, `NotchWindow.xaml.cs:639-674`; timeline `NotchFace.cs:347-353` |
| `ExpireResponding`, `ExpireNudge`, `ExpireHappy` | fair | face moment timer, `MainWindow.xaml.cs:175-202` (4 s, 6 s, 2.5 s) |
| `ExpireThinking` (Fix F7 only) | fair | the fix is a local timer |
| `HoldTimeout` | fair | 250 ms ticker, `MainWindow.Talk.cs:57-58`, `:196-208`; 120 s limit `HoldToTalk.cs:163-174` |
| `FocusComplete` | fair | 1 s focus timer, `FocusTimer_OnTick` `MainWindow.xaml.cs:1438-1460` |
| `FocusCheckStart` | fair | capture timer, `FocusCaptureTimer_OnTick` `MainWindow.xaml.cs:1497-1500` |
| `FocusCheckCapture` (Fix F5 only) | fair | local step of the fix |
| `FocusCheckDone` | fair | local capture, then `ClassifyAsync` over the 45 s `HttpClient` (`:1506-1582`, `:1624`) |
| `TypedCapture` or `TypedNoFrame` | fair | local screenshot, `CaptureForRequestAsync` `:1999-2058` |
| `TypedReply` or `TypedUndoPlan` or `TypedEnd` | fair when the request is stale, or on the HTTP path (speech off; every typed undo) | `RespondAsync` over the 45 s `HttpClient` (`:2123`); undo is local (`Edits.cs:71-123`); a stale request's token is cancelled |
| `TypedHandOff`, and `TypedEnd` of a current speech-on request | **not fair** | `SendTextAsync` has no timeout (`RealtimeClient.cs:406-410`) |
| `VToolCapture` or `VToolNoFrame` | fair | local screenshot |
| `VToolAnswer` or `VToolFail` | fair | `RespondAsync` over the 45 s `HttpClient`; failure keeps Thinking on (`:2384-2391`, `:2415-2432`) |
| `VToolRelease` (the tool's `finally`) | fair only when stale | otherwise it waits for `CompleteToolAsync`, no timeout (`RealtimeClient.cs:412-419`) |
| `VUndoCapture` (Fix F5 only) | fair | local step of the fix |
| `VUndoDone` | fair only when stale | otherwise it may wait for `InterruptAsync`, no timeout (`Edits.cs:144-150`, `RealtimeClient.cs:457-458`) |
| `Apply` or `ExecEnd` | fair | `ExecuteAsync` is local and honours the lease token; read-back is at most five 120 ms polls (`MainWindow.xaml.cs:1225-1226`, `Edits.cs:358-364`) |
| `PointerEnter/Leave`, `Send`, `TalkButton`, `TalkDown/Up`, `Stop`, `Quit`, `Approve`, `Reject`, `FocusStart/Pause/Resume/End`, `NudgeBack`, `NudgeBreak` | not fair | the user |
| `VSpeechStarted/Stopped`, `VAudioDelta`, `VResponseDone`, `VDrop`, `VGoToSleep`, `VToolCall`, `VUndoStart` | not fair | the provider |
| `BuggyApply`, `FocusEndAgain` | not fair | planted bugs (red proofs only) |

## Simplifications (what the model leaves out)

Each is LOW: it changes no check result.

- After the answer, a current voice tool's capture stays open until `CompleteToolAsync` returns; the model keeps it
  open too (`st = "Send"`). If an undo card is pending, the tool first sends a decline with `CompleteToolAsync`
  (`DeclineRealtimeToolDuringUndoAsync`, `Edits.cs:190-210`, called at `:2360` and `:2401`); the model ignores
  that wait, so its answer step is fair even then.
- After a voice-originated approval runs, the code awaits `CompletePendingRealtimeToolAsync` (a websocket send)
  before the card's `finally`; the model closes the card in the same step as the commit.
- A failed `SendTextAsync` after a successful connect leaves a connected voice session in the code
  (`MainWindow.xaml.cs:777`, `:797`); the model's `TypedEnd` leaves `voice = "Off"`.
- A spoken undo's capture has no "no frame" branch; a spoken undo is unfair while current even when no reply is
  playing (then `InterruptAsync` is skipped and the step is local).
- The two undo journals are independent in the model; in the code each new kind of edit clears the other
  (`MainWindow.Edits.cs:244`, `:533`). A reply that arrives while a card is up keeps the old card (the code replaces
  it). A multi-step undo is one Apply step.
- A typed question handed to voice opens a text-only session (`"Text"`): no speech events and no spoken undo.
- A stale approval that is still executing blocks a new card in the model; in the code the new card appears but
  its Approve stays disabled until the old approval's `finally`.
- The hold-to-talk keyboard hook posts StartListening to the UI thread (`MainWindow.Talk.cs:89-154`); the model
  makes TalkDown one atomic step (a Stop between the key and the posted start is not modelled; UNVERIFIED race).
- The Responding moment may expire in the model even while audio deltas keep refreshing it in the code.
- Time is steps, not milliseconds. Request generations are a "still current" flag (exact: the code only asks
  "is this the current generation?").
- At most one typed request, one voice tool, one spoken undo and one focus check at a time, and at most
  `MaxActive` busy lanes at once. The Talk button's connect is one step. Not modelled: the panel opening, the
  pointer and ring, reduced motion.

## Size and time

Measured with a Python copy of the model (MOCK, not TLC): about 200 thousand states with `MaxActive = 2`. The PR run
is about 45 TLC runs; most red proofs and witnesses stop at their first counterexample, the main run and the fix
runs explore everything. The nightly `tlc-wide` job runs only the main cfg with `MaxActive = 3` (about 2 million
states, 10 times more; same Python copy) and has its own 40 minute limit.

## Run it locally

Needs Java 11 or newer and `curl`. From the repo root:

```bash
bash scripts/ci/run_tlc.sh          # everything (MaxActive 2)
bash scripts/ci/run_tlc.sh --wide   # main cfg only, MaxActive 3
```

It downloads `tla2tools.jar` (pinned release, SHA-256 checked before use), runs the run table and prints a summary.
Logs, with the counterexample steps, land in `artifacts/tlc/`. In CI the same output is printed in the `formal` job log; nothing is uploaded, so the BOO-SAFE-002 key scan has nothing to guard there.

## Exit bar for reviewers

Review the model against three tables in this file: the **fairness table** (is every fair step really bounded by
the code line given, and is every unfair one really unbounded?), the **red-proof list** (is each planted bug a
plausible code mistake, and is it caught?) and the **fix-switch matrix** (does each gap fail today and pass with its
fix, with its trigger still reached?). A fidelity gap that changes **no check result** is a LOW entry under
Simplifications, not a FAIL. A FAIL is a check that passes or fails for the wrong reason.

## Changing things: doc, model and code move together

1. Behaviour changes are agreed in `docs/architecture/notch-lifecycle.md` first.
2. The same PR updates the matching action here, the cfg lists, and the lists and run table at the top of
   `scripts/ci/run_tlc.sh`. The script refuses a cfg that adds or drops a rule, a main rule without a red proof or
   witness, and a gap without its fix run and fix witness.
3. A gap that starts passing fails CI on purpose: if the code really fixed it, move the rule from the gaps cfg to
   the main cfg (give it a red proof and a witness) and update the doc in the same PR.
4. From PR 2.4 the C# reducer (`src/Boo.Core/NotchLifecycle*`) follows this model, and from PR 2.7 a conformance
   test checks they agree; that test is the real proof of the structural rules. Changes to those files also run
   this workflow.
