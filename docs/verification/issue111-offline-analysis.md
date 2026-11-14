# Issue 111: existing-receipt diagnosis and producer trace specification

**Result: inconclusive attribution.** This is manual source analysis plus inspected live failure evidence, not a repair or full-app PASS.

## Pinned evidence

[Run 43575488622](https://github.com/vikas53953/bluey/actions/runs/43575488622) published app and harness `9feda80da7ee0d0e708169d7569f0a0151a4d282`; Q13H failed its unchanged nominal-duration compatibility assertion. No key, provider call or retry. Identity, TRX, observation health, event log and Happy screenshot inspected.

| Direct read interval (ms, recorder clock) | Observed state |
| --- | --- |
| 1697–1742 | Paused |
| 1782–1784 | Happy |
| 4329–4330 | Happy |
| 4370–4371 | Idle |

Conservative entry bracket [1697,1784], exit [4329,4371], duration [2545,2674] ms. Entry width87ms and exit42ms satisfy the existing100ms bracket bound. The duration interval excludes2500. UIA return time is not exact observation time. Callback delivery Happy1785/1787, Idle4334/4335 and Hidden5409/5411 independently supports ordering, not exact producer or rendered duration. End invocation is [1651,1752].

## Complete source ordering at the artifact revision

1. `Focus_OnSessionEnded` stops monitoring, saves history, updates view/status and requests the end chirp. Its `finally` calls `SetFace(...Happy=true, 2.5s, Happy)` then raises `FocusSessionEnded`.
2. `SetFace` samples `Environment.TickCount64`, records the Happy hold as that value+2500 in `FaceMomentSchedule`, clears inactive moments, and calls `ArmFaceMomentTimer(now)`.
3. `ArmFaceMomentTimer` computes `NextDue(now)`, stops the existing timer, assigns its interval and starts it. The timer has Background dispatcher priority. A later `SetFace` may stop/rearm it against the remaining deadline; every rearm matters to diagnosis.
4. Only afterward does `SetFace` assign `_faceInputs` and synchronously invoke `FaceInputsChanged`.
5. `App.OnFaceInputsChanged` synchronously calls `NotchWindow.UpdateFace`, which recomputes pinned state and reveal phase, resolves the face, and calls `ApplyState` for a change.
6. `ApplyState` assigns `_state` and calls `PublishFaceState` before morph/layout/visual work. `PublishFaceState` sets ItemStatus, then raises the UIA property event. Consequently ItemStatus does not establish completed rendering.
7. On a timer tick, the timer stops; `Expire(TickCount64)` clears due moments; `SetFace` removes expired Happy, rearms any remaining moments and republishes through the same path. Once Happy unpins, the reveal timeline can produce Idle then Hidden.

The source establishes possible deadline/start skew, tick dispatch delay and publication cost. It does not measure those costs in this run. Healthy UIA reads do not prove that the Background tick was serviced promptly. Screenshot/UIA overhead remains a possible contributor. No exclusive hotspot or timer repair is established.

## Bounded diagnostic specification (not yet implemented or executed)

Use the existing opt-in QA data-marker diagnostic mechanism; default behavior must remain unchanged. Record only timestamps/state/timer metadata, with buffered background output and no secrets, paths or conversation data. No broad dispatcher-hook stream or ETW capture is needed for this question.

- One sequence number per SetFace invocation/Happy hold and every rearm, including unchanged-input early returns.
- QPC frequency and a bounded clock anchor `QPC-before / TickCount64 / QPC-after`. Keep both endpoints; do not treat coarse TickCount64 as exact QPC time.
- Happy hold origin TickCount64, requested duration and computed deadline; QPC before/after Hold.
- Every timer stop/rearm: originating sequence, sampled `now`, NextDue, QPC before/after Interval assignment and Start.
- Tick entry QPC/TickCount64 and expiry-decision before/after values/flags; correlate to the active hold/deadline and rearm.
- Before/after FaceInputsChanged forwarding and before/after Happy/Idle ItemStatus assignment, correlated to the same hold. Label event publication separately from visual rendering.
- Preserve existing test/action/read intervals, screenshots and all verdicts unchanged. Instrumented timing is diagnostic evidence, not an acceptance replacement; logger overhead must remain a stated limitation.

One reviewed no-key hosted Q13H attempt with these receipts could distinguish origin/start skew, late tick entry and publication delay. A new attempt is not authorized by this document alone. No priority, refactor, assertion tolerance, retry or Q10/issue78 change follows from this analysis.

## Reviewed implementation for one bounded diagnostic attempt

**manual:** independent implementation review approved the uncommitted producer trace on base `f12e37b`. SetFace invocation, Happy generation and timer arm IDs are distinct. Scoped context is app-owned on the UI thread and restored on return; the notch retains the causal input context for later recomputation. Primitive operation timestamps are sampled before enqueue; formatting and file writes run on the background sink. The narrow marker enables no dispatcher hooks. Disabled mode samples no extra clocks. Default scheduling and all Q13H timing assertions are unchanged.

A separate Q13H fixture seeds only `qa-face-timing.enabled` when explicitly selected by the focused workflow. Outside the measured interval, it writes an atomic nonce request and waits at most3seconds for an atomic drain receipt. The receipt freezes an issued-record cutoff and reports committed count, drops, write failures, pending tail and future events outside coverage. Failure to obtain complete cutoff evidence is retained separately without masking the original assertion. Trace copying is attempted even if receipt collection fails.

Compilation/runtime remain UNVERIFIED until CI. One no-key GitHub-hosted diagnostic attempt is planned after this review. Instrumentation overhead and events after cutoff remain limits; a diagnostic pass is not uninstrumented acceptance.

## Single producer diagnostic result

**Diagnostic evidence, not acceptance:** [run43577334463](https://github.com/vikas53953/bluey/actions/runs/43577334463) app/harness/workflow `7b0f4ed455672616fe763e45638e64802393123e`. Exact Q13H1/1 passed with assertions unchanged. TRX, identity, direct-read log, producer records, drain receipt and Happy screenshot inspected. Header confirms `broad=False producer=True`, QPCfrequency10MHz.

The acknowledged cutoff is38, written38, dropped0, writeFailures0, pending0, completeThroughCutofftrue, incompleteTailfalse. Future events are explicitly not covered. There is one Happy hold (generation1), one timed arm3 and no replacement Happy hold in this captured interval; arm4 stops with no remaining due moment after expiry.

| Producer phase | QPC interval | TickCount64 / value |
| --- | --- | --- |
| Hold origin clock anchor | 3543941200?3543941204 | now354359ms |
| Hold | 3543941208?3543941327 | deadline356859ms; Happy mask4 |
| Timer Start, arm3 | 3543941378?3543941457 | interval25000000 TimeSpan ticks =2500ms |
| Happy ItemStatus assignment | 3543941759?3543941879 | previousPaused8 ? Happy9 |
| Tick entry anchor | 3568939812?3568939831 | now356859ms |
| Expire | 3568939839?3568940152 | now356859ms; expiredHappy4 |
| Idle ItemStatus assignment | 3568940932?3568941144 | Happy9 ? Idle2 |
| Hidden ItemStatus assignment | 3579814578?3579814771 | Idle2 ? Hidden0; retained causal generation1 |

In this instrumented attempt, sampled expiry TickCount64 equals the stored deadline. Happy-to-Idle assignment duration bounds are2499.9053?2499.9385ms using the separate QPC clock. They measure publication, not rendering. Do not interpret coarse TickCount64 anchors as exact continuous time.

Observer entry[1337,1409], exit[3908,3949], duration[2499,2612]ms contains2500 and passes the unchanged compatibility gate. Deduplicated reads and events have the required forward order; the same durable session ends. Instrumentation changes the observed workload, and this is a different VM from the failing9feda80 attempt. No exclusive cause for that earlier2545?2674ms interval is established. No product repair, broader tolerance, uninstrumented acceptance, new capture or automatic retry follows. Issue111 stays open; attribution remains inconclusive for the original failure.
