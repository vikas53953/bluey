# Native keyboard-entry receipt

- Candidate source commit: `0f779533b09bab7ffca9fec892590529175eae3e`
- Candidate ZIP: `artifacts/Boo-0.1.0-candidate2-win-x64.zip`
- Candidate ZIP SHA-256: `32082E32350BBE71EF9F46BFE2114B80718F67F200B4337E5004B1F84930C615`
- Candidate manifest SHA-256: `911A59D6801577ABC1DEAEA7265EBE0608D6B18B3772010C9F6B1D7F02BF0309`
- Candidate `Boo.dll` SHA-256: `FAAE74D85CC2A74A55247405AED97765E3925124E472D007B27C0C63BC73D3E0`
- Candidate process: PID `36176`, `artifacts/candidate2/Boo.exe`, responsive.
- Fixture process/window: PID `16996`, window `244516272`, `Boo QA Workspace`.

## Procedure

1. Refreshed Sky window inventory and selected the returned fixture window.
2. Activated the fixture and captured text-only accessibility state. The tree
   exposed the expected design button, edit fields, Apply edit, Undo, status,
   and scroll controls.
3. Sent the documented product shortcut `Control_L+Alt_L+b` to that observed
   fixture window.
4. Refreshed Sky windows immediately and again after a one-second settle.

## Result

The fixture remained the only returned Boo-native window. No candidate2 notch
or interactive panel appeared in Sky's targetable window inventory. No Boo
window was selected, no screenshot of Boo was taken, and no further input was
sent.

This is `BLOCKED`/evidence-gap for keyboard entry: the shortcut input was sent
to an observed fixture, but the resulting Boo controls were not observable
through the supported Sky route. It is not a product PASS or FAIL, and privacy
exclusion was not disabled.
