---
title: U8 close remaining Riley gaps (pause-all hotkey, voice focus flow, hover proof)
date: 2026-11-08
branch: claude/project-thread-0ih2hc (off lane/face 042356e)
---

# U8: close remaining Riley gaps

Audit source: github.com/rbrown101010/bluey-by-riley (Mac/Sources/GooglyMac/AppDelegate.swift, RealtimeHost.swift, README).
Process: frozen failing tests first (separate author), then a separate builder, then a separate reviewer. Existing tests are frozen and must not be edited.

## A. Pause-everything hotkey Ctrl+Alt+S (Riley: ⌃⌥S "pauses focus and stops computer control")

Boo already has Ctrl+Alt+F12 (emergency stop). Add Ctrl+Alt+S as Riley's gesture; both stay, both run the same `StopAll`.

Contract (Boo.Core, `ShortcutStatus`):
- Record becomes `ShortcutStatus(bool EmergencyStop, bool OpenPanel, bool HoldToTalk, bool PauseAll = true)`; existing 3-argument construction keeps compiling and behaves exactly as before.
- `public const string PauseAllGesture = "Ctrl+Alt+S";`
- `Pending.PauseAll` reports nothing (pending reports no failures, as today).
- `UnavailableShortcuts` appends `"Ctrl+Alt+S"` (after the existing entries) when `PauseAll` is false and not pending.
- `AllAvailable` also requires `PauseAll`.
- `PanelMessage`: when `PauseAll` is false, append the sentence `"Ctrl+Alt+S is used by another app; Ctrl+Alt+F12 still stops Boo."` if EmergencyStop is true, otherwise `"Ctrl+Alt+S is used by another app."` (the existing emergency-stop sentence already explains the tray fallback). When PauseAll is true the message is byte-identical to today's.
- `NotchMessage`: a PauseAll failure alone (everything else fine) returns null (Ctrl+Alt+F12 still works); otherwise unchanged.
- `TrayStopLabel` unchanged ("Stop (Ctrl+Alt+F12)").
- `public ShortcutStatus WithPauseAll(bool pauseAll)`: from Pending gives `new(true, true, true, pauseAll)`; otherwise `this with { PauseAll = pauseAll }`.
- `WithGlobalHotkeys` and `WithHoldToTalk` keep the current PauseAll value (from Pending: PauseAll true).

