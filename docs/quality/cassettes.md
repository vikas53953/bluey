# OpenAI cassettes (rule BOO-CASS-001)

**What.** A cassette is one saved OpenAI reply: its status, a few safe headers, and its body cut into pieces with the
delay between them. On every pull request, `windows-ci` plays each cassette back to Boo's **real** OpenAI client
(`src/Boo.Providers/ResponsesClient.cs`) as if it came from the internet, and checks what Boo does with it. No key, no
network, no money.

**Why.** Real replies are messy: slow, cut off, refused, rate-limited, sometimes not even JSON. Cassettes test Boo's
handling of all of that on every PR, which the live run cannot do (the live run gets one good reply).

**Cassettes never replace the live run.** Cassette results are **mock** evidence (AGENTS.md). The live real-AI run
(`live-ai-smoke`, real key, published `Boo.exe`) stays the truth check, and no gate may accept a cassette run in its
place. A cassette proves "Boo handles this reply shape correctly", never "the real AI works".

## Where things are

| Path | Plain-words role |
| --- | --- |
| `tools/Boo.Cassettes.Tests/cassettes/*.json` | The cassettes (one file each). |
| `tools/Boo.Cassettes.Tests/CassetteHandler.cs` | Plays a cassette back as the client's network, with its delays and faults. |
| `tools/Boo.Cassettes.Tests/CassetteReplayTests.cs` | The tests (in `Boo.sln`; run by the `Cassette replay tests` step of `windows-ci`). |
| `tools/Boo.Cassettes.Tests/Cassette.cs` | The cassette format and the redaction rules (shared with the recorder). |
| `tools/Boo.Cassettes.Record/` | The recorder. Only `record-cassettes.yml` runs it. |
| `.github/workflows/record-cassettes.yml` | By-hand workflow that records real replies and uploads them for review. |

**No production change.** The client already takes an `HttpClient`, so the test hands it one whose network is the
cassette. Nothing in `src/` changed for this.

## What each test checks

1. **Every cassette replays as expected**: the request Boo sends has the right shape (path, model, answer format,
   tools, body keys, a bearer header), and Boo's result matches the cassette's `expect` (the answer text, the number
   of sources, or the exact plain message and failure kind the user sees). Good replies must also really take as long
   as recorded. An error message must never contain text from the reply body.
2. **Fault injection**: every good-reply cassette is replayed again with each fault: no answer until the deadline,
   connection refused, connection reset before the reply, body cut part-way. Each must fail cleanly.
3. **Every cassette is safe to commit**: no key-shaped text, no auth / organization / project / request-id header,
   only allowlisted headers, and an honest `origin` label.
4. **The bad-day list is complete** (below), and there is a good chat, research and nudge reply.
5. **The redaction works**: ids replaced, caller fields blanked, OpenAI's masked echo of a key removed, and the
   recorder refuses a file that still holds the key.

## The bad-day set

| Cassette | What happens | What Boo must do |
| --- | --- | --- |
| `bad-malformed-json` | 200 with broken JSON | "OpenAI responses returned an invalid result." |
| `bad-truncated-body` | 200, body ends half-way (cleanly) | same "invalid result" message |
| `bad-cut-mid-chunk` | connection resets inside the body | "OpenAI responses returned an unreadable result." |
| `bad-timeout-hang` | no reply at all | Boo's timeout ("OpenAI response timed out. Try again.") |
| `bad-slow-body-timeout` | headers fast, body stalls | the same timeout |
| `bad-refusal` | a refusal instead of an answer | "OpenAI responses returned no assistant text." |
| `bad-incomplete-max-tokens` | `status: incomplete`, answer stops mid-string | "invalid result" |
| `bad-429-rate-limit-retry-after` | 429, `rate_limit_exceeded`, `Retry-After: 20` | RateLimited, retryable, "busy" message |
| `bad-429-insufficient-quota` | 429, `insufficient_quota` | QuotaExhausted, not retryable, "quota is used up" |
| `bad-500-server-error` | 500 | ServerError, retryable |
| `bad-503-html` | 503 with an HTML page | ServerError, retryable |
| `bad-empty-body` | 200 with no body | "invalid result" |
| `bad-wrong-content-type` | 200 with an HTML page (proxy or captive portal) | "invalid result" |
| `bad-socket-reset` | reset before any reply | failure kind Unknown, retryable |
| `bad-connect-refused` | cannot connect | Offline, retryable, "Can't reach OpenAI" |
| `bad-nudge-malformed` | nudge reply without its text | "OpenAI nudge result is missing its text." |
| `recorded-401-invalid-key` | real 401 from OpenAI | InvalidCredential, "rejected the API key" |

## Honest labels

- `origin: "synthetic"`: hand-made from OpenAI's documented Responses API shape, **not** a real recording. All but one
  cassette in this first PR are synthetic.
- `origin: "recorded"`: captured from the real API. `recorded-401-invalid-key` was recorded on 13 Nov 2026 with a
  deliberately invalid placeholder credential (no key needed or used). The first recordings with a valid key come
  from `record-cassettes.yml` after merge.
- Scope: OpenAI chat (Responses API: chat, research, nudge). Voice (Realtime, WebSocket) is not covered here; that
  client is owned by the voice live-test lane. Claude and Gemini are out of scope (Boo uses only OpenAI).

## How to record real cassettes

1. Actions → **record-cassettes** → Run workflow (optional `model`, default `gpt-5-mini`, a cheap mini model).
   It sends about 3 small requests (one with web search; one to a model id that does not exist, which is free).
2. It needs the `BOO_LIVE_OPENAI_KEY` secret and fails closed without it. It runs on GitHub's own VM only.
3. Download the `recorded-cassettes` artifact. **Read every file**, then copy the ones worth keeping into
   `tools/Boo.Cassettes.Tests/cassettes/` in a PR. The workflow never commits anything itself.

## Redaction (what a cassette may hold)

- Headers: only `content-type` and `retry-after`. Never `authorization`, `openai-organization`, `openai-project`,
  `x-request-id`, cookies or rate-limit counters.
- Body: response and item ids become `resp_redacted`, `msg_redacted`, ...; `org-`, `proj_`, `req_` and `user-` ids
  become `redacted`; the `user`, `safety_identifier`, `prompt_cache_key` and `metadata` fields are blanked; OpenAI's
  masked echo of a key (`...*****...`) becomes `[redacted-key]`.
- The recorder refuses to write a file that still holds the key or anything key-shaped, and the workflow runs
  `scripts/quality/key_scan.py --secret-env OPENAI_KEY` on every file before the upload (BOO-SAFE-002).
- The request is stored only as a summary (path, model, answer format, tools, body keys), never its headers.

## Known gaps (found by these tests; not fixed in this PR)

1. **Nudge, body cut part-way**: `NudgeAsync` lets .NET's own `HttpRequestException` out instead of Boo's plain
   message (`ResponsesClient.RespondAsync` wraps it). The app catches it (the nudge is optional), so the user sees
   nothing wrong. The test pins today's behaviour so a fix shows up as a change.
2. **Retry-After is not read**: on a 429 Boo says "wait a moment" but does not use the server's `Retry-After` value.

Each gap is its own small fix PR, with a live run, if wanted.
