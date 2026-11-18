# Candidate12 startup/registration limitation

This is a bounded startup observation, not a typing or acceptance result.

- Source: `f747fb181aa3bea7ab4816e950d5ed9279dd6b54`.
- Candidate12 package identity: `artifacts/candidate12/Boo.exe`, launched as
  PID `32996`; executable SHA-256
  `57B7A6F4416C1B59590882D32BD6D1F47FF93862CC157343DEC283258292E2E5`.
  Manifest: `C056E44056BFCF8BC54147C16FEC3CE29C8E4ED33C096E69919A5D30DA1A0C85`;
  ZIP: `24D6C18820194C745CB8123C3EB4A62BBDCF28B3B957239D3767C106E1A78CBE`;
  `Boo.dll`: `80DA26FF70D07B372A116C101D35FC5F64921FDE64EA98005A9735491EEA627C`.
- Candidate11 PID `44124` was path-revalidated as
  `artifacts/candidate11/Boo.exe` before replacement. Only that process was
  stopped. Candidate12 was launched with `ProcessStartInfo`, exact executable
  path, workspace working directory, `UseShellExecute=false`, and
  `BOO_DATA_DIR=%USERPROFILE%\Downloads\blue--fo\artifacts\qa-profile-candidate1`.
  No `WindowStyle` override was supplied.
- The synthetic fixture PID `16996` was freshly returned and activated. Its
  independent accessibility read was
  `Button text Value: Create account`.
- One documented `Control_L+Alt_L+b` hotkey was sent using the returned fixture
  window. Immediate enumeration, a 2-second startup wait, and a second
  `list_windows` plus `list_apps` all returned only the fixture; no candidate12
  Boo controls window was returned. The candidate12 process remained alive at
  its exact path during this observation, but was not discoverable through
  supported Sky windows/apps.
- No Boo request, provider call, proposal, approval, microphone action, or
  fixture mutation occurred. Candidate12 PID `32996` was then revalidated by
  exact path and stopped; it was absent afterward. The fixture remained
  `Button text Value: Create account`.

The evidence is limited to process launch plus failure to register an
observable Sky window. It does not establish the cause and is not a typing
pass/fail result.

## Separate bounded entry retry (controls registered; no request)

- No competing Boo process was present before this retry. Candidate12 hashes
  were revalidated unchanged. It was launched as PID `21584` from the exact
  `artifacts/candidate12/Boo.exe` path with `ProcessStartInfo`,
  `UseShellExecute=false`, the workspace working directory, and the existing
  QA profile through `BOO_DATA_DIR`; no `WindowStyle` override was supplied.
- A fresh returned fixture window was activated. After a short readiness
  re-observation its independent field read was
  `Button text Value: Create account`.
- One `Control_L+Alt_L+b` was sent at `2026-11-07T13:35:41.423Z` against the
  returned fixture window. The first retained registration poll at
  `2026-11-07T13:35:51.385Z` returned candidate12 controls window ID
  `51448896`, app path `artifacts/candidate12/Boo.exe`; the corresponding
  `list_apps` result identified the running Boo app and that controls window.
  No other input or retry was sent.
- The first retained poll timestamp is approximately 9.96 seconds after the
  hotkey, so this record establishes registration but does not establish a
  five-second entry-latency bound. No earlier sub-five-second observation was
  retained.
- Fresh controls state at `2026-11-07T13:35:59.276Z` was idle and safe:
  focused `InputBox`, no input value, `Screen sharing off · Primary display`,
  `Typing is ready. Microphone starts only after Talk.`, and `Ready`.
- PID `21584` was revalidated at the exact candidate12 path and left running
  idle with the controls window returned. No request, provider call, capture,
  microphone action, proposal, approval, or fixture mutation occurred.

This retry resolves the earlier entry-registration observation for this run,
but it is not a typing or acceptance result and does not erase the first
startup/registration limitation.

## Corrected-input live request (fresh target-identity failure; no proposal)

- Candidate12 PID `21584` and the returned controls/fixture windows were
  freshly rebound. Baseline fixture read was
  `Button text Value: Create account`; controls were idle with sharing off,
  microphone idle, and status `Ready`.
- Supported `set_value` set the exact request at
  `2026-11-07T13:37:43.910Z–2026-11-07T13:37:45.651Z`. The refreshed
  InputBox's own `Value:` matched the complete request and its returned
  `focused_element` remained InputBox.
