| Date | Decision | Why | Source | Status |
| --- | --- | --- | --- | --- |
| 17 Nov 2026 | Contract 18 requires all five offline spoken-answer tests added by merged PR #180, as ST8-ST12 in the smoke group. | The exact-head contract gate rejected these unmapped methods at 71b32b5. Register every method without exclusions or changes to existing acceptance rules. | PR #54; run 44058358522; tools/Boo.LiveSmoke/SpokenAudioTests.cs. | UNVERIFIED pending static and hosted checks; offline tests are mock evidence and cannot establish native voice or aggregate acceptance. |
