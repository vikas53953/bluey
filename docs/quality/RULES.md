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

**What it promises: it catches ordinary spellings, and it never counts less than the `lane/face` ratchet did, except
where that one counted text that is not code (listed in the PR #23 evidence bullet below).** The text as written is
always counted. Extra *views* of the same file can add hits, never remove them (per line, the highest count
of any view wins): identifiers spelled the plain way (`\u0043ollect` and `@Collect` read as `Collect`; invisible
formatting characters dropped), and `using` aliases and `using static` members written out in full (`using G =
System.GC;` then `G.Collect()`), including `global using` lines in any file of the project. A call split over lines (`System.GC` / `.Collect()`) counts at the line it starts on.
Deliberate evasion (build-file tricks, subclasses, aliases the text cannot resolve) is closed by the compiler: for the
banned APIs by [RS0030](#banned-apis-rs0030) and the [compile list](#compile-list) (PR 1.3), for the shape rules by
PR 1.4; see Known gaps.

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
  more than 12 characters of `$` and quotes, which the old lexer misread. Code that is not valid C# (a Unicode
  line-break character (U+0085, U+2028, U+2029) inside a string or char literal) may also count differently; the build
  rejects it anyway. A second review (13 Nov) fuzzed 120,000 random inputs and found no other way to count less.

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

## Banned APIs (RS0030)

PR 1.3 makes the ban exact: the compiler, not a text scan, decides which member a call reaches, so subclasses
(`class P2 : Process { ... Start("x") }`), generic types (`Task<int>.Delay(1)`), `using static`, aliases, method groups
(`new(Environment.GetEnvironmentVariable)`) and extension calls all resolve to the real member.

- **Wiring.** [`Directory.Build.props`](../../Directory.Build.props) adds `Microsoft.CodeAnalysis.BannedApiAnalyzers`
  5.6.0 and [`src/BannedSymbols.txt`](../../src/BannedSymbols.txt) to every project under `src/`. The condition reads
  only reserved MSBuild properties (the project's own folder), so a project cannot set a property to opt out.
  `RS0030` is an error through `TreatWarningsAsErrors`. `tests/` and `tools/` are not judged (`tests/` is the frozen
  oracle; they get their own reviewed list later).
- **The list** (86 lines) is generated, never hand-edited:
  [`scripts/quality/canary/banned-groups.txt`](../../scripts/quality/canary/banned-groups.txt) names each member once
  (`BAN M:System.Threading.Thread.Sleep | message`), and `python -I scripts/quality/banned_apis.py generate` expands it
  into **every overload** the `net10.0` and `Microsoft.WindowsDesktop.App` reference packs declare, using Roslyn
  (`scripts/quality/canary/ListGen`). `ALLOW` lines keep the `Task.Delay(TimeSpan, TimeProvider[, CancellationToken])`
  overloads. Beyond the plan's list it bans what the reviews found: `Task.WaitAll(ReadOnlySpan<Task>)` and
  `Task.WaitAll(IEnumerable<Task>, CancellationToken)`, the instance `Process.Start()`, both `SecureString`
  `Process.Start` overloads, `ConfigureAwait(false).GetAwaiter().GetResult()` (Task and Task<T>), the same four for
  `ValueTask`, `ValueTask<T>.Result`, `DateTime.Today`, `Environment.GetEnvironmentVariables` and
  `Dispatcher.PushFrame` (the WPF `DoEvents`). CI step `list-check` regenerates the list and fails on any difference,
  so a .NET update that adds an overload is caught.
- **Today's hits are baselined per member**, in `src/<Project>/AnalyzerBaseline.cs`: one
  `[assembly: SuppressMessage("ApiDesign", "RS0030", Scope = "member", Target = "~M:...", Justification = "Baselined by
  PR 1.3; cleanup Cn; BOO-XXX-NNN xN; <same target>")]` per member, generated from the compiler's own diagnostics.
  39 hits in 34 members (Boo.App 32 in 27, Boo.Core 3, Boo.Windows 4): BOO-ASYNC-002 1, BOO-UI-001 2, BOO-UI-002 15,
  BOO-TIME-001 14, BOO-TIME-002 1, BOO-RT-002 3, BOO-SAFE-003 3. These equal the ratchet's counts; the plan's table said
  13 `BeginInvoke` because it predates the lower-case `dispatcher.BeginInvoke` and PR #12's new one.
- **A new call in an already baselined member.** A member-scoped suppression hides *every* RS0030 in that member, so
  the build alone stays green. `python -I scripts/quality/banned_apis.py product` closes that: it builds each `src/`
  project with a SARIF error log and requires, per member and per rule, **exactly** the declared count (a new call:
  `0 -> 1` or `1 -> 2` fails; a fixed call fails with "lower the count" until the file is lowered), and refuses any
  RS0030 silenced by `#pragma`, by a `[SuppressMessage]` outside these files, or by an unknown justification. On a
  pull request the **base branch's copy** of `banned_apis.py` (`rise-check`) refuses a new entry, a raised count or a
  removed ban line, the same way the ratchet is judged by its base copy.
- **The probe.** The same step compiles the canary files inside every `src/` project (`BooBannedApiProbe`), with that
  project's own settings, and requires every banned line in them to fire as an unsuppressed error. So `NoWarn`,
  `WarningsNotAsErrors`, an `.editorconfig` severity, `RunAnalyzers=false`, a removed `PackageReference` or
  `AdditionalFiles`, or a project-local ban list all fail closed, and so does a project that stops writing the log.

**Fix:** use the replacement named in the error (the rule sections above). **Do NOT** add a suppression, raise a count,
or edit `src/BannedSymbols.txt` by hand. `BOO-ASYNC-002`'s one hit (`PushToTalkUplink.Discard`) has no cleanup PR in
plan 2.8 yet; its entry says "cleanup not yet planned".

## Canary

[`scripts/quality/canary/`](../../scripts/quality/canary/) holds one deliberate call per banned line
(`Canary.Core.cs`, `Canary.Wpf.cs`, `Canary.WinForms.cs`; each call line ends in a `// BAN <doc id>` marker), built by
`Canary.csproj` with the same analyzer and the same list. `python -I scripts/quality/banned_apis.py canary` proves:
every listed line has exactly one marked call; Roslyn binds each marked call to exactly that overload (a call that
drifts to a neighbouring overload fails); and the canary build reports **exactly one** RS0030 per marked line, as an
unsuppressed error with that line's message, and none anywhere else. It runs on `ubuntu-latest`: WPF and Windows Forms
compile on Linux with `EnableWindowsTargeting`, so no Windows job is needed.

## Compile list

[`scripts/quality/compile_list.py`](../../scripts/quality/compile_list.py) asks MSBuild what each `src/` project really
compiles (`dotnet msbuild <proj> -t:Build -getItem:Compile -getItem:Using`: the items after the build ran, so
`<Import>`ed files, `Directory.*.props` and items added by targets are included) and fails when that differs from the
files the ratchet reads: a compiled file outside the project, not `.cs`, or git-ignored; a scanned file that is not
compiled; a generated file under `obj/` that is not a known SDK or WPF output; an `x:Code` block in a XAML page; a
`<Using>` item that is `Static="true"` or has an `Alias`. **Fix:** put the code in a `.cs` file in the project.
**Do NOT** pull files in through build items.

## Known gaps (the text-level ratchet does not see these; PRs 1.3 and 1.4 make them exact)

The ratchet reads text, so a determined writer can always find a spelling the compiler accepts and the text scan does
not follow. After five review rounds (PRs #21 and #23) the rule is: the text ratchet must never count less than before,
and deliberate evasion is closed by the compiler, which sees the real names, types and compiled files.

| Gap | Example | Closed by |
| --- | --- | --- |
| Build files decide what is compiled and which usings apply | `<Compile Include="../x.txt"/>`, `<Using Include="System.GC" Static="true"/>`, `<Import>`, `Directory.Packages.props`, `obj/*.props`, `CustomAfterMicrosoftCommonTargets` | **Closed by PR 1.3** for the banned APIs (the compiler judges whatever is compiled) and for the ratchet's file set ([compile list](#compile-list)). Still open: a build target that adds a file and removes it again after compiling (the list is read after the build), and code added by a source-generator package; both need a project-file edit that review sees, and RS0030 still judges generated code |
| Calls through a subclass or a generic type | `class P2 : Process { ... Start("x"); }`, `Task<int>.Delay(1)` | **Closed by PR 1.3** for the banned members (RS0030 resolves the real member); the text ratchet still counts by spelling |
| Target-typed `new` with a nullable type | `DispatcherTimer? t = new();` | **Not closed**: `new DispatcherTimer` (BOO-TIME-003) is counted, not banned, so the compiler does not judge it; PR 1.4 |
| `using static` false hits | `using static System.Diagnostics.Process;` plus the class's own `Start()`; `using static System.DateTime;` plus a local `Now` (none in `src/` today) | **Closed by PR 1.3** for the banned APIs (RS0030 fires only on the real member); the text ratchet may still over-count, which is safe |
| Instance calls by name | `.Wait()` / `.Result` on a task variable whose name lacks "task"; an instance `process.Start()` | **Closed by PR 1.3** (RS0030 bans `Task.Wait`, `Task<T>.Result`, `ValueTask<T>.Result` and the instance `Process.Start()` by type) |
| Shape of the code | a broad catch with an unreachable `throw` counts as "rethrows"; a static field or property declared over two lines, or a tuple type plus `=>`; `///` comments inside a method body count as documentation | PR 1.4 analyzers |
| Disabled `#if` blocks | the compiler skips their text, the ratchet reads it, so an unmatched quote or `/*` inside one can hide or invent a later hit (none in `src/` today) | PR 1.4 (compiler); for the banned APIs the compiler already judges only what is compiled |
| Swap of a baselined banned call | remove one baselined RS0030 hit and add a new call in another member, silenced by a `[SuppressMessage]` that copies the removed entry's justification | **Not closed**: the counts still match. It needs a deliberately copied suppression, which review sees; PR 1.5 (`BOO-LINT-002`, no suppression outside `AnalyzerBaseline.cs`) refuses it |
| Scope outside `src/` | projects in `tools/` and `tests/` are not judged | by design for now |

- Identity: a swap (one removed, one added, same rule and project) passes, as explained above.
- Renames: a baselined project that disappears fails both the PR's own check and the base branch's check, on purpose:
  a judged-on-totals mode would let a PR swap hits between projects or drop a project from the scan. A real rename is
  rare; it needs the owner to override the check once, with the reason in the PR. An extra project with no hits
  changes nothing.
- Exemption tags: `// INVARIANT: ... see NameTests.Method` is exempt when any file under `tests/` declares
  `class NameTests` and contains `Method(`. The barrier against a fake test is review: `tests/` is the frozen oracle, so
  a new test file needs a visible `scripts/test_oracle.py --extend` revision that the reviewer must check.
