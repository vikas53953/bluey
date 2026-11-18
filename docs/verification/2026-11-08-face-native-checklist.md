# Face lane (U1 notch presence + U2 living eyes): Windows native QA checklist

Date: 2026-11-08. Applies to branch `lane/face`. Spec: `docs/design/boo-notch-spec.json`, `chosen.face` = B ("Button eyes").
Logic is covered by the frozen Boo.Core tests (`FaceSpecTests`, `FaceStateResolverTests`, `GazeSolverTests`, `GazeSpringTests`,
`BlinkSchedulerTests`, `NotchLayoutTests`, `NotchRevealTimelineTests`). Everything below can only be checked on a real Windows 10/11 machine.
Record each case as PASS / FAIL / NOT RUN with the build number, display setup and a screenshot or recording. Do not mark a case PASS from
the Linux container: there it is "compiled, not verified on Windows".

Setup: primary display 1920x1080, a second display to the right (and, for case 3, one to the left), Windows Display Settings scaling switchable
between 100%, 125% and 150%. Use a screen ruler (for example PowerToys Screen Ruler) for pixel checks and Task Manager / `Get-Counter` for CPU.

## Eyes (U2)

1. **Gaze, four corners (automated + visual).** Move the cursor to each corner of the primary display (0,0), (1919,0), (0,1079), (1919,1079), pausing 1 s at each. Both pupils move toward the cursor and rest on the edge of their 3.5 px travel (pupil never leaves the 16 px white eye). Automated: drive the cursor with `SetCursorPos` and read the pupil offset from the UI Automation / debug overlay; expected magnitude 3.5 px +/- 0.5 px.
2. **Gaze, centre.** Hover the cursor directly over the eye centre: pupils centre (offset 0 +/- 0.5 px), no jitter.
3. **Gaze, second monitor.** Move the cursor to the far right of a monitor on the right and to the far left of a monitor on the left (negative virtual coordinates), and onto a monitor above if available. Pupils point right / left / up respectively; no freeze, no jump to the wrong side.
4. **Gaze motion quality.** Flick the cursor from left edge to right edge. Pupils glide (spring k 260, damping ratio 0.82): visibly smooth, settle in about 0.3 s, overshoot is barely noticeable (< 10% of travel), no wobble or stutter at 60 Hz and 144 Hz displays.
5. **Blink visible.** Watch the idle face for 60 s. Expect a blink every 2.8 to 6 s (count 10 to 21 blinks), each one a quick vertical squash of the whole eye (about 70 ms close, 90 ms open). Occasionally (about 1 in 7) a double blink. Slow-motion screen recording at 120 fps or more confirms the eye fully closes.
6. **Happy expression.** End a focus session successfully: eyes become 3 px "^" arcs with a small bounce; width grows to 96 px.
7. **Reduced motion.** Turn on Settings > Reduced motion (and separately Windows Settings > Accessibility > Visual effects > Animation effects off). For 60 s: blinks still happen (spec: "blinks and state changes stay"), no gaze springing or wander (pupils snap to the pointer or rest centred), no bounce, no shake, captions appear whole, reveal/retract happen without animation. Turning it off restores motion without restarting.
8. **Idle CPU < 2%.** With the notch idle (eyes tracking, blinking) and the cursor still for 2 min, Boo's process CPU averages < 2% of one logical core (Task Manager "CPU" column or `Get-Counter '\Process(Boo)\% Processor Time'` sampled every second for 120 s). Repeat while moving the cursor continuously: report the number. Render loop must pause when the notch is hidden and the cursor is not near it.

21. **Moods (face polish, studio parity).** Compare each state with the studio contact sheet (docs/design/boo-notch-studio.html) and the CI notch gallery: listening eyes slightly taller with bigger pupils and 5 live bars on the right; thinking eyes squint and look up-right with 3 rising dots; responding eyes glance around and bob with 5 talking bars and the reply typed into the notch; capture shows a steady red dot left of the eyes with a pulsing halo and a stop square on the right; nudge shakes twice, eyes look down-left at the typed caption with "Back to it" / "5 min break" pills; paused eyes become dimmed slits with two pause bars; happy shows hopping "^" arcs.
22. **Idle wander.** Leave the pointer still for 3 s with the notch revealed: the eyes start glancing around (a new glance every 0.6 to 2.2 s) and return to tracking as soon as the pointer moves.

## Notch presence (U1)

9. **Collapsed notch has no text.** In Hidden / Hover / Idle states the notch shows only the black pocket and the two eyes: no caption, label or tooltip text is drawn on it (UI Automation name may exist for screen readers).
10. **Shape.** Collapsed notch is 84x36 px at 100%, flush with the top edge, 18 px bottom radius, 10 px curved shoulders flowing into the screen edge, horizontally centred on the display it lives on.
11. **Per-state sizes at 100% / 125% / 150%.** Trigger each state and measure with the screen ruler. Expected physical px (spec DIP x scale), x centred, y = 0:

    | State | 100% | 125% | 150% |
    |---|---|---|---|
    | hidden / hover / idle | 84x36 | 105x45 | 126x54 |
    | listening | 172x36 | 215x45 | 258x54 |
    | thinking | 128x36 | 160x45 | 192x54 |
    | responding | 348x94 | 435x117.5 (+/-1) | 522x141 |
    | capture | 148x36 | 185x45 | 222x54 |
    | nudge | 348x108 | 435x135 | 522x162 |
    | paused | 120x36 | 150x45 | 180x54 |
    | happy | 96x36 | 120x45 | 144x54 |

    Tolerance +/- 1 px. Growth is sideways: the centre x does not move between states, height stays 36 DIP except responding / nudge.
12. **Resize timing.** Switching states animates size over about 280 ms (recording at 120 fps: 30 to 37 frames).
13. **Hover strip.** An invisible 220x8 DIP strip at the top centre (220x8 / 275x10 / 330x12 px at 100/125/150%) triggers reveal; moving the cursor along the top edge outside the strip does not.
14. **Reveal timing.** Entering the strip reveals the notch in about 240 ms with a slight (~4%) overshoot.
15. **Retract timing.** Leaving the notch: it stays fully visible for 900 ms (+/- 50 ms), then retracts in about 160 ms. Measure with a 120 fps recording from the frame the cursor leaves.
16. **Re-enter cancels retract.** Leave, then return within 500 ms: the notch never starts retracting. Leave, then return mid-retract: it reveals again from where it was, without a jump.
17. **Capture disclosure never hidden.** Start a screen capture, then: hover and leave repeatedly, wait 30 s outside, start listening, start a response, trigger a nudge, pause focus, and choose "Hide Boo". In every case the capture state (148 px, capture disclosure visible) stays on screen until capture ends. Same for the microphone listening state.
18. **Click opens full window.** Clicking the revealed notch opens the main Boo window; right-click opens the context menu.
19. **DPI change at runtime.** Change scaling 100% -> 150% while Boo is running and move Boo to a monitor with a different scale: notch re-centres and re-sizes within 1 s, eyes stay crisp, no clipping.
20. **Face swap is data-only.** Edit the deployed spec's `chosen.face` to "A", restart: Pebble eyes, 96x32 collapsed. Set to "C": Peek face growing down. Corrupt the JSON: Boo starts with face B defaults and logs one clear warning.