- One `Ctrl+Enter` was sent at `2026-11-07T13:37:50.738Z` and completed at
  `2026-11-07T13:37:50.828Z`. Immediate state showed the InputBox cleared,
  `Screen sharing on · Primary display`, and a fresh `You` transcript line
  containing the exact request, confirming a new submission rather than old
  transcript text.
- At `2026-11-07T13:38:00.926Z`, the fresh response was complete with status
  `Reply complete. No action was committed.` Boo's exact response was:
  `The current screen shows ChatGPT, but no Button text field is visible or
  available in the supplied controls. Please bring the window containing that
  field into view so I can propose entering “Boo field check” without clicking
  Apply.` Annotation text was `Boo explained the screen without a verified
  mark.` No `ActionPreview`, requested/resolved target, Cancel, or Approve
  control appeared. Sharing was already off by this observation; no precise
  capture-duration claim is made.
- An independent fixture read at the same pre-stop point remained
  `Button text Value: Create account`. Because the target/proposal identity
  precondition failed, no focus navigation, Tab, Space, approval, fixture edit,
  or restore write was attempted.
- Stop was sent once at `2026-11-07T13:38:15.192Z–2026-11-07T13:38:15.348Z`;
  state remained sharing off, microphone idle, and `Reply complete. No action
  was committed.` Escape was sent at `2026-11-07T13:38:15.882Z`. Final fixture
  read at `2026-11-07T13:38:20.218Z` remained `Button text Value: Create
  account`; Boo controls were absent.

This proves corrected input and one fresh request, but not proposal generation,
keyboard approval, native mutation, Boo readback, Undo, or typing acceptance.
The remaining failure is target identity/context selection, not an approval
result.

## Primed-fixture context retry (exact proposal; focus handoff still fails)

- Candidate12 PID `21584` remained at the verified candidate12 path. Fresh Sky
  enumeration returned the fixture; activation produced actual fixture focus
  on `18 edit ... Button text Value: Create account ID: button-text` before
  the hotkey. No unrelated activation occurred.
- `Control_L+Alt_L+b` was sent at `2026-11-07T13:40:22.257Z`. Fresh enumeration
  immediately returned controls window `51448896` owned by candidate12.
- Supported `set_value` set the exact request at
  `2026-11-07T13:40:37.018Z–2026-11-07T13:40:39.964Z`; the refreshed InputBox
  `Value:` and `focused_element` matched the complete request/focused InputBox.
- One `Ctrl+Enter` was sent at `2026-11-07T13:40:44.846Z` and completed at
  `2026-11-07T13:40:45.119Z`. The immediate state still showed the full
  InputBox value, a fresh `You` transcript line, and thinking/capture state.
  At the later fresh poll `2026-11-07T13:40:56.198Z`, the InputBox was clear
  and the exact proposal was present. This progression is retained rather than
  treating the immediate non-clear as a submission failure.
- The exact proposal contained only `Type text`, requested target
  `button-text`, resolved destination `button-text`, automation ID
  `button-text`, name `Button text`, text `Boo field check`, and Cancel/Approve
  controls. The proposal also had annotation
  `Boo explained the screen without a verified mark.`
- Actual returned `focused_element` remained the InputBox at both the first
  proposal observation (`13:40:56.198Z`) and settle observation
  (`13:41:03.278Z`). Cancel was visible but never focused; no Tab, Space,
  approval, fixture edit, or Undo was attempted. An earlier stale/context
  response line said no Button text field was visible, but the later exact
  proposal proves this retry reached the expected proposal object; no claim is
  made about the source of that earlier line.
- Independent fixture read before stop remained `Button text Value: Create
  account`. Stop was sent once at
  `2026-11-07T13:41:14.238Z–2026-11-07T13:41:14.612Z`; the returned state
  still exposed the proposal while capture was off. Escape was sent at
  `2026-11-07T13:41:15.781Z`. Final fixture read at
  `2026-11-07T13:41:21.336Z` remained `Button text Value: Create account`, and
  Boo controls were absent.

This retry proves primed fixture context, exact proposal identity, and fresh
field verification, but not keyboard approval, native mutation, Boo readback,
Undo, or typing acceptance because the repaired Cancel focus handoff still was
not observed.

## Final primed-fixture request with explicit Tab navigation (approval blocked)

- Candidate12 PID `21584` remained at the verified path. Fresh fixture
  activation returned actual focus on
  `18 edit ... Button text Value: Create account ID: button-text`.
  `Control_L+Alt_L+b` was sent at `2026-11-07T13:43:18.196Z`, returning the
  candidate12 controls window.
