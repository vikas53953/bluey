# Candidate13 native presence and typing evidence

Candidate13 source: `013be73c6f2eb11efc032ce406087094c90a9560`.

## Identity

- PID `44120`, executable `artifacts/candidate13/Boo.exe`, SHA-256
  `27A3BDA0C2E62E576489A7ABE73D7289E3E2502BA303B5BF47C3140D9F7BE1B4`.
- `Boo.dll`: `1A9D9B1DEFD1832418F0F984F8845F5D8B6830E0569DC64F3BF7CC5296D8A9EB`.
- Manifest: `4CC98E5FC1B853634D7332E1001474B6881DD446926BA9B4EC63E0F8B07E3981`.
- ZIP: `E1691A51C923BA4F96717F0837A871C0351A3BA72CA4C4F627FDC00681D8F3BF`.
- Synthetic fixture PID `16996`; candidate13 returned window ID `98305932`,
  title `Boo companion`.

## Startup and native window presence

- Fixture was freshly returned, focused, and read as
  `Button text Value: Create account`.
- One `Control_L+Alt_L+b` was sent at `2026-11-07T14:24:47.904Z`.
  After one short refresh, Sky returned the candidate13 `Boo companion`
  window. The accessibility tree exposed a native title bar and system
  controls: `Minimize`, `Restore`/`Maximize`, and `Close`, plus the full
  conversation/settings surface. It also exposed body color `#000000` and eye
  color `#FFFFFF` fields, with Save/Reset controls.
- Screenshot-backed state was returned, but no stable screenshot path was
  retained and the candidate13 visual was not treated as proven from the
  capture because the window was not visibly represented in the returned
  desktop frame. Accessibility is the presence evidence.
- Initial Minimize click failed with the supported error that the point was
  over ChatGPT rather than Boo. After activating Boo and refreshing, a retry
  returned no error but produced no observable chrome/state transition;
  Maximize likewise produced no observed `Restore` transition. Close was not
  invoked. Native minimize/maximize/close semantics are therefore
  `UNVERIFIED`, not passes.

## One authorized synthetic typing request

- Fresh fixture read before the request remained
  `Button text Value: Create account`.
- Exact request was set with supported `set_value` and field-verified at
  `2026-11-07T14:26:21.510Z–2026-11-07T14:26:22.220Z`; InputBox focus and its
  complete `Value:` matched.
- One `Ctrl+Enter` was sent at
  `2026-11-07T14:26:27.329Z–2026-11-07T14:26:27.541Z`.
- At `2026-11-07T14:26:37.687Z`, the exact immutable proposal was present:
  `Type text`; requested/resolved/automation ID `button-text`; name
  `Button text`; text `Boo field check`; Cancel/Approve controls. Access-key
  labels were explicitly present: `Approve proposed action (Alt+A)` and
  `Cancel proposed action (Alt+C)`. Fixture remained `Create account` before
  approval.
- One `Alt_L+a` was sent at
  `2026-11-07T14:26:53.526Z–2026-11-07T14:26:53.918Z` on that exact authorized
  proposal. Immediate fixture read was still old, but delayed independent
  read at `2026-11-07T14:27:09.880Z` showed
  `Button text Value: Boo field check`. Boo separately reported
  `Completed after verification: observed UI Automation value read back
  exactly after typing` and its transcript said `Enter “Boo field check” in
  the Button text field without clicking Apply.` This is real field mutation
  plus separate Boo readback evidence.
- Observed fixture Undo button was element `27`. Its supported click returned
  `coordinate input geometry is unavailable`; no Apply click or alternate
  automation was attempted. The field remained `Boo field check` after that
  failed Undo attempt. Root source inspection adds an important qualification:
  this fixture's Undo restores a prior applied button edit; the preceding
  field-only typing had not invoked Apply. Even a delivered Undo click at
  this point would not prove the complete edit/Undo scenario. Boo has no
  dedicated universal Undo control in this candidate.
- The fresh fixture target element `18` was restored with supported
  `set_value("Create account")` at
  `2026-11-07T14:27:41.894Z–2026-11-07T14:27:43.034Z`; refreshed readback was
  exactly `Button text Value: Create account`.

## Final state and limits

Final candidate13 state read showed `Screen sharing off · Primary display`,
`Microphone idle`, the exact Boo readback completion status, and the restored
fixture. This proves the bounded typing mutation and Boo readback, but not
Undo acceptance or native window chrome semantics. No credential value,
microphone activation, provider retry, Apply click, privacy change, or
alternate automation occurred.

## Follow-up full edit/Apply/Undo workflow — blocked before new request

- After the successful bounded Type/readback and fixture restoration above, a
  fresh workflow bind returned the fixture and candidate13 companion. The first
  activation/read sequence was rejected with the raw Sky message:
  `user input was detected in this window; call get_window_state before
  continuing`.
- A follow-up `get_window_state` on the fixture returned:
  `window is minimized; call activate_window, refresh with get_window, then
  retry get_window_state`. Calling the returned fixture app ID through
  `launch_app` re-enumerated the same windows but did not make the fixture
  readable. A fresh `activate_window` attempt returned the same user-input
  guard.
- No new Type, Apply, or Undo request was submitted; no credentials,
  microphone, capture, approval, Apply click, or alternate automation was
  attempted. The last confirmed fixture state before this blocked workflow was
  `Button text Value: Create account`, restored by the supported set-value
  operation above.
- Candidate13 PID `44120` was revalidated at the exact candidate13 path and
  stopped for cleanup; it was absent afterward. This follow-up is blocked by
  the supported-driver minimized/user-input guard and does not add any
  Apply/Undo acceptance claim.
