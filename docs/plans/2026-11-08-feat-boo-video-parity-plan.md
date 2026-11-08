---
title: Boo video-parity plan
type: feat
date: 2026-11-08
origin: docs/plans/2026-11-06-2242-feat-boo-windows-companion-plan.md
review_copy: https://claude.ai/artifact/Wzm7oSC8vEhHE9HL5knNKx
deadline: 2026-11-09 02:00 IST (2026-11-08 20:30 UTC)
---

# Boo video-parity plan

Goal: Boo matches everything Riley Brown's Bluey does in the video (github.com/rbrown101010/bluey-by-riley), on one Windows PC, no phone, plus HeyClicky-style drawing and requested PC tasks. Keep the existing build (candidate15); plan only gaps. Prior plan and all its requirements (R1-R17, AE1-AE6, KTD1-KTD9) remain in force.

## Decisions (Vikas, 8 Nov 2026)
- Hold-to-talk: BOTH a held global hotkey Ctrl+Alt+Space (low-level keyboard hook, because RegisterHotKey cannot see key release) AND press-and-hold on the eyes.
- Pointing: BOTH Riley's blue pointer cursor (flies to target) AND a HeyClicky ring/arrow once it lands.
- AI: OpenAI now (existing models/config); keep provider behind interfaces so others can be added later.
- Builder: Claude, here, same CE loop (plan -> frozen failing tests -> build -> independent review -> native QA).
- Notch look: "like HeyClicky" -> HeyClicky pocket, face B "Button eyes" (directions.B in docs/design/boo-notch-spec.json).
- Eye expressions: blink, listening, thinking, speaking/responding, nudge, happy (session end), plus paused/capture states per spec.
- Done = all three demos (AE1 draw-explain, AE2 voice, AE3 research/task incl. edit+Undo) + one 25-minute focus session, on one packaged build on Vikas's PC.

## Feature map (Riley -> Boo build 15)
| Riley | Windows equivalent | Status | Unit |
|---|---|---|---|
| Menu bar/notch home | Top-center notch, hover reveal, tray | built, hover unproven | U1 |
| Eyes follow mouse | Pupils track cursor on all monitors | missing | U2 |
| Blink/brows/expressions | Eye-only expressions per state | missing | U2 |
| Blue cursor points at targets | Pointer overlay + ring/arrow | rings built/unproven; pointer missing | U3 |
| Hold-to-talk (iPhone) | Ctrl+Alt+Space hold + hold eyes | missing | U4 |
| Realtime voice | OpenAI Realtime spoken+text, interrupt | built, unproven | U4 |
| Research cards | Research with sources in full window | built, unproven | U5 |
| Computer control on request | UIA actions, preview, approve, Undo | field edit proven; Apply+Undo unproven | U5 |
| Focus session 1-240 min | same | built, unproven natively | U6 |
| 30 s screenshots, Decisions classify | same, Boo excluded | built, unproven natively | U6 |
| Nudge bubble + OCR pointer + chirp, 120 s cooldown | notch bubble, pointer via Windows.Media.Ocr, chirp | policy+chirp built; pointer missing | U6 |
| History timeline | SQLite timeline | built, unproven natively | U6 |
| Developer Mode 4x / 10 s | same | done | - |
| Ctrl+Opt+S stop | Ctrl+Alt+F12 | done | - |
| iPhone pairing | not needed | dropped | - |

## Units (each: frozen failing tests first, separate build, separate QA)
- U0 Baseline: build + 242 tests green; fix repo-wide format gate; QA-only switch disabling SetWindowDisplayAffinity so native screenshots of Boo can be verified.
- U1 Notch presence: HeyClicky pocket (10 px curved shoulders), face B "Button eyes" (Vikas picked, 8 Nov): collapsed 84x36, 18 px bottom radius, grows sideways, 16 px white eyes 7 px apart, 7 px black pupils with 2.5 px glint, pupils travel 3.5 px toward pointer, blink squashes whole eye vertically, happy = 3 px ^ arcs; eye shape, sizes and grow direction loaded DATA-DRIVEN from docs/design/boo-notch-spec.json so face A/B/C can be swapped without code changes; states incl. happy (^ arcs + bounce); hover strip 220x8; reveal 240 ms (~4% overshoot); retract after 900 ms then 160 ms; resize 280 ms; works at 100/125/150% scaling; click opens full window.
- U2 Living eyes: cursor gaze with spring k260/d0.82 across monitors; blink 70+90 ms every 2.8-6 s; expressions per state; reduced motion; idle CPU < 2%.
- U3 Pointer + drawing: pointer flies to target then ring/arrow; marks clear on scroll/resize/foreground change; AE1 passes at 2 scale levels and on 2nd monitor if present.
- U4 Hold-to-talk voice: LL keyboard hook Ctrl+Alt+Space hold; press-hold eyes; release always stops mic; AE2: 20 warm turns median <1.5 s, p95 <3 s end-of-speech -> first audio.
- U5 Research + tasks: AE3 with source links; full Apply + Undo demo with readback; file creation verified before "done".
- U6 Focus parity: native session; nudge bubble (caption 28 ms/char, shake 2x +-3 px); OCR pointer; chirp; timeline; AE4 in Developer Mode.
- U7 Package + demo: portable build, start-with-Windows as a setting (off by default), full rerun on one package with evidence (build, scale, model).

## Order
1. Wave 1: U0, then U3 + U4 + U5 in parallel.
2. Wave 2: U1 + U2 (spec ready).
3. Wave 3: U6, then U7.
Deadline-driven priority if time runs short: U0 > U4 > U2 > U1 > U3 > U5 > U6 > U7. Report anything unfinished as unfinished.

## Rules
Tests frozen before building, never weakened/skipped to go green. Builders never self-certify. Untested = reported untested. No routine approval prompts to Vikas. Update docs/STATUS.md after each wave. Commit on feat/boo-windows; no push (no remote).
