# Boo quality rules

Every check added by Phase 1 of the [agent-trust plan](../plans/agent-trust-plan.md) has an id here. A failing check
prints **What / Why / Fix / Do NOT / Rule** and links to its anchor below. The "Do NOT" line names the tempting wrong
fix, because that is what an agent reaches for next.

## How the ratchet works

[`scripts/quality/ratchet.py`](../../scripts/quality/ratchet.py) reads every `.cs` file (any letter case) git would
commit under `src/` (tracked files, plus new files that are not git-ignored; build output in `bin/` and `obj/` is ignored,
but a file forced into git there is read) with a small C# lexer (strings, `$"{...}"` interpolation holes and their format
clauses, verbatim and raw strings, char literals, line and block comments, `#` directive lines, every C# line break). It
counts each pattern below **per project, every match**, and compares with [`baseline.json`](baseline.json).

**What it promises: it catches ordinary spellings, and it never counts less than the `lane/face` ratchet did, except where that one counted text that is not code (listed below).** The text as
written is always counted. Extra *views* of the same file can add hits, never remove them (per line, the highest count
of any view wins): identifiers spelled the plain way (`\u0043ollect` and `@Collect` read as `Collect`; invisible
formatting characters dropped), and `using` aliases and `using static` members written out in full (`using G =
System.GC;` then `G.Collect()`), including `global using` lines in any file of the project. A call split over lines (`System.GC` / `.Collect()`) counts at the line it starts on.
Deliberate evasion (build-file tricks, subclasses, aliases the text cannot resolve) is closed exactly by the compiler
checks of PRs 1.3 and 1.4; see Known gaps.

- a count that goes **up** fails (new problem);
- a count that goes **down** also fails until you run `python -I scripts/quality/ratchet.py --lower` and commit the
  lower baseline, so every gain is locked in; `--lower` refuses to raise any number, and refuses while the layout check
  below fails;
- a C# file or project in `src/` that is not inside a `src/<Name>/` project (a loose `src/X.cs`, a folder without a
  `.csproj`, a project nested deeper such as `src/Group/Hack/Hack.csproj`) fails, because the build would compile it
  but no project count would include it; so does a symbolic link committed under `src/`;
- `--list <RULE>` prints every hit; `--self-test` checks 133 snippets with exact expected counts (every attack the PR
  #23 reviews found that the text can follow is a sample), checks on every snippet that the extra views never count less
  than the text as written, that clean code stays quiet, and the pass/fail logic itself on a throwaway git tree.
- PR #23's evidence (13 Nov 2026): each guard switched off one at a time in a scratch copy made the self-test fail; and
  a differential run over every sample and every review attack file found the new ratchet never counting less than the
  one on `lane/face`, except where the old lexer counted text that is not code. These corrections are deliberate and
  each has a self-test sample: (1) text in an interpolation format clause, such as `$"{d:dd//MM}"`; (2) the text of a
  `#` directive line (`#warning do not call GC.Collect`, `#region static fields`, a `/* */` on `#warning`), except a `//`
  comment on a line other than `#region` / `#endregion`, which counts; as a side effect a `catch { }` whose body holds
  only a directive is "empty" (BOO-ERR-001), not "handles" (BOO-ERR-002); (3) raw and interpolated strings that open with
  more than 12 characters of `$` and quotes, which the old lexer misread. Code that is not valid C# (a Unicode line
  separator inside a string or char literal) may also count differently; the build rejects it anyway. A second review (13 Nov) fuzzed 120,000 random
  inputs and found no other way to count less.

**A PR cannot loosen its own gate.** On a pull request, the workflow
[`quality-gates.yml`](../../.github/workflows/quality-gates.yml) (job `quality`, Linux, about a minute, no uploads)
also runs the **base branch's** `ratchet.py` with the **base branch's** `baseline.json`, both read from git rather than
from the PR, and fails on any rise. Raising a number in the PR's `baseline.json`, or weakening a pattern in the PR's
`ratchet.py`, therefore changes nothing. (The workflow file itself is the remaining hole: see HANDOFF.md, known
issues, item 0.)

A ratchet counts, it does not track identity: removing one hit and adding another of the same rule in the same project
keeps the count equal and passes. That is accepted; the compiler-exact checks in later Phase 1 PRs close it per rule.

## Rules (baseline 13 Nov 2026, IST, measured on `lane/face` 8474556, after PR #12)

