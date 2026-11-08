# Native teaching check — candidate5

- Source commit: `50636a5bb8713dff43925e97afae9aa142c0a988`
- Candidate PID `15968`; exact Boo controls window ID `611061096`.
- Candidate ZIP SHA-256: `5058F31F9F69852056D937DB3D0B70BC5413334972CD178011D43315D868A9F2`
- Candidate manifest SHA-256: `67A46870276481526E186BFDF9B360107264E7E660182F43432168500804F9F7`
- Candidate `Boo.dll` SHA-256: `AD43397A3A81551FFB246CD33D410F9B911C623BA30051CE4EFD5B66FFB94521`
- Fixture PID `16996`; fixture window `244516272`.

## Setup

- Fresh Sky enumeration returned the exact synthetic `Boo QA Workspace`.
- The fixture was activated and verified maximized with a full-window screenshot
  showing only the disposable `Create account` target.
- Boo controls were opened from that observed fixture with
  `Control_L+Alt_L+b`.
- Text-only accessibility showed the question field focused, capture idle as
  `Screen sharing off · Primary display`, status `Ready`, microphone status
  `Typing is ready. Microphone starts only after Talk.`, and a masked
  credential control.

## First actual failure

- `type_text` was called once with the exact requested text:
  `Explain the Create account button and draw a ring around it.`
- The refreshed accessibility state still showed the focused `InputBox`, but
  did not expose the requested value (`requestVisible: false`).
- `Control_L+Enter` was pressed once. The first post-submit result was
  `StatusText: Type a question first.`
- No `CaptureDetail`, provider request, provider reply, or TargetObserver
  rejection was observed. This is an input-entry failure before capture.
- Emergency Stop was sent once at `2026-11-07T06:24:37.002Z`, completed at
  `2026-11-07T06:24:37.174Z`. Final listening text was `Stopped. Microphone and
  capture are idle until you activate them again.`

No retry, second submission, microphone, voice, approval, private-app
interaction, capture success, ring geometry, or teaching success was claimed.

## Verified input follow-up and capture diagnostic

- A fresh Sky enumeration/fixture activation was performed; the fixture was
  maximized and showed only the synthetic target.
- Boo was reopened from the fixture. The observed `InputBox` remained focused;
  Sky's element-click path reported `coordinate input geometry is unavailable`,
  so no alternate UI automation was used.
- A second exact `type_text` attempt was made and then verified through the
  refreshed accessibility tree. `InputBox` visibly contained:
  `Explain the Create account button and draw a ring around it.`
- `Control_L+Enter` was submitted once at
  `2026-11-07T06:26:52.952Z` (completed `06:26:53.055Z`). The transcript
  contained the exact user request.
- The first post-submit `CaptureDetail` and `StatusText` were identical:
  `The selected window moved, resized, or changed identity after it was
  observed.`
- No provider reply or screen capture was produced; no microphone or approval
  action occurred.
- Post-error Stop was sent once at `2026-11-07T06:27:00.574Z`, completed at
  `06:27:00.680Z`; final listening text confirmed capture and microphone idle.

This is a precise TargetObserver identity-change rejection, not evidence of
target capture success, ring geometry, overlay exclusion, or teaching success.
