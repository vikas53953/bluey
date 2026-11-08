# Native expanded-controls receipt

- Candidate source commit: `b27b330a50e7414c921877319409bb12fd4b50f8`
- Candidate ZIP: `artifacts/Boo-0.1.0-candidate3-win-x64.zip`
- Candidate ZIP SHA-256: `43F595D8A5A5DEEB1CE9779352E5E7D482545E4409DD31263393B2E6CD98881A`
- Candidate manifest SHA-256: `3399B2855EA19C8EE917E7257F5753820CD475D419AC7FEE9C1416935257294F`
- Candidate `Boo.dll` SHA-256: `44E3330AD2CA98C1CBA33D435EEACC843F60EB79E36505CBE298A9CA499B0CBF`
- Candidate process: PID `26884`, `artifacts/candidate3/Boo.exe`, responsive.
- Fixture process/window: PID `16996`, window `244516272`, `Boo QA Workspace`.

## Observed keyboard entry

1. Refreshed Sky and selected the returned fixture window.
2. Activated the fixture and captured text-only accessibility state.
3. Sent the documented `Control_L+Alt_L+b` shortcut.
4. After a one-second settle, Sky returned the exact candidate3 window
   `Boo controls` (window ID `189532212`).
5. Text-only accessibility exposed Boo's Stop, Close, capture indicator,
   question input, Send/Talk, mute controls, focus controls, display selector,
   task folder, settings, and status controls. No microphone or provider call
   was made.

## Close and reopen

- The observed Close control was present, but a direct click returned
  `coordinate input geometry is unavailable` under capture exclusion; no
  success was inferred.
- After re-observing, the documented Escape behavior hid the controls. A fresh
  Sky inventory then contained only the fixture for the Boo-native surfaces.
- Re-selecting and activating the returned fixture, then sending
  `Control_L+Alt_L+b`, reopened the exact `Boo controls` window. A fresh
  text-only snapshot confirmed the same accessible controls and focused
  question input.

Result: the candidate3 keyboard-entry, observable-controls, Escape-hide, and
shortcut-reopen subchecks are evidenced. This is not overall native acceptance;
screen capture, annotations, editing, focus return, Stop semantics, live
provider behavior, microphone, and other acceptance rows remain untested.
