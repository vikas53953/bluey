# Appearance save feedback acceptance

This additive native contract freezes truthful feedback for the appearance
settings workflow. It is evaluated against the exact packaged Windows
candidate through supported accessibility/UI observation and file readback; it
is not a source-text, XAML, or automated-pass claim. Existing frozen acceptance
files are unchanged.

## Candidate14 RED evidence

The RED observation is bound to the Candidate14 receipt
`docs/verification/native-shell-candidate14.md`:

- Source: `1493d18fb1133a645ee3f6ee0110ddaedf863453`.
- Executable: `artifacts/candidate14/Boo.exe`.
- Boo.exe SHA-256:
  `5E896AE774EBF582B17BDBF2C6C18A7F23526019B9088127FDDED42D02C8793E`.
- A valid save of body `#123456` and eyes `#F0F0F0` produced the general
  status `Settings saved. Credential input was protected and cleared from the UI.`
  and appearance bytes `{"BodyColor":"#123456","EyeColor":"#F0F0F0"}`
  with SHA-256
  `8AA0ABB263449B7094BA0FC63538D659CF8AEA19FBFF36BA8A35D0BD77B3265A`.
- A subsequent invalid save of body `#777777` and eyes `#888888` exposed the
  detailed validation message `Appearance was not saved: BodyColor and
  EyeColor must have a contrast ratio of at least 3:1.` and preserved those
  last-good bytes, but the general status still said `Settings saved`.

That stale general status is the observed RED for AP-FEEDBACK-001. The receipt
does not establish reset-status or post-rejection-valid-save feedback, so those
cases remain unverified until directly observed.

## Native regression contract

Each run must record the exact candidate number, executable path and SHA-256,
source commit, Windows build, supported driver, accessibility observations,
timestamps, and before/after appearance file bytes or hashes. A driver that
cannot expose the required native fields makes the case **UNVERIFIED**, not a
pass. Do not infer feedback state from source text or a build result.

| ID | Scenario | Required observable result | Current evidence |
| --- | --- | --- | --- |
| AP-FEEDBACK-001 | Save valid body `#123456` / eyes `#F0F0F0`; then attempt invalid body `#777777` / eyes `#888888`. | The general status reports the current rejection, the detailed validation text reports the contrast failure, and `appearance.json` remains byte-for-byte equal to the valid last-good file. It must not continue to report the prior success. | **RED on Candidate14:** detailed validation and unchanged bytes were observed, but general status remained `Settings saved`. |
| AP-FEEDBACK-002 | After AP-FEEDBACK-001 rejection, activate Reset appearance. | Validation text is cleared; general status reports reset success; body/eye controls and `appearance.json` contain actual black/white values (`#000000` / `#FFFFFF`) with matching readback bytes. | **UNVERIFIED:** Candidate14 receipt observed black/white reset bytes, but did not establish both required feedback states. No reset failure is claimed. |
| AP-FEEDBACK-003 | After AP-FEEDBACK-001 rejection, enter a new valid custom pair and save. | General status reports truthful save success for the current save, detailed validation is cleared, and `appearance.json` contains exactly the newly saved valid colors. | **UNVERIFIED:** the receipt does not provide the required post-rejection status/validation observations. |

These cases are acceptance gates for the small feedback repair only. They do
not establish history, capture, voice, monitor, provider, credential, or other
native acceptance requirements.
