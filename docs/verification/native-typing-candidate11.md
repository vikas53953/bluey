# Native observed-control typing case — candidate11

- Canonical source HEAD: `f9cc48f7daec98fc72956ecd364a5cc231e753f1`.
- Candidate package path: `artifacts/candidate11/Boo.exe`.
- Actual candidate11 process PID after exact-path replacement: `44124`.
  The old PID `41924` was verified as candidate10 before it was stopped; it
  was not treated as a candidate11 process.
- Candidate11 controls window ID `24317470`.
- Fixture PID `16996`; fixture window `244516272`.
- Manifest SHA-256:
  `A7FF309931EB8469B9E0FBE0AD516FAB73B1AA5648484DC152A808018CE9C60A`.
- ZIP SHA-256:
  `EB89EBC0069DB8A8085774BA3A3D9271FFBAE94D0529F36735F2387753D8E297`.
- `Boo.dll` SHA-256:
  `B4EA235923ED893BB04A22E90A9A6ADD92438F11785A0B9F0C6AC8500AA049EF`.

## Request and immutable proposal

- Fixture was observed with `Button text Value: Create account` and focused.
- Boo question `InputBox` was observed focused. Exact request typed and
  verified: `Type Boo field check into the Button text field. Do not click
  Apply.`
- Typing action UTC: `2026-11-07T09:03:44.862Z` to
  `2026-11-07T09:03:45.085Z`.
- One request submitted at `2026-11-07T09:03:52.415Z`; key completed at
  `2026-11-07T09:03:52.534Z`.
- First proposal observation: `2026-11-07T09:04:05.518Z`.
- Immutable proposal fields were all exact:
  - action: `Type text`
  - requested target: `button-text`
  - resolved destination: `button-text`
  - automation ID: `button-text`
  - observed name: `Button text`
  - exact text: `Boo field check`
  - controls: `Approve proposed action` and `Cancel proposed action`
- Before approval, a fresh fixture readback remained
  `Button text Value: Create account`.

## Approval boundary and cleanup

- First exact ApproveButton attempt began at `2026-11-07T09:04:26.190Z`
  and returned a window-change warning before a completion timestamp was
  retained. Refreshing showed the proposal still present and the fixture
  unchanged.
- One re-observed exact-target attempt began at
  `2026-11-07T09:05:14.135Z` and returned
  `coordinate input geometry is unavailable`. No alternate UI automation or
  further approval attempt was made.
- No field edit occurred; there was no real-field value change or Boo
  readback-completion result to compare.
- Stop was sent once at `2026-11-07T09:05:27.096Z`, completed at
  `2026-11-07T09:05:27.884Z`. Capture was off, proposal controls were absent,
  and listening text reported `Microphone stopped. Screen activity is shown in
  the sharing indicator.`
- `Escape` closed Boo. Final fixture readback remained focused
  `Button text Value: Create account`.

This proves the exact immutable handoff and the Sky approval boundary only. It
does not prove a native UIA call, field mutation, Boo readback completion,
interruptibility, edit/Undo behavior, or full acceptance. No provider retry,
microphone, approval of another target, Apply click, private-app interaction,
or alternate automation occurred.

## Separately labeled keyboard-only follow-up

- Candidate11 process/package binding and all three recorded hashes were
  revalidated; fixture PID `16996` and original field value were also
  revalidated.
- The same exact request was typed once at `2026-11-07T09:07:45.874Z` to
  `2026-11-07T09:07:46.028Z` and submitted once at
  `2026-11-07T09:07:52.407Z` (key completed `09:07:52.596Z`).
- Immediately before `Ctrl+Enter`, retained accessibility showed focus on
  `InputBox`, but the `InputBox` line had no `Value:`. A wrapper flag reported
  the request string somewhere in the tree, but that string also existed in
  the earlier transcript; current field-value verification was therefore not
  established.
- The bounded poll ran from `2026-11-07T09:08:03.118Z` through the last
  retained state at `2026-11-07T09:09:06.504Z`. Capture was off and the stale
  status remained `Review the exact target and values before Boo can act.`;
  no fresh `ActionPreview`, Cancel/Approve controls, or new proposal appeared.
- No fresh response text, `ResponseText`, pending-action detail, or error was
  retained. Send enablement was not retained; the only control-state evidence
  was focused `InputBox`, capture off, and microphone-stopped listening text.
- Because no fresh proposal existed, Cancel focus could not be evidenced;
  neither Tab nor Space was sent. No field edit or readback completion
  occurred.
- Stop was sent once at `2026-11-07T09:09:21.205Z`, completed at
  `2026-11-07T09:09:21.456Z`; Escape closed Boo. Final fixture readback was
  focused `Button text Value: Create account`.

This follow-up tests the keyboard handoff boundary only. It does not prove the
native UIA call, field mutation, readback, interruptibility, or edit/Undo
acceptance.

## Field-specific InputBox readback characterization

- This is a receipt correction/characterization only; no provider request,
  approval, microphone activation, or fixture mutation was performed.
- Fresh fixture observation before opening Boo showed
  `Button text Value: Create account`. Boo was reopened from that observed
  fixture, and the focused control was exactly:
  `10 edit (settable, string) Type a question for Boo Description: Type a
  screen question or a bounded task request ID: InputBox`.
