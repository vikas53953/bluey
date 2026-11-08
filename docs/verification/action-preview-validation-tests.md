# Action preview validation tests

Date: 2026-11-07

The follow-up public-API suite validates typed multiline, empty, and long
argument values; denied, stale, and unsupported authority; file content alias
precedence including an explicitly empty `contents` value; blank-path fallback;
canonical path overrides; missing roots, parents, and contents; UTF-8 size
limits; malformed overwrite values; existing targets without overwrite; regular
write/delete plans; directory, missing, and traversal delete/path rejection;
and read-only input preservation. Fixtures use only uniquely named directories
under the system temp directory and guarded cleanup.

Command:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -NoRestore -Filter ActionPreviewValidationTests
```

Observed result: exit code 0. Release compilation succeeded for `Boo.Core` and
`Boo.Core.Tests`; the filtered run reported 15 passed, 0 failed, and 0 skipped.

No live APIs, native capture, GUI calls, credentials, or user files were used.
