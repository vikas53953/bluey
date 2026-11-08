# NAT-004 focus-return check — candidate8

- Candidate source: `7183f52f80b35454d6af57afb6698df4e66bffca`
- Candidate PID `39196`; Boo controls window `52104710`.
- Synthetic fixture PID `16996`; fixture window `244516272`.
- Package identity and manifest are recorded in
  `docs/verification/native-teaching-candidate8.md`; manifest SHA-256:
  `0F0CB0CDFF8146754B193C22F87F97C968CCAC5A6015ADC2B761177ACDAC2200`.

## Action and readback sequence

- Fresh Sky enumeration returned the exact disposable fixture. Its observed
  editable `Button text` field was focused with initial value `Create account`.
- `Control_L+a` selected the observed field value (`selected_text:
  Create account`). One unsaved fixture sentinel was typed and read back:
  `NAT004_FIXTURE_A` (typing action UTC `2026-11-07T07:14:07.321Z` to
  `07:14:07.444Z`). No Apply action was used.
- Boo was opened with `Control_L+Alt_L+b`. Its observed `InputBox` was focused;
  one different unsubmitted sentinel was typed and read back as
  `NAT004_BOO_B` (typing action UTC `2026-11-07T07:14:31.380Z` to
  `07:14:31.975Z`). No submit key, provider call, microphone, or approval
  action occurred.
- `Escape` closed Boo. A fresh fixture observation showed the fixture
  foreground and its `Button text` field focused, still containing
  `NAT004_FIXTURE_A`.
- After `Control_L+a` on that observed returned field, the second fixture
  sentinel `NAT004_FIXTURE_B` was typed once (typing action UTC
  `2026-11-07T07:15:00.092Z` to `07:15:00.356Z`). The immediate refreshed
  readback still showed `NAT004_FIXTURE_A`; a later read immediately before
  restoration showed `NAT004_FIXTURE_B`. This is an accessibility/update timing
  caveat, not cross-window leakage.
- Supported indexed value replacement restored the fixture field to
  `Create account` without Apply.
- Boo was reopened only to clear its unsubmitted `NAT004_BOO_B` via supported
  indexed value replacement. Readback showed an empty `InputBox`; `Escape`
  closed Boo again.
- Final fresh fixture readback showed foreground/focus on `Button text` with
  value `Create account`.

## Verdict

Focus separation and return were observed: Boo's unsubmitted sentinel did not
appear in the fixture, and the fixture's original value was restored. The
immediate stale readback after the second fixture sentinel is a tooling/UI
accessibility timing caveat. This was a no-API focus check only, not current
source or full native acceptance. Action-level timestamps were not retained
for initial activation, focus-selection/observation, Boo open/Escape close,
indexed restore/clear, or final close; only the three typing intervals above
are timestamped. Mouse activation and closed/reused-window variants remain
untested.
