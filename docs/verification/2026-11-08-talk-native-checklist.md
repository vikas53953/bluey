# U4 hold-to-talk: native Windows QA checklist

Windows-only checks for plan unit U4. The pure logic (HoldToTalkController, LatencyRecorder) is covered by
frozen tests in tests/Boo.Core.Tests; these cases check the LL keyboard hook, mouse, microphone and audio,
which can't run in the Linux container. Record for each case: build number, Windows version, display scale,
microphone device, model, PASS/FAIL, and a screenshot or short note. Anything not run stays "not verified on Windows".

Setup: OpenAI key configured, Boo running, a working microphone and speakers, Notepad open with an empty document.

| # | Case | Steps | Expected |
|---|------|-------|----------|
| T1 | Chord in Notepad, no space typed | Focus Notepad with the caret in text. Hold Ctrl+Alt+Space for ~2 s (long enough for auto-repeat), saying "what time is it". Release Space. | No space characters added to Notepad (check before/after length). Notepad keeps focus. The window menu (Alt+Space) does not open. |
| T2 | Listening state shown | During T1, watch the notch. | Within ~150 ms of the chord the notch shows the listening state (mic indicator / listening expression). It goes away on release. |
| T3 | Release sends | After T1's release. | The turn is committed: Boo answers out loud (and transcript shows it). Only one reply. |
| T4 | Any key order | Repeat T1 pressing Space, then Alt, then Ctrl; release Ctrl first. | Starts on the third key; releasing Ctrl sends. A space may be typed by the first plain Space press (expected: plain Space is never swallowed), but none during the chord. |
| T5 | Plain Space untouched | In Notepad type "a b c" normally, and Ctrl+Space, Alt+Space (the window menu must open). | Text types normally; Alt+Space still opens the window menu; Boo doesn't listen. |
| T6 | Tap shows hint | Tap Ctrl+Alt+Space quickly (< 200 ms). | No turn is sent, no reply. Hint "Hold to talk" appears briefly. Mic indicator off. |
| T7 | Hold the eyes | Press and hold the mouse on Boo's eyes ~2 s while speaking, then release. | Listening starts on press, the turn is sent on release, Boo replies. A quick click on the eyes shows the "Hold to talk" hint instead (if click also opens the full window, note it). |
| T8 | Keys + eyes together | Hold the eyes, then also hold the chord; release keys first, then eyes. Repeat in the opposite order. | Mic stays on until both are released; exactly one turn sent. |
| T9 | Release while unfocused | Start the chord with Notepad focused, Alt+Tab is NOT used; instead click another window with the mouse while holding, then release. Also: start a hold, have another app steal focus (e.g. a toast or Win+D from a second keyboard), release. | Release still sends; mic indicator goes off. Mic must never remain on after all keys are up. |
| T10 | Lock screen mid-hold | Hold the chord, press Win+L (or lock via Ctrl+Alt+Del) while holding. Unlock. | The turn is cancelled (no reply), mic off while locked and after unlock. Releasing keys after unlock does nothing. |
| T11 | Sleep mid-hold (optional) | Hold the eyes, put the PC to sleep via the power button, wake. | Cancelled, mic off, no reply after wake. |
| T12 | 120 s safety cap | Hold the chord for > 2 minutes (a book on the keys works). | At ~120 s the mic stops and the turn is sent automatically. Holding longer starts nothing new; releasing does nothing more. |
| T13 | Interrupt playback | Ask a question that gets a long spoken reply. While Boo is speaking, hold the chord and ask something else. | Speech stops immediately on the press (before you speak), then Boo listens; the new reply plays, the old one does not resume. |
| T14 | Ctrl+Alt+F12 stop | Hold the chord (or eyes) and press F12 while still holding Ctrl+Alt. Then release everything. | Immediate cancel: mic off, no reply, nothing sent on release. Same while Boo is speaking: speech stops. |
| T15 | Never stuck on | Mash: random quick chords, eye clicks, Ctrl+Alt+F12, Win+L, for ~1 min. | At the end with nothing pressed, the mic indicator is off and Windows' privacy indicator (taskbar mic icon) shows no active use within a couple of seconds. |

## T16: latency run (AE2)

Target: 20 warm turns, median <= 1500 ms and p95 <= 3000 ms, measured end of speech -> first reply audio.

1. Start Boo fresh (cold connect). Note network (wired/Wi-Fi), model and region.
2. Do 21+ hold-to-talk turns with short questions ("what's 2 plus 2"), waiting for each reply to finish. Release the
   chord promptly when you stop speaking (end of speech = release / commit time as recorded by Boo).
3. The first turn after connecting is cold and is excluded. If the connection drops and reconnects, the next turn is cold again.
4. Read Boo's latency summary (LatencyRecorder: warm count, warm median ms, warm p95 ms, MeetsTarget). How to read it:
   - **warm count** must be >= 20, otherwise the run is incomplete regardless of the numbers.
   - **median**: middle warm value (mean of the two middle values for an even count). Must be <= 1500 ms.
   - **p95**: nearest-rank: sort warm values ascending and take the value at position ceil(0.95 x n). For n = 20 that is
     the 19th value, so one slow outlier is tolerated but two are not. Must be <= 3000 ms.
   - **cold** values are listed separately for information only.
5. Copy the per-turn list (turn, ms, cold/warm) and the summary into the verification note. If it misses the target,
   note which (median or tail) and network conditions; don't re-run selectively to cherry-pick.
