---
title: U5 research sources and edit -> Apply -> Undo chain
type: feat
date: 2026-11-08
origin: docs/plans/2026-11-08-feat-boo-video-parity-plan.md (unit U5)
---

# U5: research with sources, and a verified edit -> Apply -> Undo chain

Goal (AE3): a research question returns an answer with clickable source links, or says plainly that it is unsourced; and on the QA fixture (`tests/fixtures/native/Boo.QaWorkspace`) Boo can type new values into the edit fields, click **Apply edit**, read back that the sample button changed, and then, when the user says "undo", reverse the change and read back that the original state is restored.

All decision logic lives in `Boo.Core` (pure, Linux-testable). `Boo.Providers` and `Boo.App` only call it. `MainWindow.xaml.cs` changes stay small; new App orchestration goes in a new partial file.

## Contract (public API in `Boo.Core`, namespace `Boo.Core`)

### 1. `RequestIntent` (file `src/Boo.Core/RequestIntent.cs`)

```csharp
public enum RequestKind { Explain, Research, Task, Undo }
public static class RequestIntent { public static RequestKind Classify(string text); }
```

- `null` -> `ArgumentNullException`. Empty or whitespace -> `Explain`.
- Normalise: trim; compare case-insensitively (ordinal ignore case).
- **Undo** (checked first): after trimming, removing trailing `.`, `!` characters, and removing one optional leading `please ` (and then trimming again), the text equals one of:
  `undo`, `undo that`, `undo it`, `undo the edit`, `undo the last edit`, `undo last edit`, `undo the change`, `revert`, `revert that`, `revert it`, `revert the edit`, `put it back`, `change it back`.
- **Task**: text starts with one of these prefixes (each includes its trailing space):
  `task `, `please open `, `open `, `please create `, `create `, `type `, `click `, `edit `, `please edit `, `change `, `please change `, `set `, `please set `, `apply `.
- **Research**: text starts with `find `, `search `, `research `, `look up `, `lookup `, `latest `, `what's the latest `, `what is the latest `, `compare `; OR the text contains (anywhere) `with sources`, `with links`, `cite sources`, `source links`.
- Order of checks: Undo, then Task, then Research, else `Explain`.

### 2. `ResearchSources` (file `src/Boo.Core/ResearchSources.cs`)

```csharp
public sealed record ResearchResult(string Text, IReadOnlyList<SourceLink> Sources, bool Sourced, string SourceStatus);
public static class ResearchSources
{
    public const int MaximumSources = 8;
    public const int MaximumTitleLength = 120;
    public const string UnsourcedStatus = "No sources were returned. Treat this answer as unverified.";
    public static IReadOnlyList<SourceLink> Normalize(IEnumerable<SourceLink?> candidates);
    public static IReadOnlyList<SourceLink> Extract(System.Text.Json.JsonElement responseRoot);
    public static ResearchResult Finalize(string text, IEnumerable<SourceLink?> candidates);
}
```

`Normalize`:
- `null` sequence -> `ArgumentNullException`. `null` entries are skipped.
- Keep only absolute `http`/`https` URLs with a non-empty host and **no user info** (`user:pass@`); anything else is dropped (including `javascript:`, `file:`, relative URLs, URLs containing control characters).
- Remove the fragment. Remove query parameters whose name starts with `utm_` (case-insensitive); keep other parameters in their original order; if none remain, drop the `?`.
- Lower-case scheme and host. Keep path case. Use this normalised string as `SourceLink.Url`.
- Deduplicate by the normalised URL (first occurrence wins, preserving order).
- Title: remove control characters, trim; if empty use the host; if longer than `MaximumTitleLength`, cut to `MaximumTitleLength - 1` characters and append `…` (U+2026) so the result is exactly `MaximumTitleLength` long.
- At most `MaximumSources` results.

`Extract(responseRoot)` reads an OpenAI Responses API JSON root object:
- Primary: every `output[*].content[*].annotations[*]` whose `type == "url_citation"`: use its `url` and `title` (title may be missing -> use url).
- Fallback only if the primary yields zero links: every `output[*]` item with `type == "web_search_call"` and `action.sources[*]` entries with a `url` (title from `title` if present, otherwise the url).
- Missing/wrong-typed properties are skipped, never thrown. Root that is not an object -> empty list.
- Result is passed through `Normalize`.

