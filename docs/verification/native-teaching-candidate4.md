# Native teaching check — candidate4

- Source commit: `b76d6532893761161907d82d721363fee4ddc880`
- Candidate PID `20516`; exact Boo controls window ID `4851608`.
- Candidate ZIP SHA-256: `F186D16EF708227F309BD0EE93366211D8FA6FF5EC7C0483DD4D127B4582A6D3`
- Candidate manifest SHA-256: `D1FDF98E138018EECA9881CCD95218599052E7EE185B48722EB53EA6A686B9E2`
- Candidate `Boo.dll` SHA-256: `CB9820AEE7864A7FC09E3B253EB9AF10E0ACB2156FCC6AFAEA7FCB7FEF9B9B9B`
- Fixture PID `16996`; fixture window `244516272`.

## Setup

- Fresh Sky enumeration returned the exact synthetic `Boo QA Workspace` fixture.
- The fixture was activated, maximized through its observed accessibility
  `Maximize` control, and verified with a full-window screenshot showing only
  the disposable QA workspace and `Create account` target.
- Boo controls were opened from that observed fixture with `Control_L+Alt_L+b`.
- The credential control remained masked; no microphone or voice action ran.

## Pre-request idle state (not a failure)

Candidate4 text-only accessibility reported:

- `CaptureIndicator`: `Screen sharing off · Primary display`
- `StatusText`: `Ready`
- `ListeningStatus`: `Typing is ready. Microphone starts only after Talk.`
- Credential control: `Provider credential masked input` (no value exposed)

These are expected idle values before a request: capture starts only after the
request is submitted. I initially misclassified the idle capture value as a
failure and sent the documented emergency Stop shortcut
`Control_L+Alt_L+F12` once before submitting. This was a premature Stop; the
post-stop accessibility state reported `Stopped. Microphone and capture are
idle until you activate them again.` No provider request was used in that
initial attempt.

The authorized single request was subsequently run from a fresh fixture state;
its post-submit capture/status result is recorded below.

## Authorized request and post-submit result

- Fresh fixture enumeration and activation were repeated; the fixture was
  maximized and showed only the synthetic target.
- Boo was reopened from that observed fixture. The question field was the
  focused control before typing.
- Exact request typed and submitted once: `Explain the Create account button
  and draw a ring around it.`
- Submit started: `2026-11-07T06:16:00.934Z`; key completed:
  `2026-11-07T06:16:01.123Z`.
- `StatusText`: `Reply complete. No action was committed.`
- `CaptureDetail`: `No stable external window was selected. Bring the window
  forward, then reopen Boo and retry.`
- `CaptureIndicator`: `Screen sharing off · Primary display`.
- Visible Boo reply: `The Create account button typically starts the
  registration for a new account. No screen image was supplied, so I can’t
  locate or draw a ring around it. Upload a screenshot so I can mark it.`
- Post-error Stop sent once at `2026-11-07T06:16:28.108Z`, completed at
  `2026-11-07T06:16:28.257Z`; final listening text confirmed capture and
  microphone idle.

This confirms the actual capture/retained-target failure and provider response,
but not ring geometry, overlay exclusion, target bounds, or teaching success.
