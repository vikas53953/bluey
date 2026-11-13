# Test-oracle revision 25: additive (2026-11-13)

Branch `quality/netarchtest` (agent-trust plan, Phase 1, PR 1.11). Made with `scripts/test_oracle.py --extend`:
new files only; every file frozen in revision 24 keeps its hash.

## Added frozen files (exactly 3, one new project)

| File | What it is |
| --- | --- |
| `tests/Boo.Architecture.Tests/Boo.Architecture.Tests.csproj` | xUnit project; references the 5 `src` projects; `NetArchTest.Rules` 1.3.2 and `Mono.Cecil` 0.11.3 (the version NetArchTest itself uses) |
| `tests/Boo.Architecture.Tests/ArchitectureRuleTests.cs` | 6 `[Fact]`s, one per rule BOO-ARCH-001..006 (plan section 2.4 B; texts in `docs/quality/RULES.md`) |
| `tests/Boo.Architecture.Tests/CecilScan.cs` | exact-type reference scan used by BOO-ARCH-002 and 003: base types, interfaces, fields, properties, events, attributes, parameters, return values, locals, and every member called or constructed, generic arguments included (no `[Fact]`) |

Changed frozen files: none. Removed: none. `Boo.sln` gains the project (not an oracle file).

## Baselines (exact allowlists, measured on `lane/face` `af089dd`)

| Rule | Allowlist | Plan section 1.5 |
| --- | --- | --- |
| BOO-ARCH-001 Core: no network / registry / SQLite / NAudio / Velopack | `Boo.Core.ProviderFailureClassifier` | 1 (`ProviderFailure.cs`) |
| BOO-ARCH-002 Core: no disk type (`File`, `Directory`, `FileInfo`, `DirectoryInfo`, `FileSystemInfo`, `FileStream`, `FileSystemWatcher`, `DriveInfo`, `RandomAccess`) | `Boo.Core.FileActionResolver`, `Boo.Core.ScopedFiles` | 2 (`ActionPolicy.cs:209`, `FileActionResolver.cs:38`; `ScopedFiles` is the class in `ActionPolicy.cs` that holds line 209) |
| BOO-ARCH-003 App: no network client or handler (`HttpClient`, `HttpMessageInvoker`, `HttpMessageHandler`, `DelegatingHandler`, `HttpClientHandler`, `SocketsHttpHandler`, `WebSocket`, `ClientWebSocket`, `Socket`, `TcpClient`, `UdpClient`) | `Boo.App.MainWindow` | 3 references, all in this one type (`MainWindow.xaml.cs:65,1624,2123`) |
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
- **Local, every rule** (after the review of `8540e1f` widened 002/003): one set of throwaway files made **all 6** fail,
  each naming its probe: 001 (`HttpRequestError` in Core), 002 (`new FileInfo(path).Exists`, the dodge `RULES.md`
  forbids), 003 (`new SocketsHttpHandler()` in App), 004 (a `Window` subclass in Boo.Windows), 005 (a mutable static in a
  nested class, reported as `RedProbeCore+Inner.Count`, next to `RedProbeCore.Network`), 006 (a 2,701-line
  `RedProbeWindow.Extra.cs`). With the probes removed all 6 pass.

## Choices the reviewer should check

- **Two tools.** 001 and 004 are namespace rules and use NetArchTest. 002 and 003 are about a class of types that
  crosses namespaces (disk access; network clients and handlers), written out type by type and checked with `CecilScan`
  (Mono.Cecil, the library NetArchTest is built on). Value-only types (`Path`, `FileAttributes`, `FileNotFoundException`)
  are deliberately not in the lists.
- Dependency offenders (001-004) are named per outermost type, so a lambda or async state machine counts towards its
  owner; static-state offenders (005) per declaring type, nested types included.
- The app assembly is loaded as `Boo` (`src/Boo.App` sets `AssemblyName` to `Boo`).
- **Known gaps** (also in `docs/quality/RULES.md`): 005 does not see a `static readonly` collection that is changed
  later (`AccessibilitySettings.Listeners`); 006 looks at `src/Boo.App/*Window*.cs` only; a disk or network type not in
  the 002/003 lists is caught only once added.

## Review history

The first freeze (head `8540e1f`) checked only `File`/`Directory` (002) and `HttpClient`/`ClientWebSocket` (003). The
fresh review failed it: `new FileInfo(p).Exists` passed and was even reported as a fix. The lists were widened to the
whole class and this revision was re-made with `--extend` from `lane/face`'s revision 24 before anything merged; the
allowlists did not change (no other type in `src` uses the added types).
