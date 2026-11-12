# HeyClicky presence observation — 2026-11-07

Read-only inspection through the supported Sky API only. No settings,
messages, microphone, credentials, or alternate UI automation were used.

## Observed

- `sky.list_apps()` returned the installed/running app:
  - app ID: `velopack.HeyClickyApp`
  - display name: `HeyClicky`
  - returned window ID: `68530`
  - title: `HeyClicky`
  - app path: `%USERPROFILE%\AppData\Local\HeyClickyApp\current\HeyClicky.exe`
- `sky.list_windows()` also returned only that HeyClicky window.
- `sky.get_window_state(...)` returned the supported runtime error:
  `window is minimized; call activate_window, refresh with get_window, then retry get_window_state`.
- `sky.activate_window(...)`, followed by `get_window`, did not make the
  window readable. Launching the already returned app by its exact returned
  app ID also left the same window minimized; refreshed enumeration still
  returned only window `68530`.
- No screenshot, accessibility tree, or text was returned, so no screenshot
  file/path is available and no visual claim is made.

## Not established

The supported Sky surface exposed no hover method. Because the window remained
minimized and no screenshot/tree was available, this inspection did not
establish HeyClicky's no-hover/resting mini state, hover-reveal state, click
behavior, full desktop window behavior, auto-hide timing, or color treatment.
No mouse movement, click, drag, guessed coordinate, or alternate UIA was used
to simulate hover or reveal.

## Official product reference

The [HeyClicky changelog](https://www.heyclicky.com/changelog), read on
7 November 2026, documents a small hover peek that tucks back when the pointer
leaves, and a separate resizable window for chat, documents and settings.
These support the interaction direction. They do not establish the exact
appearance, dimensions, timing or Windows-version behavior of this installation.

## Product requirements carried forward (not observations)

The current request requires the collapsed top-center mini to show no
Boo/profile text, use a black body with white eyes and customizable colors,
open the full desktop app when clicked, and auto-hide/reveal on hover like the
installed HeyClicky reference. These remain requirements rather than verified
reference behavior until a supported readable HeyClicky state is available.
