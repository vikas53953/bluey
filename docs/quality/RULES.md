# Boo quality rules

Every check added by Phase 1 of the [agent-trust plan](../plans/agent-trust-plan.md) has an id here. A failing check
prints **What / Why / Fix / Do NOT / Rule** and links to its anchor below. The "Do NOT" line names the tempting wrong
fix, because that is what an agent reaches for next.

## How the ratchet works

[`scripts/quality/ratchet.py`](../../scripts/quality/ratchet.py) reads every `src/**/*.cs` with a small C# lexer
(strings, `$"{...}"` interpolation holes, verbatim and raw strings, char literals, line and block comments), counts
each pattern below **per project, every match on a line**, and compares with [`baseline.json`](baseline.json):

- a count that goes **up** fails (new problem);
- a count that goes **down** also fails until you run `python -I scripts/quality/ratchet.py --lower` and commit the
  lower baseline, so every gain is locked in; `--lower` refuses to raise any number;
- `--list <RULE>` prints every hit; `--self-test` checks 83 snippets with exact expected counts (including tricky
  strings, interpolation format clauses and `#` directive lines that could hide code), that clean code stays quiet,
  and the pass/fail logic itself (equal passes, rise fails, fall fails until lowered, a vanished or renamed project,
  an extra empty project, a nested `bin/`, the test loader). Each lexer rule has a snippet whose count changes if that
  rule breaks: string, verbatim `""`, `\` escapes, char literals, `{{`, hole brace and bracket depth, format clauses,
  `::`, raw strings, line and block comments, `#` directive lines, and the comment exemptions (checked by switching
  each off in a scratch copy, 13 Nov 2026).

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
are not checked. Not counted: whole-line comments of the form `// INVARIANT: ... see NameTests.Method` or
`// SECURITY: ... see NameTests.Method`, where a class `NameTests` with a method `Method(` really exists under `tests/`,
and whole-line `// TODO(#123): ...`. The same text at the end of a code line, or naming a test that does not exist,
counts. **Fix:** fix the cause instead of explaining it; if it states a rule, write the test and use
the INVARIANT tag; API docs use `///`. **Do NOT** reword the comment to dodge the count.

## BOO-CMT-002
**Untracked TODO / FIXME / HACK / XXX** in any comment, `///` included. **Fix:** open a GitHub issue and write
`TODO(#123): ...`. **Do NOT** delete the note and keep the debt.

## Known gaps (the text-level ratchet does not see these; later Phase 1 PRs make them exact)

- `.Wait()` / `.Result` on a task variable whose name does not contain "task"; an instance `process.Start()`.
- A broad catch whose body has an unreachable `throw` counts as "rethrows".
- A static field declared over two lines, or with a tuple type plus `=>` initializer.
- `///` comments written inside a method body count as documentation.
- Identity: a swap (one removed, one added, same rule and project) passes, as explained above.
- Scope: only `src/<Project>/` folders that hold a `.csproj` are scanned; only the project's own top-level `bin/` and
  `obj/` are skipped (a nested `Sub/bin/` is scanned, as the build compiles it); files compiled in from outside `src/`
  (`<Compile Include="../..">`) are not scanned.
- Renames: a baselined project that disappears fails both the PR's own check and the base branch's check, on purpose:
  a judged-on-totals mode would let a PR swap hits between projects or drop a project from the scan. A real rename is
  rare; it needs the owner to override the check once, with the reason in the PR. An extra project with no hits
  changes nothing.
- Disabled `#if` blocks: the compiler skips their text, but the ratchet reads it, so an unmatched quote or `/*` inside a
  disabled block can hide or invent a later hit (none in `src/` today).
- Exemption tags: `// INVARIANT: ... see NameTests.Method` is exempt when any file under `tests/` declares
  `class NameTests` and contains `Method(`. The barrier against a fake test is review: `tests/` is the frozen oracle, so
  a new test file needs a visible `scripts/test_oracle.py --extend` revision that the reviewer must check.
