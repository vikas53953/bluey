# Multi-provider text AI: Anthropic Claude and Google Gemini (8 Nov 2026)

Vikas: "OpenAI at later stage we can add more." This lane adds the "more" without changing the OpenAI defaults.

## Scope

- Text requests only: screen explain / draw (RespondAsync), research (RespondAsync with Research=true), focus nudge wording (NudgeAsync), focus classification (ClassifyAsync).
- Voice stays on OpenAI Realtime (RealtimeClient, OpenAI key) regardless of the chosen text provider.
- OpenAI remains the default (`ProviderSettings.TextProvider = AiProvider.OpenAI`). Existing settings.json files deserialize unchanged.
- One DPAPI-protected credential file per provider in Boo's data directory (`AiProviders.CredentialFileName`): `credential.bin` (OpenAI, unchanged), `credential.anthropic.bin`, `credential.gemini.bin`. Keys never go in settings.json, logs, status text or error messages.

## Contracts (Boo.Core, done)

- `enum AiProvider { OpenAI, Anthropic, Gemini }` (JSON as strings).
- `AiProviders`: `DisplayName`, `VendorName`, `Origin`, `IsOfficialOrigin`, `CredentialFileName`, `MissingCredentialMessage(provider)` = "{Vendor} credential is not configured.", `ReasoningModel(settings, provider)`, `FocusModel(settings, provider)`, `IsValidModelId`.
- `ProviderSettings` gained `TextProvider`, `AnthropicModel` (claude-opus-5-5), `AnthropicFocusModel` (claude-haiku-5-5), `GeminiModel` (gemini-4-argon), `GeminiFocusModel` (gemini-3.8-flash).
- `IScreenAssistant { RespondAsync(request, token, controls); NudgeAsync(frame, task, token) }`, `IFocusClassifier { ClassifyAsync(frame, session, token) }`.
- `ProviderFailureClassifier.FromHttp(status, body, provider)`, `FromTransport(error, provider)`, `Timeout(provider)`, `ForVendor(message, provider)`; `ProviderException(provider, operation, failure)`. Anthropic `error.type` and Gemini `error.status` are classified; user messages name the vendor. The OpenAI overloads and constants are unchanged (frozen tests).

## Boo.Providers (done: skeleton; lanes fill in)

- `StructuredReplies` (internal, shared): schema builders (`ScreenHelpSchema(nullableAsTypeArray)`, `NudgeSchema(...)`), instructions text, `ContextText`, `ObservedControlsPayload`, `Build(vendor, rawText, sources, request, controls)`, `BuildNudge(vendor, json)`, `UsesStructuredOutput`, `ValidateControlObservation`. All three providers must go through these so the bounded action catalog and control binding are identical.
- `ResponsesClient : IScreenAssistant`, `DecisionsClient : IFocusClassifier` (OpenAI, behaviour unchanged).
- `AssistantProviders.CreateScreenAssistant/CreateFocusClassifier(provider, http, credential, settings)`.
- `AnthropicClient`, `GeminiClient : IScreenAssistant, IFocusClassifier` with ctor `(HttpClient http, Func<string?> credential, ProviderSettings settings)`.

### Common client rules (mirror ResponsesClient)

- Resolve the endpoint from `http.BaseAddress ?? AiProviders.Origin(provider)`; refuse (throw `InvalidOperationException("{Vendor} requests require the official HTTPS API origin.")`) before reading the credential if `!AiProviders.IsOfficialOrigin(provider, baseUri)`. Tests pass a fake BaseAddress on the official host; the handler is in-memory.
- Missing/blank key -> `InvalidOperationException(AiProviders.MissingCredentialMessage(provider))`.
- Validate model ids with `AiProviders.IsValidModelId` before building a URL (Gemini puts the model in the path).
- Transport error -> `new ProviderException(provider, operation, ProviderFailureClassifier.FromTransport(ex.HttpRequestError, provider))`; non-2xx -> read at most `MaxErrorBodyChars`, `FromHttp(status, body, provider)`; never include the response body in any exception message.
- Honour `http.Timeout` like ResponsesClient (linked CTS, `ResponseTimeoutException` on timeout, `OperationCanceledException` on caller cancel).
- Malformed/empty results -> `InvalidOperationException("{Vendor} ... returned an invalid result.")` without echoing the body.
- Request body must never contain the key (header only).

