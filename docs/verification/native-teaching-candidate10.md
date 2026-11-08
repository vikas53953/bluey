# Native teaching probe — candidate10

- Source commit: `4df299bd762d3be7d59c29954830af796e4ede85`
- Candidate PID `41924`; exact Boo controls window ID `2361400`.
- Candidate ZIP SHA-256: `397FBEFE1328C3AE13C4C78E71A245E6498D8B9C9AD5337CA5E46E2644730D28`
- Candidate manifest SHA-256: `B2D40FD3D0F0041371FD82940878B979000282534E60546D4E85D6BB2DE4BCC3`
- Candidate `Boo.dll` SHA-256: `BF64E6AE5CF38E6862AA326E1BD5D06B8A1AC29A305C6743A7ED4581BB915D2D`
- Fixture PID `16996`; fixture window `244516272`.

## Setup and verified request

- Fresh Sky enumeration returned the synthetic `Boo QA Workspace`; it was
  activated, maximized through its observed title-bar control, and verified
  with a full-window synthetic-only screenshot.
- Boo controls were opened from that observed fixture; after a short refresh,
  the exact returned controls window was observed text-only.
- The question field was focused and the exact request was entered once and
  verified in refreshed accessibility:
  `Explain the Create account button and draw a ring around it.`
- Pre-request capture was idle and the credential control remained masked; no
  microphone or approval action ran.

## One bounded teaching result

- `Control_L+Enter` submitted once at `2026-11-07T07:57:36.591Z`; key
  completed at `2026-11-07T07:57:36.887Z`.
- Immediate post-submit state: `CaptureIndicator: Screen sharing off ·
  Primary display`, `StatusText: Thinking about the selected context…`, with
  no populated `CaptureDetail` or `AnnotationDetail`. The immediate snapshot's
  exact UTC was not retained; it was taken after the key completed at
  `2026-11-07T07:57:36.887Z` and the bounded tool wait.
- First populated reply/annotation observation: `2026-11-07T07:57:47.209Z`.
  This is 10.618 seconds after submit start, an observed upper bound rather
  than a provider completion timestamp. `CaptureIndicator` was off;
  `StatusText` was `Reply complete. No action was committed.`;
  `CaptureDetail` had no populated value.
- No active-sharing interval was actually observed: both the immediate and
  first-reply snapshots reported sharing off, and no retained poll snapshot
  reported sharing on. Capture active-phase timing therefore remains
  unproven; no bug or pass is inferred from the indicator.
- Exact `AnnotationDetail`: `Screen explanation is shown. Marks are
  click-through and tied to this observation.`
- Exact visible Boo reply:

  `The blue “Create account” button is in the Design preview section. In this QA workspace, it serves as a test target for clicks and annotations rather than a verified account-registration flow. Its label and colors can be changed using the Edit target controls below.`

- The transcript contained the exact user request and this Boo reply. No
  geometry, bounds, or screenshot evidence was claimed from text alone.
- Stop was sent once at `2026-11-07T07:58:00.590Z`, completed at
  `2026-11-07T07:58:00.992Z`. After cleanup, capture remained off and
  `AnnotationDetail` was absent; listening text reported
  `Microphone stopped. Screen activity is shown in the sharing indicator.`
- `Escape` then closed Boo. Final Sky enumeration contained the fixture as
  targetable/foreground; its focused field read `Button text Value: Create
  account`.

This proves one typed-request screen-context round trip and the app's reported
click-through annotation state only. It does not prove ring geometry, overlay
exclusion, target bounds, click-through behavior, or full native acceptance.
