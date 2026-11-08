# Provider contract acceptance

This document freezes the provider boundary for Boo's first Windows companion build. It covers only public transport and protocol behavior; it does not claim that a fixture can prove live model quality, microphone intelligibility, native WebSocket playback, capture exclusion, or Windows packaging.

## Executable cases

The executable cases live in `tests/Boo.Integration.Tests/ProviderContractTests.cs` and use xUnit 2 with an injected `HttpClient` whose `HttpMessageHandler` is the transport seam. The handler records outbound method, URI, headers, and JSON body. It does not replace provider parsing or call private helpers.

| ID | Trigger | Observable acceptance result | Required environment |
| --- | --- | --- | --- |
| P-01 | Submit a text request with a captured JPEG frame and a structured Responses fixture | The client sends `POST /v1/responses` with the configured reasoning model, memory-only `data:image/jpeg;base64,...` input, and `web_search` when research is requested. The reply exposes `text`, marks, and actions. | xUnit fixture; no credential or paid call |
| P-02 | Return an output-text citation annotation | The returned `SourceLink` preserves the provider URL and title. | xUnit fixture |
| P-03 | Return model-invented observation/generation values for a submitted frame | The annotation uses the submitted `ScreenContext` observation ID, generation, and display ID; an action uses the submitted request generation and retains string arguments. | xUnit fixture |
| P-04 | Supply a credential and official API base URI | Authorization is `Bearer` on the official `https://api.openai.com` request only. A non-official origin cannot receive the credential. | xUnit fixture |
| P-05 | Return empty, malformed, or non-2xx Responses bodies; cancel an in-flight request | The client fails with a sanitized error that does not echo provider bodies or credentials, and cancellation reaches the transport boundary. | xUnit fixture |
| D-01 | Classify a captured frame with the pinned Decisions choice response | The request uses `POST /v1/decisions`, `gpt-6-luna`, and a memory-only JPEG data URI. Decode returns the selected activity, the `off_task` probability, all three choice probabilities, and separate confidence. | xUnit fixture |
| D-02 | Decode malformed, non-finite, duplicate/unknown, or out-of-range choice data | Decoding rejects the answer rather than treating confidence as a second probability threshold or inferring missing probabilities. | xUnit fixture |
| R-01 | Build a Realtime session update | The JSON uses current Realtime `session.update`/`session.type: realtime`, configured model and voice, PCM16-compatible `audio/pcm` at 24 kHz for input and output, server VAD, and input transcription. | xUnit protocol fixture |
| R-02 | Parse documented server events | Input/output transcript deltas, output audio deltas, speech-turn events, errors, and function-call argument events are exposed at the public `RealtimeEvent` boundary with readable sanitized error text; unsupported events and malformed JSON are ignored without echoing credentials or raw provider bodies. | xUnit protocol fixture |
| R-03 | Encode a typed text turn | The event is a `conversation.item.create` user message with one `input_text` content part and the exact user text. | xUnit protocol fixture |

The exact Decisions response fixture follows the pinned Bluey `FocusAPI.swift` contract: an `answers` entry named `activity`, `type: choice`, choice values `on_task`, `off_task`, and `uncertain`, a `probabilities` array, and a separate `confidence` value. The product's `FocusDecision` continues to expose the `off_task` probability as the legacy `Probability` field while retaining the complete distribution.

## Live gate

**L-20 — NOT TESTED.** Twenty warm voice turns require deliberate credentials with access to the configured Realtime model, a real microphone and playback device, and the packaged Windows build. The fixture suite cannot establish speech intelligibility, barge-in behavior, first-audio latency, server entitlement, or native audio lifecycle. Do not mark L-20 green from these tests; record the exact build, model, connection, audio device, and latency distribution when it is intentionally run.

## References

- OpenAI Realtime guide and current client/server event references: `https://developers.openai.com/api/docs/guides/realtime` and `https://developers.openai.com/api/reference/resources/realtime/client-events`.
- OpenAI Decisions guide: `https://developers.openai.com/api/docs/guides/decisions`.
- Pinned source contract: `docs/research/bluey-reference/Mac/Sources/GooglyMac/FocusAPI.swift` and `docs/research/bluey-reference/Shared/FocusSession.swift`.
- Boo's shared public vocabulary: `src/Boo.Core/Contracts.cs` and `docs/verification/public-contracts.md`.
