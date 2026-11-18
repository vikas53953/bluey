# Contributing to Boo (people and agents)

**Start with [HANDOFF.md](HANDOFF.md).** It explains what Boo is, how the code is laid out, how to build and test,
the branch model and the rules every change follows.

## Every PR must

1. **Write this PR's status row in its own file `docs/handoff/<pr number>.md`** (state, latest QA verdict with head
   SHA and run links), and update HANDOFF.md → "Current status" for any known issue it adds or closes and anything it
   moves into or out of UNVERIFIED. The PR table in HANDOFF.md itself is frozen history (BOO-DOCS-001).
2. **Write a decision row in `docs/decisions/<pr number>-<slug>.md`** for every decision the PR makes or changes:
   date, what, why, source, status (same columns as [docs/DECISIONS.md](docs/DECISIONS.md), whose table is frozen
   history). That includes decisions the owner gave in chat. Shapes: `docs/quality/RULES.md`, BOO-DOCS-001.
3. Follow the pipeline in [HANDOFF.md §5](HANDOFF.md#5-vikass-pipeline-rules-every-agent-follows):
   - branch off `origin/lane/face`, then PR into `lane/face`;
   - frozen tests change only through `scripts/test_oracle.py --extend` or a reviewed `--rebaseline`;
   - no skipped tests;
   - a separate fresh reviewer;
   - a real-key live run where AI is involved;
   - an evidence table marking each row mock / live / manual / UNVERIFIED;
   - honest red when evidence cannot upload;
   - never capture the owner's real screen;
   - never print a key.
4. Never merge, tag or release, and never push to `lane/face` or `main`. Those stay with Vikas.
