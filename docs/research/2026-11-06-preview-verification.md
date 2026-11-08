# Boo visual preview verification

Artifact: `../preview/boo/screens/index.html`, CSS, and JavaScript. This record concerns the browser prototype only.

## Observed checks

- JavaScript syntax check passed with `node --check`.
- Rendered in Chrome on Windows at a reported 1920 x 945 viewport. DOM width was 1900, with no horizontal page overflow. All avatar image elements reported loaded. The installed exact HeyClicky layout remains unverified.
- Pocket notch opens and closes its panel; Show me displays a ring and arrow around the sample button with a nearby explanation. Clear marks removes them.
- Voice demo reaches the captioned reply. Hear Boo reported speech playback completion through browser speech events; this is not a live microphone or intelligibility/latency test. Stop displays the stopped state.
- The research rehearsal reaches a concrete overwrite preview. Cancel visibly reports that nothing changed; Approve sample reaches an explicitly simulated completion which says no file was changed.
- Focus preview counts down. Pause changes its state and screen indicator to off. The manual nudge displays a choice to return or take a break and says no browsing was observed.
- All three composition options were selected and visually inspected, including expanded floating and assistant-strip panels. Ghost, blueberry, and minimal-eye selections were exercised; Segoe UI was verified as the computed font for the native typography option.
- Typing an unsupported request displays the input and an explicit prepared-examples limitation rather than inventing an AI reply.
- Keyboard Space selected an appearance option. Complete keyboard traversal, screen-reader quality, smaller-window breakpoints, and native DPI behavior were not comprehensively tested.
- Two preview-state issues were corrected: stale conversation content after switching demonstrations, and an idle panel failing to cancel a running rehearsal/reset its status. The refreshed idle panel was observed with matching ready/idle status.

## Visual evidence

Screenshots in `../preview/boo/`: `explanation.jpg`, `voice.jpg`, `appearance-choices.jpg`, `floating.jpg`, and `assistant-strip.jpg`. Some alternative-state captures are scrolled to the preview area rather than the page header. `task-approval.jpg` is a partial scrolled capture; approval/cancellation behavior was verified separately through the visible accessibility state.

The low-contrast sample button is deliberately the subject of the explanation. It is not a proposed Boo control style. Main prototype copy and status colors were reviewed and the faint reviewer-caption colors were darkened. This is not a full accessibility audit or a claim that every decorative sample-workspace label meets contrast requirements.

## Not established

No real AI answer, live web search, actual microphone recognition, native overlay drawing, PC task execution, provider cost, native latency, installed-package acceptance, or exact HeyClicky visual match is proven by this prototype. No production tests exist or ran. User visual approval remains pending.
