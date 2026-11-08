# Surface readback review

## Plan review

Independent Advisor returned PLAN_APPROVED on 2026-11-07 for
`2026-11-07-native-surface-readback-repair.md` against source `f82c17d`.

The review confirmed the documented HRESULT/REFIID/out-pointer ABI and the
separate ownership of the GetRef pointer, queried interface, and texture.
Implementation must report non-projected surfaces as a readable limitation,
release nonzero output pointers even on failure, and preserve the existing
texture validation, readback, and JPEG path. Device.Close and factory lifetime
remain separate work.

Candidate7's native frame-readback failure is the RED observation. Technical
approval establishes a sound repair hypothesis; it does not establish capture
success.

## Implementation review and root verification

A separate code reviewer inspected the integrated source hash
`161D7DE5A36D75958867C1BA9CE481B53DD6BBA9AE5FF33837EE41DFFB502839`
and found no actionable ABI or ownership defect in the scoped diff. The review
confirmed the native unwrap guard, slot 3 StdCall signature, failed-output
cleanup, and one cleanup owner per acquired reference. Device and factory
lifetime were not changed.

Root independently ran the Release capture-filter suite: 14 passed, zero
failed. Full Release build passed with zero warnings/errors. Oracle revision 8
verified all 30 frozen files unchanged, and `git diff --check` passed. These
checks do not exercise an actual projected WGC surface. Candidate8 native
verification remains required before the original failure is marked repaired.

## Candidate8 native result

The next exact package, source `7183f52`, reached `Screen sharing on` after
the same verified request. That indicator is set only after CaptureAsync
returns its frame; the former readback InvalidCastException did not recur.
This is narrow evidence that the readback path progressed successfully, not
proof of image quality or correct marks. The provider stage produced no reply
within the bounded observation and reported Stopped before QA sent Stop.
The independent receipt is `native-teaching-candidate8.md`. Teaching remains
unaccepted; response cancellation reporting is a separate repair.
