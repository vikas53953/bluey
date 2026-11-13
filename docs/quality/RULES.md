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
  JSON, regex, tasks, hashing, and the exceptions and enums it needs), and only when it really is the installed .NET runtime's: the type must exist in the runtime's own folder, and each
  framework assembly Core references must load from that folder (the test loads it the way the app does). A package
  or project that borrows a framework name, namespace or Microsoft key token, or ships its own higher-version copy of
  a framework file, fails one of the two, because .NET does not check those claims itself. **The test for every approval:** nothing
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
compiled code. For 002 that leaves one gap: an approval that fails the test and that review missed (a type or
assembly that only looks like the framework's is caught by the two runtime checks above; the checks run on the build
machine, so they see what the app would load there). Accepted on
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

| Rule | Checks | How | Today (plan section 1.5, except 002 and 006) |
| --- | --- | --- | --- |
| [BOO-ARCH-001](#boo-arch-001) | `Boo.Core` uses nothing from `System.Net.Http`, `System.Net.WebSockets`, `Microsoft.Win32`, `Microsoft.Data.Sqlite`, `NAudio`, `Velopack` | NetArchTest, namespace match | 1: `ProviderFailureClassifier` (`ProviderFailure.cs`, `HttpRequestError`) |
| [BOO-ARCH-002](#boo-arch-002) | `Boo.Core` does not touch the disk: it uses only approved framework types and members (see the test above), no raw memory, and no native, COM or by-name call | allowlist (Mono.Cecil) | 3: `ScopedFiles` (the second class in `ActionPolicy.cs`), `FileActionResolver`, and `StartupRegistration` (`Path.GetFullPath`, lines 68-69), which the plan's text count of `File`/`Directory` could not see |
| [BOO-ARCH-003](#boo-arch-003) | `Boo.App` does not talk to the network through `System.Net.*` (harmless types aside), WebView2, the WPF browser controls, other Windows Runtime areas, or native network DLLs | deny rule (Mono.Cecil); gaps listed above | 1 type: `MainWindow` (its 3 references: `MainWindow.xaml.cs:65,1629,2128` since PR #33; the plan quotes 65, 1624, 2123) |
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
