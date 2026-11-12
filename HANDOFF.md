# Handoff

Where things stand for the next person or agent. Update this file at the end of every work session.

## PR #12: Living ink notch (`feat/living-ink` → `lane/face`), 2026-11-12

**State:** open, not merged. Rebased onto `lane/face` after #10 and #11 merged. Head SHA, CI and live-smoke results
are in the PR's latest QA comment.

**Done in this session**
- Found and fixed the ~17 MB post-hover memory finding from the first review. Cause: Windows drawing the always-on
  notch on the graphics card. Fix: the notch alone draws in software. Evidence and numbers: `docs/DECISIONS.md`
  (D-2026-11-12-notch-software-drawing).
- New guard test `tests/Boo.Perf.Tests/NotchDrawingMemoryTests.cs` (seen failing before the fix, passing after).
- `BOO_PERF_DIAG` now also reports `notchRenderMode`.
- Oracle revision 20: four face tests now load face B by name (same assertions), two new test files frozen
  (`docs/verification/test-oracle-revision-20.md`). Before this, PR #12 failed the frozen-test gate that #10 added.

**Known, not caused by this PR**
- `ScopedFilesTests.Reparse_point_escapes_are_rejected` (FileSymlink, DirectorySymlink) fails on the dev PC because
  Windows refuses to create symbolic links without Developer Mode. Same failure on unchanged `lane/face`. CI runs it.
- GitHub artifact storage is full, so `live-ai-smoke` runs go red at the "Upload live evidence" step even when the
  AI round trip passes. Read the "Real AI round trip" step, not the run's overall colour, until storage frees up.

**Still open from the first review (low)**
- Built-in fallback face is still B (`NotchSpec.Default`).
- Dark-desktop rim follows the Windows app mode, not the wallpaper.
- Exact `#000000`/`#FFFFFF` colour choice is drawn as ink/cream.
- Wake-up first beats faint on dark desktops; eyes not drawn in the "eyes shut" beat.
- Window-level wake-up/celebrate rules not unit-tested (gallery and live runs only).

**Next**
- Vikas reviews the QA page, then decides whether to merge #12 into `lane/face`. Agents never merge.
