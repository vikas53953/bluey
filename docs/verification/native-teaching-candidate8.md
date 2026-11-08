# Native teaching check — candidate8

- Source commit: `7183f52f80b35454d6af57afb6698df4e66bffca`
- Candidate PID `39196`; exact Boo controls window ID `52104710`.
- Candidate ZIP SHA-256: `0C1E9C4BB3CA2FF36FDDE81A8BD8682446793E08FCB02E71B3132C86510CB820`
- Candidate manifest SHA-256: `0F0CB0CDFF8146754B193C22F87F97C968CCAC5A6015ADC2B761177ACDAC2200`
- Candidate `Boo.dll` SHA-256: `DE51859605024C1895AE9C40119532D4F825698B0A10694B0B9175B739DFE82E`
- Fixture PID `16996`; fixture window `244516272`.

## Setup and verified request

- Fresh Sky enumeration returned the synthetic `Boo QA Workspace`; it was
  activated and observed maximized with a full-window synthetic-only
  screenshot.
- `Control_L+Alt_L+b` opened candidate8 controls after a short refresh; the
  exact returned controls window was then observed text-only.
- The question field was focused, capture was idle before Send, microphone was
  idle, and the credential control remained masked.
- The exact request was entered once and verified in refreshed accessibility:
  `Explain the Create account button and draw a ring around it.`

## Bounded result

- `Control_L+Enter` submitted once at `2026-11-07T06:51:54.094Z`; key
  completed at `2026-11-07T06:51:54.221Z`.
- Initial status was `Thinking about the selected context…` with no
  `CaptureDetail`. During the poll, `CaptureIndicator` became
  `Screen sharing on · Primary display`.
- The bounded poll ended at `2026-11-07T06:52:42.345Z` with
  `StatusText: Stopped before the response completed.` No `CaptureDetail`,
  provider reply, or accessible annotation/window evidence appeared. The
  exact user request remained the only transcript line.
- Emergency Stop was sent once at `2026-11-07T06:52:50.935Z`, completed at
  `2026-11-07T06:52:51.128Z`. Final state was capture off, status `Stopped
  before the response completed.`, and listening text
  `Stopped. Microphone and capture are idle until you activate them again.`

This is a bounded incomplete-response result. It is neither evidence of a
capture failure nor evidence of screen-context/annotation or teaching success.
No retry, microphone, audio, approval, private-app interaction, or consequential
action occurred.
