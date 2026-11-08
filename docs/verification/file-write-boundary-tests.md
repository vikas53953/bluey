# File-write byte-boundary tests

Date: 2026-11-07

This independent resolver suite uses the literal agreed maximum of 4,194,304
UTF-8 bytes. It verifies that an exactly 4,194,304-byte payload is accepted as
a plan without creating the target file, and that a 4,194,305-byte payload is
blocked. Both payloads include a multibyte `é` prefix so the boundary is based
on encoded bytes rather than character count. Fixtures use only a uniquely
named, guarded owned temp directory.

Command:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -NoRestore -Filter FileWriteBoundaryTests
```

Observed result: exit code 0. Release compilation succeeded for `Boo.Core` and
`Boo.Core.Tests`; the focused run reported 2 passed, 0 failed, and 0 skipped.

No production, frozen-test, oracle, GUI, live-API, credential, or actual target-file
writes were performed.
