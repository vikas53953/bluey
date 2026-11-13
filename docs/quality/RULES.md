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

## Build facts (the shipped build)

Two gates were once bypassed because they judged a different build from the one that ships (the banned-API gate by a
per-folder `.editorconfig` and by `<CompilerResponseFile>`; the architecture gate, which evaluated each project alone in
Debug on Linux, by references that exist only under `Configuration == Release`, a `RuntimeIdentifier`, `_IsPublishing`,
`SolutionName` or `IsOSPlatform('Windows')`, by `PackageDownload` plus an `Exists()`-guarded import, and by NuGet MSBuild
SDKs). So the gates now read **facts of the real build**: `windows-ci`'s own Release build of `Boo.sln` and its publish
of `Boo.App` write binary logs (`-bl`, nothing else changes), and the step **Build facts (the shipped build)** turns them
into `build-facts.json`. This PR is the plumbing only; the banned-API and architecture judges are added on top later.

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
quote-aware. Values are read only to find roots. Global properties and the MSBuild command line keep values only for
reviewed property names (`PropertyFilter`); any other `-p:Name=value` is written as `Name=<value not recorded>`, so a
secret passed as a property never reaches the facts. The log's environment block is never read. Any absolute path left
after that fails closed in both the reader and the check, naming the JSON path of the value (for example
`$.binlogs[1].units[0].importsSkipped[12]`); the fix is to add the root's property to `PathTokens.FromProperties`, never
to widen the guard. `BuildFacts --self-test` proves the tokens on a GitHub-runner layout (`C:\Program Files\dotnet`,
`C:\Users\runneradmin\.nuget\packages`, `D:\a\_temp`, `C:\PROGRA~1`), every kind of field on roots that hold spaces
(the Csc compiler path unquoted, quoted and as `dotnet exec`; Csc arguments; the MSBuild command line; imports,
references and copies; analyzer-config, response-file and skipped-import text), the guard's JSON paths and the property
filter.
Same logs, same file (sorted keys).

**What fails closed** (`scripts/quality/build_facts.py check`, exit 1): malformed facts or a failed build; a machine
path or the user name in the facts (by JSON path); a project of `Boo.sln` without a `Csc` call from `CoreCompile` in the
build log; a `Csc` compile of any project **outside** the solution (only WPF's `*_wpftmp.csproj` may be); a `Csc` call
with no output hash, a compiler or task class outside the SDK install, a command-line parse error, a response file (the
SDK passes none, so build code added it), or a switch not in `scripts/quality/buildfacts/known.json`; any task not in
`known.json` (so a target added by repo build code, a package or an MSBuild SDK that runs a task is unknown until
reviewed; the check then prints the candidate `learn` lines for review); two solution projects compiling one assembly
name; any recorded copy **into** a file named like a solution project's assembly whose source does not trace to that
project's own compile; and any managed `.dll`/`.exe` in the publish folder that does not trace, hash for hash and under
its own name, through recorded copies and ReadyToRun compiles to a recorded `Csc` output or to a restored package or
runtime pack, or whose name belongs to a solution project but whose content is not that project's compile.
`--self-test` proves each failure on planted facts.

`fixtures` builds eight fixtures (`scripts/quality/buildfacts/fixtures/`) and quotes the facts lines that show each
bypass. **Four only have to SHOW the bypass in the facts** (their judges come in #28 and #32): per-folder
`.editorconfig` severity, the five conditions (Configuration, RuntimeIdentifier, `_IsPublishing`, SolutionName,
`IsOSPlatform`), `PackageDownload` + `Exists()` import, and a NuGet MSBuild SDK; their check result is only reported.
**Four must also FAIL the check**: `compiler-response-file` (response file), `tamper-after-compile` (a `Copy` over the
compiled assembly right after `CoreCompile`: an unknown task), `tamper-publish` (an `Exec` that copies other code over
the published `App.dll`: an unknown task and the hash trace), and `same-name-compile` (a project outside the solution
with `<AssemblyName>Lib</AssemblyName>`, moved over the judged `Lib.dll` only by the SDK's own allowed copy of a linked
`None` item: an outside compile, and copies whose source is not `Lib`'s compile). `learn FACTS` lists task keys and
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
