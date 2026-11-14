# Native shell integration receipt

The separate native-shell builder implemented the pocket notch, interactive
panel, and annotation window in its managed worktree. Root inspected the
changes and integrated only the eight App XAML/code files. Existing project
manifests, assets, public contracts, and frozen tests were not replaced.

The integration connects focus/history, typed and realtime conversation,
screen capture/marks, source links, microphone and folder selection, concrete
action previews, emergency Stop, tray recovery, and lock/sleep handling. Code
review during authoring corrected raw Realtime event names, stale tool events,
pending target identity, second-instance mutex release, initial target capture,
and frame-to-physical-to-DIP mapping. These fixes are not native UX evidence.

Root's canonical Release solution build completed with zero warnings/errors.
The Release tests passed 62 core cases and 22 integration cases; one explicit
host-dependent reparse fixture remains skipped. Receipts:

- `TestResults/<user>_<machine>_2026-11-07_10_31_03_net10.0.trx`
- `TestResults/<user>_<machine>_2026-11-07_10_31_05_net10.0.trx`

Oracle revision 3 verifies 23 files. The full native matrix, live app demos,
accessibility, real microphone/speaker behavior, performance distributions,
and exact packaged soak remain pending. No acceptance or delivery is claimed.