- A unique harmless unsent string, `Boo input check 1107`, was entered once at
  `2026-11-07T09:12:16.737Z` and the action completed at
  `2026-11-07T09:12:17.016Z`. After refresh, the exact InputBox accessibility
  line remained the line above and exposed no `Value:` field; the unique
  string did not appear in the field-specific tree readback. Therefore the
  earlier wrapper `exactVisible` signal must not be treated as input-value
  proof.
- Targeted supported-API/guidance inspection found `get_window_state` and its
  accessibility tree as the legitimate read route, but no `get_value` or
  field-value read operation; `set_value` is a write operation only. No
  alternate UIA, PowerShell, clipboard, or privacy-bypass route was used.
- Focus was freshly reverified before cleanup. `Ctrl+A` and `Backspace` were
  sent through the supported Sky route; refreshed state still showed the
  focused InputBox with no exposed `Value:` and no selected text. No submit
  occurred. `Escape` was sent to close Boo. The combined post-Escape fixture
  readback output was truncated and was not retained, so no final post-Escape
  field-value claim is made.

Conclusion: field-specific verification of the typed InputBox value was not
possible through the supported Sky accessibility/API surface. This documents
an evidence boundary, not whether the hidden control accepted the text.

## Supported Sky `set_value` field-specific probe

- This bounded probe used no provider request, microphone, approval, fixture
  write, secret, alternate UIA, or privacy change. Candidate11 and the
  synthetic fixture were selected from freshly returned windows.
- The bundled API schema defines `set_value({ window, element_index, value })`
  as replacing an editable element from the latest accessibility tree. The
  fresh Boo observation identified element `10` as the focused InputBox:
  `10 edit (settable, string) Type a question for Boo Description: Type a
  screen question or a bounded task request ID: InputBox`.
- One supported `set_value` call with exact harmless value
  `Boo input check 1107` succeeded at
  `2026-11-07T09:15:54.633Z–2026-11-07T09:15:55.553Z`. The immediately
  refreshed, field-specific accessibility line exposed
  `Value: Boo input check 1107` on `ID: InputBox`.
- A second supported `set_value` call with an empty string succeeded at
  `2026-11-07T09:16:03.084Z–2026-11-07T09:16:04.509Z`. The refreshed
  field-specific InputBox line again had no `Value:`, proving the diagnostic
  value was cleared. Focus remained on InputBox and selected text was empty.
- `Escape` was sent once at `2026-11-07T09:16:10.298Z`; no submit or provider
  request occurred. This probe establishes that supported `set_value` can be
  verified through the returned accessibility `Value:` even though the prior
  keyboard `type_text` route did not expose one.

## Corrected-input live request attempt (one submission; approval blocked)

- Candidate11 process/package binding was revalidated before the attempt:
  PID `44124`, executable
  `artifacts/candidate11/Boo.exe`, executable SHA-256
  `4796252C0E74BCF0C202F16A5DFD3464458340DE9F188C1053E773DA2DDB5646`,
  manifest `A7FF309931EB8469B9E0FBE0AD516FAB73B1AA5648484DC152A808018CE9C60A`,
  ZIP `EB89EBC0069DB8A8085774BA3A3D9271FFBAE94D0529F36735F2387753D8E297`,
  and `Boo.dll` `B4EA235923ED893BB04A22E90A9A6ADD92438F11785A0B9F0C6AC8500AA049EF`.
  Fixture PID `16996` was returned as the synthetic Boo QA Workspace and its
  fresh field read was `Button text Value: Create account`; Boo showed screen
  sharing off and microphone stopped before the request.
- On a freshly focused InputBox, supported `set_value` set the exact complete
  request at `2026-11-07T09:17:58.146Z–2026-11-07T09:17:59.276Z`, and the
  refreshed field-specific line exposed the complete `Value:`. One
  `Ctrl+Enter` was then sent at `2026-11-07T09:18:04.947Z` and completed at
  `2026-11-07T09:18:05.949Z`.
- Immediate refreshed state showed the InputBox cleared, `Screen sharing on ·
  Primary display`, a fresh `You` transcript line containing the exact request,
  and Boo transcript lines describing the requested `Boo field check` edit.
  This confirms this corrected-input submission; it is not inferred from old
  transcript text.
- At `2026-11-07T09:18:17.517Z`, the fresh immutable proposal contained only
  the expected action: `Type text`; requested target `button-text`; resolved
  destination `button-text`; automation ID `button-text`; name `Button text`;
  text `Boo field check`; and Cancel/Approve proposed-action controls. The
  annotation text was `Boo explained the screen without a verified mark.`
  Fixture remained `Button text Value: Create account` on an independent
  read.
- Approval was intentionally not attempted. At the first proposal observation
  and a fresh re-observation at `2026-11-07T09:18:29.754Z`, accessibility focus
  remained the InputBox, not `Cancel proposed action`. Therefore no Tab or
  Space was sent and no action was approved. This is a focus handoff failure,
  not a proposal-content failure.
- Stop was sent once at `2026-11-07T09:18:37.254Z–2026-11-07T09:18:37.442Z`;
  the proposal cleared and capture returned off. Escape was sent at
  `2026-11-07T09:18:40.999Z`. Final independent fixture read at
  `2026-11-07T09:18:49.541Z` remained `Button text Value: Create account`,
  and Boo controls were absent. No fixture restore write was needed because no
  approval or edit occurred.

This proves corrected field-value verification, one fresh submission, exact
proposal generation, and preserved fixture state. It does not prove approval,
native UIA mutation, Boo readback after an approved edit, Undo, or full typing
acceptance because the required observed Cancel focus never appeared.
