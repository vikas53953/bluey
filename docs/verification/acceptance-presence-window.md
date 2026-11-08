# Presence and full desktop window acceptance

This additive presence and full-window contract is evaluated against the exact packaged Windows
candidate. It is native acceptance, not a substitute for the Core contract
tests or a browser/XAML source-text check. Each case records observed behavior,
the candidate identity, Windows build, monitor layout, scale, and evidence path.

The latest presence correction supersedes only the old frozen NAT-002 wording
for visible idle state and compact expansion. The existing placement, fixture
usability, minimal-eyes/rounded-type direction, honest reference limits, and
all unrelated native cases remain in force. The frozen acceptance file is not
edited by this contract.

| ID | Native scenario | Expected observable result | Evidence |
| --- | --- | --- | --- |
| NAT-P-001 | Start the packaged app while idle | The mini is visually retracted: no body, eyes, Boo/profile text, status, or other visible label is shown. Only the narrow top-center edge hit area remains for hover. | Screenshot plus accessibility tree |
| NAT-P-002 | Hover the retracted mini without clicking | The black body and white eyes reveal without taking foreground focus or changing the foreground app. | Before/after foreground HWND and screenshot |
| NAT-P-003 | Move the pointer away after hover | The mini retracts; its narrow edge hit area does not intercept unrelated desktop content or create pointer oscillation. | Pointer path, foreground check, screenshot |
| NAT-P-004 | Click the mini | A normal native desktop window opens, with accessible identity, system minimize/maximize/close chrome, and resizable behavior. | UI Automation tree, resize/maximize screenshots |
| NAT-P-005 | Open settings and choose body/eye colors | A labeled native color chooser accepts valid opaque `#RRGGBB` values, applies them, and saves them separately from provider credentials. | UI Automation actions plus settings file hash/path |
| NAT-P-006 | Enter a low-contrast or malformed/nonopaque color | Validation text is readable; save is rejected; the currently applied colors and last-good file remain unchanged. | Before/after screenshots and file bytes |
| NAT-P-007 | Restart after saving valid custom colors | The saved custom colors are loaded before the mini is shown; reset restores black body/white eyes. | Cold-start screenshot and file evidence |
| NAT-P-008 | Start screen capture, then move away/click around the mini | A readable capture disclosure remains visible outside the full window while capture is active; no brand/profile text appears. | Native screenshot and activity trace |
| NAT-P-009 | Start microphone activity independently | A readable microphone/listening disclosure remains visible; it is not coupled to capture state. | Native screenshot and activity trace |
| NAT-P-010 | Stop capture/microphone activity | The corresponding disclosure clears; any remaining activity keeps its disclosure and the final end permits retraction. Emergency Stop remains available. | Before/after screenshots and activity trace |
| NAT-P-011 | Use keyboard and tray entry paths | Both paths open/recover the app without requiring the mini to be visible or stealing focus from the external target before activation. | UI Automation actions and foreground HWND log |
| NAT-P-012 | Minimize, close, and reopen the full window | Minimize and close return to the background without an orphan window; reopening restores a usable app and preserves the saved appearance. | Window inventory and screenshots |
| NAT-P-013 | Resize, maximize, and restore the full window | Native chrome remains functional; restored default is approximately 1120x780 DIPs, constrained to the current working area, and the conversation plus settings remain usable through a narrow width and scrolling. | Window bounds at each state and accessibility tree |
| NAT-P-014 | Run with Windows high contrast enabled | System high-contrast colors and readable accessible names are used; no critical control depends on color alone. | High-contrast screenshots and UI Automation properties |
| NAT-P-015 | Use selected monitor with negative origin and mixed DPI | Mini and restored full-window placement use the selected monitor's physical bounds and actual scale; no zero-origin clamp or wrong-monitor DPI transform occurs. | Monitor/DPI inventory, physical bounds, screenshots |
| NAT-P-016 | Repeat open/close/reopen and inspect names | The collapsed mini has no visible Boo/profile text; the opened full window may use descriptive accessible app/launcher names and the product name, but no invented profile appears anywhere; no orphan window remains. | Window inventory, accessibility tree, screenshots |

Cases that the available native driver cannot exercise are recorded as
`UNVERIFIED` with the reason. They are not counted as passes, and capture
protection is never disabled to make a case executable.
