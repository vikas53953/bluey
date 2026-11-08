# Native teaching check — candidate3

- Source commit: `b27b330a50e7414c921877319409bb12fd4b50f8`
- Candidate ZIP SHA-256: `43F595D8A5A5DEEB1CE9779352E5E7D482545E4409DD31263393B2E6CD98881A`
- Candidate manifest SHA-256: `3399B2855EA19C8EE917E7257F5753820CD475D419AC7FEE9C1416935257294F`
- Candidate `Boo.dll` SHA-256: `44E3330AD2CA98C1CBA33D435EEACC843F60EB79E36505CBE298A9CA499B0CBF`
- Boo PID `26884`; fixture PID `16996`; fixture window `244516272`.
- Boo controls window observed from Sky: ID `189532212`, title `Boo controls`.

## Setup and request

- The synthetic fixture was returned by Sky, activated, and observed with a
  full-window screenshot. It was maximized and showed only the disposable QA
  workspace and `Create account` target.
- Boo was opened with `Control_L+Alt_L+b` from the observed fixture.
- Boo was observed text-only with the question field focused. The credential
  field remained a masked/empty control; no microphone or voice action ran.
- Exact typed request: `Explain the Create account button and draw a ring around it.`
- Exactly one provider request was submitted with `Control_L+Enter`.

## Timestamps (UTC)

- Submit started: `2026-11-07T05:57:56.478Z`
- Submit key completed: `2026-11-07T05:57:56.598Z`
- First response observation: `2026-11-07T05:58:18.602Z`
- Emergency Stop shortcut sent: `2026-11-07T05:58:35.089Z`

## Visible result

The transcript showed:

- You: `Explain the Create account button and draw a ring around it.`
- Boo: `A “Create account” button typically starts registration for a new account. No screen image was supplied, so I can’t locate or draw a ring around it. Please upload a screenshot.`

The text-only tree exposed the capture-indicator label, Sources label, and Boo
status label, but no populated capture/status/source value. The credential
control remained masked and no approval UI appeared.

Because Boo reported that no screen image was supplied, this is a capture/
retained-target failure for the teaching request. `Control_L+Alt_L+F12` was sent
once as emergency Stop; no retry or further input followed. The response proves
typed request round-trip and provider response only. It does not prove ring
geometry, overlay exclusion, target bounds, or teaching success.
