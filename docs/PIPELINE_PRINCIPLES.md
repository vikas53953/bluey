# Boo pipeline principles

Set by Vikas on 14 Nov 2026. Every agent working on Boo, human or AI, reads this before starting work.
The detailed, enforced rules are in `HANDOFF.md` section 5 and `docs/quality/RULES.md`. This page is the "why"
behind them. When a rule and this page seem to disagree, ask; never pick the looser reading.

1. **Lock the inner loop before speeding up shipping.** Static analysis, type checks, tests, and formal checks
   (property tests, model checks) where a bug costs most. Automation must not produce slop.
2. **No fake greens.** Never weaken a test, an oracle or a gate to make CI pass. Never hide a failure with a skip,
   a retry or `continue-on-error`. Old green evidence never proves a new head SHA.
3. **Test what the user sees,** not implementation details. The end-to-end path is real: GitHub `windows-latest`,
   FlaUI on the published `Boo.exe`, and the cheapest real OpenAI model.
4. **Builder and checker are separate.** Claude builds; Codex verifies. No agent grades its own work.
5. **Every claim has a label:** CONFIRMED (with evidence), REPORTED, or UNVERIFIED. Every PR has an evidence table
   with run links (`scripts/quality/pr_evidence.py`, BOO-EVID-001).
6. **Encode lessons in structure.** When the same mistake happens twice, add an automatic check (a lint rule, a test
   or a CI gate) so it cannot happen a third time.
7. **Fix root causes, not symptoms.** Small, verifiable PRs, one concern each. Don't open a PR that touches files an
   open PR already changes.
8. **Phase gates are hard.** A phase's PRs merge only after the previous phase gate passed. Releases, tags, the
   installer, signing, auto-update and PR #3 (`lane/face` → `main`) are Vikas's alone.
9. **Never print keys** (names only), and never touch or capture Vikas's real screen or desktop.
10. **The pipeline learns.** Each loop's lessons go into `docs/DECISIONS.md` or `HANDOFF.md`.

## Limits

- OpenAI live-test spend: at most $10 a month, cheapest models. Near the cap: pause live tests and escalate.
- GitHub spend: $0 (public repo, hosted runners only).
- Spare agent capacity goes to quality, in this order: the full-app E2E (#54) honestly green; crash-and-reopen; the
  first real daily sentinel result; mutation testing and app exploring on merged code; turning repeated mistakes
  into automatic checks; then the next phase items. Spare capacity is never a reason to break a limit above.

## How merges happen

A PR into `lane/face` merges by GitHub auto-merge only when all required CI checks pass on its current head, it is up
to date with `lane/face`, and the Codex merge verifier has set the `codex-verified` status on that exact head after
checking gates 1 to 7 (QA PASS, all checks green, real AI round trip, red-proof for new checks, mutation report for
app code, Codex reviewer PASS, HANDOFF/DECISIONS updated). Every new head needs a fresh status.
