# Action preview focus handoff — builder receipt

Date: 2026-11-07
Base: `0d7dd6ba6f0022667d5df40c0afaf4fac214a517`

Behavior changed: **true**. The existing Cancel focus request is now deferred
with explicit `DispatcherPriority.Loaded`. The callback captures the pending
preview, request lease, request generation, and keyboard-focused element at
scheduling time. It checks preview identity, the same non-null current lease,
current request and authority generations, close/visibility/active state,
visible preview state, approval state, and unchanged keyboard focus before
calling `ActionPreview.BringIntoView()` and then `CancelButton.Focus()`.
Immediate preview display, data binding, and status behavior remain unchanged.

## Changed files

- `src/Boo.App/MainWindow.xaml.cs`
- `docs/verification/action-preview-focus-build.md`

No tests, contracts, policy, payload, styling, retry loop, activation/raise,
or other source files were changed. No commit was created.

## Existing evidence and tests

The bounded work statement and native RED receipt were read before editing:
`docs/verification/action-preview-focus-work.md` and
`docs/verification/native-typing-candidate11.md`. The existing
`ShowActionPreview` callers, `StopAll`, `ClearActionPreview`, and
`BeginRequest` paths were inspected. Existing Core tests
`tests/Boo.Core.Tests/ActionPreviewTests.cs`,
`tests/Boo.Core.Tests/ActionPreviewValidationTests.cs`, and
`tests/Boo.Core.Tests/RequestCoordinatorTests.cs` remain unchanged; they do
not provide a deterministic WPF dispatcher/focus seam. No private-method or
source-string test was added.

## Verification

The first requested no-restore build could not start compilation because this
fresh worktree had no `src/Boo.App/obj/project.assets.json`. The same scoped
App Release build with the pinned restore path passed:

```text
./scripts/build.ps1 -Action build -Project src/Boo.App/Boo.App.csproj -Configuration Release
```

Result: **PASS** — `Boo.App` built with 0 warnings and 0 errors.

```text
python scripts/test_oracle.py
ORACLE_VERIFIED revision 14 37 files
git diff --check
```

Both checks passed. The full suite was intentionally not run: this worktree
has seven known baseline Core coordinator-stub failures owned by a separate
builder, and this unit has no deterministic WPF focus test seam.

## Native boundary

The native RED source is candidate11's fresh exact proposal: Cancel and Approve
were present, while actual Sky `accessibility.focused_element` remained
`InputBox` at `09:18:17.517Z` and `09:18:29.754Z`; no driver action refocused
InputBox. That establishes the failed focus handoff, not a confirmed Windows
layout cause. No manual native/package test was run by this builder; the
deliberate exception is owned by root's later exact-package native QA. Until
that QA, this is a source/build repair and the focus behavior is not proven
native-fixed. Typing, approval, click, Undo, and full delivery remain open.