| Rule | Pattern | Baseline |
| --- | --- | --- |
| [BOO-ASYNC-001](#boo-async-001) | `Thread.Sleep` | 0 |
| [BOO-ASYNC-002](#boo-async-002) | blocking wait on async work | 1 |
| [BOO-ASYNC-003](#boo-async-003) | `async void`, `+= async` event lambdas | 7 |
| [BOO-UI-001](#boo-ui-001) | `Dispatcher.Invoke` (any dispatcher variable) | 2 |
| [BOO-UI-002](#boo-ui-002) | `Dispatcher.BeginInvoke` (any dispatcher variable) | 15 |
| [BOO-TIME-001](#boo-time-001) | `DateTime(Offset).Now/UtcNow/Today` | 14 |
| [BOO-TIME-002](#boo-time-002) | `Task.Delay` | 1 |
| [BOO-TIME-003](#boo-time-003) | `new DispatcherTimer` | 8 |
| [BOO-RT-001](#boo-rt-001) | `GC.Collect` | 0 |
| [BOO-RT-002](#boo-rt-002) | `Environment.GetEnvironmentVariable` | 3 |
| [BOO-SAFE-003](#boo-safe-003) | `Process.Start` | 3 |
| [BOO-ERR-001](#boo-err-001) | broad `catch` with an empty body | 24 |
| [BOO-ERR-002](#boo-err-002) | broad `catch` that handles and carries on | 74 |
| [BOO-STATE-001](#boo-state-001) | mutable static state | 2 |
| [BOO-CMT-001](#boo-cmt-001) | comment lines that are not `///` (allowed tags excepted) | 399 |
| [BOO-CMT-002](#boo-cmt-002) | `TODO`/`FIXME`/`HACK`/`XXX` without `(#issue)` | 1 |
| **total** | | **554** |

Why 554 and not the plan's 525: the lexer finds 2 the plan's grep missed (a lower-case `dispatcher.BeginInvoke`
and `_pump.Task.Result`), and PR #12 (Living ink), merged on 13 Nov before the ratchet existed, added 25 comment
lines, 1 `BeginInvoke` and 1 broad catch. The baseline starts at what `lane/face` holds today and only goes down.

## BOO-ASYNC-001
**Thread.Sleep** (also `Thread .Sleep`, fully qualified, a method group, and bare `Sleep(` after
`using static System.Threading.Thread`). A sleep hides a race instead of fixing it. **Fix:** await the thing you wait
for (`Task`, `TaskCompletionSource`, `SemaphoreSlim.WaitAsync`) or inject `TimeProvider`. **Do NOT** make the sleep longer.

## BOO-ASYNC-002
**Blocking wait on async work:** `Task.WaitAll/WaitAny`, `.GetAwaiter().GetResult()`, and `.Wait(` / `.Result` on a
variable whose name contains `task`. Blocking from the WPF UI thread deadlocks. **Fix:** make the caller async and
await. **Do NOT** wrap it in `Task.Run` to hide the block.

## BOO-ASYNC-003
**async void** methods and `+= async (...) =>` event handlers. Their exceptions cannot be caught by the caller and can
end the app. **Fix:** return `Task`; a WPF event handler becomes a one-liner that hands an async `Func<Task>` to one
shared helper that awaits it and reports errors. **Do NOT** add a catch-everything block.

## BOO-UI-001
**Dispatcher.Invoke** on any dispatcher variable. A synchronous cross-thread call deadlocks when the UI thread is itself
waiting. **Fix:** `await Dispatcher.InvokeAsync(...)`. **Do NOT** add a timeout to "unstick" it.

## BOO-UI-002
**Dispatcher.BeginInvoke** on any dispatcher variable. Fire and forget: exceptions are lost and the order is a guess.
**Fix:** `Dispatcher.InvokeAsync` and observe the result. **Do NOT** add a delay to "fix" ordering.

## BOO-TIME-001
**Hidden clock.** Time-based behaviour (focus sessions, nudges, update checks) cannot be tested with a fake clock.
**Fix:** take a `TimeProvider` (tests pass a fake) and call `GetUtcNow()` / `GetLocalNow()`. **Do NOT** wrap the clock in
a static helper; that is the same hidden clock.

## BOO-TIME-002
**Task.Delay.** A delay used to let things settle hides a race. **Fix:** wait for the real signal; for a real timeout,
use the `TimeProvider` overload. **Do NOT** make the delay longer.

## BOO-TIME-003
**new DispatcherTimer** (also `DispatcherTimer t = new(...)` and fully qualified). Timers are a common fix for ordering
bugs and cannot be tested without a fake clock. Counted so new ones are visible. **Fix:** prefer an event or an awaited
signal; keep real periodic timers in one owner class. **Do NOT** add a timer to wait for something to finish.

## BOO-RT-001
**GC.Collect.** Hides a leak. **Fix:** find what holds the memory and dispose it. **Do NOT** call it "just in case".

## BOO-RT-002
**Environment.GetEnvironmentVariable.** Hidden switches (for example `BOO_QA_CAPTURABLE`) change privacy behaviour.
**Fix:** read environment variables in one place and pass the value in. **Do NOT** add another reader.

## BOO-SAFE-003
**Process.Start.** Launching a program is permissioned. **Fix:** go through the `ActionPolicy` gate (`DesktopTools`).
**Do NOT** call `Process.Start` directly.

## BOO-ERR-001
**Broad catch, empty body** (`catch`, `catch (Exception)`, `catch (global::System.Exception)`). The failure vanishes.
**Fix:** catch the specific exception you expect and handle it, or let it propagate. **Do NOT** widen the catch.

## BOO-ERR-002
**Broad catch that carries on** (no `throw` in the body). It also catches the bugs you did not expect. **Fix:** narrow
the type, or add a `when (...)` filter for the exceptions you really expect. **Do NOT** log and continue as a habit.

## BOO-STATE-001
**Mutable static state** (static field, or static property with a setter). Leaks between tests and windows, hides
ordering bugs. **Fix:** an instance member owned by one object, or `readonly`. **Do NOT** add a lock around it as the fix.

## BOO-CMT-001
**Comment that is not `///`** (`////` counts). Agents use comments to justify workarounds, and rules written in comments
are not checked. A `//` at the end of a `#` directive line (`#pragma ... // why`, `#endif // HACK`) counts like any
other; on `#region` / `#endregion` the text is a name and does not. Not counted: whole-line comments of the form `// INVARIANT: ... see NameTests.Method` or
`// SECURITY: ... see NameTests.Method`, where a class `NameTests` with a method `Method(` really exists under `tests/`,
and whole-line `// TODO(#123): ...`. The same text at the end of a code line, or naming a test that does not exist,
counts. **Fix:** fix the cause instead of explaining it; if it states a rule, write the test and use
the INVARIANT tag; API docs use `///`. **Do NOT** reword the comment to dodge the count.

## BOO-CMT-002
**Untracked TODO / FIXME / HACK / XXX** in any comment, `///` included. **Fix:** open a GitHub issue and write
`TODO(#123): ...`. **Do NOT** delete the note and keep the debt.

## Known gaps (the text-level ratchet does not see these; PRs 1.3 and 1.4 make them exact)

The ratchet reads text, so a determined writer can always find a spelling the compiler accepts and the text scan does
not follow. After five review rounds (PRs #21 and #23) the rule is: the text ratchet must never count less than before,
and deliberate evasion is closed by the compiler, which sees the real names, types and compiled files.

| Gap | Example | Closed by |
| --- | --- | --- |
| Build files decide what is compiled and which usings apply | `<Compile Include="../x.txt"/>`, `<Using Include="System.GC" Static="true"/>`, `<Import>`, `Directory.Packages.props`, `obj/*.props`, `CustomAfterMicrosoftCommonTargets` | PR 1.3: the compiled file list and usings taken from MSBuild itself (`dotnet msbuild -getItem:Compile -getItem:Using`) and banned APIs checked by the compiler |
| Calls through a subclass or a generic type | `class P2 : Process { ... Start("x"); }`, `Task<int>.Delay(1)` | PR 1.3 (BannedApiAnalyzers resolves the real member) |
| Target-typed `new` with a nullable type | `DispatcherTimer? t = new();` | PR 1.3 |
| `using static` false hits | `using static System.Diagnostics.Process;` plus the class's own `Start()`; `using static System.DateTime;` plus a local `Now` (none in `src/` today) | PR 1.3 |
| Instance calls by name | `.Wait()` / `.Result` on a task variable whose name lacks "task"; an instance `process.Start()` | PR 1.4 analyzers |
| Shape of the code | a broad catch with an unreachable `throw` counts as "rethrows"; a static field or property declared over two lines, or a tuple type plus `=>`; `///` comments inside a method body count as documentation | PR 1.4 analyzers |
| Disabled `#if` blocks | the compiler skips their text, the ratchet reads it, so an unmatched quote or `/*` inside one can hide or invent a later hit (none in `src/` today) | PR 1.4 (compiler) |
| Scope outside `src/` | projects in `tools/` and `tests/` are not judged | by design for now |

- Identity: a swap (one removed, one added, same rule and project) passes, as explained above.
- Renames: a baselined project that disappears fails both the PR's own check and the base branch's check, on purpose:
  a judged-on-totals mode would let a PR swap hits between projects or drop a project from the scan. A real rename is
  rare; it needs the owner to override the check once, with the reason in the PR. An extra project with no hits
  changes nothing.
- Exemption tags: `// INVARIANT: ... see NameTests.Method` is exempt when any file under `tests/` declares
  `class NameTests` and contains `Method(`. The barrier against a fake test is review: `tests/` is the frozen oracle, so
  a new test file needs a visible `scripts/test_oracle.py --extend` revision that the reviewer must check.

## Architecture rules (BOO-ARCH, `tests/Boo.Architecture.Tests`)

Plan section 2.4 B, PR 1.11. A normal xUnit project that reads the compiled `src` assemblies, so it runs wherever the
solution builds; no app starts and no screen is touched. Each rule lists today's offenders **by name** (baseline
13 Nov 2026, `lane/face` `af089dd`). The list is exact both ways: a **new** offender fails, and an offender that has been
**fixed** also fails until its name is removed, so every gain is locked in. The test project is frozen (oracle
revision 25), so removing a name, like adding a rule, goes through a reviewed `scripts/test_oracle.py --rebaseline`.
Dependency offenders (001-004) count per outermost type (lambdas and async state machines count towards the type that
wrote them); static-state offenders (005) are named per declaring type, nested types included.

**Why two tools.** 001 and 004 are about whole namespaces and use NetArchTest. 002 and 003 are about a *kind of
access* (touching the disk; talking to the network) and are **default-deny**, checked with `CecilScan` (Mono.Cecil,
the library NetArchTest is built on): every type in the disk namespaces (`System.IO.*`, `System.Xml.*`, plus
`Process` / `FileVersionInfo`) or the network namespaces (`System.Net.*`, `Windows.Networking.*`, `Windows.Web.*`,
WebView2) counts, except a short list of harmless types (paths as text, in-memory streams, enums, exceptions, status
codes). Native calls count too: Core may make none, and the app may only call `user32`, `kernel32`, `shcore`,
`dwmapi` and `gdi32`. So an API nobody listed (`new StreamWriter(path)`, `ZipFile`, `XDocument.Load(path)`,
`TcpListener`, `SslStream`, `Dns`, a `ws2_32` import) is caught, and a harmless type missing from the list fails loudly
instead of slipping through. The scan covers base types, interfaces, fields, properties, events, attributes,
parameters, return values, locals and every member the code calls, constructs, casts to or names, generic
arguments included.

**Known gaps.** (1) BOO-ARCH-005 sees fields and setters, not what a `readonly` field points to: a
`static readonly List<T>` that is changed later (today `AccessibilitySettings.Listeners`, `.Add` at line 48) is
shared mutable state the rule does not count; counting every `static readonly` collection or array would flood it
with read-only tables. (2) BOO-ARCH-006 looks at `src/Boo.App/*Window*.cs` in the top folder, which holds every
window code-behind today; a window elsewhere or not named `...Window` would be missed. (3) 002/003 see what the
compiled code names, not what it does at run time: reaching a type by reflection (`Type.GetType("...")`), through a
library outside the listed namespaces, or through a function pointer is not caught. (4) 003 does not count Velopack's
update download (`UpdateService`, release lane) or opening a link in the browser (`Process.Start`); neither is a
chat or AI client.

| Rule | Checks | How | Today (matches plan section 1.5) |
| --- | --- | --- | --- |
| [BOO-ARCH-001](#boo-arch-001) | `Boo.Core` uses nothing from `System.Net.Http`, `System.Net.WebSockets`, `Microsoft.Win32`, `Microsoft.Data.Sqlite`, `NAudio`, `Velopack` | NetArchTest, namespace match | 1: `ProviderFailureClassifier` (`ProviderFailure.cs`, `HttpRequestError`) |
| [BOO-ARCH-002](#boo-arch-002) | `Boo.Core` does not touch the disk: no type from `System.IO.*` / `System.Xml.*` except the harmless list (`Path`, in-memory streams, enums, exceptions), no `Process` / `FileVersionInfo`, no `Path.GetTempFileName`, no native call | default-deny (Mono.Cecil) | 2: `ScopedFiles` (the second class in `ActionPolicy.cs`), `FileActionResolver` |
| [BOO-ARCH-003](#boo-arch-003) | `Boo.App` does not talk to the network: no type from `System.Net.*`, `Windows.Networking.*`, `Windows.Web.*` or WebView2 except the harmless list (`HttpStatusCode`, `WebUtility`, request errors, `NetworkCredential` as a password reader), no native call outside `user32`/`kernel32`/`shcore`/`dwmapi`/`gdi32` | default-deny (Mono.Cecil) | 1 type: `MainWindow` (its 3 references: `MainWindow.xaml.cs:65,1624,2123`) |
| [BOO-ARCH-004](#boo-arch-004) | `Boo.Windows` uses nothing from `System.Windows.Controls` / `System.Windows.Shapes` and has no `Window` subclass | NetArchTest | 0 |
| [BOO-ARCH-005](#boo-arch-005) | no static field that is not `readonly`/`const`, no static property with a setter, in all 5 `src` assemblies | reflection; compiler-made caches excluded; named per declaring type (`Outer+Inner.Field`) | 2: `AccessibilitySettings._uiSettings`, `App.StartedInBackground` |
| [BOO-ARCH-006](#boo-arch-006) | every `src/Boo.App/*Window*.cs` file has at most 2,674 lines | line count | 0 over (largest: `MainWindow.xaml.cs`, 2,674) |
| [BOO-ARCH-007](#boo-arch-007) | a new `MainWindow` feature goes in its own `MainWindow.<Feature>.cs` partial | written rule only (review) | |

## BOO-ARCH-001
**Boo.Core stays pure.** Core is the code every other layer and every unit test relies on; network, registry,
database, audio or installer types make it slow and hard to test. **Fix:** put the outside call in `Boo.Providers`,
`Boo.Storage` or `Boo.Windows` and pass Core plain values or an interface. **Do NOT** move the type into Core "to reuse it".

## BOO-ARCH-002
**Core does not touch disk.** File checks in Core make rules depend on the machine. **Fix:** an interface in Core (like
`IStartupRegistry`) with the real implementation outside. **Do NOT** swap `File.Exists` for `FileInfo` to dodge the
check, or for `StreamReader(path)`, `ZipFile`, `XDocument.Load(path)` or a native call: every disk namespace is denied
by default, so all are caught. **Do NOT** add a type to the harmless list unless it cannot reach the disk.

## BOO-ARCH-003
**The window does not talk to the network.** Network calls belong in `Boo.Providers`, where timeouts, failures and
keys are handled in one place (the 45 s timeout lives in `MainWindow` today for this reason). **Fix:** create the
client in Providers and inject it. **Do NOT** hide a new `HttpClient` in a helper class in `Boo.App`, or swap it for a
`SocketsHttpHandler`, a `TcpListener`, `Dns` or a `ws2_32` import: every network namespace is denied by default, so
all are caught. **Do NOT** add a type to the harmless list unless it cannot reach the network.

## BOO-ARCH-004
**Boo.Windows is Windows integration, not UI.** Bitmaps (`System.Windows.Media`) and UI Automation are fine; controls,
shapes and windows belong in `Boo.App`.

## BOO-ARCH-005
**No mutable static state.** A static that changes is shared by every caller and every test, which makes order-dependent
bugs. **Fix:** an instance field passed in, or `static readonly` set once. **Do NOT** add `readonly` and mutate the
object inside it to get past the check.

## BOO-ARCH-006
**Window code-behind stays under its budget** (today's largest file, 2,674 lines). The budget may only go down, in a
reviewed oracle revision. **Fix:** move the feature into `MainWindow.<Feature>.cs` (BOO-ARCH-007) or its logic into
`Boo.Core`. **Do NOT** raise the budget.

## BOO-ARCH-007
**One feature, one partial.** `Boo.App` is already sliced by feature (`MainWindow.Talk.cs`, `.Voice.cs`, `.Edits.cs`,
`.Focus.cs`, `.Updates.cs`, `.Accessibility.cs`, `.Shortcuts.cs`). A new feature gets its own file. Checked by review;
BOO-ARCH-006 is what makes skipping it fail.
