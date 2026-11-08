# Native teaching check — candidate7

- Source commit: `f82c17dabab4129c9804c11d1861f2fbfde3a02a`
- Candidate PID `42640`; exact Boo controls window ID `8326622`.
- Candidate ZIP SHA-256: `0797EBB0B57FE78493A6540D51D4F76E7007A9EBFF7B0F2D2723AB185A01981A`
- Candidate manifest SHA-256: `6241E77E71B9222545BA686832C264B6DBF3CE13D89436ADE22DCD260DAA465C`
- Candidate `Boo.dll` SHA-256: `BB255C5C21817D5A141C2D80FCE03734781459E8782DE0275AC03A9DEABA9BE4`
- Fixture PID `16996`; fixture window `244516272`.

## Setup and verified request

- Fresh Sky enumeration returned the synthetic `Boo QA Workspace`; it was
  activated and observed maximized with a full-window synthetic-only
  screenshot.
- Candidate7 controls were opened from that observed fixture with
  `Control_L+Alt_L+b`.
- Text-only accessibility showed the question field focused, capture idle,
  microphone idle, and the credential control masked.
- The exact request was entered once and verified in refreshed accessibility:
  `Explain the Create account button and draw a ring around it.`

## First staged-capture failure

- `Control_L+Enter` submitted once at `2026-11-07T06:36:20.446Z`; key
  completed at `2026-11-07T06:36:20.612Z`.
- The first post-submit `CaptureDetail` and `StatusText` were identical:
  `Windows could not read the captured screen frame (InvalidCastException,
  error 0x80004002). No frame was sent.`
- The transcript contained the exact user request. No provider reply or frame
  was produced. This is the staged frame-read/readback failure; no target
  identity rejection was reported.
- Post-error Stop was sent once at `2026-11-07T06:36:29.456Z`, completed at
  `2026-11-07T06:36:29.658Z`; final listening text confirmed microphone idle.

No retry, microphone, voice, approval, private-app interaction, capture
success, ring geometry, overlay exclusion, or teaching success was claimed.
