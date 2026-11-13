# specs: the notch lifecycle model

A small formal model of how Boo's notch behaves **today**: when it shows, when the mic and screen capture are on,
how Stop, chat replies, the approval card, undo and focus sessions move it. A checker called **TLC** walks every
reachable state of the model and tells us whether a rule (an "invariant") can ever be broken, with the exact steps
when it can.

Agent-trust plan, Phase 2, PR 2.2 (`docs/plans/agent-trust-plan.md` 3.2 B and 3.3).

## Files

| File | Role |
| --- | --- |
| `NotchLifecycle.tla` | The model. One action per user, timer or provider event. |
| `NotchLifecycle.cfg` | The rules that **hold** today. TLC must **pass**. |
| `NotchLifecycle_plan_gaps.cfg` | The plan's rules, as written, that today's code **breaks**. Each must **fail**. |
| `NotchLifecycle_redproof.cfg` | Turns on a planted bug ("apply without Approve"). Must **fail** on `INV_APPLY_1`. |
| `../scripts/ci/run_tlc.sh` | Downloads the pinned checker, verifies it, runs all of the above, prints a summary. |
| `../.github/workflows/formal.yml` | Runs the script on Linux for changes here, and nightly. |

## How it maps to the lifecycle doc

The model copies `docs/architecture/notch-lifecycle.md`, not the plan's ideal picture. Every action has a comment
with the doc's row ids, for example `[R4]` (reveal timeline), `[F10b]` (face flags), `[H8]` (hold-to-talk), `[Fo2]`
(focus), `[G3]` (request generations), `[A5]` (approval card), `[U6]` (undo). Search the `.tla` for a row id to find
its action.

| Rule | Plain words | Where | Expected |
| --- | --- | --- | --- |
| `INV_PRIV_1` | Mic indicator or capture on: the notch is out and shows Listening or Capture | main | pass |
| `INV_APPLY_1` | Nothing is applied without Approve of that exact proposal | main | pass |
| `INV_APPLY_2_FIELD` | A field undo needs a recorded edit, and clears it | main | pass |
| `INV_APPLY_3` | Nothing from an older request is applied | main | pass |
| `INV_TALK_1`, `INV_TALK_1_RELEASE` | After the last hold release or Stop, the hold's mic and the indicator are off | main | pass |
| `INV_FOCUS_AT_MOST_ONE` | At most one end event per focus session | main | pass |
| `LIVE_1_RESPONDING` | Never stuck on Responding | main | pass |
| `LIVE_2` | After the pointer leaves with nothing pinning it, the notch hides (unless new input comes) | main | pass |
| `INV_STOP_1` | Stop leaves the notch quiet (finding F4: Stop leaves it on Paused) | gaps | fail |
| `INV_PRIV_2` | No capture starts while hidden (finding F5: a focus check does) | gaps | fail |
| `INV_APPLY_2_FILE` | Undo only once (finding F6: a second file undo re-applies) | gaps | fail |
| `INV_FOCUS_1_EXACT` | Exactly one end event (finding F9: replaced or quit sessions have none) | gaps | fail |
| `LIVE_1_THINKING` | Thinking always ends (finding F7: no local timeout) | gaps | fail |
| `INV_PRIV_1_DEVICE` | Mic device on: the notch is visible (**new**, not in the doc yet, see below) | gaps | fail |
| `INV_APPLY_1` with the planted bug | The checker catches a real apply-without-approve bug | red proof | fail |

`INV_PRIV_1_DEVICE`: holding Ctrl+Alt+Space, then starting a Talk-button session, then releasing the chord turns
the notch's mic indicator off but leaves the Talk-button microphone running (`StopPushToTalkMic`,
`src/Boo.App/MainWindow.Talk.cs:384-397`). Found while building the model, by reading the code; no run has shown
it yet. It should be added to the lifecycle doc as a finding before Vikas decides on it.

## Simplifications (what the model leaves out)

- Time is steps, not milliseconds: the render loop and the moment timers are fair actions; the user and the AI
  provider are not fair (they may never act).
- Request generations are a "still current" flag on each piece of in-flight work. This is exact, because the
  code only ever asks "is this the current generation?".
- At most one typed request, one voice tool call, one spoken undo and one focus check at a time, and at most
  `MaxActive = 2` busy lanes at once (typed request, voice tool, spoken undo, focus check, approval card, voice
  session, a hold, a live focus session). So every pair of features meets; three at once is not explored.
  `MaxActive = 3` is about 8 times more states (measured with a script copy of the model: about 130 thousand
  states at 2, about 1 million at 3).
- The Talk button's connect step is one step; a failed connect is the later disconnect action.
- Undo journals keep only the newest entry; refused undo plans (file changed, newer field edit) are a yes/no
  choice, not file contents.
- Not modelled: the panel opening (no notch flag changes), the pointer and ring, reduced motion, two typed
  requests in flight at once.

## Run it locally

Needs Java 11 or newer and `curl`. From the repo root:

```bash
bash scripts/ci/run_tlc.sh
```

It downloads `tla2tools.jar` (pinned release, SHA-256 checked before use), runs the three cfgs and prints a
summary. Logs, with the counterexample steps for every gap, land in `artifacts/tlc/`.

## Changing things: doc, model and code move together

1. Behaviour changes are agreed in `docs/architecture/notch-lifecycle.md` first.
2. The same PR updates the matching action here, and the cfg lists plus the two lists at the top of
   `scripts/ci/run_tlc.sh` (the script refuses a cfg that drops or adds a rule without the script).
3. A gap that starts passing fails CI on purpose: if the code really fixed it, move the rule from the gaps cfg to
   the main cfg and update the doc in the same PR.
4. From PR 2.4 the C# reducer (`src/Boo.Core/NotchLifecycle*`) follows this model, and from PR 2.7 a conformance
   test checks they agree. Changes to those files also run this workflow.
