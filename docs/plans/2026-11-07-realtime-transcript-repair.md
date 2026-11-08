# Realtime transcript identity and completion repair

Status: PLAN_APPROVED by independent Advisor on 2026-11-07. Autonomous repair
is authorized; test authoring precedes implementation.

## Source evidence and scope

The approved product requires readable text matching spoken turns. MainWindow
currently concatenates deltas into the last row based only on speaker and then
adds a second user row on transcription completion. Two consecutive turns by
one speaker can merge; interleaving can also append text to the wrong turn.
Final assistant text/transcript events are parsed but not applied. The parser
does not read the `text` field of `response.output_text.done`.

This is a source-confirmed defect, not a claimed microphone reproduction.
Official event contracts identify content by item_id and content_index. Input
transcription can finish before or after assistant events. Final text appears
in `text`; audio and input transcriptions use `transcript`.

References inspected this run:
- https://developers.openai.com/api/reference/resources/realtime/server-events#conversation.item.input_audio_transcription.completed
- https://developers.openai.com/api/reference/resources/realtime/server-events#response.output_text.done

Preserve existing audio, action authorization, tool-call IDs, connection
lifecycle, generation guards, typed conversation rows, and public frozen tests.
No API requests, microphone capture, or credential access in this repair.

## Contract and implementation

1. Add optional `int ContentIndex = 0` after existing RealtimeEvent parameters.
   Preserve the existing constructor and call-ID preference through optional
   compatibility. Parse nonnegative integer content_index when provided; an
   invalid provided index makes the event unprocessable, without changing
   other malformed-event handling. Read final output-text from `text`.
2. Add a small provider-side `RealtimeTranscript` reducer, independent of WPF.
   `Apply(RealtimeEvent)` returns `TranscriptUpdate?` with ItemId, ContentIndex,
   Speaker, Text. Keep separate state by item/content and speaker. Only the six
   transcript delta/completion events can produce updates. Missing/blank item
   identity, null text, malformed content index, and unrelated events produce
   no update. Nonempty whitespace deltas remain valid text.
3. Deltas append only to their own unfinished content. Completion replaces
   accumulated text, never appends it or adds a duplicate row. Completion may
   arrive without deltas, may contain an empty string, and is authoritative.
   Repeated completion is harmless; deltas after completion are ignored for
   that channel. An empty final clears an existing mapped row but does not
   create a new empty row. Track audio-transcript and output-text channels
   separately, including finalization; when
   both appear for one assistant content, prefer audio transcript so displayed
   text corresponds to speech, without concatenating both representations.
   A finalized audio transcript cannot be replaced by later output-text events.
4. MainWindow owns one reducer per Realtime connection and a map from
   (item, content index, speaker) to the actual transcript row index. Updates
   replace that row even when other rows arrived meanwhile; first updates add
   one row. Keep the collection's first-observed order rather than inventing
   chronological order from asynchronous arrivals. Reset reducer and row map
   for a new connection while retaining visible history. Expose `Reset()` to
   clear reducer session state. Reducer and update types are public for the
   actual application and separately compiled test consumers. Existing source/
   generation checks reject old connection callbacks.
5. Replace AppendStreaming usage with reducer application. Handle all six
   delta/completion events. Retain the existing voice-task authorization update
   only on input transcription completion. Keep source/typed rows independent.
   Do not broaden this repair into authorization inference changes.

## Test-first and ownership

After Advisor approval, an independent author writes new tests before the
builder. Root supplies only compile-time reducer/record stubs if necessary.
Required cases: delta then corrected completion yields one final text; same
speaker different items remain separate; interleaved user/assistant items;
same item different content indexes; completion without delta; duplicate final;
late delta; empty final; channel preference; missing item; session reset; parser
extracts final text/index while preserving function call id behavior.

Root observes and preserves RED results and extends the frozen oracle only
after the test author releases ownership. Builder gets a fresh worktree with
frozen tests and owns only src/Boo.Providers/RealtimeClient.cs and new
src/Boo.Providers/RealtimeTranscript.cs. Root owns MainWindow integration, using
the approved mapping contract. A separate reviewer checks the assembled diff
and integration. No builder may alter acceptance criteria or tests.

Root runs the affected tests, oracle, full Release build and diff check. Tests
prove reducer/protocol behavior; independent source review verifies WPF wiring.
Real conversation, audible quality, and latency remain native acceptance cases
and are not passed by these deterministic checks.
