# Bluefu — reference research and agreed direction

Date: 2026-11-06 (Asia/Calcutta)

Status: requirements dialogue in progress. This is research input, **not** an implementation-ready plan, a finished visual MVP, or authorization to begin production implementation.

## Workflow and review boundary

The user invoked Compound Engineering `lfg`, explicitly requiring `ce-plan` before `ce-work`, a visual MVP for review after planning, and approval before the build phase. Their review boundary overrides LFG's ordinary automatic progression. Current route: `lfg` → `ce-brainstorm` → `ce-plan` → visual MVP and user review → `ce-work` only after approval.

The workspace was empty and was not a Git repository at intake. CE pack resolution returned no packs and the warning that no repository config could be read. Research copies below are reference material only; none has been executed. The installed CE plugin moved from 3.36.0 to 3.36.1 during research; subsequent skill reads must resolve the current installed path rather than assume the old cache exists.

## User requirements and decisions

- Windows only, operated entirely from the same PC. No phone, pairing flow, or second device requirement.
- A cute, expressive companion at the top center of the screen, inspired by HeyClicky's notch. The user explicitly rejected a taskbar-based primary home.
- Working name: Bluefu, as supplied by the user. Exact displayed spelling and character identity remain to be confirmed visually.
- Focus-coach capabilities from Riley's video: task and timer, screen awareness, distraction nudges, expressive companion, and session history.
- Additional assistant capabilities: answer questions, explain the screen, draw on the screen, and broader assistant tools. Do not silently reduce the product to a focus timer.
- Both teaching and requested action: Bluefu may click, type, open apps, and work with files when explicitly asked, with confirmation for consequential actions. (session-settled: user-directed — chosen over teaching-only and broad autonomy: the user wants both help and requested execution, retaining consequential-action review.)
- Cloud AI using the user's own paid API account, with a clear screen-capture indicator. (session-settled: user-directed — chosen over local-only and a comparison-first route: the user selected cloud processing after being told active focus sessions send periodic screenshots to OpenAI.)
- Voice and typing input; spoken explanations with matching text; optional gentle chirps. (session-settled: user-directed — chosen over Riley's text/chirp-only replies and typing-only input: the user wants the combined spoken teaching experience.)
- Typography, icons, avatar, layout, and motion require explicit visual review. Proposals are not approved defaults.

## Verified references

### Riley's video

Source: https://www.youtube.com/watch?v=8QPd6WD9Zzg&t=347s

The currently visible page title was **I Built My Most Useful App Ever (Full Guide)**, by Riley Brown. Read the full English auto-generated transcript via the browser's transcript export; inspected selected video frames. This was not a complete real-time playback or functional test of the app.

- 0:39–3:45: original companion, mouse-following gaze, hold-to-talk, screen pointer/comment, research, phone/Mac pairing.
- 5:53–8:37: focus flow, task plus timer, periodic screenshots, on-task/off-task/uncertain categories, repeated evidence before a nudge.
- 8:38–10:48: intended contextual classification, brief detours tolerated, cooldown between nudges.
- 11:29–14:58: pairing and testing, task input, timer/control revisions, accelerated developer mode, nudge and session review.
- 15:13–16:38: finished desk demonstration and on/off-task timeline.

The transcript's cost claims are historical claims about its demonstrated configuration, not a budget for Bluefu's expanded voice and action features.

### Bluey source repository

Source: https://github.com/rbrown101010/bluey-by-riley

Pinned reference revision: `3f273636e3b51b29f21791d495c86e3345f168b0`.

Public reference files downloaded under `docs/research/bluey-reference/`:

- `Shared/Palette.swift`: original periwinkle/indigo palette, including A9BCFF, 6C86F5, 4254D6, 2B2F8F.
- `iOS/Sources/FaceView.swift`: spring-based gaze, brows, blinking, leaning, hops, blush, and expressions. Read the initial animator section; full animation has not been ported or tested.
- `iOS/Sources/FocusViews.swift`: task, countdown, focus controls, session views. Source uses Fredoka, Plex Sans, Plex Mono, and a rounded system countdown. These are reference choices, not yet approved Bluefu typography.
- `Shared/FocusSession.swift`: session state, interval and result structures. Normal check interval is 30 real seconds; developer interval is 10 seconds.
- `Mac/Sources/GooglyMac/FocusAPI.swift`: Decisions classification and separate Responses wording. Verified source names `gpt-6-luna` for Decisions and `gpt-5.5` for nudge text; model choices are not yet the final Bluefu architecture.
- `Mac/Sources/GooglyMac/FocusCompanion.swift`: downloaded for later lifecycle research; not yet reviewed fully.
- `docs/previews/expressions.png`: inspected original character sheet. It is a source reference, not a Bluefu design deliverable.
- `LICENSE`: repository includes the Unlicense/public-domain dedication.

The repository README describes memory-only screenshots by default, local text/session history, pause/lock/disconnect handling, and existing manual assistant capabilities. Actual Windows equivalents need separate implementation and verification.

### HeyClicky

Official Windows reference: https://www.heyclicky.com/for-windows

The official page calls the top-of-screen surface a notch and describes voice help, screen drawing, and agents.

Found local installation at `C:\Users\vikasmit\AppData\Local\HeyClickyApp\HeyClicky.exe`. Launched it for the user-authorized layout inspection. The returned window belongs to `HeyClickyApp\current\HeyClicky.exe`, title `HeyClicky`.

**Observation gap:** screenshot capture reported the window as minimized. Activation followed by fresh window lookup still returned the same capture error. Stopped after the documented retry. Exact installed geometry, hover behavior, and expanded layout remain unverified. No HeyClicky conversation, API key, account record, or private history was read. No settings were changed.

### Official API documentation

Searched and fetched: https://developers.openai.com/api/docs/guides/decisions

The current guide supports text/image evidence and typed choice answers using `gpt-6-luna`; the application must implement the follow-up behavior. Generated explanations and tool calls require other API capabilities. Account access, latency, classification quality, voice cost, and live integration have not been tested. No secret was requested or read, and no paid model request was made.

## Questions still needed before a dependable plan

1. Two or three concrete tasks and target apps to define the assistant's acceptance demonstrations and what “broader tools” means in practice.
2. Character identity: retain/adapt the original blueberry or approve a distinct Bluefu character after visual comparison.
3. Notch design and interaction, including compact/expanded states, screen annotations, keyboard/voice activation, and how it coexists with HeyClicky in the same top-center location.
4. Visual review of typography, icons, color, motion, and readable controls at actual display scale.
5. User-facing capture/voice behavior, session pause/stop behavior, action confirmations, and memory/history retention, resolved through concrete flow proposals.

Technical feasibility and architecture are for `ce-plan`: native window behavior, capture exclusion, DPI/multi-monitor coordinates, annotation lifecycle, action interruption, model/tool integration, credentials, packaging, and verification. Do not ask the user to select implementation libraries without a product reason.

## Review evidence to produce

- Traceability from reference behaviors and user decisions to requirements and acceptance checks.
- A CE plan explicitly distinguishing the Windows adaptation, broader assistant additions, and any unverified claims.
- A visual MVP covering compact companion, focus start/running/paused/nudge/history, teaching with annotations, action preview/running/stop, and settings/error states at appropriate fidelity.
- User approval of plan and visual direction before production `ce-work`.

No production app, installed Bluefu build, live API integration, or acceptance test exists yet.
