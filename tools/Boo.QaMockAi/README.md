# Boo.QaMockAi

CI-only impostor for `https://api.openai.com`. Boo pins that origin
(`src/Boo.Providers/ProviderEndpoint.cs`), so instead of changing app code the CI job makes the
real hostname resolve to this process:

1. Append `127.0.0.1 api.openai.com` to `C:\Windows\System32\drivers\etc\hosts`.
2. Start this server; on startup it generates a self-signed certificate (CN=api.openai.com,
   SAN DNS api.openai.com, localhost, 127.0.0.1) and writes the public DER cert to `--cert-out`.
3. Import that `.cer` into `LocalMachine\Root` (`certutil -addstore -f Root boo-qa.cer` or
   `Import-Certificate -CertStoreLocation Cert:\LocalMachine\Root`). Boo's HttpClient then trusts it.
4. Run the UI test with any non-empty OpenAI key saved in Boo (the server ignores the bearer token).

Not part of `Boo.sln`. Build and run separately:

```
dotnet build tools/Boo.QaMockAi -c Release -p:EnableWindowsTargeting=true
dotnet run --project tools/Boo.QaMockAi -c Release -- --port 443 --cert-out artifacts\boo-qa.cer --log artifacts\mock-ai.log --workspace tests\fixtures\native\Boo.QaWorkspace
```

Arguments: `--port` (default 443), `--cert-out <path.cer>`, `--pfx-out <path.pfx>` and
`--pfx-password` (default `boo-qa`), `--log <path>` (every request path, body and response is
appended; base64 image payloads are elided), `--workspace <dir>` (folder holding the
`MainWindow.xaml` used as the write_file template; auto-discovered from the repo tree if omitted).

## Request paths handled

| Path | Caller | Behaviour |
| --- | --- | --- |
| `POST /v1/responses` with `text.format.name == boo_screen_help_result` | `ResponsesClient.RespondAsync` (structured) | Keyword-scripted screen-help JSON (`text`, `marks`, `actions`) wrapped in a Responses envelope. |
| `POST /v1/responses` without a `text.format` | `ResponsesClient.RespondAsync` (plain research) | Prose `output_text` with `url_citation` annotations plus a `web_search_call.action.sources` item. |
| `POST /v1/responses` with `text.format.name == boo_focus_nudge` | `ResponsesClient.NudgeAsync` | `{"text": "...", "pointer_target": "..."}`. |
| `POST /v1/decisions` | `DecisionsClient.ClassifyAsync` | `answers[0]` with `name=activity`, `type=choice`, `choice`, `confidence`, 3 `probabilities`. |
| `GET /v1/models` | none (health) | Minimal model list. |
| `GET /v1/realtime` WebSocket upgrade with `Authorization: Bearer sk-qa-mock-realtime-key-000000000000000` (`QaConstants.RealtimeKey`) | `RealtimeClient.ConnectAsync` (voice session) | Accepted: `session.update` -> `session.created` + `session.updated`; a user `message` item followed by `response.create` -> one `response.function_call_arguments.done` (`name` `screen_help`, `call_id` `call_qa_<session>_<n>`, `arguments` = `{"question": <user text>, "mode": "task" if the text starts with `task `, "research" if it contains `research`, else "teach"}`) then `response.done` (`completed`); a `function_call_output` item is logged as `[realtime] function_call_output call_id=... output=...` and the `response.create` after it gets a bare `response.done`. Every frame is logged as `[realtime <-]` / `[realtime ->]`. |
| `GET /v1/realtime` WebSocket upgrade with any other key | `RealtimeClient.ConnectAsync` | Not upgraded: the plain `200` below, so `ClientWebSocket` fails the handshake and Boo falls back to `/v1/responses` (all other demos rely on this). |
| anything else | - | `200` with a minimal object; logged. |

Keyword matching is case-insensitive over all `input_text` parts and `instructions` of the request
(the app sends `User request: <text>` followed by the screen context sentence).

## Scripted responses and constants (`QaConstants` at the top of Program.cs)

