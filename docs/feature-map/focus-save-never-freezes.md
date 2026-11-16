Section: Focus

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| focus-save-never-freezes | A slow or locked focus-history file never freezes Boo: Pause shows the Paused face at once and Boo keeps answering (face reads within 100 ms) while sessions.db is locked; the "Paused ..." status, which promises a saved state, appears only after the write lands, and the stored session is then Paused | automated live (published Boo.exe on windows-latest, no key, no AI) | `tools/Boo.CrashReopen/FocusSaveLockedDiskTests.cs::PauseWhileTheHistoryFileIsLocked_FaceAnswersAtOnce_AndTheSaveLandsAfterTheLock` (workflow `crash-reopen` on every PR); issue #168 |
