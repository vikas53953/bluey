# Shell failure recovery test-first RED

Test author: independent shell-failure test lane. Baseline checkout:
`013be73c6f2eb11efc032ce406087094c90a9560`.

The executable tests use only the public `AppearanceStore` API and real,
uniquely named temporary paths. They do not inspect source text, private state,
or XAML. Existing tests were not changed. The native fault checklist is
prewritten in `acceptance-shell-failures.md`; no native case was executed in
this lane.

## Added contract

`AppearanceReadFailureTests` covers:

- A directory at the configured appearance path must produce a bounded
  `InvalidDataException` and leave a sentinel file inside that directory
  unchanged.
- A missing parent directory must return default appearance values without
  creating the parent.
- An exclusive lock on an existing saved appearance must produce a bounded
  `InvalidDataException`; after releasing the lock, the saved bytes and
  appearance must still load unchanged.

## RED run receipt

Run date: 2026-11-07 (Asia/Calcutta), Windows x64, Release configuration.

Command:

```powershell
& .\scripts\build.ps1 test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Configuration Release -Filter 'FullyQualifiedName~AppearanceReadFailureTests'
```

The wrapper restored the required projects, compiled the test assembly, and
discovered all three tests. The process exited `1`: one test failed and two
passed. This is the required RED state for the known directory-path defect;
the missing-parent and exclusive-lock behaviors already pass against this
baseline.

Raw TRX: `TestResults/<user>_<machine>_2026-11-07_20_17_25_net10.0.trx`

Observed test outcomes in that TRX:

| Test | Outcome |
| --- | --- |
| `Directory_at_configured_path_reports_bounded_error_and_preserves_sentinel` | Failed |
| `Missing_parent_returns_defaults_without_creating_parent_directory` | Passed |
| `Exclusive_read_lock_reports_bounded_error_and_preserves_saved_bytes` | Passed |

The actual failure message is:

```text
Assert.Throws() Failure: No exception was thrown
Expected: typeof(System.IO.InvalidDataException)
```

The failing assertion is the required directory-path error boundary; the
sentinel-preservation assertion was not reached because no exception was
reported. The TRX is retained locally under the ignored `TestResults/` output
directory and is bound to the baseline SHA above. It must not be reused as
post-repair evidence.

Root reran the same Release filter in the canonical checkout before any repair:
`TestResults/<user>_<machine>_2026-11-07_20_18_43_net10.0.trx`.
It independently reproduced the same one failure and two passes, exit code 1.