`Finalize(text, candidates)`: `Sources = Normalize(candidates)`; `Sourced = Sources.Count > 0`; `SourceStatus` = `"1 source"` / `"{n} sources"` when sourced, else `UnsourcedStatus`. `Text` unchanged. `null` text -> `ArgumentNullException`.

### 3. `ControlDiff` and `ActionReadback` (file `src/Boo.Core/ActionReadback.cs`)

```csharp
public sealed record ControlChange(string AutomationId, string Property, string? Before, string? After);
public sealed record ExpectedControlState(string AutomationId, string Property, string? Value);
public static class ControlDiff
{
    public static IReadOnlyList<ControlChange> Compare(ControlObservation before, ControlObservation after);
}
public static class ActionReadback
{
    public static ToolOutcome VerifyClick(ControlObservation before, ControlObservation after);
    public static ToolOutcome VerifyExpected(ControlObservation after, IEnumerable<ExpectedControlState> expected, string successPrefix);
    public static string Describe(ControlChange change);
}
```

- `Property` is exactly `"Name"` or `"Value"`.
- `Compare`: null arguments -> `ArgumentNullException`. If `before.Context.Target.Handle != after.Context.Target.Handle` or the `ProcessId`s differ -> `ArgumentException` (different window). Match controls by `AutomationId` (ordinal). For each control in `before.Controls` (in order) that also exists in `after`: a `Name` change (ordinal) yields `ControlChange(id, "Name", oldName, newName)`, then a `Value` change (ordinal, null-aware) yields `ControlChange(id, "Value", oldValue, newValue)`. Controls only in one side are ignored.
- `Describe(change)` = `{AutomationId}.{Property}: {B} -> {A}` where a non-null value is rendered in double quotes (`"Sign up"`) and null as `(none)`.
- `VerifyClick`: changes = `Compare(before, after)`. Non-empty -> `ToolOutcome(true, true, "observed after click: " + string.Join("; ", changes.Select(Describe)))`. Empty -> `ToolOutcome(false, true, "partial/unverified: the click was issued but no observed control changed.")`.
- `VerifyExpected`: for each expected state (in order), find the control by id in `after`; read `Name` or `Value`. All match (ordinal, null-aware) -> `ToolOutcome(true, true, successPrefix + ": " + string.Join("; ", expected rendered as {id}.{Property} = "{value}" or (none)))`. Any missing control or mismatch -> `ToolOutcome(false, true, "partial/unverified: " + first problem)` where the problem is `{id} is not visible` for a missing control or `{id}.{Property} is {actual} but expected {expected}` (values rendered as in `Describe`). Empty `expected` -> `ArgumentException`.

### 4. `UndoJournal` (file `src/Boo.Core/UndoJournal.cs`)

```csharp
public sealed record UndoPlan(bool CanUndo, string? BlockedReason, IReadOnlyList<ToolRequest> Steps, IReadOnlyList<ExpectedControlState> Expected);
public sealed class UndoJournal
{
    public bool HasPendingUndo { get; }
    public WindowIdentity? Window { get; }
    public void RecordEdit(WindowIdentity window, string automationId, string? before, string after);
    public void RecordApply(WindowIdentity window, string automationId, IReadOnlyList<ControlChange> observedChanges);
    public UndoPlan Plan(ControlObservation current, long generation);
    public ToolOutcome VerifyUndo(ControlObservation after, UndoPlan plan);
    public void Clear();
}
```

Window identity means equal `Handle` and `ProcessId` (bounds may change).

