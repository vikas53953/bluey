# Provider action-argument regression RED receipt

Date: 2026-11-07 (Asia/Calcutta)

The additive transport-boundary tests were authored in the provider worktree at commit `b2b515bf7023ac665a2c4f3d159452a6df5c0389` as `tests/Boo.Integration.Tests/ProviderActionArgumentTests.cs`. The frozen provider-contract tests and all production files remain unchanged by this test-author unit.

## Command

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Filter 'FullyQualifiedName~ProviderActionArgumentTests'
```

The wrapper used the canonical workspace-local .NET SDK `10.0.403`, restored successfully, built the referenced projects, discovered one test assembly, and ran both additive cases. No provider credential or live API call was used.

Raw test result: `%USERPROFILE%\.codex\worktrees\boo-providers-audio\blue--fo\TestResults\<user>_<machine>_2026-11-07_10_28_03_net10.0.trx`.

## Result before provider repair

`2 failed, 0 passed, 0 skipped`.

- `Responses_omits_null_unused_action_arguments_without_omitting_meaningful_fields` failed because null `x`, `y`, and `name` were exposed in `ToolRequest.Arguments`; the fixture expects only the meaningful `automationId` entry.
- `Responses_preserves_meaningful_empty_string_arguments_while_omitting_null_aliases` failed because a null `contents` alias was exposed alongside the meaningful empty-string `content`; the empty string must remain present, while the null alias must be omitted.

The fixture proves this at the injected `HttpClient` transport boundary by returning a Responses structured output and observing the public `AssistantReply.Actions` result. It does not call private parser helpers.

## Contract basis

- `docs/verification/public-contracts.md`: action arguments are a dictionary of strings and Responses uses a bounded structured result.
- The policy worktree's `src/Boo.Windows/DesktopTools.cs` consumes `x`, `y`, `automationId`, `text`, `path`, `contents`/`content`, and `overwrite`; the regression keeps meaningful empty strings while dropping only JSON nulls.
