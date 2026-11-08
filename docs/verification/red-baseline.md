# Test-first baseline

Independent authors wrote the contracts and executable tests before feature implementation. Production seams still throw NotImplementedException. Test fixtures supply external transport data only; they do not implement the features under test.

Root ran the pinned SDK wrapper on 2026-11-07:

- Core: 61 discovered; 60 failed for absent behavior; one explicitly skipped native reparse-point fixture requirement. No passing feature tests.
- Integration: 17 discovered; all 17 failed for missing behavior. The final corrected run is recorded in artifacts/red-provider-baseline.log and TestResults. Negative assertions reject NotImplementedException as a false success.
- Native acceptance: 29 documented cases plus disposable WPF fixture; fixture build passes. These are not passes for Boo itself.

Root review corrected test fixtures before freezing: continuous nudge samples must respect the reference's 45-second maximum gap; forced Stop/commit race must start Stop after commit entered; missing link privileges must never produce PASS; malformed provider data must produce a genuine validation error, not pass on a missing implementation.

Dependency audit originally rejected SQLitePCLRaw.lib.e_sqlite3 2.1.11. Microsoft.Data.Sqlite is now pinned to 10.0.14, whose restore passes without disabling audit warnings.

The baseline commit intentionally contains failing product tests and nonfunctional stubs as required by the user's tests-first pipeline. It is a test-oracle checkpoint, not a deliverable application. All subsequent feature commits require their affected tests to pass. The frozen manifest names its baseline commit and hashes independently authored oracle files.
