# Contributing to Boo (people and agents)

**Start with [HANDOFF.md](HANDOFF.md).** It explains what Boo is, how the code is laid out, how to build and test,
the branch model and the rules every change follows.

## Every PR must

1. **Update [HANDOFF.md](HANDOFF.md) → "Current status"**: this PR's row (state, latest QA verdict with head SHA and
   run links), any known issue it adds or closes, and anything it moves into or out of UNVERIFIED. Update the
   "Status last updated" line.
2. **Add a row to [docs/DECISIONS.md](docs/DECISIONS.md)** for every decision the PR makes or changes: date, what,
   why, source. That includes decisions the owner gave in chat.
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