| Trigger | Response |
| --- | --- |
| text contains `explain`, `what is on my screen`, `what's on my screen` | `text` = `QA-EXPLAIN: ...`; one `ring` mark centred at **x = 0.25 x frameWidth, y = 0.40 x frameHeight**, diameter 0.08 x frameWidth, label `QA ring`. Frame size is parsed from `framePixels=WxH` in the request context (fallback 1920x1080). Coordinates are image pixels, so they satisfy `AnnotationValidator` (inside frame, positive size, finite endX/endY = 0); the app maps them to the display. |
| text contains `edit`, `rename`, `apply` | `text` = `QA-EDIT: ...`; one action `write_file`, `target` and `arguments.path` = `MainWindow.xaml`, `arguments.overwrite` = `"true"`, `arguments.contents` = the fixture `MainWindow.xaml` with every `Create account` replaced by **`QA-EDIT-APPLIED`** (other argument slots are `null`, as the strict schema requires). Boo resolves it through `FileActionResolver` relative to the user-selected file root, so the test must set Boo's file root to `tests/fixtures/native/Boo.QaWorkspace` and approve the preview. Actions are only parsed when the app sends the request with `AllowActions=true` (an explicit user task). |
| text contains `qa-blind-mixed` | `text` = `QA-BLIND-MIXED: ...`; three actions in this order: `click` (`x` 120, `y` 80), `type_text` (`automationId` `QaBlindBox`, `text` `qa blind text`), `write_file` (`path` `MainWindow.xaml`, `overwrite` `"true"`, `contents` `QA-BLIND-WRITE`). Used with Boo started under `BOO_QA_CAPTURE_FAULT=1` (no frame, no control observation): Boo must offer the `write_file` alone, with no "Step 1 of 3" queue. |
| text contains `qa-blind-click` | `text` = `QA-BLIND-CLICK: ...`; one `click` action (`x` 120, `y` 80). Under `BOO_QA_CAPTURE_FAULT=1` Boo must offer nothing and say that no screen observation backs the action. Over the realtime session (`RealtimeKey`), Boo must return exactly one function result, `ActionPolicy.UnobservedScreenActionRefusal`, at once; the mixed route must hold the call open while the `write_file` is on the preview and return one `Action cancelled; no side effect was requested.` on Cancel (`tests/Boo.Demo.Tests/UnobservedScreenActionVoiceDemoTests.cs`). |
| text contains `research`, `sources` | `text` = `QA-RESEARCH: ...`; three `url_citation` annotations: `https://example.com/a`, `https://example.com/b`, `https://example.com/c` (titles `QA source A/B/C`). `ResearchSources.Extract` reads these from annotations first and from `web_search_call.action.sources` otherwise; both are present. |
| nudge schema | `text` = `QA-NUDGE: Back to your task?`, `pointer_target` = `Scrollable geometry area` (visible text in the QA workspace so OCR can find it). |
| `/v1/decisions` | Decision labels `off_task` / `on_task` / `uncertain`. Requests 1 and 2 return `off_task` (P(off)=0.90, confidence 0.9) so Boo nudges; from request 3 on, or whenever the request text contains `resume` or `qa-on-task`, `on_task`. `qa-uncertain` in the text yields `uncertain`. Probabilities always sum to 1.0. |
| anything else on `/v1/responses` | `text` = `QA-DEFAULT: mock reply.`, empty marks and actions. |

Priority when several keywords match: nudge schema first, then whichever route's keyword appears earliest in the user's text (`Route` in Program.cs); the blind routes are reached by their `qa-blind-*` keywords alone, so keep `edit`, `rename`, `apply`, `explain`, `research` and `sources` out of those requests.

## Notes

- The server trusts no one and authenticates nothing; it must only run on a disposable CI VM.
- Kestrel is used (not HttpListener) so the cert is bound in-process without `netsh http add sslcert`.
- Port 443 needs an elevated shell on Windows; use `--port` plus a `netsh interface portproxy`
  only if elevation is unavailable (the app will not accept a non-443 origin).