- `RecordEdit`: null window / blank automationId / null `after` -> `ArgumentException` (or `ArgumentNullException`). If the journal holds records for a different window, they are discarded first. If an apply was already recorded, a new edit starts a new transaction (previous edits and apply discarded). Repeated edits of the same field keep the **first** `before` and the latest `after`.
- `RecordApply`: same window rule. `observedChanges` empty -> ignored (nothing recorded, no exception). Otherwise remembers the apply control id and the changes. Only one apply per transaction (a second apply replaces the first's changes, merging so each `(id, property)` keeps its earliest `Before` and latest `After`).
- `HasPendingUndo` is true when at least one edit or apply is recorded.
- `Plan(current, generation)` returns `CanUndo=false` with these exact reasons and empty `Steps`/`Expected`:
  1. Nothing recorded: `"Nothing to undo: Boo has no recorded edit."`
  2. Different window: `"The edited window is no longer the current target."`
  3. Conflict: for every edited field the current `Value` must equal the recorded `after`, and for every apply change the current property must equal its `After`. First failure (edits in recording order, then apply changes) -> `"{id} changed since Boo edited it; Undo would overwrite a newer change."` (missing control counts as a conflict).
  4. Ambiguous undo control: see below -> `"More than one Undo control is visible; Boo will not guess."`
  5. A restore that would need empty text (fallback paths below need `before` non-empty): `"{id} was empty before the edit; Boo cannot restore an empty value."`
- Otherwise `CanUndo=true`, `BlockedReason=null`, and:
  - **App Undo path** (an apply was recorded AND `current` has exactly one control with `CanInvoke == true` whose `AutomationId` contains `undo` (ignore case) or whose `Name` equals `Undo` (ignore case)): `Steps = [ ToolRequest("click", current.Context.Target.ProcessName, { "automationId": id, "name": name }, generation) ]`.
  - **Re-apply path** (an apply was recorded and no undo control): one `type_text` per edited field in **reverse recording order** with arguments `{ "automationId": id, "text": before }`, then a `click` on the recorded apply control id (`{ "automationId": applyId }` plus `"name"` = current name of that control if visible). If the apply control is not visible -> blocked `"{applyId} is not visible; Boo cannot re-apply the original values."`.
  - **Fields-only path** (no apply recorded): one `type_text` per edited field in reverse recording order as above.
  - `ToolRequest.Target` is `current.Context.Target.ProcessName` for every step; `Generation` is the `generation` argument.
  - `Expected` = for each edited field `ExpectedControlState(id, "Value", before)` (recording order), followed by each apply change `ExpectedControlState(change.AutomationId, change.Property, change.Before)`, skipping any `(id, property)` already listed.
- `VerifyUndo(after, plan)`: `plan.CanUndo == false` -> `ArgumentException`. Result = `ActionReadback.VerifyExpected(after, plan.Expected, "observed Undo restored")`. On success the journal is cleared; on failure it is kept.
- `Clear()` drops everything.

## App wiring (not unit-testable on Linux; build must pass with `-p:EnableWindowsTargeting=true`)

- `MainWindow`: replace `LooksLikeResearch` / `LooksLikeExplicitTask` with `RequestIntent.Classify`. `Undo` intent is handled locally (no model call): capture the current target, read controls, `UndoJournal.Plan`, then show the steps through the existing preview/approve queue; after the last step read controls again and show `VerifyUndo`'s message.
- After an approved `type_text` succeeds, `RecordEdit` with the value from the pre-action observation. After an approved `click`, read controls again, `ActionReadback.VerifyClick` against the pre-click observation, show its message, and `RecordApply` when changes were observed.
- Research replies show `ResearchResult.SourceStatus` next to the source list.
- Put new App code in `src/Boo.App/MainWindow.Edits.cs` (partial class); keep `MainWindow.xaml.cs` edits small.

## Provider (`ResponsesClient`)

- Use `ResearchSources.Extract` for sources (replaces the inline loop; existing behaviour for `url_citation` stays).
- When `Research == true`, add `"include": ["web_search_call.action.sources"]` to the request body.
- When `Research == true && Frame == null && AllowActions == false`, omit the `text.format` json_schema (plain-text answer, so citations attach to prose). All other requests keep the strict json_schema (existing frozen tests require it).

## Test ownership

- Test author: new files only, `tests/Boo.Core.Tests/RequestIntentTests.cs`, `ResearchSourcesTests.cs`, `ActionReadbackTests.cs`, `UndoJournalTests.cs`, and `tests/Boo.Integration.Tests/ResearchRequestShapeTests.cs`. Frozen by SHA-256 in `docs/verification/u5-test-freeze.json`.
- Builder: `src/` only. Never edits a frozen test.
- Reviewer: separate agent, reads diff and contract, reports defects.
