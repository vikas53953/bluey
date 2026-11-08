# Riley Bluey -> Boo parity (U8, 8 Nov 2026)

Source audited: github.com/rbrown101010/bluey-by-riley (Mac AppDelegate/RealtimeHost/FocusCompanion/CursorOverlay, README).
Boo base: lane/face 042356e + U8 (branch claude/project-thread-0ih2hc).

| Riley feature | Boo | Status | Evidence |
|---|---|---|---|
| Eyes follow mouse, blink, moods (listening/thinking/talking/happy/sleepy) | Face B notch, gaze spring, blinks, Listening/Thinking/Responding/Nudge/Paused/Happy | Built (lane/face) | Core tests, notch gallery in CI |
| Menu-bar home | Top-centre notch + tray icon | Built | UI smoke 1, 4 |
| Hidden until wanted | Hover strip reveal, 900+160 ms retract | **Proven in CI (U8)** | UI smoke step 10 asserts Hidden -> revealed -> Hidden via ItemStatus |
| Hold face to talk (phone) | Hold Ctrl+Alt+Space or hold the eyes | Built | HoldToTalk tests; real mic not proven |
| Voice describes task -> focus starts (focus_start/pause/resume/end/status) | Same six Realtime tools incl. go_to_sleep, routed to the focus buttons | **Built (U8)** | VoiceCommandTests (101), VoiceToolProtocolTests (26); live voice not proven |
| Voice: "bye" -> go back to sleep | go_to_sleep ends the voice session after the reply, focus untouched | **Built (U8)** | Review-checked; not proven live |
| Ctrl+Opt+S stops computer control + pauses focus | Ctrl+Alt+S (new) and Ctrl+Alt+F12, tray Stop, right-click eyes; "Stopped" bubble | **Built + proven in CI (U8)** | UI smoke step 9 (Ctrl+Alt+S), step 7 (F12) |
| Blue cursor flies to target, ring | Pointer flight + ring/arrow | Built (PR #1) | Landing accuracy at 150% / 2nd monitor not proven |
| Research card with sources | Research in full window with source links | Built | Not proven live |
| Computer control on request | UIA actions with preview, approve, Undo | Built | Apply+Undo demo not proven live |
| Focus 1-240 min, 30 s screenshots, Decisions classify | Same | Built | Not proven natively for 25 min |
| Nudge bubble + OCR pointer + chirp, 120 s cooldown | Same | Built | Audible chirp not proven |
| Timeline history | FocusTimeline bars (PR #5) | Built | |
| Developer Mode 4x / 10 s | Same | Built | |
| Menu focus controls (Pause/Resume/End) | Panel buttons + voice; not in tray | Partial | |
| Point Here / Follow toggle / Stop Pointing / Hide hotkeys (Ctrl+Opt+P/F/D/H) | Not added | Gap (low value) | |
| Cursor size, pointer trail styles, Mood menu | Not added | Gap (cosmetic) | |
| Personality editor | Not added | Gap | |
| Alt+Space wake/sleep | Not used: Alt+Space is Windows' window-menu key; Ctrl+Alt+Space hold instead | Deliberate | |
| iPhone pairing, phone face | Not needed (decision) | Dropped | |
| Codex controller, meeting notes | Out of scope | Dropped | |
| Text-only replies (no spoken voice) | Boo speaks + shows text (Vikas's decision) | Deliberate difference | |

Known risk: Ctrl+Alt+S equals AltGr+S on some layouts (e.g. Polish "ś"); Boo takes that key while running. Ctrl+Alt+F12 remains if this needs dropping.
