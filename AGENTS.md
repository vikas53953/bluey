# Agent instructions for Boo (vikas53953/bluey)

Boo is a .NET 10 / WPF Windows app: a hidden top-centre notch with eyes that wakes on hover or Ctrl+Alt+B, with typed
and voice chat, screen explain (pointer + ring), research with sources, permissioned actions with Undo, and focus
sessions. These rules apply to every agent working in this repository.

## Prove it works (non-negotiable)

**No feature or fix is "done", "fixed" or "working" without a LIVE run with evidence.**

1. **Live** = the published `Boo.exe`, driven like a user (FlaUI), nothing faked; a real AI provider with a real key
   for anything that uses AI. Mock runs (demo-ci, `tools/Boo.QaMockAi`) and unit tests are labelled **mock**: they are
   required gates, but never the proof that a user-facing AI feature works.
2. **Evidence** = screenshots + logs + a run link (or, for a local run, `artifacts/verify/<time>/summary.md` and the
   commit SHA). Look at the screenshots before claiming anything.
3. Every claim in a report or PR body is labelled **live / mock / manual / UNVERIFIED**. No run link = UNVERIFIED.
4. If the live run cannot happen (no key, no runner), report **BLOCKED** in plain words. Never green.
5. How: the `verify-boo` skill (`.claude/skills/verify-boo/SKILL.md`). What proves each feature:
   `docs/FEATURE_MAP.md`. When a feature changes, its map row and live journey change in the same PR, and
   `.claude/skills/verify-boo/scripts/Test-FeatureMap.ps1` must pass.

## Tests and gates

- `tests/**` and `docs/verification/test-oracle.json` are the **frozen test oracle**. Never edit, weaken, skip or
  delete a frozen test to get green. New live checks go in `tools/Boo.VerifyLive` (it links the existing harness).
- `windows-ci` and `demo-ci` must be green on the PR head. `scripts/ci/assert-tests-ran.ps1` rejects empty or skipped
  runs; do not bypass it.
- The real-key round trip is the manual `live-ai-smoke` workflow (`-f journeys=true` for every live journey).

## Branches, keys and safety

- Work on a branch and open a PR. Never push to `main` or `lane/face`, never merge, tag or release unless the owner
  explicitly says so for that change.
- Keys live only in repository secrets (`BOO_LIVE_*_KEY`) or Boo's own DPAPI files. Never print, log, commit or
  screenshot a key; logs record lengths only. Do not touch signing secrets.
- Boo is single-instance: quit any running Boo before a native run, and do not use the PC's mouse or keyboard while
  FlaUI drives it.

## Writing for the owner

Plain words, no jargon; short. Anything he must review or approve is a visual page, not a wall of text.
