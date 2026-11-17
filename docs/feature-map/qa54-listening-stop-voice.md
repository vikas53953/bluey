Section: Accessibility and performance

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| qa54-listening-stop-voice | j12c Ctrl+Alt+Space actual Listening, physical F12 Stop while chord remains held, then release without restarting | UNVERIFIED | `tools/Boo.VerifyLive/ListeningStopVoiceTests.cs::J12C_StopWhileChordHeld_DoesNotRestartOnRelease`; pending published Boo.exe FlaUI GitHub-hosted Windows run with real provider and bounded synthetic audio. Shared 1s Stop acknowledgement/Listening-clear deadline, unchanged input/capture/playback counters from pre-Stop snapshot with no late-byte allowance, exact Review action visibility sampling, unchanged transcript, 3s held and 3s released watches, 40ms recorder with 100ms UIA read/gap limits. Physical audio and provider request count UNVERIFIED. |

No hosted run performed for this fragment. Existing Q10B failures remain RED; no retry or health-limit relaxation. Root owns workflow/contract integration. Existing product hosted ceiling is one response / 128 output tokens; stopping before completion may produce zero responses. Stable observations only infer absence of a second request; no request receipt means no complete provider claim.
