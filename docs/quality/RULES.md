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
`ratchet.py`, therefore changes nothing. The same job runs the **base branch's** `banned_apis.py` (once it exists on
the base) on the PR's tree as `rise-check` (no ban line removed, no baseline entry added or count raised, RS0030 named
only in `AnalyzerBaseline.cs`), and `windows-ci` runs the base branch's copy as `judge` on the PR's build facts (see
[Banned APIs](#banned-apis-rs0030)). What the base copies cannot guard: the workflow files themselves and
`Directory.Build.props` (the analyzer wiring) come from the PR. That is the remaining hole: HANDOFF.md, known issues,
item 0 (CODEOWNERS decision pending with Vikas).

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
  Re-measured on 13 Nov after PR #33 (TimeProvider): **25 hits in 20 members** (Boo.App 21 in 16, Boo.Core 2,
  Boo.Windows 2): BOO-ASYNC-002 1, BOO-UI-001 2, BOO-UI-002 15, BOO-TIME-002 1, BOO-RT-002 3, BOO-SAFE-003 3;
  BOO-TIME-001 is 0. These equal the ratchet's counts; the plan's table said 13 `BeginInvoke` because it predates the
  lower-case `dispatcher.BeginInvoke` and PR #12's new one.
- **Judged on the shipped build.** The gate reads the **build facts** of `windows-ci`'s own Release build and win-x64
  publish ([Build facts](#build-facts-the-shipped-build)), not a separate build: `python -I
  scripts/quality/banned_apis.py judge artifacts/build-facts/build-facts.json` runs right after the "Build facts" step.
  `Directory.Build.props` makes every compile of a `src/` project write a SARIF error log
  (`obj/.../<Project>.rs0030.sarif`, `/errorlog`), which the facts record with the compiler call. For **every** `Csc`
  call of a `src/` project in both logs (the WPF twin `src/<Name>/<Name>_wpftmp.csproj` included, exactly as
  `build_facts.py` accepts it) the judge fails closed unless:
  - it runs from `CoreCompile`, and the compiler is the SDK's own csc (`<dotnet>/sdk/*/Roslyn/bincore/csc[.exe]` or
    `<dotnet>/dotnet exec .../csc.dll`); anything else is reported alone, since its command line cannot be trusted;
  - `/noconfig` is there and no response file (`@file`, `<CompilerResponseFile>`) is passed;
  - both `Microsoft.CodeAnalysis.(CSharp.)BannedApiAnalyzers.dll` from the pinned 5.6.0 package are analyzers;
  - the only `BannedSymbols*` additional file is `src/BannedSymbols.txt`;
  - Roslyn's own reading of the options has no RS0030 entry (`/nowarn`, `/warnaserror-:RS0030`, ...), warnings are
    errors, the warning level is at least 1, there is no `/ruleset`, analyzers are not skipped and the line parses;
  - every analyzer config it received has readable text, and no config it received (the SDK's generated
    `.editorconfig` included) nor any `.editorconfig` / `.globalconfig` in the repository sets
    `dotnet_diagnostic.RS0030.severity`, `dotnet_analyzer_diagnostic.category-ApiDesign.severity` or
    `dotnet_analyzer_diagnostic.severity` (none does today), whatever its file name, section or path;
  - it wrote the SARIF log, and every RS0030 in it is suppressed by exactly one `AnalyzerBaseline.cs` entry of that
    project, with **exactly** the declared count per member and rule. A member-scoped suppression hides *every* RS0030
    in that member, so a new call in an already baselined member keeps the build green: the count (`0 -> 1`, `1 -> 2`)
    fails it. A fixed call fails with "lower the count" until the file is lowered. RS0030 silenced by `#pragma`, by a
    `[SuppressMessage]` outside these files or by an unknown justification is refused.
  A compile skipped as up to date (the publish reuses the build's outputs) has no call to judge; `build_facts.py`
  traces every shipped assembly to a recorded compile, and every `src/` project must have one in the build log.
  Publish-only code (a file included only under a `RuntimeIdentifier` or `_IsPublishing`) is compiled, logged and
  judged in the publish log.
- **On a pull request** `windows-ci` also runs the **base branch's** copy of `banned_apis.py` as `judge` on the same
  facts (skipped while the base has none), and the `quality` job runs its `rise-check`: no new entry, no raised count,
  no removed ban line, RS0030 named in no `src/` C# file other than `AnalyzerBaseline.cs`. On Linux the `quality` job
  keeps the parts that need no build of the app: `list-check`, the [canary](#canary) and `format` (the ban list and
  every `AnalyzerBaseline.cs` parse in their strict formats).
- **Not covered:** the workflow files and `Directory.Build.props` themselves come from the pull request, so a PR that
  edits them can switch these checks off (HANDOFF known issue 0, CODEOWNERS decision pending with Vikas). A build step
  other than `Csc` that overwrites the compiled assembly is the build facts' job (unknown tasks and the hash trace);
  their own holes are listed under [Build facts](#build-facts-the-shipped-build).

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

[`scripts/quality/compile_list.py`](../../scripts/quality/compile_list.py) reads the same build facts (`windows-ci`,
right after the banned-API judge): for every compile of a `src/` project, the source files the compiler really got
(so `<Import>`ed files, `Directory.*.props`, publish-only items and files a target adds and removes again are all
included) must equal the files the ratchet reads. It fails on a compiled file outside the project, not `.cs`, or
git-ignored; a scanned file that is not compiled; a generated file under `obj/` that is not a known SDK or WPF output;
a static or alias `global using` in the `GlobalUsings.g.cs` the compiler got; an `x:Code` block in a XAML file under
`src/`; a `<ProjectReference>` to anything but `src/<Name>/<Name>.csproj`. **Fix:** put the code in a `.cs` file in
the project. **Do NOT** pull files in through build items.

## Known gaps (the text-level ratchet does not see these; PRs 1.3 and 1.4 make them exact)

The ratchet reads text, so a determined writer can always find a spelling the compiler accepts and the text scan does
not follow. After five review rounds (PRs #21 and #23) the rule is: the text ratchet must never count less than before,
and deliberate evasion is closed by the compiler, which sees the real names, types and compiled files.

| Gap | Example | Closed by |
| --- | --- | --- |
| Build files decide what is compiled and which usings apply | `<Compile Include="../x.txt"/>`, `<Using Include="System.GC" Static="true"/>`, `<Import>`, `Directory.Packages.props`, `obj/*.props`, `CustomAfterMicrosoftCommonTargets`, a target that adds a file and removes it after `CoreCompile` | **Closed by PR 1.3** for the banned APIs (the compiler judges whatever is compiled) and for the ratchet's file set ([compile list](#compile-list), read from the compiler call in the build facts). Still open for the text ratchet: code added by a source-generator package (RS0030 still judges generated code) |
| Calls through a subclass or a generic type | `class P2 : Process { ... Start("x"); }`, `Task<int>.Delay(1)` | **Closed by PR 1.3** for the banned members (RS0030 resolves the real member); the text ratchet still counts by spelling |
| Target-typed `new` with a nullable type | `DispatcherTimer? t = new();` | **Not closed**: `new DispatcherTimer` (BOO-TIME-003) is counted, not banned, so the compiler does not judge it; PR 1.4 |
| `using static` false hits | `using static System.Diagnostics.Process;` plus the class's own `Start()`; `using static System.DateTime;` plus a local `Now` (none in `src/` today) | **Closed by PR 1.3** for the banned APIs (RS0030 fires only on the real member); the text ratchet may still over-count, which is safe |
| Instance calls by name | `.Wait()` / `.Result` on a task variable whose name lacks "task"; an instance `process.Start()` | **Closed by PR 1.3** (RS0030 bans `Task.Wait`, `Task<T>.Result`, `ValueTask<T>.Result` and the instance `Process.Start()` by type) |
| Shape of the code | a broad catch with an unreachable `throw` counts as "rethrows"; a static field or property declared over two lines, or a tuple type plus `=>`; `///` comments inside a method body count as documentation | PR 1.4 analyzers |
| Disabled `#if` blocks | the compiler skips their text, the ratchet reads it, so an unmatched quote or `/*` inside one can hide or invent a later hit (none in `src/` today) | PR 1.4 (compiler); for the banned APIs the compiler already judges only what is compiled |
| Swap of a baselined banned call | remove one baselined RS0030 hit and add a new call in another member, silenced by a `[SuppressMessage]` that copies the removed entry's justification | **Not closed**: the counts still match. It needs a deliberately copied suppression, which review sees; PR 1.5 (`BOO-LINT-002`, no suppression outside `AnalyzerBaseline.cs`) refuses it |
| A `src/` project referencing a project outside `src/` | `<ProjectReference Include="../../tools/X/X.csproj"/>` | **Closed by PR 1.3**: the [compile list](#compile-list) fails on any reference that is not `src/<Name>/<Name>.csproj` |
| Inputs to the compiler outside the build items | an analyzer config passed by `<CompilerResponseFile>`, a wrapper compiler (`CscToolPath`), a target that adds a config or a source before `CoreCompile` and removes it afterwards, a swapped `BannedSymbols.txt`, `/nowarn:RS0030`, code compiled only when publishing | **Closed by PR 1.3**: the compiler calls of the shipped build and publish are judged from the build facts (see [Banned APIs](#banned-apis-rs0030)); the compile list takes its sources from the same calls |
| A build step that replaces the compiled assembly after `Csc` ran, or edits to the workflow files or `Directory.Build.props` | an `<Exec>` after `CoreCompile` that copies another DLL over the output; a PR that edits `windows-ci.yml` | The first is the build facts' job (unknown task, hash trace; see its known holes). **Not closed**: the second is HANDOFF known issue 0 (CODEOWNERS decision pending with Vikas) |
| Scope outside `src/` | projects in `tools/` and `tests/` are not judged | by design for now |

- Not banned yet (owner: PR 1.4 or a cleanup PR; deliberately not in PR 1.3): `Environment.ExpandEnvironmentVariables`
  (reads environment variables), `DispatcherOperation.Wait` (a blocking wait on the UI thread), Windows Forms
  `Control.Invoke` (synchronous cross-thread call), and the hidden clocks `Environment.TickCount` / `TickCount64`,
  `Stopwatch.GetTimestamp` and `TimeProvider.System` (the injected default today, for example
  `src/Boo.App/App.xaml.cs:39` and `src/Boo.Core/FocusClock.cs:27`, since PR #33).
- Identity: a swap (one removed, one added, same rule and project) passes, as explained above.
- Renames: a baselined project that disappears fails both the PR's own check and the base branch's check, on purpose:
  a judged-on-totals mode would let a PR swap hits between projects or drop a project from the scan. A real rename is
  rare; it needs the owner to override the check once, with the reason in the PR. An extra project with no hits
  changes nothing.
- Exemption tags: `// INVARIANT: ... see NameTests.Method` is exempt when any file under `tests/` declares
  `class NameTests` and contains `Method(`. The barrier against a fake test is review: `tests/` is the frozen oracle, so
  a new test file needs a visible `scripts/test_oracle.py --extend` revision that the reviewer must check.

## Key scan before every upload (PR 1.7)

[`scripts/quality/key_scan.py`](../../scripts/quality/key_scan.py) runs right before **every** upload step in `windows-ci`,
`demo-ci` and `live-ai-smoke`, in the same job, on exactly the path that step uploads. **The upload runs only when its scan
step reports `safe=true`**: the upload's `if:` is `${{ steps.keyscan_<path>.outputs.safe == 'true' }}` (with
`!cancelled() &&` in front where the upload also runs after a failed step). The scan step writes `safe=true` only after a
clean scan (exit 0) or a scan whose every hit was deleted (exit 10). Anything else skips the upload: a crash, a file that
cannot be deleted, an unreadable folder, a wildcard or odd path, a self-test or workflow-check failure (exit 3, and the
folder is emptied), Python failing to start, broken PowerShell, or a scan in a different job.

Each scan step's `id:` and `run:` block are generated by `key_scan.scan_id(path)` and `key_scan.step_run(path)`, and
`--check-workflows` (run by every scan) accepts nothing else: same job, one literal path of letters, digits and `._/-`
that starts with a letter, `shell: pwsh`, no other keys, `env:` only `NAME_KEY: ${{ secrets.X }}`, no `working-directory` or `defaults:` in the
workflow, and the upload's `if:` exactly as generated. It works in two layers, and both must pass:

1. **Grammar.** Every line of every workflow (except `release.yml`, see the gaps below), outside `run: |` scripts, must
   be plain block YAML: a comment, `key: value`, `- key: value` or `- item`, with unquoted ASCII keys, plain values
   and space-only indentation. Other block scalars (`path: |`, `run: >`), quoted or escaped keys, `?` keys, `!` tags,
   anchors, aliases, merges, flow-style `{...}` steps, tabs and Unicode spaces all fail.
2. **Meaning.** The workflow is read by PyYAML, a real YAML parser, not a home-made one. Duplicate keys fail (GitHub
   refuses them too), and so does a key that differs only in case from a workflow keyword (`Uses:`, `Steps:`).
   Reusable workflows (job-level `uses:`) and local actions (`uses: ./...`) fail, because an upload inside them could
   not be checked. All three workflows install PyYAML hash-pinned (`scripts/ci/requirements-ci.txt`) before their
   scans. **No PyYAML means the check fails closed.**

   PyYAML reads YAML 1.1 and GitHub reads YAML 1.2, and they disagree on some plain values: `08` is the text "08" to
   PyYAML but the number 8 to GitHub. That is why an upload path must start with a letter: no reading of such a value
   is a number, and `yes`/`no`/`true`/`null` fail because PyYAML does not read them as text. Every other value the
   check relies on is text to both readers: generated (`id`, `run`, `if`), a `${{ }}` expression, `shell: pwsh`, a
   `NAME_KEY` env name, or a `uses:` containing `upload`.

The self-test proves each piece on its own: the grammar, the PyYAML layer, the case rule, the letter-first path rule,
duplicate keys (also when they differ only in case), reusable workflows, local actions and the missing-PyYAML failure
each have cases that only they catch, so switching any one off turns the self-test red. A local `--self-test` therefore
needs PyYAML installed (`pip install --require-hashes --only-binary=:all: -r scripts/ci/requirements-ci.txt`). Any action
whose name contains `upload` counts as an upload.

Each scan first proves itself (`--self-test`: every pattern, the word edges, the failure paths and the PowerShell wrapper's own logic, on fakes built at run time), then reads
every file as raw bytes (binaries, screenshots and the published app included), as UTF-16 LE at both byte offsets (BE
when marked), URL-decoded, and every file name, following folder links. Output never shows a value: a file or folder
name that holds a key in any form prints as `<hidden name sha256:...>`, and errors print only their type and errno,
never their text. `--diff BASE` scans the lines and file names a PR adds (text, binary and UTF-16 files, git-quoted
names) and prints file:line (for the `quality` workflow, see Known gaps).

## BOO-SAFE-002
**An API key in an upload (or in a PR's added lines).** Two kinds of hit:
- **key-shaped text:** OpenAI / Anthropic `sk-`, GitHub `ghp_`/`gho_`/`ghu_`/`ghs_`/`ghr_` and `github_pat_`, Google
  `AIza`, AWS access key ids, private-key blocks, Slack `xox?-`, Tailscale `tskey-` and Hugging Face `hf_` tokens, JSON
  Web Tokens, long bearer tokens. The only exception: text that appears verbatim inside a **frozen** test file (listed
  in `docs/verification/test-oracle.json`), i.e. the redaction tests' deliberate fakes, also when xUnit shortens them
  in a test name. Only frozen files whose content still matches their oracle hash count, and that list changes only
  through a reviewed oracle revision, so there is no hand-kept allow-list.
- **real secret:** the exact value of a secret the job holds (`--secret-env`; `live-ai-smoke` passes the three
  `BOO_LIVE_*_KEY` secrets), found plain, UTF-16 (LE or BE), URL-encoded, or base64 (standard or URL-safe, any byte
  alignment). Never allowed, not even if the text is in a test.

**Fix:** key-shaped text: find what wrote it and redact it at the source. Real secret: revoke and rotate the key, then
fix the code that wrote it (log lengths, never values). **Do NOT** skip the scan, narrow a pattern, move the file out
of the upload path, add an allow-list, rename the secret or drop `--secret-env`.

Known gaps of the key scan:
- a process still running from an earlier step could write into the upload folder after the scan;
- a folder link that points at a folder already scanned is not scanned again under its own name (a key in that
  link's NAME would be missed); theoretical on the Windows runners;
- a key shown as **pixels** in a screenshot is not text and is not found (Boo never draws its keys; the Settings key
  box is masked);
- key-shaped text that is base64-encoded, gzip/zip-compressed, or an AWS *secret* key (a plain 40-character string)
  is not detected; base64 is covered only for real secrets;
- an upload the check cannot see as an upload step is not scanned: a remote action whose name does not contain
  `upload` (a composite or docker action that uploads inside it), or a `run:` command that uploads
  (`gh release upload`, `curl`); such steps need a human review;
- `release.yml` uploads (installer, portable ZIP, `gh release create`) are not scanned yet (not in PR 1.7's files);
- the PR-diff scan (`--diff`) is ready but not wired: it needs one step in `quality-gates.yml`, which belongs to
  another PR;
- the plan's "high-entropy strings" check is not implemented: on uploads it would flag every hash, GUID, compressed
  image and compiled file (see `docs/DECISIONS.md`).

Red proof (13 Nov 2026): run [43359573803](https://github.com/vikas53953/bluey/actions/runs/43359573803) on a
throwaway branch planted a run-time-built fake key in the test results; the scan before that upload reported
`OpenAI/Anthropic API key ... The value is not shown`, deleted the file and failed the job (`KEY_SCAN_FAILED 1 of 7`).

## Build facts (the shipped build)

Two gates were once bypassed because they judged a different build from the one that ships (the banned-API gate by a
per-folder `.editorconfig` and by `<CompilerResponseFile>`; the architecture gate, which evaluated each project alone in
Debug on Linux, by references that exist only under `Configuration == Release`, a `RuntimeIdentifier`, `_IsPublishing`,
`SolutionName` or `IsOSPlatform('Windows')`, by `PackageDownload` plus an `Exists()`-guarded import, and by NuGet MSBuild
SDKs). So the gates now read **facts of the real build**: `windows-ci`'s own Release build of `Boo.sln` and its publish
of `Boo.App` write binary logs (`-bl`, nothing else changes), and the step **Build facts (the shipped build)** turns them
into `build-facts.json`. PR #37 added the plumbing; PR 1.3 (#28) judges the banned APIs and the compile list on it
([Banned APIs](#banned-apis-rs0030)); the architecture judge (#32) comes on top later.

**What is captured** (`scripts/quality/buildfacts`, a small .NET reader pinned to `MSBuild.StructuredLogger` 2.3.269
and Roslyn 5.6.0 with a lock file, restored in locked mode): for every project / target framework / runtime identifier
evaluated in each log: its global properties, key properties, every imported file (`<= importer`), every skipped import
with the reason, `PackageReference` / `PackageDownload` / `ProjectReference` / `Reference` / `FrameworkReference`
items, the packages and download dependencies restored into `obj/project.assets.json`, and the ProjectReferences really
followed into the compile. For every `Csc` task that ran: its target and the file defining it, the task's assembly, the
compiler, the whole command line with **every response file expanded** (and each response file's text), each
`/analyzerconfig:` with its **text**, analyzers, additional files, sources, `/nowarn`, `/warnaserror`, `/ruleset`,
`/errorlog` (with the SARIF log), Roslyn's own reading of the options, every reference with where it came from
(ProjectReference, PackageReference, FrameworkReference, direct Reference) and the output assembly with its SHA-256. For
the whole build: every task that ran (`Task | Target | file defining the target | task assembly`), every copy of an
assembly with the source's SHA-256, every ReadyToRun compile (input and output with SHA-256) and the runtime packs.
Paths are repo-relative or under a token. Every machine root is taken from the build's **own evaluated properties**
(the SDK: `NetCoreRoot`, `DOTNET_HOST_PATH`; NuGet: `NuGetPackageRoot`, `NuGetPackageFolders`; `%ProgramFiles%`,
`%ProgramFiles(x86)%`, `MSBuildProgramFiles32`; the Windows SDK and reference-assembly folders; `TEMP`, `RUNNER_TEMP`,
the runner tool cache, `USERPROFILE`; every folder above the repository) plus this process's, and mapped to `<dotnet>/`,
`<nuget>/`, `<programfiles>/`, `<programfiles-x86>/`, `<winsdk>/`, `<temp>/`, `<toolcache>/`, `<localappdata>/`,
`<home>/`, `<repo-parent-N>/` and so on, case-insensitively, with either slash and in 8.3 short and long forms, longest
root first. Paths may hold spaces (`C:\Program Files\dotnet` on windows-latest): a whole raw string is tokenised before
it is ever split, the compiler path in a `Csc` line (logged unquoted) is cut at its own name, and arguments are split
quote-aware. A root matches only as a whole folder: followed by a separator, a quote, a bracket, `;`, `,`, the end, or
whitespace that starts the next argument (so `D:\a\my repo` never eats `D:\a\my repo 2`). Values are read only to find
roots.

**Secret guard.** The log's environment block is never read. `globalProperties` keep values only for reviewed property
names (`PropertyFilter.Allowed`); any other is written `Name=<value not recorded>`. The MSBuild `commandLine` keeps **no
property value at all**: every `-p:`/`/p:`/`-property:`/`--property:`/`-rp:`/`-restoreProperty:` value, in any case,
quoted, repeated, or with pairs split by `;` or `,`, becomes `<value not recorded>`, and so does the value of every
other switch except a short list whose values are fixed words or tokenised paths (`-m`, `-v`, `-tlp`, `-tl`, `-t`,
`-restore`, `-nologo`, `-clp`, `-nr`, `-bl`). What is not covered: values the build itself writes into compiler inputs
or reviewed properties are recorded, because the judges need them: `/define:` symbols, `/nowarn` and `/warnaserror`
lists, `build_property.*` lines in the generated analyzer config, and the per-unit `properties` map (for example
`NoWarn`, `AssemblyName`, `DOTNET_HOST_PATH`). A secret put into one of those would be written out.

**Path guard.** Any absolute path left after tokenising fails closed in both the reader and the check, naming the JSON
path of the value (for example `$.binlogs[1].units[0].importsSkipped[12]`). It looks for every absolute-path shape, case
insensitive: a drive letter followed by `\` or a single `/`, at **any** position, even glued right after a letter or
digit (`/reference:libC:\x`, `abc1D:\x`, `file:///C:/x`; a URL's `scheme://` is not a drive), also inside `\\?\C:\`;
a URL-encoded drive (`C%3A%5C`, `C%3A%2F`, `C%3A/`); and after any delimiter (`:`, `@`, `;`, `,`, `[`, `=`, quotes,
spaces) a UNC path (`\\srv\`, `\\?\UNC\`), `//host/` (a URL's `scheme://` is allowed), a POSIX root (`/home/`,
`/users/`, `/root/`, `/opt/`, `/tmp/`) and an msys drive (`/c/`); for these last shapes a path character right before
means the middle of a relative path, which passes. It also refuses the user name. Still open: a drive-relative path
without a separator (`C:Users`), which cannot be told apart from text like `x:Class`; other POSIX roots (`/var/`,
`/mnt/`, `/usr/`). The fix for a hit is to add the root's property to
`PathTokens.FromProperties`, never to widen the guard.

`BuildFacts --self-test` proves the tokens on a GitHub-runner layout (`C:\Program Files\dotnet`,
`C:\Users\runneradmin\.nuget\packages`, `D:\a\_temp`, `C:\PROGRA~1`), every kind of field on roots that hold spaces
(the Csc compiler path unquoted, quoted and as `dotnet exec`; Csc arguments; the MSBuild command line; imports,
references and copies; analyzer-config, response-file and skipped-import text), the root boundary, every path-guard
shape and the relative paths and URLs that must pass, and the command-line filter (comma, semicolon, quoted, `/p:`,
`-property:`, `-rp:`, repeated switches).
Same logs, same file (sorted keys).

**What fails closed** (`scripts/quality/build_facts.py check`, exit 1): malformed facts or a failed build; a machine
path or the user name in the facts (by JSON path); a project of `Boo.sln` without a `Csc` call from `CoreCompile` in the
build log; a `Csc` compile of any project **outside** the solution (the only exception is WPF's temporary markup
project: `<dir>/<Name>_wpftmp.csproj`, after the reader drops its random part, next to a solution project
`<dir>/<Name>.csproj` and compiling that project's own assembly name; any other `*_wpftmp.csproj` is an ordinary
project); a `Csc` call
with no output hash, a compiler or task class outside the SDK install, a command-line parse error, a response file (the
SDK passes none, so build code added it), or a switch not in `scripts/quality/buildfacts/known.json`; any task not in
`known.json` (so a target added by repo build code, a package or an MSBuild SDK that runs a task is unknown until
reviewed; the check then prints the candidate `learn` lines for review); two solution projects compiling one assembly
name; any recorded copy **into** a file named like a solution project's assembly whose source does not trace to that
project's own compile; and any managed `.dll`/`.exe` in the publish folder that does not trace, hash for hash and under
its own name, through recorded copies and ReadyToRun compiles to a recorded `Csc` output or to a restored package or
runtime pack, or whose name belongs to a solution project but whose content is not that project's compile.
`--self-test` proves each failure on planted facts.

`fixtures` builds nine fixtures (`scripts/quality/buildfacts/fixtures/`) and quotes the facts lines that show each
bypass. **Four only have to SHOW the bypass in the facts** (their judges come in #28 and #32): per-folder
`.editorconfig` severity, the five conditions (Configuration, RuntimeIdentifier, `_IsPublishing`, SolutionName,
`IsOSPlatform`), `PackageDownload` + `Exists()` import, and a NuGet MSBuild SDK; their check result is only reported.
**Five must also FAIL the check**: `compiler-response-file` (response file), `tamper-after-compile` (a `Copy` over the
compiled assembly right after `CoreCompile`: an unknown task), `tamper-publish` (an `Exec` that copies other code over
the published `App.dll`: an unknown task and the hash trace), `same-name-compile` (a project outside the solution
with `<AssemblyName>Lib</AssemblyName>`, moved over the judged `Lib.dll` only by the SDK's own allowed copy of a linked
`None` item: an outside compile, and copies whose source is not `Lib`'s compile), and `fake-wpftmp` (an ordinary
project outside the solution named `Extra/Extra_wpftmp.csproj`, referenced by the app, ships `Extra.dll`: an outside
compile). `learn FACTS` lists task keys and
switches that `known.json` does not hold yet; adding one is a reviewed change.

Only `build-facts.json` is uploaded (artifact `build-facts`, 7 days, through its key scan); the binary logs never are,
because they can hold environment values.

**Still open (known holes):**
- the workflow file itself: a PR can edit `windows-ci.yml` (drop `-bl`, the step, or `known.json`) and judge itself
  (HANDOFF known issue 0); the first PR has no base-branch judge, so this PR's own copy judges it;
- build code that lies to the logger: a task that writes files it does not log, or a compiler server or tool that is
  not what its path says; the facts record what MSBuild logged;
- hashes are taken when the reader runs, after the build: a file changed **in place** after the compile by a step the task rule allows (an SDK task fed crafted items): the
  hash trace compares the publish folder with the compile output as it is on disk when the reader runs, so an
  overwrite of the compile output itself is caught only by the task rule; replaying the recorded `csc` command (the
  build is deterministic) would close it;
- what the code does at run time (reflection, loading other assemblies, `Process.Start`) is not a build fact;
- the reader and `build_facts.py` run from the PR's own tree and the repo's `Directory.Build.props` applies to the
  reader's build; a later PR runs the base branch's copy (like the ratchet's `--rise-only`);
- `known.json` was learned from one build on 13 Nov 2026 (SDK 10.0.403); an SDK or package change that runs a new
  task fails closed until reviewed.

## Architecture rules (BOO-ARCH, `tests/Boo.Architecture.Tests`)

Plan section 2.4 B, PR 1.11. A normal xUnit project that reads the compiled `src` assemblies, so it runs wherever the
solution builds; no app starts and no screen is touched. Each rule lists today's offenders **by name** (baseline
13 Nov 2026, `lane/face` `af089dd`). The list is exact both ways: a **new** offender fails, and an offender that has been
**fixed** also fails until its name is removed, so every gain is locked in. The test project is frozen (oracle
revision 25), so removing a name, like adding a rule, goes through a reviewed `scripts/test_oracle.py --rebaseline`.
Dependency offenders (001-004) count per outermost type (lambdas and async state machines count towards the type that
wrote them); static-state offenders (005) are named per declaring type, nested types included.

**Why two tools, and two shapes.** 001 and 004 are about whole namespaces and use NetArchTest. 002 and 003 use
`CecilScan` (Mono.Cecil, the library NetArchTest is built on), which sees base types, interfaces, fields,
properties, events, attributes, parameters, return values, locals and every member the code calls, constructs,
casts to or names, generic arguments included.

- **002 is an allowlist.** Disk access is spread across the whole framework (`File`, `FileInfo`,
  `StreamWriter(path)`, `Path.Exists`, `XDocument.Load(path)`, `TarFile`, certificate and resource loaders,
  `AssemblyLoadContext`, ...), so any list of bad types or namespaces misses one; reviews proved it several times.
  Core is small and pure, so it may use **only** the framework types approved in the test (text, collections, maths,
  JSON, regex, tasks, hashing, and the exceptions and enums it needs), and only when they really come from a listed
  framework assembly (a package cannot borrow a framework namespace name). **The test for every approval:** nothing
  reachable through it may depend on or change the files on this machine, or find or build code from a name. A type
  where only some members pass is approved **member by member**: of `Path` only the members that work on the text of
  a path (not `Exists`, `GetTempFileName`, or `GetFullPath`, which asks Windows for the real names behind short ones
  like `RUNNER~1`), of `Environment` only reads such as `GetEnvironmentVariable` (not `CurrentDirectory`), of `Type`,
  `Delegate`, `SHA256` and `RandomNumberGenerator` nothing that finds code by its name. Raw memory is not allowed:
  `Unsafe` (only the compiler's own helper type may use it), raw handles, function pointers, `calli`, and compiling
  Core with unsafe code. Native calls, COM classes and `[UnsafeAccessor]` methods are not allowed at all, and type
  names inside attributes (`typeof`) are checked too. Anything else fails and the message names it; if it passes the
  test, a reviewer approves it in a one-line oracle revision.
- **003 is a deny rule**, because the app is the UI and uses far too much of WPF and Windows for an allowlist. It
  counts every `System.Net.*` type except a few harmless ones (status codes, request errors, `WebUtility`,
  `NetworkCredential` as a password reader), WebView2, the WPF browser controls (`WebBrowser`, `Frame`,
  `NavigationWindow`), every Windows Runtime area except the two the app uses (`Windows.Foundation`,
  `Windows.UI.ViewManagement`), and native calls into anything but `user32`, `kernel32`, `shcore`, `dwmapi`, `gdi32`.

**Known gaps.** (1) BOO-ARCH-005 sees fields and setters, not what a `readonly` field points to: a
`static readonly List<T>` that is changed later (today `AccessibilitySettings.Listeners`, `.Add` at line 48) is
shared mutable state the rule does not count; counting every `static readonly` collection or array would flood it
with read-only tables. (2) BOO-ARCH-006 looks at `src/Boo.App/*Window*.cs` in the top folder, which holds every
window code-behind today; a window elsewhere or not named `...Window` would be missed. (3) 002 and 003 read the
compiled code. For 002 that leaves one gap: an approval that fails the test and that review missed. Accepted on
purpose, because they read the machine's settings or the app's own files, never the user's: a stack trace reads the
app's own debug file (`.pdb`), local time and culture read Windows settings, `AppContext` hands Core the install folder
as text, and a named `Mutex` is a signal between processes. Core can always use disk through an object the app hands
it (a `Stream`, an interface); that is the design, and no scan of Core alone can rule it out. For 003, reflection and
anything reached by name are not seen. (4) 003 is a deny rule, so it does **not** see the app reaching
the web some other way: a WPF image, media or font given a web address (`BitmapImage`, `MediaElement`), anything
declared only in XAML (compiled XAML is not scanned), an XML loader given a web address (`XDocument.Load`,
`XmlReader.Create`), COM objects, or a third-party library. It also does not count,
on purpose, Velopack's update check and download (`UpdateService`, release lane) or opening a link in the browser
(`Process.Start`); neither is a chat or AI client.

| Rule | Checks | How | Today (plan section 1.5, except 002) |
| --- | --- | --- | --- |
| [BOO-ARCH-001](#boo-arch-001) | `Boo.Core` uses nothing from `System.Net.Http`, `System.Net.WebSockets`, `Microsoft.Win32`, `Microsoft.Data.Sqlite`, `NAudio`, `Velopack` | NetArchTest, namespace match | 1: `ProviderFailureClassifier` (`ProviderFailure.cs`, `HttpRequestError`) |
| [BOO-ARCH-002](#boo-arch-002) | `Boo.Core` does not touch the disk: it uses only approved framework types and members (see the test above), no raw memory, and no native, COM or by-name call | allowlist (Mono.Cecil) | 3: `ScopedFiles` (the second class in `ActionPolicy.cs`), `FileActionResolver`, and `StartupRegistration` (`Path.GetFullPath`, lines 68-69), which the plan's text count of `File`/`Directory` could not see |
| [BOO-ARCH-003](#boo-arch-003) | `Boo.App` does not talk to the network through `System.Net.*` (harmless types aside), WebView2, the WPF browser controls, other Windows Runtime areas, or native network DLLs | deny rule (Mono.Cecil); gaps listed above | 1 type: `MainWindow` (its 3 references: `MainWindow.xaml.cs:65,1624,2123`) |
| [BOO-ARCH-004](#boo-arch-004) | `Boo.Windows` uses nothing from `System.Windows.Controls` / `System.Windows.Shapes` and has no `Window` subclass | NetArchTest | 0 |
| [BOO-ARCH-005](#boo-arch-005) | no static field that is not `readonly`/`const`, no static property with a setter, in all 5 `src` assemblies | reflection; compiler-made caches excluded; named per declaring type (`Outer+Inner.Field`) | 2: `AccessibilitySettings._uiSettings`, `App.StartedInBackground` |
| [BOO-ARCH-006](#boo-arch-006) | every `src/Boo.App/*Window*.cs` file has at most 2,679 lines | line count | 0 over (largest: `MainWindow.xaml.cs`, 2,679; the plan measured 2,674 before PR #33 added 5 lines) |
| [BOO-ARCH-007](#boo-arch-007) | a new `MainWindow` feature goes in its own `MainWindow.<Feature>.cs` partial | written rule only (review) | |

## BOO-ARCH-001
**Boo.Core stays pure.** Core is the code every other layer and every unit test relies on; network, registry,
database, audio or installer types make it slow and hard to test. **Fix:** put the outside call in `Boo.Providers`,
`Boo.Storage` or `Boo.Windows` and pass Core plain values or an interface. **Do NOT** move the type into Core "to reuse it".

## BOO-ARCH-002
**Core does not touch disk.** File checks in Core make rules depend on the machine. **Fix:** an interface in Core (like
`IStartupRegistry`) with the real implementation outside. The failure message names every framework type or member
that is not approved. **Do NOT** swap `File.Exists` for `FileInfo`, `Path.Exists`, a stream or another loader: Core may
use only approved types and members, so all are caught. **Do NOT** approve a type unless no member of it can reach
the disk; if some can, approve only the members that cannot.

## BOO-ARCH-003
**The window does not talk to the network.** Network calls belong in `Boo.Providers`, where timeouts, failures and
keys are handled in one place (the 45 s timeout lives in `MainWindow` today for this reason). **Fix:** create the
client in Providers and inject it. **Do NOT** hide a new `HttpClient` in a helper class in `Boo.App`, or swap it for a
handler, a socket, `Dns`, a browser control or a Windows Runtime network API: all are caught. The rule cannot see a web
address handed to a WPF image or media element, or XAML-only elements (known gap 4); reviewers check those.

## BOO-ARCH-004
**Boo.Windows is Windows integration, not UI.** Bitmaps (`System.Windows.Media`) and UI Automation are fine; controls,
shapes and windows belong in `Boo.App`.

## BOO-ARCH-005
**No mutable static state.** A static that changes is shared by every caller and every test, which makes order-dependent
bugs. **Fix:** an instance field passed in, or `static readonly` set once. **Do NOT** add `readonly` and mutate the
object inside it to get past the check.

## BOO-ARCH-006
**Window code-behind stays under its budget** (today's largest file, 2,679 lines). The budget may only go down, in a
reviewed oracle revision. **Fix:** move the feature into `MainWindow.<Feature>.cs` (BOO-ARCH-007) or its logic into
`Boo.Core`. **Do NOT** raise the budget.

## BOO-ARCH-007
**One feature, one partial.** `Boo.App` is already sliced by feature (`MainWindow.Talk.cs`, `.Voice.cs`, `.Edits.cs`,
`.Focus.cs`, `.Updates.cs`, `.Accessibility.cs`, `.Shortcuts.cs`). A new feature gets its own file. Checked by review;
BOO-ARCH-006 is what makes skipping it fail.

## Evidence table in every PR body (PR 1.10)

Workflow `pr-evidence` (job `evidence`, Linux, well under a minute, no artifacts) runs `scripts/quality/pr_evidence.py`
on every pull request when it is opened, edited or pushed to, so fixing the body re-runs only this check. It reads the
body from the event file, never from a `${{ }}` expression pasted into a shell. Step order: the **base branch's** copy
of the checker (read from git) judges the body first, before any code from the PR runs, so a PR cannot loosen the
checker script it is judged by; then the PR's own copy runs its self-test (every rule on HTML samples), its render test
(the known tricks, sent through GitHub's real renderer) and the check.

**GitHub renders the body, the check reads the result.** The script does not parse Markdown itself: it sends the body
to GitHub's Markdown API (the same renderer as the PR page) and reads only the rendered tables and the real link
targets (`<a href>`). What GitHub does not show as a table or a link does not count: HTML comments, code blocks, text
inside HTML attributes, a run URL that is only a link's hover title. If GitHub cannot render the body, the check fails
closed.

What the check guarantees:
- **Every table is a plain Markdown pipe table.** A table written in raw HTML can be drawn in a different order than it
  is written (merged cells, rows before the header, a footer, a table inside a table), and raw HTML inside a cell
  (`<details>`, `<ruby>`, `<span>`, ...) can hide text or links. So a table without exactly the shape GitHub gives a
  pipe table, or with anything in a cell other than text, links, bold, italic, strikethrough, code, emoji, images and
  line breaks, fails (footnotes and math in a cell fail too).
- **The Label column reads as shown.** A Label cell holds only the label word and plain words after it (bold, italic,
  strikethrough, code, emoji); an image, link or line break there fails. Struck-out text is not read, as a reader
  would not read it: `~~mock~~ live` is a live row. Header rows and Label cells may hold only plain ASCII plus everyday
  dashes, quotes, arrows, comparison and currency signs, check marks and common emoji: a look-alike letter (Cyrillic
  `а`, circled `ⓛ`, Roman-numeral `ⅼ`, full-width or squared letters) fails there. Other cells of an evidence table
  are free text.
- **Every live row has a readable run link** (see BOO-EVID-001).

Best effort, not a guarantee: a table without a `Label` column is not evidence, and a cell there that starts with
`live` (header row included; also `Iive`, `LlVE`, `L1VE`) fails, as does any character outside the allowed set in such
a table, to catch a misspelled or look-alike `Label` header. Claims written in prose, a list or a code block are not
checked and do not count.

## BOO-EVID-001
**A claim without evidence.** The PR body must have a Markdown table with a column headed `Label`, one row per claim:

| Claim | Label | Proof |
| --- | --- | --- |
| Real AI round trip on this head | **live** | [run 123](https://github.com/vikas53953/bluey/actions/runs/123) |
| Demo journeys (mock AI) | mock | [run 124](https://github.com/vikas53953/bluey/actions/runs/124) |
| Voice | UNVERIFIED | not tested |

- Each label (the first word of the cell; bold and plain words after it are fine) is one of `live`, `mock`, `manual`,
  `UNVERIFIED`, `BLOCKED`, `review`.
- Every `live` row has a real link whose text has a letter or digit (not an image, not an invisible character) to a
  GitHub Actions run of this repository, written as a full `https://github.com/...` address (a run, a job or an
  attempt of it). No run link means it is not live.
- A PR that changes anything under `src/` or `tools/` has at least one `live` row, or a `BLOCKED` row that says what
  blocks the live run (after the label, in the same cell or a column to its right).

**Fix:** add or correct the table in the PR body (edit the body; the check re-runs). A local run is `manual` or
`mock`, never `local`; a claim that was not checked is `UNVERIFIED`, never `—`. **Do NOT** label a mock run `live`,
link an unrelated run, or hide the table or a link in a code block or an HTML comment (neither is counted).

What this check is for: it catches a missing or sloppy evidence table and every trick its reviews found (listed in
the self-test and the render test). It cannot prove an author is honest: a claim outside the Label column (`tested
live` in a Status table, a badge image in a table without a Label column, prose), or a link to a run that proves
nothing, can pass it. The separate reviewer and the QA PASS line remain the judge of whether the evidence is true.

Known gaps: the check needs GitHub's Markdown API (it fails closed when that is down; re-run the job). A table inside
a collapsed `<details>` counts (a reader can open it). It proves a table and links exist, not that each linked run is
green, is on the PR's head, or proves the claim next to it (the separate reviewer and the QA PASS line cover that). A
PR can edit `pr-evidence.yml` itself; like every gate here, that is caught by review and the required-check settings
(Vikas's decision), not by the workflow.

## BOO-TEST-001
**A skipped or empty test outside `tests/`.** `scripts/test_gate.py static` (in `windows-ci`) checks every xUnit
test project in the repository, wherever it lives (a test fixture project that references xUnit is checked too) (today the 8 under `tests/` and `tools/Boo.CaptureProof`,
`tools/Boo.LiveSmoke`, `tools/Boo.VerifyLive`): no empty `.cs` file, every `*Tests.cs` file declares a `[Fact]` or
`[Theory]`, every test project declares a test, and no `Skip =` on any test attribute.

**Fix:** make the test run, or delete it in a reviewed change. **Do NOT** add `Skip =`, empty the file, or move the
test into a project the gate does not recognise as a test project (one without xUnit).
