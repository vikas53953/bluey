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

## Architecture references (BOO-REF)

Plan section 2.4 A, PR 1.2. [`scripts/quality/arch_refs.py`](../../scripts/quality/arch_refs.py) checks every project
under `src/` against [`docs/architecture/layers.json`](../architecture/layers.json): which `src/` projects it may
reference, which packages, which frameworks, and (for `Boo.Core` and `Boo.Providers`) which target framework it must
stay on. The file is **exact both ways**: a reference it does not list fails, and an allowance the tree no longer uses
also fails until it is removed, so the file always says what is true today.

| Project | May reference projects | May reference packages | Frameworks (from the SDK) | Must stay on |
| --- | --- | --- | --- | --- |
| `Boo.Core` | none | none | `Microsoft.NETCore.App` | `net10.0` |
| `Boo.Providers` | Core | none | `Microsoft.NETCore.App` | `net10.0` |
| `Boo.Storage` | Core | `Microsoft.Data.Sqlite`, `System.Security.Cryptography.ProtectedData` | + Windows SDK | |
| `Boo.Windows` | Core | `NAudio` | + Windows SDK, `Microsoft.WindowsDesktop.App` (WPF, WinForms) | |
| `Boo.App` | Core, Windows, Providers, Storage | `Velopack` | + Windows SDK, `Microsoft.WindowsDesktop.App` | |

**How it reads the references.** MSBuild itself evaluates each project (`dotnet msbuild <project>
-getItem:ProjectReference -getItem:PackageReference -getItem:FrameworkReference -getItem:Reference -getItem:COMReference
-getItem:Analyzer -getProperty:TargetFramework -getProperty:TargetFrameworks -p:EnableWindowsTargeting=true`: evaluation
only, no restore, no build, a few seconds). So a reference added from `Directory.Build.props`, an `<Import>`,
`Directory.Packages.props` or under a condition is seen exactly as the build sees it, and a project with several target
frameworks is evaluated once per framework. The projects judged are the project files git would commit under `src/`
(any `*proj` extension, any letter case). There is no text fallback: without `dotnet` the check fails, because a text
reader would miss exactly those build-file routes. `--self-test` judges 31 planted graphs and 9 loosening cases with
exact expected rule ids, and runs real MSBuild on 9 throwaway trees (an import, a false condition, a per-framework
reference, `tools/` and `tests/` targets, a new project, a package, a DLL and an analyzer, a target change). `--show`
prints what MSBuild reports, project by project.

**A PR cannot loosen its own layers.** On a pull request the workflow also runs the **base branch's** `arch_refs.py`
with the **base branch's** `layers.json` (both read from git) against the PR's tree, and refuses a PR `layers.json`
that allows anything the base one does not (BOO-REF-008). Splitting "loosen" and "use" over two PRs fails the first one.

**Changing the layers.** A new edge, package, framework or project is an architecture decision: the PR edits
`layers.json` and says why, the owner approves, and the base-branch check (BOO-REF-008) is overridden once with the
reason in the PR. Removing something only needs the PR's own `layers.json` to drop it (BOO-REF-007 asks for that).
Package versions are not judged.

