# Living ink — builder evidence (12 Nov 2026)

Branch `feat/living-ink` (from `lane/face` a3bdf20). Brief: `%USERPROFILE%\bluey-design-assets\living-ink\LIVING_INK_SPEC.md` (outside the repo).
This page is the **builder's** record. The verdict belongs to the separate reviewer (see the PR).

## What changed (one line each)

| File | Role |
| --- | --- |
| `src/Boo.Core/LivingInk.cs` | New. The look as testable numbers: blot outlines per mood (morph point by point), eye poses, morph, wake-up, celebrate, lid blink, palette, layout. |
| `src/Boo.App/NotchWindow.xaml(.cs)` | Draws the blot, eyes, lids, shine, happy arcs, blooms, drip, rising dots, splatters, speaking bars; light/dark rim; reduced motion. |
| `src/Boo.App/NotchGallery.cs` | Gallery adds dark-theme, wake-up and celebrate stills (21 PNGs). |
| `docs/design/boo-notch-spec.json` | New face `D` "Living ink" (sizes); `chosen.face` = `D`. Faces A/B/C unchanged. Hover/retract timings unchanged. |
| `tests/Boo.Core.Tests/LivingInkTests.cs` | New behaviour tests (Core count 1682 → 1752). |
| `tests/Boo.Core.Tests/{FaceSpec,FaceMotion,NotchLayout,NotchFaceGeometry}Tests.cs` | These pinned "the chosen face" to face B's numbers. They now load face B explicitly (`FaceSpecFixture.LoadFaceB()`), same assertions; `Shipped_spec_chooses_face_B_button_eyes` became `..._D_living_ink`. None of these files is in the frozen oracle (`docs/verification/test-oracle.json`); no frozen file was touched. |

No token file was added: the five Living ink colours live in `LivingInkPalette` (code), because only the notch uses them.

## Evidence the builder collected

Live runs drove the **published** `artifacts/ci/Boo.exe` with a driver outside the repo (mouse moves, hotkeys, panel buttons through UI Automation) over a plain full-screen test backdrop, so screenshots and the screen sent to OpenAI hold only test content. The real OpenAI key came from Boo's own DPAPI-encrypted `credential.bin` already on this PC, copied into a throw-away data folder and deleted after each run; the key was never read, printed or logged.

| Check | How | Result |
| --- | --- | --- |
| Build (Release, whole solution) | local, this PC | 0 warnings, 0 errors |
| Core / Integration / Providers tests | local | 1752 pass (+1 skip that already existed) / 148 / 115 |
| UI smoke (FlaUI, real Boo.exe) | local | 15/15 on rerun; first run had 1 UI Automation timeout in step 5 (panel "no key" step, not the notch) |
| Perf budgets (real Boo.exe) | local, A/B against unchanged lane/face build on the same PC | Both builds fail `idlePrivateBytesMb` (≤80) on this PC (lane/face 92–108 MB, Living ink 93.5 MB); Living ink also 160.6–162.6 MB vs ≤160 working set (lane/face 159.2–179.6). CPU, allocations, Gen0, frame-rate budgets pass. GitHub runner measured lane/face at 102 MB / 33 MB. |
| Live AI round trip, typed | live, real key | "Explain what is on my screen": notch Capture → Thinking → Responding → Hover; answer described the test backdrop ("Rule book - Q4 review" …); status "Reply complete" |
| Live AI round trip, voice | live, real key, hold the eyes 2.2 s | Listening → Thinking → Responding (spoken reply) |
| Wake-up | live 150 % burst (25 ms) | plays on first hover, ~450 ms from first ink to settled; `live-wakeup-150.gif` |
| Blink | live 150 % burst | blinks at 2.35 s and 6.66 s (4.3 s apart, ~0.2 s each); `live-blink-150.gif` |
| Celebrate | live 150 %, 1-minute focus session in Developer Mode | squash + splatters, faded by ~1 s, smile rests; "Session complete"; `live-celebrate-150.gif` |
| Paused (sleepy) | live 150 % | heavy lids |
| Light and dark theme | live, Windows app mode flipped at 100/125/150/200 % and restored | blot stays ink, eyes cream; dark adds only the faint rim; `live-dpi-theme.png` |
| 100 / 125 / 150 / 200 % scaling | live, display scale switched and restored to 200 % | shape and eyes crisp at each scale |
| Animation effects off | live 150 % | pocket appears whole on the first frame (no wake-up), no blink in 6.5 s; `live-reduced-motion-150.gif` |
| Idle (pointer just left) | live 200 % | Idle face, then Hidden ~1.1 s after leaving (existing 900 + 160 ms) |
| Gallery | `Boo.exe --notch-gallery` locally | 21 PNGs; `gallery-contact-sheet.png` |
| Nudge (alert, red pupils) | gallery only | **UNVERIFIED live**: the live focus classifier did not flag off-task in the short session |
| Hold-to-talk by keyboard (Ctrl+Alt+Space) | — | **UNVERIFIED live**: Boo ignores injected key presses for this hook on purpose, so automation cannot press it; the eye-hold path was used instead |
| Ctrl+Alt+B | live | opens the panel; the notch stays hidden (same as before this change) |

The thin yellow line at the very top of the live screenshots is Windows' screen-capture border from another app on this PC, not Boo.

## Known limits, said plainly

- Built-in fallback (spec file missing or broken) is still face B's sizes, drawn in the ink style.
- Wake-up replays only on Boo's first appearance and after 5 minutes hidden, never on a quick re-hover.
- A user-chosen notch colour pair (Settings) is kept; only the untouched default (#000000/#FFFFFF) draws as ink + cream.
- Speaking bars follow Boo's existing talk level (synthetic), not the real audio amplitude.
