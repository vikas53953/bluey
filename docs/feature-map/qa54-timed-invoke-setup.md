Section: Accessibility and performance

| # | Feature | How verified | Proof |
| --- | --- | --- | --- |
| qa54-timed-invoke-setup | Timing journeys resolve action controls before marked Invoke windows; no UIA search charged to Boo action timing | UNVERIFIED | `tools/Boo.QaSuite/Journeys/FocusEndTimingTests.cs::Q13H_EndFromPaused_HappyIntervalContainsNominalDurationThenRetreats`; cached helpers in focus/Stop neighbors; static `tools/Boo.QaSuite/timing_guard.py`; pending exact-head Q13H/Q13S series. Existing100ms brackets,2500ms nominal and assertions retained. |
