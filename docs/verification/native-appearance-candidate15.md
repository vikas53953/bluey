# Candidate15 native appearance feedback retest

Date: 2026-11-07  
OS: Microsoft Windows 11 Pro, version `10.0.26200`, build `26200` (root verified after the native run).  
Driver: supported `@oai/sky` through node_repl.  
Source: `114e4999fb077312a4bd83acfa71b3ef74b3eb8f`  
Executable: `artifacts/candidate15/Boo.exe`  
Executable SHA256: `CBE583B4A7607E9BA4CDEEC98F2019FCFBC2BF90037F9253CB3EDD7C5B85441E`  
Boo.dll SHA256: `BE9D8873B194F103060A212610A4F3D7EE3F11AC54E76E6A3E08B5EC5CFB19B2`  
Manifest SHA256: `BE6E9ACFB5D4EFEA4826EE970BA5673415E6DB7898CF221940EC7D10619F9BF7`  
ZIP SHA256: `F7666589300C5CCE5EA62E97046B3FC0450FF740278708A257046C4CE2324D50`

## Bounded isolated retest

The test used a newly created empty profile at `artifacts/qa-profile-candidate15-feedback`; no credentials or user profile files were copied or read. Candidate15 ran as PID 5896 and was stopped only after its exact executable path was revalidated. The supported Sky driver opened the full window from a fresh non-fixture Codex window with one `Control_L+Alt_L+b` action.

Results:

- AP-FEEDBACK-001: PASS. A valid `#123456` / `#F0F0F0` save reported the normal success text. An invalid `#777777` / `#888888` save reported both the general rejection and the detailed 3:1 contrast rejection; the last-good file hash stayed unchanged.
- AP-FEEDBACK-002: PASS. Reset reported `Appearance reset to black with white eyes`, cleared the detailed validation line, restored the fields to black/white, and wrote the matching file bytes.
- AP-FEEDBACK-003: PASS. The repeated invalid pair again reported both rejection lines. The later valid `#102030` / `#FFFFFF` pair produced the normal saved-status text in a fresh field-specific read and the exact matching file hash/content. The tool output for the save dispatch was truncated, so the receipt preserves that limitation; no save retry was made.

Exact sanitized state lines and file hashes are in [native-candidate15-appearance-state.txt](../../artifacts/native-candidate15-appearance-state.txt).

Root inspected those excerpts and independently rehashed the candidate executable, DLL and final isolated appearance file. They match the receipt; the final file contains `#102030` / `#FFFFFF`. The exact timestamp of the final AP-FEEDBACK-003 field-specific read was not retained, although its post-action state and file hash were. No latency result is inferred from this run.

These checks establish native accessibility/file-feedback behavior only. They do not prove rendered colors, hover/reveal behavior, screenshot visibility, or visual appearance of the capture-excluded notch.

## Final normal-profile presentation

After the isolated process was stopped, candidate15 was launched as PID 31424 from the exact executable without a `BOO_DATA_DIR` override. One fresh supported `Control_L+Alt_L+b` opened window id `26676650` (`Boo companion`). At the final observation the app reported `StatusText: Ready`, `CaptureIndicator: Screen sharing off · Primary display`, typing-ready microphone status, black/white appearance fields, and the normal no-saved-sessions history message. The app was left open and idle. No default-profile files were inspected, and no provider request, capture, microphone, or settings mutation occurred during presentation launch.
