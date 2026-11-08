# Full-window shell build receipt

This receipt records the bounded native shell implementation in the clean
worktree at commit `2638356be7cc6d9de4538234cf24b759c1cfd21a`. It is build
evidence only; it does not claim native desktop acceptance.

## Implemented surface

- Startup initializes local settings, appearance, and selected-display state
  before the notch is shown. The idle launcher is an 8-DIP edge hit area; it
  physically expands on hover or active capture/microphone state without
  activating the external window.
- The idle layered hit strip has a minimal nonzero alpha and the open target
  uses a plain transparent control template, so transparent pixels do not pass
  pointer input through and hover does not paint a button chrome. The template
  uses a stretch hit border over the eyes/disclosure, and foreground capture
  ignores all Boo-process windows before retaining an external target.
- The launcher renders only a configurable body and eyes while idle/revealed.
  Activity disclosure is separate, readable, and driven by typed capture and
  microphone booleans. The existing display-affinity exclusion and emergency
  stop paths remain wired.
- The revealed face is centered over the fixed-width hit region, with the
  activity caption in a separate row whose height is measured from its text.
  Physical placement uses `DisplayGeometry.NotchBounds`, target-window
  `GetDpiForWindow`, and `SetWindowPos`; display/DPI notifications refresh the
  selected monitor metadata and notch position.
- Clicking opens a normal resizable Windows window. The first open is
  maximized; minimized reopen restores the prior normal/maximized state. The
  restored design size is 1120 by 780 DIPs, constrained to the working area,
  with independent conversation and focus/settings scroll regions.
- Settings expose labeled body/eye color fields, owned native color choosers,
  validation, save, and reset. A chooser stops active capture, microphone, and
  running focus monitoring first and does not resume them. Applied colors
  update only after a successful appearance-store save. Corrupt appearance
  data reports a bounded status message and uses a new default appearance.
- High-contrast resource colors are applied centrally by replacing frozen
  brush resources, and restored when the system setting changes. Dynamic
  brush bindings cover styles, inputs, panels, status text, and transcript
  rows; the opened-window header follows the saved body/eye appearance and
  switches to system colors in high contrast.
- Approval buttons retain the existing generation/target/focus guards and add
  `_Cancel` / `_Approve` access keys with matching tooltips.

## Checks run

| Check | Result |
| --- | --- |
| `scripts/build.ps1 -Action build -Configuration Release` | PASS; zero warnings and zero errors |
| `python scripts/test_oracle.py` | PASS; oracle revision 15, 41 files |
| `git diff --check` | PASS |
| Ownership check | PASS; only the six owned app files plus this receipt changed |

Full tests, packaging, and native Windows interaction were intentionally left
to the root integration lane. The worktree still contains the delegated
throwing Core/Storage stubs, so it is compile-only until the canonical Core
implementation is integrated. No hover, foreground, DPI, high-contrast
screenshot, color-dialog, capture-exclusion, or accessibility case is claimed
as passed by this build receipt.
