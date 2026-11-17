Section: Accessibility and performance

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| qa54-eye-hold-voice | j10b hold eyes about three seconds (required physical hold3000-3500ms; full short WAV capture required), release and receive real native voice reply | UNVERIFIED | `tools/Boo.VerifyLive/EyeHoldVoiceTests.cs::Q10B_EyeHold_RecordsAndReleasesARealVoiceTurn`; pending published Boo.exe FlaUI run on GitHub Windows with real provider, synthetic hosted audio devices. Physical microphone/speakers, provider attempt count and canonical j10 capture-loss remain UNVERIFIED. |
