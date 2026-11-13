# Test-oracle revision 26: reviewed rebaseline (2026-11-13)

Branch `docs/arch-followups` (PR #40). Reason: the non-blocking items from the final review of PR #31 (architecture
rules, revision 25). Both changes make BOO-ARCH-002/003 catch **more**; no offender list or budget changes.

## Changed frozen files (exactly 2, no `[Fact]` added or removed)

| File | Before | After |
| --- | --- | --- |
| `tests/Boo.Architecture.Tests/ArchitectureRuleTests.cs` | An approval in BOO-ARCH-002 counted when the type came from an assembly whose **name** is on `CoreApprovedAssemblies`; built-in names (`int`, `IntPtr`, `string`, `object`, `void`) were trusted with no check at all | The reference must also carry one of Microsoft's public key tokens (`MicrosoftKeyTokens`), **and** the type must really exist in the installed .NET runtime (`CecilScan.IsFromInstalledRuntime`), **and** every framework assembly Core references must load from the installed runtime (`CecilScan.FrameworkReferencesNotFromRuntime`; the test process loads them the way the app would for every dependency that flows on to the test project, which is all of them today). Only Core's own code is trusted unchecked: built-in names go through the same checks. A name or token is only a claim (.NET does not check strong-name signatures); the runtime checks stop a project or package that borrows a framework name or type name, and one that ships its own higher-version copy of a framework file **if that dependency reaches the test project**; one an app project keeps private (`PrivateAssets="all"`) would not be seen (`docs/quality/RULES.md` known gap 3). |
| `tests/Boo.Architecture.Tests/CecilScan.cs` | Attributes were scanned on types and their fields, properties, events and methods | Also on method parameters, return values and generic parameters (attribute type and any `typeof` argument). New `FrameworkReferencesNotFromRuntime`: loads each framework reference and checks its file is in the shared framework folder. New `IsFromInstalledRuntime`: resolves a type with a resolver that searches only the shared framework folder of the running .NET (type forwarders followed) and checks the file it came from is in that folder. The summary names what is still not scanned: attributes on the assembly or module itself (the one that matters, `[module: UnverifiableCode]`, is checked separately), generic constraints, catch clause types, compiled XAML. |

Decision: ACCEPT.

## Why this does not weaken any test

- Purely stricter: each change adds a way to fail, none removes one. No assertion, allowlist, budget, timeout or `Skip`
  changed; the 6 `[Fact]`s are the same.
- On `lane/face` `a3ba89e` all 6 rules still pass with the change (every approved type Core uses today is found in
  the installed runtime, and its references carry the Microsoft tokens `b03f5f7f11d50a3a` or `cc7b13ffcd2ddd51`).

## Red proof

- **CI** (first head `cfe60ba`, run [43396744177](https://github.com/vikas53953/bluey/actions/runs/43396744177), throwaway
  branch `tmp/arch-followups-red-proof`, deleted): BOO-ARCH-002 named both probes, `RedProbeImposter` (a type in the
  approved namespace `System.Linq` from a project named `System.Runtime.Intrinsics`) and `RedProbeParamAttribute`
  (`[DefaultValue(typeof(FileInfo), "x")]` on a parameter); the other 5 rules passed. The run for this head's code is in
  the PR description.
- **Local** (labelled **local**), with the key-token check switched off for the run: the imposter is still caught by
  the runtime lookup alone (`System.Linq.RedFakeDisk` not approved), and probes with the same attribute on a **return
  value** and on a **generic parameter** are caught too. With the probes removed and the check restored all 6 rules pass.
- **Local**, after the review of `7885efc`: a replacement assembly named `System.Runtime.Intrinsics`, version
  99.0.0.0, public-signed with Microsoft's public key (token `b03f5f7f11d50a3a`, which passes the token check), referenced
  from Core: reported as `<System.Runtime.Intrinsics 99.0.0.0 not loaded from the installed .NET runtime>` (the .NET
  loader prefers the higher-version app copy, exactly as it would for Boo). With the probe removed all 6 rules pass.
- **Local**, after the review of `f0b9628`: built-in type names were trusted by name alone (Mono.Cecil reports
  `[Other]System.String` as a string). Now only Core's own code skips the checks. A probe project `RedFakeString`
  declaring `System.String` and `System.Int32` with a disk-writing method, called from Core through an extern alias, is
  reported as `Boo.Core.RedProbeFakeString uses (not approved for Core): System.Int32, System.String`. With the probe
  removed all 6 rules pass.
