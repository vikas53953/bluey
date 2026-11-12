# Test-oracle revision 20: reviewed rebaseline for Living ink (2026-11-12)

PR #12 (Living ink notch) was written before PR #10 put the oracle into CI. Rebased onto `lane/face` (now with #10),
the oracle reports 4 frozen files changed and 2 new test files. This note records the decision for each one before
`scripts/test_oracle.py --rebaseline`. The independent PR reviewer re-checks this note against the diff.

## Why the frozen files changed

The shipped notch spec (`docs/design/boo-notch-spec.json`) now chooses face **D (Living ink)** instead of face B.
The frozen tests pin face B's exact numbers by loading "the shipped spec". To keep every one of those B assertions,
they now load the same shipped spec with face B selected (`FaceSpecFixture.LoadFaceB()` =
`Load().WithChosenFace("B")`). Only the test that names the shipped face changes its expected value.

| File | Tests before → after | Asserts before → after | What changed | Decision |
|---|---|---|---|---|
| `tests/Boo.Core.Tests/FaceSpecTests.cs` | 19 → 19 | 58 → 59 | `Shipped_spec_chooses_face_B_button_eyes` became `Shipped_spec_chooses_face_D_living_ink` (asserts key D, name "Living ink", eye kind "ink", faces A–D: one more assert). B tests load `LoadFaceB()`. `WithChosenFace` test: the untouched original is now "D". `Default_equals_shipped_face_B` compares the fallback with shipped face B | ACCEPT: deliberate spec change, nothing weakened |
| `tests/Boo.Core.Tests/FaceMotionTests.cs` | 49 → 49 | 183 → 183 | `Load()` → `LoadFaceB()` (1 line) | ACCEPT: same assertions on the same face |
| `tests/Boo.Core.Tests/NotchFaceGeometryTests.cs` | 7 → 7 | 19 → 19 | `Load()` → `LoadFaceB()` (1 line) | ACCEPT |
| `tests/Boo.Core.Tests/NotchLayoutTests.cs` | 16 → 16 | 62 → 62 | `Load()` → `LoadFaceB()` (5 lines) | ACCEPT |

No `[Fact]`/`[Theory]` removed, no `Skip =` added (the one `Skip` text in FaceMotionTests is LINQ `.Skip(1)`,
present before), no assertion loosened.

## New files frozen

| File | Tests | What it pins | Decision |
|---|---|---|---|
| `tests/Boo.Core.Tests/LivingInkTests.cs` | 32 (132 asserts) | Living ink maths: blot stays in its box, eyes and caption on the ink, morph cancel and snap, wake-up order and cancel, celebrate, blink timing, reduced motion | ACCEPT |
| `tests/Boo.Perf.Tests/NotchDrawingMemoryTests.cs` | 1 | The notch reports `SoftwareOnly` drawing through `BOO_PERF_DIAG`, and one hover adds at most 30 MB of private memory (see `docs/DECISIONS.md`, D-2026-11-12-notch-software-drawing). Written first and seen failing on the pre-fix build (mode not reported, +30.6 MB) | ACCEPT |

The frozen perf budget files (`BooPerfBudgetTests.cs`, `budgets.json`, `PerfAppFixture.cs`, ...) are untouched.
