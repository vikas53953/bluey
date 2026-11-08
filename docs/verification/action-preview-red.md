# Action preview tracer — RED verification

Date: 2026-11-07

The first approval-preview tracer is recorded before the implementation builder
is released. It covers a `write_file` request whose raw target differs from the
relative `path` override, with an existing parent directory, multiline
`contents`, and an `overwrite` option. The assertions also require ordinal field
ordering and a copied request that is unchanged when the caller mutates its
original argument dictionary.

Command:

```text
./scripts/build.ps1 -Action test -Project tests/Boo.Core.Tests/Boo.Core.Tests.csproj -Configuration Release -NoRestore -Filter ActionPreviewTests
```

Observed result: exit code 1. Release compilation succeeded for `Boo.Core` and
`Boo.Core.Tests`; the filtered run found one test and reported one failure, zero
passes, and zero skips. The failure is the expected initial stub failure:
`System.NotImplementedException` from
`Boo.Core.ActionPreviewBuilder.Create(ToolRequest, TaskAuthority)` at
`src/Boo.Core/ActionPreviewBuilder.cs:16`.

This is genuine RED evidence for the public preview contract, not an
implementation pass. No live APIs, credentials, user files, or UI calls were
used.
