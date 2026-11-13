# Test-oracle revision 25: additive (2026-11-13)

Branch `quality/netarchtest` (agent-trust plan, Phase 1, PR 1.11). Made with `scripts/test_oracle.py --extend`:
new files only; every file frozen in revision 24 keeps its hash.

## Added frozen files (exactly 3, one new project)

| File | What it is |
| --- | --- |
| `tests/Boo.Architecture.Tests/Boo.Architecture.Tests.csproj` | xUnit project; references the 5 `src` projects; `NetArchTest.Rules` 1.3.2 and `Mono.Cecil` 0.11.3 (the version NetArchTest itself uses) |
| `tests/Boo.Architecture.Tests/ArchitectureRuleTests.cs` | 6 `[Fact]`s, one per rule BOO-ARCH-001..006 (plan section 2.4 B; texts in `docs/quality/RULES.md`) |
| `tests/Boo.Architecture.Tests/CecilScan.cs` | reference scan used by BOO-ARCH-002 and 003, driven by a deny predicate (namespace denied by default, short harmless list), plus a native-call (DllImport) scan: base types, interfaces, fields, properties, events, attributes, parameters, return values, locals, and every member called, constructed, cast to or named, generic arguments included (no `[Fact]`) |

Changed frozen files: none. Removed: none. `Boo.sln` gains the project (not an oracle file).

## Baselines (exact allowlists, measured on `lane/face` `af089dd`, re-confirmed on the PR head)

| Rule | Allowlist | Plan section 1.5 |
| --- | --- | --- |
| BOO-ARCH-001 Core: no network / registry / SQLite / NAudio / Velopack | `Boo.Core.ProviderFailureClassifier` | 1 (`ProviderFailure.cs`) |
| BOO-ARCH-002 Core: no disk access (default-deny `System.IO.*` / `System.Xml.*`, `Process`, `FileVersionInfo`, native calls) | `Boo.Core.FileActionResolver`, `Boo.Core.ScopedFiles` | 2 (`ActionPolicy.cs:209`, `FileActionResolver.cs:38`; `ScopedFiles` is the class in `ActionPolicy.cs` that holds line 209) |
| BOO-ARCH-003 App: no network access (default-deny `System.Net.*`, `Windows.Networking.*`, `Windows.Web.*`, WebView2, native calls outside the window DLLs) | `Boo.App.MainWindow` | 3 references, all in this one type (`MainWindow.xaml.cs:65,1624,2123`) |
| BOO-ARCH-004 Windows: no WPF controls / shapes / windows | none | 0 |
| BOO-ARCH-005 no mutable statics | `Boo.App.AccessibilitySettings._uiSettings`, `Boo.App.App.StartedInBackground` | 2 (same two) |
| BOO-ARCH-006 window code-behind <= 2,674 lines | budget 2,674 | 0 over (largest `MainWindow.xaml.cs`, 2,674) |

Each list is exact both ways: a new offender fails, and a fixed one fails until removed, in a reviewed
`--rebaseline`. BOO-ARCH-006 is a budget: only a file over it fails.

## Why this does not weaken anything

Purely additive: no existing test, assertion, timeout or `Skip` is touched, and the new tests contain no `Skip`.

## Red proof

- **CI** ([43378130263](https://github.com/vikas53953/bluey/actions/runs/43378130263), throwaway branch
  `tmp/netarchtest-red-proof`, commit `46d9e4f`, deleted after the run): a mutable static `Boo.Core.ArchRedProbe.Count`
  made the windows-ci step "Architecture tests" fail with `BOO-ARCH-005: new offender(s): Boo.Core.ArchRedProbe.Count`
  (1 failed, 5 passed); the steps after it did not run.
- **Local, every rule** (labelled **local**, no run link; after the review of `8540e1f`): one set of throwaway files made
  **all 6** fail, each naming its probe, for example `Boo.Core.RedProbeCore+Inner.Count` for 005. With the probes removed
  all 6 pass.
- **Local, default-deny 002/003** (labelled **local**; after the review of `0d4e55b`): throwaway probes for every bypass
  that review named were all caught. 002: `new StreamWriter(path)`, `new StreamReader(path)`, `ZipFile.OpenRead`,
  `XDocument.Load(path)`, `Path.GetTempFileName()`, `MemoryMappedFile.CreateFromFile`, a `DllImport` (7 of 7). 003:
  `TcpListener`, `Dns.GetHostEntry`, `SslStream`, `Ping`, a `ws2_32` import (5 of 5). With the probes removed all 6 pass.

## Choices the reviewer should check

- **Default-deny for 002 and 003.** Listing bad types one by one failed review twice (`FileInfo`, then `StreamWriter`,
  `TcpListener`, ...). Now whole namespaces are denied and only a short harmless list is allowed, so an unlisted API is
  caught and a missing harmless type fails loudly. 001 and 004 are plain namespace rules and use NetArchTest.
- Not counted by 003 on purpose: Velopack's update download (`UpdateService`, release lane) and opening a link in the
  browser; `NetworkCredential` is allowed because the app only uses it to read a pasted password.
- Dependency offenders (001-004) are named per outermost type, so a lambda or async state machine counts towards its
  owner; static-state offenders (005) per declaring type, nested types included.
- The app assembly is loaded as `Boo` (`src/Boo.App` sets `AssemblyName` to `Boo`).
- **Known gaps** (also in `docs/quality/RULES.md`): 005 does not see a `static readonly` collection that is changed
  later (`AccessibilitySettings.Listeners`); 006 looks at `src/Boo.App/*Window*.cs` only; 002/003 see what the compiled
  code names, not reflection or function pointers.

## Review history

- Head `8540e1f`: 002 checked only `File`/`Directory`, 003 only `HttpClient`/`ClientWebSocket`. REVIEW FAIL:
  `new FileInfo(p).Exists` passed.
- Head `0d4e55b`: the lists were widened type by type. REVIEW FAIL: `new StreamWriter(path)`, `ZipFile`, `TcpListener`,
  `Dns` and others still passed, and the docs said "the whole class".
- Now: 002/003 are default-deny by namespace. Each time the revision was re-made with `--extend` from `lane/face`'s
  revision 24 before anything merged; the allowlists never changed.