| Rule | Fails when | Today |
| --- | --- | --- |
| [BOO-REF-001](#boo-ref-001) | a `src/` project references a `src/` project its row does not allow | 0 |
| [BOO-REF-002](#boo-ref-002) | a `src/` project references anything that is not a `src/<Name>/<Name>.csproj` project (`tools/`, `tests/`, outside the repo) | 0 |
| [BOO-REF-003](#boo-ref-003) | a package its row does not allow, or any direct `<Reference>`, `<COMReference>` or own `<Analyzer>` | 0 |
| [BOO-REF-004](#boo-ref-004) | a project file under `src/` with no row, or not at `src/<Name>/<Name>.csproj` | 0 |
| [BOO-REF-005](#boo-ref-005) | a framework reference its row does not allow, or a target framework other than the required one | 0 |
| [BOO-REF-006](#boo-ref-006) | `layers.json` is malformed: unknown or missing keys, duplicates, an allowance naming no row, a self edge, a cycle | 0 |
| [BOO-REF-007](#boo-ref-007) | `layers.json` allows or declares something the tree no longer has (the PR's own check only) | 0 |
| [BOO-REF-008](#boo-ref-008) | the PR's `layers.json` allows more than the base branch's (the base-branch check only) | n/a |

## BOO-REF-001
**A project reference the layers do not allow** (for example `Boo.Windows` → `Boo.Providers` "to reuse a helper").
One such edge ties two layers together for good. **Fix:** move the shared code down into a project both may reference
(usually `Boo.Core`, as plain values or an interface), or do the wiring in `Boo.App`. **Do NOT** add the edge to
`layers.json` to make the check pass.

## BOO-REF-002
**A reference that leaves `src/`.** `tools/` and `tests/` are not shipped and not judged; `tests/` is the frozen oracle.
**Fix:** move the code the product needs into a `src/` project, or turn the dependency around. **Do NOT** copy the
project into `src/` under a new name or link its files in with `<Compile Include>`.

## BOO-REF-003
**A package, assembly or analyzer the layer does not allow.** A package in the wrong layer breaks it from below without
any project reference. **Fix:** put the code that needs it in the layer that owns that kind of work. **Do NOT**
reference the DLL directly or add the package from `Directory.Build.props`; MSBuild reports both.

## BOO-REF-004
**An undeclared project.** A project without a row has no rules. **Fix:** put it at `src/<Name>/<Name>.csproj` and add
its row, with the smallest set of references it needs, in the same PR (owner approval, see Changing the layers).
**Do NOT** put product code in `tools/` or `tests/` instead.

## BOO-REF-005
**A framework the layer does not allow.** `Boo.Core` and `Boo.Providers` stay on plain `net10.0`, so WPF, WinForms and
Windows APIs are impossible there. **Fix:** keep Windows-only code in `Boo.Windows` or `Boo.App`. **Do NOT** add
`-windows` or a `<FrameworkReference>` to reach one Windows type.

## BOO-REF-006
**A broken rule file.** **Fix:** known keys only, lists of distinct names, no self edge, no cycle. **Do NOT** delete
the file or a row to silence it.

## BOO-REF-007
**An unused allowance (good news).** **Fix:** remove it from `layers.json` in the same PR, so the gain is locked in.
**Do NOT** add a dummy reference to keep it.

## BOO-REF-008
**A PR that loosens `layers.json`.** **Fix:** the owner approves the new allowance and overrides the check once, with
the reason in the PR. **Do NOT** split the loosening and its use into two PRs.

## Known gaps (architecture references)

| Gap | Example | Closed by |
| --- | --- | --- |
| References added while building, not while evaluating | a `<Target BeforeTargets="ResolveProjectReferences">` that adds a `ProjectReference` or `PackageReference` item | not yet: a follow-up can read `obj/project.assets.json` after restore in a build job |
| Packages a package brings with it | `Microsoft.Data.Sqlite` brings `SQLitePCLRaw.*`; only direct references are judged | by design (the direct choice is the decision); versions are not judged either |
| Sharing code without a reference | `<Compile Include="../Boo.Providers/X.cs" Link="X.cs"/>` | PR 1.3's compile-list check (what each project compiles must be its own files) |
| Loading code at run time | `Assembly.LoadFrom`, `[DllImport]` of a DLL by path, reflection | type-level rules (PR 1.11, BOO-ARCH) and review |
| Projects outside `src/` | `tools/` and `tests/` references are not judged (plan 2.4 lists today's test references as their allowlist) | a later PR |
| The PR supplies the workflow, `global.json` and `Directory.Build.props` the check runs with | a PR that edits `quality-gates.yml` | HANDOFF.md known issue 0 (CODEOWNERS, pending) |
| Merge order with PR 1.3 (#28) | #28 adds the `Microsoft.CodeAnalysis.BannedApiAnalyzers` package to every `src/` project from `Directory.Build.props`, which this check reports (BOO-REF-003) until `layers.json` allows it | the second of the two PRs to merge adds `"all_src_projects": {"packages": ["Microsoft.CodeAnalysis.BannedApiAnalyzers"]}` |