- The supported `set_value` call for the exact request timed out waiting on the
  accessibility channel; no completion timestamp was retained and it was not
  retried. A fresh read at `2026-11-07T13:43:43.751Z` showed the complete
  request in the InputBox's own `Value:` and actual focus on InputBox, so the
  next action was a separately observed submit, not a blind set-value retry.
- One `Ctrl+Enter` was sent at `2026-11-07T13:43:52.317Z` and completed at
  `2026-11-07T13:43:52.795Z`. The refreshed InputBox was clear and capture was
  on; the later fresh proposal at `2026-11-07T13:44:02.495Z` contained exactly
  `Type text`, requested/resolved/automation ID `button-text`, name `Button
  text`, text `Boo field check`, and Cancel/Approve controls.
- Actual returned focus at proposal time was InputBox. Supported one-step Tab
  navigation was then performed with a fresh `get_window_state` after every
  step; every returned focus remained the InputBox:

  | Step | Tab interval UTC | Returned focus |
  |---:|---|---|
  | 1 | `13:44:08.837–13:44:09.042` | InputBox |
  | 2 | `13:44:16.579–13:44:16.954` | InputBox |
  | 3 | `13:44:24.912–13:44:25.283` | InputBox |
  | 4 | `13:44:30.644–13:44:30.844` | InputBox |
  | 5 | `13:44:35.258–13:44:35.442` | InputBox |
  | 6 | `13:44:39.862–13:44:39.968` | InputBox |
  | 7 | `13:44:44.927–13:44:45.033` | InputBox |
  | 8 | `13:44:49.299–13:44:49.533` | InputBox |
  | 9 | `13:44:54.077–13:44:54.197` | InputBox |
  | 10 | `13:44:58.180–13:44:58.359` | InputBox |
  | 11 | `13:45:02.547–13:45:02.743` | InputBox |
  | 12 | `13:45:08.412–13:45:08.671` | InputBox |

- No Space/Enter, approval, fixture edit, or Undo was sent. Stop was sent once
  at `2026-11-07T13:45:20.625Z–13:45:21.018Z`; Escape was sent at
  `2026-11-07T13:45:22.768Z`. Final fixture read at
  `2026-11-07T13:45:28.195Z` remained `Button text Value: Create account`,
  and Boo controls were absent.

This independently records the automatic-focus handoff failure and the manual
keyboard-navigation result. It does not establish a root cause or active/
foreground-window state, which was not retained by the supported observation.
No typing, approval, native mutation, Boo readback, or Undo pass is claimed.

## Idle generic-Tab characterization (no request or mutation)

- Fresh fixture activation returned actual focus on
  `18 edit (settable, string) Button text Value: Create account ID: button-text`.
  The supported call used was
  `await sky.press_key({window: fixtureState.window, key: "Tab"})`, followed
  by
  `await sky.get_window_state({window: fixtureState.window,
  include_screenshot: false, include_text: true})`.
- That one fixture Tab ran at `2026-11-07T13:49:45.847Z–13:49:46.041Z`.
  Returned `focused_element` remained the Button text edit with value
  `Create account`; `Apply edit` was present in the returned tree but was not
  focused. No Apply action occurred.
- One `Control_L+Alt_L+b` opened the returned candidate12 controls at
  `2026-11-07T13:49:50.266Z`. Fresh idle controls state had an empty InputBox
  focused, `Screen sharing off · Primary display`, microphone stopped, and
  status `Review the exact target and values before Boo can act.`
- The one idle Boo Tab used the same supported syntax with the fresh controls
  state window at `2026-11-07T13:50:00.882Z–13:50:01.402Z`. Returned
  `focused_element` remained the empty InputBox; no proposal controls existed
  in this idle state.
- Returned state keys were only `window`, `accessibility`, and `screenshots`;
  accessibility keys were `document_text`, `focused_element`, and `tree`.
  No active/foreground property was exposed, so none is inferred.
- Escape was sent at `2026-11-07T13:50:07.132Z`. Final fixture read retained
  focus/value `Button text Value: Create account`; candidate12 controls were
  absent. No set-value, typing, request, microphone, approval, Apply, or
  alternate automation occurred.

This comparison shows the supported one-step Tab driver also did not advance
the ordinary fixture edit to Apply; it characterizes the driver boundary and
does not prove a product root cause or acceptance result.
