# Provider additive regression RED receipt

Date: 2026-11-07 (Asia/Calcutta)

The additive provider regression tests were authored in the provider worktree at commit `b2b515bf7023ac665a2c4f3d159452a6df5c0389`. Only `tests/Boo.Integration.Tests/ProviderRegressionTests.cs` was added there. No production file, manifest, credential, or frozen provider-contract test was changed by this test-author unit.

## Command

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File scripts/build.ps1 -Action test -Project tests/Boo.Integration.Tests/Boo.Integration.Tests.csproj -Filter 'FullyQualifiedName~ProviderRegressionTests'
```

The wrapper used the canonical workspace-local .NET SDK `10.0.403`, restored successfully, built all referenced Boo projects, discovered one test assembly, and executed all three additive cases.

Raw test result: `C:\Users\vikasmit\.codex\worktrees\boo-providers-audio\blue--fo\TestResults\vikasmit_VIKASMIT_2026-11-07_10_09_21_net10.0.trx`.

## Oracle correction and rerun

The first draft incorrectly allowed `actions[].arguments.additionalProperties` to describe arbitrary string values. After review against the official Structured Outputs supported-schema rule that object schemas use `additionalProperties: false`, the additive test was corrected to require a fixed bounded argument object, all named fields required, and string-or-null field types. The allowed catalog is derived from the policy worktree's `DesktopTools`: `x`, `y`, `automationId`, `name`, `text`, `path`, `contents`/`content`, and `overwrite`.

The corrected test was rerun before any provider repair with the same result: `2 failed, 1 passed, 0 skipped`. The failures remained the missing Responses `instructions` field and the Realtime `item_id`/`call_id` precedence bug; the transcript completion guard remained passing.

Corrected-run raw result: `C:\Users\vikasmit\.codex\worktrees\boo-providers-audio\blue--fo\TestResults\vikasmit_VIKASMIT_2026-11-07_10_11_25_net10.0.trx`.

## Result before provider repair

`2 failed, 1 passed, 0 skipped`.

- `Responses_request_declares_structured_result_context_and_constrained_action_schema` failed because the current outgoing body had no `instructions` field. This is the intended RED for the missing system prompt, official `text.format.type=json_schema` contract, frame/target context, untrusted-screen guidance, and constrained action schema.
- `Realtime_function_call_arguments_done_prefers_call_id_when_item_id_is_also_present` failed: expected `ItemId = call-42`, actual `item-17`. The parser currently prefers `item_id`, which would prevent `CompleteToolAsync` from returning the result to the correct call.
- `Realtime_output_audio_transcript_done_reads_transcript_field` passed, confirming the additive guard for the already-correct `transcript` field behavior.

The transport fixture uses an injected `HttpClient` handler and inspects only the public outbound JSON request. It makes no live provider call and uses no key.

## Contract basis

- OpenAI Structured Outputs: `https://developers.openai.com/api/docs/guides/structured-outputs` — Responses requests use `text.format` with `type: "json_schema"`, a named schema, and strict mode.
- OpenAI Function Calling: `https://developers.openai.com/api/docs/guides/function-calling` — Responses function tools use `type`, `name`, `parameters`, and strict JSON-schema arguments.
- Boo public contract: `docs/verification/public-contracts.md` — structured `text`/`marks`/`actions`, bounded action names, and provider transport boundaries.
- Realtime event shape: `src/Boo.Providers/RealtimeClient.cs` and current Realtime server-event documentation; function-call completion carries both `item_id` and `call_id`, with `call_id` authoritative for tool completion.
