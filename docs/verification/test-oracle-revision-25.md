# Test-oracle revision 25: additive (2026-11-13)

Branch `quality/netarchtest` (agent-trust plan, Phase 1, PR 1.11). Made with `scripts/test_oracle.py --extend`:
new files only; every file frozen in revision 24 keeps its hash.

## Added frozen files (exactly 3, one new project)

| File | What it is |
| --- | --- |
| `tests/Boo.Architecture.Tests/Boo.Architecture.Tests.csproj` | xUnit project; references the 5 `src` projects; `NetArchTest.Rules` 1.3.2 and `Mono.Cecil` 0.11.3 (the version NetArchTest itself uses) |
| `tests/Boo.Architecture.Tests/ArchitectureRuleTests.cs` | 6 `[Fact]`s, one per rule BOO-ARCH-001..006 (plan section 2.4 B; texts in `docs/quality/RULES.md`) |
| `tests/Boo.Architecture.Tests/CecilScan.cs` | exact-type reference scan used by BOO-ARCH-002 and 003 (no `[Fact]`) |

Changed frozen files: none. Removed: none. `Boo.sln` gains the project (not an oracle file).

## Baselines (exact allowlists, measured on `lane/face` `af089dd`)

| Rule | Allowlist | Plan section 1.5 |
| --- | --- | --- |
| BOO-ARCH-001 Core: no network / registry / SQLite / NAudio / Velopack | `Boo.Core.ProviderFailureClassifier` | 1 (`ProviderFailure.cs`) |
| BOO-ARCH-002 Core: no `File` / `Directory` | `Boo.Core.FileActionResolver`, `Boo.Core.ScopedFiles` | 2 (`ActionPolicy.cs:209`, `FileActionResolver.cs:38`; `ScopedFiles` is the class in `ActionPolicy.cs` that holds line 209) |
| BOO-ARCH-003 App: no `HttpClient` / `ClientWebSocket` | `Boo.App.MainWindow` | 3 references, all in this one type (`MainWindow.xaml.cs:65,1624,2123`) |
| BOO-ARCH-004 Windows: no WPF controls / shapes / windows | none | 0 |
| BOO-ARCH-005 no mutable statics | `Boo.App.AccessibilitySettings._uiSettings`, `Boo.App.App.StartedInBackground` | 2 (same two) |
| BOO-ARCH-006 window code-behind <= 2,674 lines | budget 2,674 | 0 over (largest `MainWindow.xaml.cs`, 2,674) |

Each list is exact both ways: a new offender fails, and a fixed one fails until removed, in a reviewed
`--rebaseline`. BOO-ARCH-006 is a budget: only a file over it fails.

## Why this does not weaken anything

Purely additive: no existing test, assertion, timeout or `Skip` is touched, and the new tests contain no `Skip`.

## Red proof

A throwaway class added to `Boo.Core` with a mutable static field and a `System.IO.File.Exists` call made
BOO-ARCH-005 and BOO-ARCH-002 fail with the new offender's name, while a second throwaway class that only reads
`System.IO.FileAttributes` was **not** flagged (exact-type matching). Removing it made all 6 pass again. Local run
first; the CI red proof is recorded in the PR description.

## Choices the reviewer should check

- **NetArchTest matches names by prefix** (`System.IO.File` would also match `FileAttributes`, `FileNotFoundException`).
  So BOO-ARCH-002 and 003, which name exact types, use `CecilScan` (Mono.Cecil, the library NetArchTest is built on)
  instead; 001 and 004 are about whole namespaces and use NetArchTest.
- Offenders are named per outermost type, so a lambda or async state machine counts towards its owner.
- The app assembly is loaded as `Boo` (`src/Boo.App` sets `AssemblyName` to `Boo`).
