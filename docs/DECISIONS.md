# Decisions

One entry per decision: what was decided, why, the evidence, and what would make us revisit it. Newest first.

## D-2026-11-12-notch-software-drawing: the notch window draws in software

**Decision.** The notch window (`NotchWindow`) tells Windows to draw it in software
(`HwndSource.CompositionTarget.RenderMode = SoftwareOnly`, set in `OnSourceInitialized`). Only the notch: the panel
and the annotation overlay still draw on the graphics card.

**Why.** PR #12 review found the Living ink build held ~17 MB more memory than lane/face after one hover. That gap
exists only on the graphics-card drawing path for the notch, a small see-through window that stays on screen all day.
The second reviewer applied the same fix to plain lane/face: in software both builds land within ~2 MB of each other
(73-77 MB after hover). Why Living ink costs more on the card path was not found; with the card path gone it no
longer matters for the notch. Measured on the dev PC (Windows 11, graphics card present), official perf test,
private memory:

| Build | Idle (never hovered) | After one hover |
|---|---:|---:|
| lane/face, graphics card | 92–108 MB | 127–129 MB |
| Living ink, graphics card | 92–94 MB | 137–144 MB |
| Living ink, software (this decision) | 58–62 MB | 75–76 MB |

How it was found, by elimination (two runs each): turning off the wake-up, its fading, its shape changes, the eye
clip, the loose-ink layer, the shine's see-through, the wider window, or forcing a .NET cleanup after hiding changed
nothing. Software drawing removed all of it, for the idle cost as well as the hover cost.

What it costs: nothing measured. Frames while the face is out: ~51–53 fps both ways. CPU while the face is out and
moving: 26.0 % vs 26.8 % of one core on average (6 samples each, noisy). Hidden CPU 0 % both ways. Threads 44 → 19.
Startup was not slower (1.7–3.8 s, within the old 1.6–11.7 s spread). The face looks the same (screenshots compared;
differences only where the drifting pupils sit).

Side effect: the frozen perf budgets `idlePrivateBytesMb <= 80` and `idleWorkingSetMb <= 160`, which failed on this
PC for every build, now pass (60 MB, 128–130 MB).

Not measured: CPU on a 4K / 200 % or high-refresh screen (software cost grows with pixels), and memory after the
panel has been opened (the panel keeps the graphics card).

**Guard.** `tests/Boo.Perf.Tests/NotchDrawingMemoryTests.cs`: the running notch must report `SoftwareOnly` through
`BOO_PERF_DIAG` (field `notchRenderMode`), and one hover may add at most 30 MB of private memory. The mode check
matters most: CI's virtual machine has no graphics card, so there memory alone cannot tell the two modes apart.
On the old builds the test fails: +30.6 to +46.6 MB pre-fix Living ink, +34 to +36 MB lane/face, mode not reported.
It has not run in CI yet (see HANDOFF.md, artifact storage).

**Revisit if.** The notch ever needs effects that are slow in software (blur, drop shadows, large bitmaps), or a
PC shows the face below ~30 fps. Measure again with the perf suite on a real PC before changing it.

## D-2026-11-12-oracle-revision-20: re-freeze the face tests for Living ink

**Decision.** Accept the PR #12 edits to four frozen face tests and freeze two new test files (oracle revision 20).

**Why.** The shipped spec now chooses face D. The face B tests keep every assertion and now load the shipped spec with
face B selected. Tests and assertion counts did not drop. Full per-file record: `docs/verification/test-oracle-revision-20.md`.
