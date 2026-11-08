# Open review findings

These findings block acceptance until repaired or disproved with evidence.
They are source-review results, not native failure reproductions. See
`shell-defects.md` and `windows-defects.md` for the original evidence.

## Repair batches remaining

1. Native capture and target handoff: deterministic capture lifetime repairs
   are integrated and independently tested. Candidate6 confirmed the narrow
   controls-reopen target refresh repair. Candidate7 identified a readback
   InvalidCastException (0x80004002); the reviewed surface projection repair
   in candidate8 progressed to sharing active without that failure. No provider
   reply followed within that bounded observation. Candidate9 subsequently
   returned a real screen-context explanation; full teaching remains unpassed.
2. Target lifetime: clear retained identity on inaccessible foreground changes;
   invalidate observations, marks, and approvals after foreground, scroll,
   geometry, display, or DPI changes, including away-and-back transitions.
3. Concrete actions: full immutable previews and shared canonical file resolver
   are integrated. Require fresh visible UIA bounds; add post-effect readback for click
   chains; close filesystem containment races with held native identities.
   The observed-control handoff is implemented; candidate11 returned exact
   observed IDs and values in its immutable typing proposal. Candidate13 later
   performed an approved synthetic field edit, with independent UIA readback
   matching the requested value. That proves a field mutation on candidate13,
   not the complete Apply/Undo demo or a pass on newer packages. The automatic
   preview-focus handoff remains unaccepted; keyboard-control experiments also
   failed in the fixture, so a Boo-specific cause is not established.
   A screenshot alone does not supply the accessibility IDs DesktopTools expects. Also
   repair post-dispatch exception handling: Invoke/SetValue helper failures can
   currently enter input fallback after the first effect may have occurred.
   Preserve an honest uncertain result instead of risking a duplicate effect.
   Natural task detection currently recognizes only a small prefix list;
   ordinary requests such as "change this" still need intent handling.
4. Shell state: per-operation capture/send indicators and independent
   annotation outcome text are integrated and reviewed at `4df299b`.
   Candidate10 showed off after reply and after Stop. Candidate11's confirmed
   corrected-input request showed sharing on after submission and off after
   Stop. This supplies a basic active-interval observation, while detailed
   timing/overlap/cancellation proof remains open. Selected-monitor DPI and
   first-open target priming were revised at `013be73`; `1493d18` adds live
   fallback after selected-monitor removal. Native proof of these changes
   remains open.
   Transcript identity and
   completion replacement are repaired at `b84f20e` with independent tests and
   source review; native voice/transcript acceptance remains open.
5. Native resource/action lifetime: deterministic ownership release where
   supported and bounded native/file operations that preserve honest partial
   outcomes and Stop behavior.
   Native-read concurrency now uses a process-shared coordinator, with eight
   independent frozen tests, root Release checks and fresh source review.
   Packaged native verification is pending. The coordinator retains ownership
   until the worker task settles; a blocked UIA provider call is still not
   interruptible. Direct native verification of password/off-screen fields
   avoiding value reads also remains open.
6. Response cancellation: candidate8 reported Stopped before the operator sent
   Stop. The test-first deadline/classification repair is now integrated and
   independently reviewed, including response-body deadlines and safe UI
   messages. Exact packaged verification and provider latency remain unproven.
7. Voice task authority needs a separate ordering review: MainWindow stores
   `_voiceTaskAuthorized` for the whole connection and reads it for each
   screen-help call, without item/response identity binding. Input transcription
   completion can arrive before or after response events according to the
   provider contract. Check stale authorization across turns and premature
   rejection before transcription completes. This is a source-level concern,
   not a demonstrated unauthorized native action; consequential-action approval
   still applies. The transcript display repair deliberately did not change it.
8. Annotation outcome visibility: the overwrite defect is repaired at
   `4df299b` with a separate accessible result, independently reviewed.
   Candidate10 retained the app's reported marks-shown result through generic
   completion, then cleared it on Stop. Ring geometry and click-through remain
   unproven. Candidate9 returned a real
   screen-context reply within the first retained 11.65-second observation,
   but no ring evidence was available. Absence of native overlay
   evidence does not establish whether the model supplied marks or validation
   rejected them. See `native-teaching-candidate9.md`.

The audio queue and voice teardown batch is integrated and independently
reviewed; native voice hardware remains unproven. Annotation exclusion failure is repaired in `bcac80f` but
native failure injection is still unproven. Stale research links are cleared
at request start in `c257c5f`; native source display remains untested.

## Triage constraints

- The concurrent-affinity report is conditional: the current App sets its
  notch/panel/annotation affinity permanently to exclusion, so restoring the
  same value does not itself demonstrate a recursive-frame leak in this app.
  The public capture service now serializes full backend lifetimes through
  cleanup. Do not present a hypothetical leak as a
  reproduced one.
- Frozen RequestCoordinator test RC-004 requires an entered effect to finish
  before Stop returns. Do not silently weaken that test or move effects outside
  the existing serialized contract. Bounded action execution needs an explicit
  design that separates already-started native work and future effects while
  retaining honest partial outcomes.
- COM release changes must respect C#/WinRT ownership; do not introduce raw
  Release/FinalRelease calls based only on reference-count guesses. Verify the
  projection's documented lifetime behavior and measure native resources.
- Do not change capture privacy or fabricate UI handles to obtain native QA.
  Candidate3 and candidate4 expanded controls are accessible through the
  supported Sky keyboard/text route. Native screenshot geometry for excluded
  Boo windows remains unavailable; exact notch/visual acceptance is unproven.
- Exact candidate/native, human speech, latency, multi-display/DPI, and soak
  checks remain required. Deterministic success does not clear those cases.

## Review decisions after capture and approval integration

- The independent integration review found no actionable defect in the scoped
  capture-lifetime and exact-preview changes; native/platform limits above stay
  open. See `capture-approval-integration-review.md`.
- Test-quality review found an implementation-derived file-size fixture. Two
  additional independent tests now assert the literal 4,194,304-byte UTF-8
  boundary while preserving the earlier frozen file. Oracle revision8 records
  the addition; the full Release suite has 120 passes and one unchanged skip.
- The proposed 4,801-byte audio-boundary defect is rejected: the existing
  public contract requires complete PCM16 samples, so odd lengths are rejected
  before the size check. Raising the limit from 4,800 to 4,801 would not admit a
  new valid frame; 4,802 is the first valid-length oversized input.
- A proposed wake-signal rewrite is deferred: queued semaphore permits can
  cause extra no-op iterations, but no measured application regression was
  supplied. Replacing synchronization during a behavior-preserving pass adds
  lifecycle risk. Buffer bounds and single-writer behavior remain protected.
- Playback/recording helper splitting was not applied: the current nullable
  upload queue expresses the actual capture-versus-playback ownership, and
  splitting it would duplicate failure/identity guards without a proven gain.
