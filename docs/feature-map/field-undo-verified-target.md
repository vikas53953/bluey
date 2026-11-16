Section: Actions, approval and Undo

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| field-undo-verified-target | After an approved write to a field in another app, Boo still recognises that app's window as its target (a child control announced as foreground never replaces it), so it can re-read the window to verify a field Undo; an Undo it cannot re-read is reported as "partial/unverified ... the recorded edit is kept", never as completed | automated mock (native: a real window and Boo's real TargetObserver and DesktopTools on windows-latest; no Boo.exe, no AI) | `tools/Boo.TypeTarget/TypeTextTargetTests.cs::ApprovedWriteThenRestore_TargetStaysCurrent_SoTheRestoreCanBeVerified` (workflow `type-target`). Whole-app proof is #54's live journey Q16F (second field Undo says "Nothing to undo"), run on this fix in PR #158; issue #157. |
