# Geometry regression RED evidence

This additive oracle was authored in the capture worktree before the
`MapToDisplay` repair. It does not change the original frozen geometry tests.

## Test source

- Worktree: `C:\Users\vikasmit\.codex\worktrees\boo-capture-geometry\blue--fo`
- Test: `tests/Boo.Core.Tests/GeometryRegressionTests.cs`
- Production seam under test: `src/Boo.Core/AnnotationValidator.cs`
- Command:

  ```powershell
  .\scripts\build.ps1 -Action test -Project tests/Boo.Core.Tests -Filter 'FullyQualifiedName~GeometryRegressionTests'
  ```

## RED result

Run date: 2026-11-07 (Asia/Calcutta)

| Result | Count |
| --- | ---: |
| Failed | 2 |
| Passed | 0 |
| Skipped | 0 |
| Total | 2 |

The command exited with code `1`. The test receipt is:
`C:\Users\vikasmit\.codex\worktrees\boo-capture-geometry\blue--fo\TestResults\vikasmit_VIKASMIT_2026-11-07_10_10_12_net10.0.trx`.

### Failed cases

1. `Full_resolution_frame_preserves_physical_coordinates_at_any_dpi_scale`
   - Display bounds: `(-1920,-200,1920,1080)`; frame: `1920×1080`; DPI scale: `1.5`.
   - Expected physical mapping: `(100,50,200,100)` → `(-1820,-150,200,100)`.
   - Observed: `(-1770,-125,300,150)`.
2. `Downsampled_frame_uses_frame_to_display_ratio_independent_of_dpi_scale`
   - Display bounds: `(-1920,-200,1920,1080)`; frame: `960×540`; DPI scale: `1.25`.
   - Expected physical mapping: `(100,50,200,100)` → `(-1720,-100,400,200)`.
   - Observed: `(-1795,-137.5,250,125)`.

The expected values use the frame-to-display ratios (1.0 and 2.0), preserve
the negative display origin, and do not apply DPI scale to already-physical
capture coordinates.

## SHA-256 manifest

| File | SHA-256 |
| --- | --- |
| `src/Boo.Core/AnnotationValidator.cs` | `E9F419D538F1A091459C940D03D98C2AE137B053931D36B4167A61F344B0A4F0` |
| `tests/Boo.Core.Tests/GeometryRegressionTests.cs` | `58F94F92EF06C80D81C696EB518252E11AA9E8E92D82C54F51B1D4016E5F89FD` |

This receipt is RED evidence for the pre-fix capture worktree. Re-run the same
filtered command after the worker repair and retain both receipts.
