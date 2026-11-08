# Preserve the visible annotation outcome

Candidate9 produced a real screen-context reply but no retained ring evidence.
Source review confirms RenderReply overwrites TryRenderAnnotations' outcome
with a generic completion message for every reply without proposed actions.
This prevents the user and native QA from distinguishing verified drawing,
rejected marks, and a response without marks.

Keep existing validation and drawing unchanged. Give annotation outcome its
own accessible text region in the conversation, parallel to CaptureDetail.
Clear it at the start of a new request and when annotations are cleared. Set
it to the current result after drawing succeeds or is rejected; when a screen
frame was supplied but no proposal exists, say that Boo explained the screen
without a verified mark. Do not infer that a model refusal, stale observation,
invalid bounds, or exclusion failure was the cause unless the actual path
establishes it. Exclusion failure still stops capture and never shows a mark.
The action status remains independent and may state that no action committed.
No coordinates, internal IDs, or model payloads enter product text. No new
colors, windows, credentials, live retry loop, or validation relaxation.

## Pre-implementation acceptance cases

These are native/UI cases extending existing frozen uncertain-target and
teaching acceptance. They are written before implementation. Core mark
validation already has frozen boundary tests; rewriting them or testing
private WPF methods would not prove this UI behavior.

1. Valid screen marks and no actions: accessible annotation result says marks
   are shown; subsequent generic completion text does not erase that result.
2. Stale or invalid marks and no actions: visible validation reason survives
   reply completion, and no annotation is created.
3. Frame supplied, no marks: visible outcome says no verified mark; no guessed
   reason is supplied and no success claim is made.
4. No screen frame: capture limitation remains visible; no annotation success
   or invented observation is shown.
5. New request, Stop, or display change: old annotation outcome clears along
   with the old marks. A late old reply must not restore either.
6. Exclusion failure: no mark appears, capture stops, and the actual exclusion
   limitation remains visible after the generic reply path returns.

Root will integrate this small presentation change after the capture activity
repair, then run the existing validation tests/build and a fresh independent
source review. Native QA inspects the accessible outcome from one bounded
screen-help request; unexercised failure injection and excluded-window visual
geometry remain explicitly unproven. This does not waive native acceptance.

Independent Advisor returned PLAN_APPROVED. Preserve the exclusion failure
outcome after StopAll clears annotations, so cleanup does not erase its reason.
