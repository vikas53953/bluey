# Native teaching probe — candidate9

- Source commit: `1b9bfa58e26a6d674ead11943709cd3ef4375f81`
- Candidate PID `31172`; exact Boo controls window ID `1116672`.
- Candidate ZIP SHA-256: `6C95F535094CE6FBCB07A9141F97AD32EED45B889DBDCC47007F1FF3438F6E2A`
- Candidate manifest SHA-256: `C53E8F73973E84AEAF8422A4379B7A78EB6D04AC99C44875C3073001B599F2FC`
- Candidate `Boo.dll` SHA-256: `C3FE57469C150E5E5ED388B06AE6A54EDDE133125DAFCFFB12B5B57F4297FD69`
- Fixture PID `16996`; fixture window `244516272`.

## Setup and verified request

- Fresh Sky enumeration returned the synthetic `Boo QA Workspace`. It was
  activated, maximized through its observed title-bar control, and verified
  with a full-window synthetic-only screenshot.
- Boo controls were opened from that observed fixture. After a short refresh,
  the exact returned controls window was observed text-only.
- The question field was focused; the exact request was entered once and
  verified in refreshed accessibility:
  `Explain the Create account button and draw a ring around it.`
- Pre-request capture was idle and the credential control remained masked; no
  microphone or approval action ran.

## One bounded teaching result

- `Control_L+Enter` submitted once at `2026-11-07T07:33:23.256Z`; key
  completed at `2026-11-07T07:33:23.457Z`.
- Capture briefly reported `Screen sharing on · Primary display`, then was
  off after the response.
- `StatusText`: `Reply complete. No action was committed.`
- `CaptureDetail`: no populated value was exposed.
- First populated reply/status observation: `2026-11-07T07:33:34.906Z`. This
  is the first retained observation after polling began, an upper bound on
  provider completion rather than a provider completion timestamp.
- Exact visible Boo reply:

  `The blue “Create account” button is in the Design preview section. In this QA workspace, it serves as a test target for checking clicks through annotations, rather than a confirmed account-registration flow. The fields below let you change its label and colors.`

- The transcript contained the exact user request and this Boo reply. No
  accessible annotation/ring window or geometry evidence appeared.
- Stop was sent once at `2026-11-07T07:34:31.883Z`, completed at
  `2026-11-07T07:34:32.025Z`. Final Boo listening text confirmed capture and
  microphone idle.
- `Escape` then closed Boo. Final Sky enumeration contained the fixture as
  the targetable app; its focused field read `Button text Value: Create
  account`.

This proves one typed-request round trip and a screen-context reply only. It
does not prove ring geometry, overlay exclusion, target bounds, click-through,
or full native teaching acceptance.
