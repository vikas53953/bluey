# Native teaching check — candidate6

Candidate6 is based on base source `50636a5bb8713dff43925e97afae9aa142c0a988`
plus the uncommitted seven-line source fix. It must not be described as a
clean committed revision.

- Candidate PID `46648`; exact Boo controls window ID `50726836`.
- Source diff SHA-256: `272562EE2E6145ABD2CF9E50195C63A66B2DFD84B9682D379E0C94920856571`
- Full source-hash manifest SHA-256: `7CA2E8B6BFC36BFBDC2E8D4E3BEB441F6E0B08EBAFA6348E77D6C30592602145`
- Candidate ZIP SHA-256: `DC5937BD93A9F03740813461C644B5DACBEC5E923CD4C6592016A4297A4FB6BE`
- Candidate `Boo.dll` SHA-256: `A11255C8149F1EDBADF7A5798A6FC09FB938CC8D5A582784553269CD76CCC958`
- Fixture PID `16996`; fixture window `244516272`.

## Required reopen/resize reproduction

- Fresh Sky enumeration returned the synthetic `Boo QA Workspace`; its first
  observed screenshot was synthetic-only.
- `Control_L+Alt_L+b` opened candidate6 controls (window `50726836`). The
  controls were observed text-only, then closed with `Escape`; no provider
  request occurred in this phase.
- The fixture was freshly activated. Its observed bounds changed from a
  bounded `889x675` capture to a maximized `1440x852` capture by invoking the
  observed title-bar Maximize control after foreground activation.
- `Control_L+Alt_L+b` reopened the existing candidate6 controls after that
  resize. The question field was observed focused, with credential masked and
  microphone idle.

## One verified request and first failure

- The exact request was entered once and verified in refreshed accessibility:
  `Explain the Create account button and draw a ring around it.`
- `Control_L+Enter` submitted once at `2026-11-07T06:31:25.683Z`; key
  completed at `2026-11-07T06:31:26.071Z`.
- The first post-submit `CaptureDetail` and `StatusText` were identical:
  `Screen capture failed (InvalidCastException, error 0x80004002). No frame was
  sent.`
- The transcript contained the exact user request. No provider reply or frame
  was produced.
- Post-error Stop was sent once at `2026-11-07T06:31:34.073Z`, completed at
  `2026-11-07T06:31:34.511Z`; final listening text confirmed capture and
  microphone idle.

The target-identity rejection reproduced in candidate5 did not occur here.
This result is evidence only of a later screen-capture/WGC failure, not of
teaching success, ring geometry, overlay exclusion, or target capture success.
