# Review checkpoint

Source: `7ef0e07cd4222a2933acb2da1ec53e12acf67d45`.

The CE simplification pass covered human-authored C# through `d181791`;
root applied the behavior-preserving changes in `7ef0e07`. This is the
`simplified_through` checkpoint. Later repairs still require review.

Release build: zero warnings/errors. Release tests: 84 passed, one unchanged
host-dependent reparse fixture skipped. Results are in
`TestResults/<user>_<machine>_2026-11-07_10_43_12_net10.0.trx` and
`TestResults/<user>_<machine>_2026-11-07_10_43_14_net10.0.trx`.
Oracle revision 3 verifies all 23 frozen files. `git diff --check` passed.
The format-only verification failed on pre-existing whitespace, including
frozen Contracts.cs. No frozen file was reformatted or weakened.

Candidate1 is still the older `d181791` payload. Its native QA receipt is
`native-driver.md`; independent review is `native-qa.md`. No native case has
passed. Sky observed the disposable fixture, but did not enumerate Boo's
notch or tray. Window style/exclusion flags are plausible explanations;
their individual effect was not isolated experimentally.

Repair work identified after this checkpoint:

- Bound microphone upload memory and queued work under a slow connection;
  stop capture with a readable error if the sender cannot keep up.
- Stop microphone/queued audio on request replacement or provider disconnect,
  and report unsuccessful Realtime completion honestly.
- Make annotation capture-exclusion failure visible before showing an overlay.

Other efficiency suggestions require measurement before changing lifecycle
semantics: D3D device reuse, transcript coalescing, bounded history reads,
and schema initialization caching. No performance improvement is claimed.

The current permission profile is unrestricted filesystem/network with
approval policy `never`. No user permission change is required. The root
Computer Use binding failed before JavaScript initialization, while a child
using the same installed runtime successfully enumerated windows. This does
not establish the underlying Codex fault or a product-permission defect.
