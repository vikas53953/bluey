# Boo visual MVP

Open `screens/index.html` in a browser, or use the running local preview URL supplied in the chat. All assets are local. This folder is a throwaway visual prototype, not production application code.

Try Show me, Let's talk, and Take care of it. The fourth moment preserves the focus-coach idea. Compare the three compositions below the desktop, change the avatar and typography, then press Try this combination.

The page does not request microphone permission, capture the desktop, call an AI service, perform a web search, or write user files. Optional Hear Boo uses the browser's installed/available speech-synthesis capability and a fixed sample line. Input only routes to prepared examples; arbitrary questions show that limit explicitly. Simulated research references are illustrative, not evidence that a live search was performed.

No visual choice is approved. Initial selections are a first proposal for comparison. The user must review the plan and design before production CE work.

## Artifact location

CE's usual no-repository temp fallback was attempted. This Windows tool sandbox created a virtualized temporary path inaccessible to the externally reachable server. To keep one usable, durable review artifact, the preview is saved here inside the authorized workspace. No Git repository or product branch was created. The original temporary attempt was not deleted. This is a platform adaptation of the prototype storage convention.

The CE 3.36.1 `ce-prototype/scripts/light-webserver.js` helper serves this folder with `screens/` as its public root. On this host the server needs a foreground terminal outside network isolation, bound only to 127.0.0.1. A direct opening of `screens/index.html` remains usable if the server stops; annotation tooling is server-only.

## Asset provenance

- `screens/assets/boo-ghost.png`: original mascot concept generated with the built-in image-generation tool; copied from the generated output into this preview. Transparent background. Not a final identity.
- Blueberry SVG: code-authored interpretation of Riley's pinned Bluey expression and palette references; original source is Unlicense. It is marked Riley-inspired, not a pixel-identical recreation.
- Minimal eyes SVG: simple original UI geometry.
- `screens/assets/fredoka.ttf`: Google Fonts Fredoka, with its license in `Fredoka-OFL.txt`. The option pairs Fredoka with the system Segoe UI. The alternate uses system Segoe UI throughout Boo's product surface.
- UI glyphs: consistent simple SVG paths authored for this prototype.
- Sample design workspace, plant illustration, copy, and onboarding card are fictional demo content.

### Mascot generation prompt

Create an original friendly desktop AI companion mascot named Boo, a single small periwinkle-blue ghost with a softly rounded dome head and three subtle rounded scallops at the base, no arms, huge curious off-white oval eyes with small navy pupils, a tiny smile and subtle pink blush. Cute, calm, smart, endearing rather than babyish. Polished flat vector-like shape with very subtle soft dimensional shading, clean silhouette readable at 28 pixels, no outlines except subtle eye definition. Front view, perfectly centered, occupies 80% of square canvas. No text, no letters, no accessories, no environment, no drop shadow, no watermark, no logos. Real transparent background. This is a proposed identity for a Windows screen-top companion, not an existing character.
