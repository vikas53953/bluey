# Test-oracle revision 26: reviewed rebaseline (2026-11-13)

Branch `docs/arch-followups` (PR #40). Reason: the non-blocking items from the final review of PR #31 (architecture
rules, revision 25). Both changes make BOO-ARCH-002/003 catch **more**; no offender list or budget changes.

## Changed frozen files (exactly 2, no `[Fact]` added or removed)

| File | Before | After |
| --- | --- | --- |
| `tests/Boo.Architecture.Tests/ArchitectureRuleTests.cs` | An approval in BOO-ARCH-002 counted when the type came from an assembly whose **name** is on `CoreApprovedAssemblies` | The assembly must also carry one of Microsoft's public key tokens (`MicrosoftKeyTokens`). A local project or package renamed to, say, `System.Runtime.Intrinsics` is no longer trusted. |
| `tests/Boo.Architecture.Tests/CecilScan.cs` | Attributes were scanned on types and their fields, properties, events and methods | Also on method parameters, return values and generic parameters (attribute type and any `typeof` argument). The summary now names what is still not scanned: attributes on the assembly or module itself (the one that matters, `[module: UnverifiableCode]`, is checked separately), generic constraints, catch clause types, compiled XAML. |

Decision: ACCEPT.

## Why this does not weaken any test

- Purely stricter: each change adds a way to fail, none removes one. No assertion, allowlist, budget, timeout or `Skip`
  changed; the 6 `[Fact]`s are the same.
- On `lane/face` `a3ba89e` all 6 rules still pass with the change (today's references all come from Microsoft-signed
  framework assemblies: tokens `b03f5f7f11d50a3a`, `cc7b13ffcd2ddd51`, `b77a5c561934e089`).

## Red proof (labelled **local**; a CI run link is in the PR description)

- A throwaway project with `AssemblyName` `System.Runtime.Intrinsics` (an approved name) and a type in the approved
  namespace `System.Linq` that writes a file, called from Core: caught as `Boo.Core.RedProbeImposter`
  (`System.Linq.RedFakeDisk` not approved). Without the token check its name and namespace would both pass.
- `[DefaultValue(typeof(FileInfo), "x")]` on a method **parameter** in Core: caught as `Boo.Core.RedProbeParamAttribute`
  (`DefaultValueAttribute`, `FileInfo`). Before this revision parameter attributes were not scanned.
- With the probes removed all 6 rules pass.