### Anthropic Messages API

- `POST {origin}/v1/messages`, headers `x-api-key: <key>`, `anthropic-version: 2026-10-21`, `content-type: application/json`.
- Body: `model`, `max_tokens` (4096 structured / 8192 research), `system` (instructions), `messages:[{role:"user",content:[{type:"text",text}, {type:"image",source:{type:"base64",media_type:"image/jpeg",data}}]}]`, `output_config: { effort: "low" }`.
- Structured output (screen help, nudge, classification): `output_config.format = { type: "json_schema", schema: StructuredReplies.ScreenHelpSchema() }` (all objects have `additionalProperties:false`; nullable as `type:["string","null"]`). Do NOT use forced `tool_choice` (400 on Opus 5.5 / Sonnet 5.5).
- Plain research: `tools:[{type:"web_search_20260209", name:"web_search", max_uses:5}]`, no output_config.format (citations are incompatible with it). Text = concatenation of `content[].type=="text"` blocks; sources = each text block's `citations[]` with `url`/`title` plus `web_search_tool_result.content[].{url,title}` (de-duplicated by url, order preserved).
- Response: `content[]` blocks; `stop_reason`. `"refusal"` -> `InvalidOperationException("Anthropic declined this request.")`; `"max_tokens"` on a structured reply -> invalid result.
- Classification: schema `{activity: enum[on_task,off_task,uncertain], confidence: number, probabilities: {on_task, off_task, uncertain: number}}`; map to `FocusDecision(activity, probabilities.off_task, confidence, probabilities)`; validate 0..1 and sum within 0.02 of 1 like DecisionsClient; use `settings.AnthropicFocusModel`.

### Gemini generateContent API

- `POST {origin}/v1beta/models/{model}:generateContent`, header `x-goog-api-key: <key>`. The key is NOT put in the query string.
- Body: `system_instruction:{parts:[{text}]}`, `contents:[{role:"user",parts:[{text}, {inline_data:{mime_type:"image/jpeg", data}}]}]`, `generationConfig:{ responseMimeType:"application/json", responseSchema: StructuredReplies.ScreenHelpSchema(nullableAsTypeArray:false) }` for structured replies (Gemini's OpenAPI-style schema: `nullable:true`, no `additionalProperties`).
- Plain research: `tools:[{google_search:{}}]`, no responseSchema. Text = concat of `candidates[0].content.parts[].text`; sources = `candidates[0].groundingMetadata.groundingChunks[].web.{uri,title}`.
- `candidates[0].finishReason` of `SAFETY`/`PROHIBITED_CONTENT`/`BLOCKLIST` or `promptFeedback.blockReason` -> `InvalidOperationException("Gemini declined this request.")`; `MAX_TOKENS` on structured -> invalid result.
- Classification: same schema/validation as Anthropic; use `settings.GeminiFocusModel`.

## App (Boo.App)

- `_responses: IScreenAssistant?`, `_decisions: IFocusClassifier?` created via `AssistantProviders` with `_settings.Provider.TextProvider`, credential store = `new CredentialStore(Path.Combine(_dataDirectory, AiProviders.CredentialFileName(provider)))`; recreate both when settings are saved with a different provider or model.
- Settings UI: "Text AI provider" ComboBox (OpenAI / Anthropic Claude / Google Gemini); model TextBoxes for the Anthropic and Gemini models; one masked key box per provider ("Anthropic Claude key", "Google Gemini key") beside the existing OpenAI box; each saves to its own DPAPI file and is cleared after save. Voice is labelled as OpenAI-only.
- `SanitizedError` maps any `AiProviders.IsMissingCredentialMessage` to "No {Vendor} key yet. Add it in Settings, then try again."

## Tests

- `tests/Boo.Providers.Tests` (net10.0, runs on Linux and Windows CI): transport contract (URL, headers, no key in body/query), image/base64 carried, structured JSON decoded through the shared binder (generation/observation bound to the request, invented ids ignored), bounded catalog rejection, control binding, research sources, error sanitising (body never echoed), cancellation and timeout, refusal handling, classification validation, factory mapping, settings defaults/serialisation round trip.
- `tests/Boo.Core.Tests`: provider-aware classifier messages, AiProviders helpers, ProviderSettings defaults. Existing (frozen) tests are not edited.
