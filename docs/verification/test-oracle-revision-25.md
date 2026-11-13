# Test-oracle revision 25: additive (2026-11-13)

Branch `quality/netarchtest` (agent-trust plan, Phase 1, PR 1.11). Made with `scripts/test_oracle.py --extend`:
new files only; every file frozen in revision 24 keeps its hash.

## Added frozen files (exactly 3, one new project)

| File | What it is |
| --- | --- |
| `tests/Boo.Architecture.Tests/Boo.Architecture.Tests.csproj` | xUnit project; references the 5 `src` projects; `NetArchTest.Rules` 1.3.2 and `Mono.Cecil` 0.11.3 (the version NetArchTest itself uses) |
| `tests/Boo.Architecture.Tests/ArchitectureRuleTests.cs` | 6 `[Fact]`s, one per rule BOO-ARCH-001..006 (plan section 2.4 B; texts in `docs/quality/RULES.md`) |
| `tests/Boo.Architecture.Tests/CecilScan.cs` | reference scan used by BOO-ARCH-002 and 003, driven by the rule's type and member predicates and reporting what it found, plus a native-call and COM scan: base types, interfaces, fields, properties, events, attributes, parameters, return values, locals, and every member called, constructed, cast to or named, generic arguments included (no `[Fact]`) |

Changed frozen files: none. Removed: none. `Boo.sln` gains the project (not an oracle file).

## Baselines (exact allowlists, measured on `lane/face` `af089dd`, re-confirmed on the PR head)

| Rule | Allowlist | Plan section 1.5 |
| --- | --- | --- |
| BOO-ARCH-001 Core: no network / registry / SQLite / NAudio / Velopack | `Boo.Core.ProviderFailureClassifier` | 1 (`ProviderFailure.cs`) |
| BOO-ARCH-002 Core: no disk access (allowlist of approved framework types; `Path`, `Environment`, `Type`, `Delegate`, `AppContext` member by member; no native, COM or `[UnsafeAccessor]` call) | `Boo.Core.FileActionResolver`, `Boo.Core.ScopedFiles` | 2 (`ActionPolicy.cs:209`, `FileActionResolver.cs:38`; `ScopedFiles` is the class in `ActionPolicy.cs` that holds line 209) |
| BOO-ARCH-003 App: no network access (deny `System.Net.*` except harmless types, WebView2, WPF browser controls, other Windows Runtime areas, native network DLLs) | `Boo.App.MainWindow` | 3 references, all in this one type (`MainWindow.xaml.cs:65,1624,2123`) |
| BOO-ARCH-004 Windows: no WPF controls / shapes / windows | none | 0 |
| BOO-ARCH-005 no mutable statics | `Boo.App.AccessibilitySettings._uiSettings`, `Boo.App.App.StartedInBackground` | 2 (same two) |
| BOO-ARCH-006 window code-behind <= 2,674 lines | budget 2,674 | 0 over (largest `MainWindow.xaml.cs`, 2,674) |

Each list is exact both ways: a new offender fails, and a fixed one fails until removed, in a reviewed
`--rebaseline`. BOO-ARCH-006 is a budget: only a file over it fails.

## Why this does not weaken anything

Purely additive: no existing test, assertion, timeout or `Skip` is touched, and the new tests contain no `Skip`.

## Red proof

- **CI, one rule** ([43378130263](https://github.com/vikas53953/bluey/actions/runs/43378130263), throwaway branch
  `tmp/netarchtest-red-proof`, deleted after the run): a mutable static `Boo.Core.ArchRedProbe.Count` made the
  windows-ci step "Architecture tests" fail with `BOO-ARCH-005: new offender(s): Boo.Core.ArchRedProbe.Count`.
- **CI, every rule and every bypass the reviews named**: see the PR description (run link, throwaway branch
  `tmp/netarchtest-red-proof-3`, deleted after the run; an earlier run of the first 13 probes is
  [43385545148](https://github.com/vikas53953/bluey/actions/runs/43385545148)). 002 probes: `FileInfo.Exists`,
  `Path.Exists`, `Path.GetTempFileName`, `StreamWriter(path)`, `ZipFile`, `XDocument.Load(path)`, `TarFile`,
  `TextWriterTraceListener(path)`, `X509CertificateLoader.LoadCertificateFromFile`, `ResourceReader(path)`,
  `AssemblyLoadContext.LoadFromAssemblyPath`, a `DllImport`, a `[ComImport]` class, `Environment.CurrentDirectory = p`,
  `Environment.GetLogicalDrives`, `Type.GetType("System.IO.File")`, `Delegate.CreateDelegate(type, target, name)`, an
  `[UnsafeAccessor]` bound to `File.WriteAllText` (18). 003 probes:
  `SocketsHttpHandler`, `TcpListener`, `Dns`, `WebBrowser.Navigate`, `Windows.Storage.StorageFile`, a `ws2_32` import
  (6). Plus one probe each for 001, 004, 005 and 006. Locally (labelled **local**) all of them were caught before the
  CI run; with the probes removed all 6 rules pass.

## Choices the reviewer should check

- **002 is an allowlist, 003 a deny rule.** Lists of bad types failed review three times (`FileInfo`; then
  `StreamWriter(path)`, `TcpListener`; then `Path.Exists`). Core is small, so it gets an allowlist of the framework types
  it may use (measured from today's code); a type with any risky member (`Path`, `Environment`, `Type`, `Delegate`,
  `AppContext`) is approved member by member. The app is too big for that, so
  003 denies the network surfaces and its gaps are named in `docs/quality/RULES.md` (known gap 4).
- Approving a new framework type for Core edits this frozen test, so it is a reviewed oracle revision; the failure
  message names the type.
- Not counted by 003 on purpose: Velopack's update check and download (`UpdateService`, release lane) and opening a
  link in the browser; `NetworkCredential` is allowed because the app only uses it to read a pasted password.
- Dependency offenders (001-004) are named per outermost type, so a lambda or async state machine counts towards its
  owner; static-state offenders (005) per declaring type, nested types included.
- The app assembly is loaded as `Boo` (`src/Boo.App` sets `AssemblyName` to `Boo`).

## Review history

- Head `8540e1f`: 002 checked only `File`/`Directory`. REVIEW FAIL: `new FileInfo(p).Exists` passed.
- Head `0d4e55b`: lists widened type by type. REVIEW FAIL: `StreamWriter(path)`, `ZipFile`, `TcpListener`, `Dns` passed.
- Head `ad20192`: deny by namespace. REVIEW FAIL: `Path.Exists` passed (`Path` was on the harmless list), and the docs
  claimed "an API nobody listed is caught" while framework APIs outside the denied namespaces still reached the disk.
- Head `8411370` (`32054fd` synced onto `lane/face` `342b93d`): allowlist. REVIEW FAIL: `System.Environment` was approved
  whole, so `Environment.CurrentDirectory = p` checked a folder; `[UnsafeAccessor]` could bind to `File.WriteAllText`.
- Now: 002 is an allowlist with risky types approved member by member and `[UnsafeAccessor]` counted; 003 a deny rule
  with named gaps. Each time the revision was re-made with `--extend` from
  `lane/face`'s revision 24 before anything merged; the allowlists of offenders never changed.
