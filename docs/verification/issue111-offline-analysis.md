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
