# Boo Figma redesign draft

Status: editable design exploration. This is not an accepted design or a Windows app implementation update.

User subsequently rejected the first eyes/notch design and requested a more interactive, professional direction. See the interactive follow-up below; the original Figma notch is not an accepted reference.

The user rejected the existing app and notch design and requested Figma. Their latest review order is **eyes and notch first, conversation-first app second, task workspace third**. The saved canvas still has the app frames above the notch board; the requested reorder was rejected by Figma's Starter-plan MCP limit. Use the focused links below in the requested order.

## Review links

1. [Eyes and notch states](https://www.figma.com/design/lysNym0saVGhv6ZlJ0QT8k?node-id=3-500)
2. [Conversation-first desktop app](https://www.figma.com/design/lysNym0saVGhv6ZlJ0QT8k?node-id=3-278)
3. [Task workspace](https://www.figma.com/design/lysNym0saVGhv6ZlJ0QT8k?node-id=3-384)
4. [Appearance controls](https://www.figma.com/design/lysNym0saVGhv6ZlJ0QT8k?node-id=3-537)

File: **Boo — Desktop & Notch Redesign**. Page: **Boo · Design exploration**.

## Scope and decisions

- Black pocket with two white eyes; no name or profile text in the mini.
- Idle storyboard hides the body; hover reveals the eyes; clicking opens a full desktop window. Active microphone/screen sharing retains disclosure and Stop.
- Full-window alternatives separate conversation-led work from reviewing a prepared task. The task sample changes only a button label, from Create account to Get started.
- Profile and appearance controls belong in the opened app. The appearance draft exposes body and eye colors, save, and reset.
- Windows-style titlebar controls. Neutral charcoal app surfaces, white actions, no decorative gradients.
- Nunito Sans is a proposed typeface, not a user-approved choice. The user's earlier warm and rounded preference describes voice, not typography.
- Dimensions, motion, spacing, and text are proposals. No exact installed HeyClicky geometry or hover timing is claimed.

## Reference basis

Inspected the official [HeyClicky Windows page](https://www.heyclicky.com/for-windows), [changelog](https://www.heyclicky.com/changelog), and published app-window and notch-peek images. These support the dark desktop window, task/conversation organization, talk control, and tucked top-center presence. The design applies the user's black/white eyes requirement and Windows chrome instead of copying the reference's platform controls.

Reference images inspected locally: `artifacts/figma-redesign-reference/app-window.webp` and `artifacts/figma-redesign-reference/notch-peek.webp`. Earlier installed-app observation limitations remain in `2026-11-07-heyclicky-presence-observation.md`.

## Structure and validation

The draft uses native editable text, auto-layout frames, vector eyes, a reusable eyes component, imported Simple Design System buttons, and local color variables. An inspection found no image-filled layers in the four review frames. All inspected text uses Nunito Sans.

Three click links were assigned: each app's settings button opens the appearance frame; the hover eyes open the conversation frame. These links were assigned in the document, not interactively acceptance-tested. The idle/hover/active board is a static storyboard; it does not prove real hover, speech, capture, edits, or AI behavior.

Root screenshot review found an overlapping assistant text line. Corrected text auto-height and re-exported the app frames and appearance panel. Also made the before/after sample buttons identical in color and width so only the requested label differs. Post-correction geometry checks found no children outside their four review frames.

Exported local previews:

- `artifacts/figma-redesign/notch-states.png`: visually inspected before the text-auto-height correction. A post-correction screenshot could not be obtained because Figma reached its Starter-plan MCP call limit; do not describe this PNG as the final exact render.
- `artifacts/figma-redesign/conversation.png`: post-correction screenshot, visually inspected.
- `artifacts/figma-redesign/task-workspace.png`: post-correction screenshot, visually inspected.
- `artifacts/figma-redesign/appearance.png`: post-correction screenshot, visually inspected.

Figma `whoami` confirmed the owning team has a Starter plan and View seat. File creation and initial edits succeeded, then the connector explicitly rejected further calls for the plan limit. No plan change or purchase was made. The file opened successfully in the embedded Figma view focused on the notch board.

## Independent visual review

A separate read-only reviewer inspected all four exported images without authoring them or making Figma calls. Root checked the findings against the previews.

- **Notch:** no material visible defect found within the static storyboard scope. This does not validate hover or active-state behavior.
- **Conversation:** no material layout defect found. Minor branding inconsistency: titlebar uses Boo while the sidebar uses boo; normalize in the next revision.
- **Task workspace:** confirmed visible defect. The top-right Windows minimize/maximize/close controls are missing. Reuse the conversation layout's complete titlebar and keep the right conversation pane below it.
- **Appearance:** intentionally an inner-panel concept, but the click destination shows the panel alone. Add it within the common full-window shell so the settings navigation demonstrates the intended context.

The last two findings remain uncorrected in Figma because the connector's plan limit rejected further writes. The full-app drafts are therefore not ready for implementation handoff. The notch can be reviewed as a design concept, with the screenshot currency limitation noted above.

Behavioral, accessibility, performance, native Windows and owner acceptance remain separate from this visual exploration. No production application code was changed for this Figma work.

## Interactive notch follow-up

Scope: eyes and notch only. A new Figma session again rejected a bounded read because of the Starter-plan MCP limit; `whoami` reconfirmed the plan. No Figma changes were made for this revision.

The root used the frontend concept-direction workflow to produce an interactive local design preview, following plan-only advisor approval. A separate test author froze `artifacts/notch-v2/acceptance.md` before the preview was built. The root authored the preview; separate agents perform behavior QA and visual review.

Preview source: `C:/Users/vikasmit/.codex/visualizations/2026/11/06/01a1b1c5-839f-70b2-a0a8-12b733c0b40b/boo-notch-interactive.html`.

Two proposals share the settled black/white, no-name, hover-reveal and full-window-entry requirements:

- **Sculpted pocket:** inverse shoulders join the screen edge, softened rectangular eyes glance toward nearby pointer movement, and active details expand sideways. Recommended for the more expressive presence the user requested. Its wider active strip takes more horizontal space.
- **Quiet shelf:** narrower vertical footprint and horizontal eyes; activity details extend below the centered eyes. Preserves a steadier eye position but occupies more vertical space while active.

Both include a single entry blink, distinct listening/thinking/speaking/sharing expressions, bounded speaking motion, keyboard reveal, a simulated app-opening shell, Stop, and explicit reduced-motion controls plus the OS preference. The design controls can tune the pocket's eye width/spacing. Motion is for event/state feedback; the preview does not run a constant idle animation.

The surrounding controls deliberately simulate scenarios. No microphone, screen capture, AI, account data or API calls are used. The full-app shell is a labelled navigation preview only. This revision does not replace the actual Windows app, prove native hover behavior, or establish aesthetic acceptance. The preview uses the Windows Segoe UI Variable/Segoe UI system font stack; it is a design proposal, not a previously approved font.

Root screenshot inspection removed two unnecessary background marks and corrected narrow active-notch sizing so its shoulders remain inside the preview. The host-provided carousel rendered locally; the browser wrapper is `artifacts/notch-v2/preview.html`.

Independent baseline QA passed 25 bounded checks, then supplementary inspection reproduced an overlap between the active shelf and the app preview. The root moved the app preview below the active shelf and increased its stage height when opened, preserving all controls. A strict sharing-disclosure ambiguity was resolved conservatively: simulated screen sharing persists when switching to thinking, speaking or listening, and clears on Stop or an explicit quiet scenario. These changes require the final recheck below; baseline evidence alone does not cover the repaired revision.

### Final preview verification

- Independent baseline rerun: **25 checks passed** at the repaired source fingerprint.
- Independent targeted rerun: **9 checks passed**, covering persistent sharing across active modes, Stop/Idle/Hover clearing, and unclipped, nonoverlapping app bounds at desktop and 320px for both variants.
- A separate visual reviewer inspected current desktop and narrow active-app captures. No material visible defect remained in that scope. The root also inspected those images directly.
- Source SHA-256: `7a857d853ef73b7b2736c7bdb7c2e62f1cd0e7aed9ba6ec298d9091619793814`.
- Wrapper SHA-256: `54933fefbf6a82c618a742b3ad0a6f89edd002232dc73917fca3143912c6b741`.
- Receipts: `artifacts/notch-v2/qa-receipt.md` and `qa-final-receipt.md`; scripts and corresponding JSON are adjacent. Earlier overlap findings are explicitly marked historical in `qa-targeted-findings.md`.
- The targeted runner initially failed due to test-harness issues: bounding-box properties were read using the wrong field names; matching ignored the explicit combined mic/screen label; hidden-subtree text was treated as a visible disclosure. The independent tester corrected those assertions against the unchanged contract, retaining the earlier harness. The root reviewed the corrected assertions and current receipts; no application change was made to conceal those harness failures.

Verification is limited to this browser-based design simulation. Figma synchronization is **BLOCKED** by the confirmed tool quota; actual Windows integration, native app acceptance, screen reader audit, and user aesthetic acceptance are **NOT RUN** for this redesign.
