# Boo quality rules

Every check added by Phase 1 of the [agent-trust plan](../plans/agent-trust-plan.md) has an id here. A failing check
prints **What / Why / Fix / Rule** and links to its anchor below. The "Do NOT" line names the tempting wrong fix.

How the ratchet works: [`scripts/quality/ratchet.py`](../../scripts/quality/ratchet.py) counts each pattern in
`src/`, per project, and compares with [`baseline.json`](baseline.json). A count that goes **up** fails (new problem).
A count that goes **down** also fails until you run `python -I scripts/quality/ratchet.py --lower` and commit the
lower baseline, so every gain is locked in. `--lower` refuses to raise any number. `--list <RULE>` prints every hit.
The workflow is [`quality-gates.yml`](../../.github/workflows/quality-gates.yml) (Linux, about a minute, no uploads).

Counts are text-level (strings blanked, comments separated). The compiler-exact versions (banned APIs, analyzers) come
in later Phase 1 PRs.

| Rule | Pattern | Baseline (12 Nov 2026) |
| --- | --- | --- |
| [BOO-ASYNC-001](#boo-async-001) | `Thread.Sleep` | 0 |
| [BOO-ASYNC-002](#boo-async-002) | blocking wait on async work | 0 |
| [BOO-ASYNC-003](#boo-async-003) | `async void` | 7 |
| [BOO-UI-001](#boo-ui-001) | `Dispatcher.Invoke` | 2 |
| [BOO-UI-002](#boo-ui-002) | `Dispatcher.BeginInvoke` | 13 |
| [BOO-TIME-001](#boo-time-001) | `DateTime(Offset).Now/UtcNow` | 14 |
| [BOO-TIME-002](#boo-time-002) | `Task.Delay` | 1 |
| [BOO-TIME-003](#boo-time-003) | `new DispatcherTimer` | 8 |
| [BOO-RT-001](#boo-rt-001) | `GC.Collect` | 0 |
| [BOO-RT-002](#boo-rt-002) | `Environment.GetEnvironmentVariable` | 3 |
| [BOO-SAFE-003](#boo-safe-003) | `Process.Start` | 3 |
| [BOO-ERR-001](#boo-err-001) | broad `catch` with an empty body | 24 |
| [BOO-ERR-002](#boo-err-002) | broad `catch` that handles and carries on | 73 |
| [BOO-STATE-001](#boo-state-001) | mutable static state | 2 |
| [BOO-CMT-001](#boo-cmt-001) | comment lines that are not `///` | 374 |
| [BOO-CMT-002](#boo-cmt-002) | `TODO`/`FIXME`/`HACK`/`XXX` without `(#issue)` | 1 |

## BOO-ASYNC-001
**Thread.Sleep.** A sleep hides a race instead of fixing it. **Fix:** await the thing you wait for (`Task`,
`TaskCompletionSource`, `SemaphoreSlim.WaitAsync`) or inject `TimeProvider`. **Do NOT** make the sleep longer.

## BOO-ASYNC-002
**Blocking wait on async work** (`Task.WaitAll/WaitAny`, `.GetAwaiter().GetResult()`, `task.Wait()`). Blocking from
the WPF UI thread deadlocks. **Fix:** make the caller async and await. **Do NOT** wrap it in `Task.Run` to hide it.

## BOO-ASYNC-003
**async void.** Its exceptions cannot be caught by the caller and can end the app. **Fix:** return `Task`; a WPF event
handler becomes a one-liner that hands an async `Func<Task>` to one shared helper that awaits it and reports errors.
**Do NOT** add a catch-everything block.

## BOO-UI-001
**Dispatcher.Invoke.** A synchronous cross-thread call deadlocks when the UI thread is itself waiting. **Fix:**
`await Dispatcher.InvokeAsync(...)`.

## BOO-UI-002
**Dispatcher.BeginInvoke.** Fire and forget: exceptions are lost and the order is a guess. **Fix:**
`Dispatcher.InvokeAsync` and observe the result. **Do NOT** add a delay to "fix" ordering.

## BOO-TIME-001
**Hidden clock.** Time-based behaviour (focus sessions, nudges, update checks) cannot be tested with a fake clock.
**Fix:** take a `TimeProvider` (tests pass a fake) and call `GetUtcNow()` / `GetLocalNow()`.

## BOO-TIME-002
**Task.Delay.** A delay used to let things settle hides a race. **Fix:** wait for the real signal; for a real timeout,
use the `TimeProvider` overload.

## BOO-TIME-003
**new DispatcherTimer.** Timers are a common fix for ordering bugs and cannot be tested without a fake clock. Counted
so new ones are visible. **Fix:** prefer an event or an awaited signal; keep real periodic timers in one owner class.

## BOO-RT-001
**GC.Collect.** Hides a leak. **Fix:** find what holds the memory and dispose it.

## BOO-RT-002
**Environment.GetEnvironmentVariable.** Hidden switches (for example `BOO_QA_CAPTURABLE`) change privacy behaviour.
**Fix:** read environment variables in one place and pass the value in.

## BOO-SAFE-003
**Process.Start.** Launching a program is permissioned. **Fix:** go through the `ActionPolicy` gate (`DesktopTools`).

## BOO-ERR-001
**Broad catch, empty body.** The failure vanishes. **Fix:** catch the specific exception you expect and handle it, or
let it propagate. **Do NOT** widen the catch.

## BOO-ERR-002
**Broad catch that carries on.** It also catches the bugs you did not expect. **Fix:** narrow the type, or add a
`when (...)` filter for the exceptions you really expect.

## BOO-STATE-001
**Mutable static state.** Leaks between tests and windows, hides ordering bugs. **Fix:** an instance member owned by
one object, or `readonly`.

## BOO-CMT-001
**Comment that is not `///`.** Agents use comments to justify workarounds, and rules written in comments are not
checked. **Fix:** fix the cause instead of explaining it; if it states a rule, write a test and tag it
`// INVARIANT: ... see Test.Name`; API docs use `///`. (The full allow-list check on new lines comes in PR 1.6.)

## BOO-CMT-002
**Untracked TODO.** **Fix:** open a GitHub issue and write `TODO(#123): ...`.
