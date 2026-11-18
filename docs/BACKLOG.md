# Boo backlog: six small sprints to v1

Set by Vikas on 17 Nov 2026 (8:19–8:50 PM IST). Boo is built in small batches (sprints). **Only the current sprint is
worked on**; everything else is frozen until that sprint is done. Feature ids are the rows in
[FEATURE_MAP.md](FEATURE_MAP.md).

**Current sprint: S1 (MVP).** Its test script: [verification/batch1-test-script.md](verification/batch1-test-script.md).

| Sprint | What it delivers | Feature ids | State |
| --- | --- | --- | --- |
| **S1 MVP** | Typing, hold-to-talk voice, screen pointer, Stop, plus what you need to use them: install, OpenAI key, the notch | T1 typed chat; T3 hold Ctrl+Alt+Space; T6 spoken replies; E1 screen explain with pointer and ring; T10 Stop clears "Thinking…"; N7 right-click notch = Stop; S1 key saved safely; N1 notch wakes on hover; N2 Ctrl+Alt+B opens Boo; install (Velopack) | **current**. Picks confirmed by Vikas 17 Nov, 8:40 PM IST. Voice bugs #179 and #177 are fixed at the root before he gets a test build. |
| S2 Research | Answers with source links | R1–R3 | frozen |
| S3 Actions and Undo | Permissioned actions, each with its own Undo | A1–A9 (#66 belongs here) | frozen |
| S4 Focus | Focus sessions | F1–F14 | frozen |
| S5 Polish | Notch, settings and display polish, DPI, performance | N\*, S\*, X\* rows not in S1; DPI; perf | frozen |
| S6 Release | Signed installer, tag and release | Vikas-only steps (tag, release, signing, PR #3) | frozen |

Parked, outside every sprint until Vikas says otherwise: #184 / #111 (Happy timing ±100 ms), #78 (0.3 s first-open
stall), #28.

## When a sprint is done

A sprint is done only when **both** are true:

1. **Real live-AI tests pass** for every feature in the sprint: the `live-ai-smoke` / `native-voice-e2e` runs against
   real OpenAI, with run links. Mock runs are required gates but never the proof.
2. **Vikas's own test script passes on his PC**: a short plain-English script (10–15 steps) he runs himself as an end
   user, on a private test build.

Nothing from the next sprint starts before that.

## Rules for every sprint

- **Freeze outside the sprint.** Work outside the current sprint is diagnose-only or declined. That includes new
  pipeline or CI tooling.
- **Design first, code small.** Before code: an architecture sketch and the feature-map rows for the sprint. Then small
  PRs, each proven live.
- **Small batches.** A batch is 3–4 features; Vikas confirms the picks before anything is built for them. Never a
  large chunk of code up front.

## When v1 is done

v1.0 is done when Vikas has **used it every day for one week with no major bug**. Claude and Gemini as text providers,
and any new ideas, go to **v1.1**.
