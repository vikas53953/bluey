# U5 verification: research sources and edit -> Apply -> Undo

Date: 8 November 2026. Contract: [plan](../plans/2026-11-08-u5-research-edit-undo.md). Test freeze: [u5-test-freeze.json](u5-test-freeze.json).

## Process
1. A test author wrote 227 Core cases (RequestIntent, ResearchSources, ActionReadback, UndoJournal, including a QA-fixture edit -> Apply -> Undo model) and 7 provider request-shape tests, frozen by SHA-256 before integration.
2. A separate builder implemented the contract without seeing the tests.
3. An independent reviewer found no approval bypass, then 2 high and 3 medium defects (Undo re-clicking a non-idempotent button, false "restored" after an unconfirmed apply, stale readback baseline, JSON instructions on plain research, voice undo not local). All fixed; a re-review found 3 more (voice undo racing a model tool call, unrecorded typing, partial apply readback). All fixed. Frozen tests unchanged.

## Evidence
- Linux: full solution Release build, 0 warnings; Core tests 535 passed, 1 pre-existing host-dependent skip.
- windows-latest CI (build, Core, Integration, publish): green on the first commit; later commits see PR #6.

## Not proven
- No native run yet: the live web-search answer with links, and the QA-fixture demo (type "Sign up", Apply, read back, say "undo", read back "Create account") still need a run on the packaged build.
- Core `UndoJournal.Plan` still allows a re-apply plan with only an apply recorded; the app never records an apply without prior Boo edits, so this path is unreachable from the app.
