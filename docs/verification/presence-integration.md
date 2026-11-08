# Presence and saved appearance integration

Independent test author and builder used separate worktrees based on
`2638356be7cc6d9de4538234cf24b759c1cfd21a` for the builder. Root inspected the
actual three-file implementation diff and integrated exact bytes.

Behavior changed: pointer, capture and microphone activity jointly control
reveal state; appearance colors validate as opaque RGB with 3:1 eye/body
contrast; validated appearance loads and saves atomically in a separate file.

The builder first witnessed the frozen RED baseline (21 Core and 7 Storage
failures), then passed those same 28 tests unchanged. Root independently
witnessed RED before releasing the builder; see `presence-tests-red.md`.

Root verification after integration:

- Full Release tests: **239 passed, 1 existing reparse-fixture skip**.
- Core: 144 passed, 1 skipped; integration: 95 passed.
- TRX: `TestResults/vikasmit_VIKASMIT_2026-11-07_19_22_47_net10.0.trx` and
  `TestResults/vikasmit_VIKASMIT_2026-11-07_19_22_49_net10.0.trx`.
- Release app build: zero warnings/errors.
- Oracle: revision 15, 41 files verified unchanged.
- `git diff --check`: passed.

Commands: `scripts/build.ps1 -Action test -Configuration Release -NoRestore`,
`scripts/build.ps1 -Action build -Configuration Release -Project src/Boo.App/Boo.App.csproj -NoRestore`,
and `python scripts/test_oracle.py`.

This verifies the public behavior and persistence seams. Native rendering,
hover, hit testing, full-window layout and saved-color UI are not established
by these tests. App wiring, fresh code review and native QA remain separate.