App: NotchWindow registers hotkey id for MOD_CONTROL|MOD_ALT + 'S' (0x53) alongside F12 and B, retried by `RetryHotkeys`, and WM_HOTKEY for it calls the same `_onStop`. The notch shows "Stopped" for ~1.6 s (Riley's bubble). Panel status after stop with a running focus session: unchanged "Paused by Stop; Resume is explicit."

## B. Voice focus flow (Riley: hold the face, say "I'm making a Codex video for 30 minutes", release, focus starts)

Riley's Realtime session exposes focus_start / focus_pause / focus_resume / focus_end / focus_status and go_to_sleep. Boo's session only had screen_help, so voice could not start or steer focus.

Contract (Boo.Core, new file `VoiceCommands.cs`, namespace Boo.Core):
```csharp
public enum VoiceCommandKind { FocusStart, FocusPause, FocusResume, FocusEnd, FocusStatus, GoToSleep }
public sealed record VoiceCommand(VoiceCommandKind Kind, string? Task = null, double Minutes = 0);
public static class VoiceCommandParser
{
    public const double DefaultMinutes = 30, MinMinutes = 1, MaxMinutes = 240;
    public const int MaxTaskLength = 120;
    // Exactly: "focus_start","focus_pause","focus_resume","focus_end","focus_status","go_to_sleep" in that order.
    public static IReadOnlyList<string> ToolNames { get; }
    public static bool IsVoiceTool(string? name);
    // false + non-null user-safe error when the name is unknown, the JSON is malformed / not an object,
    // or focus_start has no usable task. Arguments may be null/empty for the no-argument tools.
    public static bool TryParse(string? toolName, string? argumentsJson, out VoiceCommand? command, out string? error);
}
public sealed record VoiceFocusDecision(bool Apply, string Reply);
public static class VoiceFocusPolicy
{
    // current == null means no session exists.
    public static VoiceFocusDecision Decide(VoiceCommand command, FocusSnapshot? current, double remainingSeconds = 0);
}
```
Parsing rules for focus_start:
- task: string, trimmed, control characters removed, internal whitespace runs collapsed to one space, cut to MaxTaskLength chars. Missing / non-string / empty after cleaning -> false, error "Say what you want to focus on."
- minutes: optional. Missing, null, non-number, NaN or infinity -> DefaultMinutes. A numeric string such as "45" is accepted. Otherwise clamped to [1, 240].
- Extra properties are ignored. Malformed JSON -> false, error "Boo could not read that request."
- Unknown tool name -> false, error "Unknown voice command."
- Other tools ignore their arguments (null, "", "{}" all fine).

Policy (Reply strings are exact; {task} is the session task, {m} the whole minutes of the command, {left} = whole minutes remaining rounded up, minimum 1):
| Command | current | Apply | Reply |
|---|---|---|---|
| FocusStart | null or Ended | true | "Focus started: {task} for {m} minutes." |
| FocusStart | Running or Paused | false | "A focus session is already going ({task}). End it first." |
| FocusPause | Running | true | "Focus paused." |
| FocusPause | otherwise | false | "There is no running focus session to pause." |
| FocusResume | Paused | true | "Focus resumed." |
| FocusResume | otherwise | false | "There is no paused focus session to resume." |
| FocusEnd | Running or Paused | true | "Focus ended." |
| FocusEnd | otherwise | false | "There is no focus session to end." |
| FocusStatus | Running | false | "Focusing on {task}, {left} minutes left." |
| FocusStatus | Paused | false | "Focus on {task} is paused with {left} minutes left." |
| FocusStatus | null or Ended | false | "No focus session is running." |
| GoToSleep | any | true | "Going quiet." |
Minutes format: whole number, invariant culture (e.g. 30, 45, 1). For FocusStart {m} = (int)Math.Round(command.Minutes).

Contract (Boo.Providers, `RealtimeProtocol`):
- `SessionUpdate(...)` tools array = screen_help (unchanged) followed by the six voice tools, each `type:"function"` with an object `parameters` schema; focus_start has `task` (string, required) and `minutes` (number, optional).
- Instructions additionally tell the model it is a gentle focus friend: when the user describes what they want to work on, call focus_start (default 30 minutes, 1 to 240) without asking for confirmation; pause / resume / end only when asked; go_to_sleep when the user says bye or asks it to be quiet. The existing screen_help sentence and the untrusted-content sentence stay.
- `internal static bool TryGetVoiceCommand(string json, out string callId, out VoiceCommand? command, out string? error)`: true when the event is `response.function_call_arguments.done` with a non-empty call_id and a name that `VoiceCommandParser.IsVoiceTool` accepts (command may still be null with an error when arguments are bad, so the model hears the error); false for every other event (including screen_help). Integration tests reach it through InternalsVisibleTo if it already exists for screen_help; otherwise make it public.
- `RealtimeClient.VoiceCommandHandler : Func<VoiceCommand, CancellationToken, Task<string>>?` - when a voice tool call arrives: parsed -> handler result sent with the existing CompleteToolAsync; parse error -> the error text sent as the tool output; handler null -> "Voice commands are not available." Handler exceptions -> "Boo could not do that.".

App (MainWindow): handler marshals to the UI thread, asks `VoiceFocusPolicy.Decide` with the current `_focus?.Snapshot`, applies when Apply is true by calling the same code paths as the Start/Pause/Resume/End buttons (Start uses the command's task and minutes, Developer Mode checkbox respected), and returns Reply. GoToSleep stops listening and ends the Realtime connection after the reply is delivered (face returns to idle). Task text box shows the voice task.

## C. Hover-to-reveal proof

The notch exposes its face state for UI automation: `AutomationProperties.ItemStatus` on the notch window = the current `NotchFaceState` name (e.g. "Hidden", "Hover", "Idle"), updated on every state change. A new UI smoke step moves the mouse onto the top-centre hover strip, waits until ItemStatus is not "Hidden" (within 1.5 s), moves the mouse 300 px below, and waits until ItemStatus is "Hidden" again (within 3 s; spec says 900 + 160 ms).

## Out of scope (documented in the parity table)
Phone pairing, Codex controller, meeting notes, menu-bar-only toggles (cursor size, trail styles, Point Here / Follow toggles), spoken-voice-off mode. Alt+Space (Riley's wake toggle) is Windows' system-menu shortcut, so Boo keeps Ctrl+Alt+Space hold-to-talk.
