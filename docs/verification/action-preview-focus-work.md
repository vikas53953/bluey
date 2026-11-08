# Action preview keyboard handoff

Work source: the accepted companion plan requires usable keyboard action
approval. Candidate11's prewritten typing case now has a verified fresh request
and exact preview, but the expected Cancel focus did not occur. The actual Sky
`accessibility.focused_element` remained InputBox at 09:18:17.517Z and
09:18:29.754Z on 2026-11-07. Cancel and Approve were present in the accessible
tree. No driver action refocused InputBox after submission. See
`native-typing-candidate11.md`; no effect was approved or committed.

The current ShowActionPreview calls CancelButton.Focus immediately after making
the previously collapsed preview visible. Failure of that call is not checked.
The native evidence proves a failed focus handoff; layout timing is the repair
hypothesis, not an observed Windows cause. Microsoft's
[WPF focus guidance](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/advanced/focus-overview)
supports setting focus after the element is visible in the loaded visual tree,
including through a dispatcher callback.

Bounded work statement: defer the existing Cancel focus request until WPF has
processed preview layout, bring the preview into view, and then request Cancel
focus. Before the deferred operation, require the same pending preview object,
current request generation, visible/active panel, visible preview, and no
approval already in flight. Stop, cancel, replacement, close, and background
windows must not receive a late focus request. Do not activate or raise the
window, focus Approve automatically, add a polling/timer retry, change approval
policy, change action payloads, or alter styling. This is confined to
`src/Boo.App/MainWindow.xaml.cs` and its implementation receipt.

Native regression test, defined before implementation: use the same isolated
fixture, muted profile and exact package binding. Set the exact requested text
with supported Sky set_value and verify InputBox's own Value before submission.
Confirm a new request by input clearing and a fresh transcript/state change.
After the exact immutable type_text proposal appears, observe actual returned
keyboard focus on Cancel. One Tab must move it to Approve, with the same exact
proposal still present; one Space may approve only the already-authorized
disposable field edit. Verify actual field value separately from Boo readback,
then restore it. Stop/cancel/replacement must clear the pending preview, and
source review must inspect every deferred-focus guard. No alternate UIA or
capture-exclusion change is permitted. Any unavailable native case stays open.

This manual Windows surface has no suitable deterministic test seam in the
current suite. Existing frozen tests remain unchanged; no source-string or
private-method test is added. The reproduced native failure above is the RED
evidence. Build the App, run existing Release checks on the integrated tree,
obtain fresh independent source review, and repeat the native case on a new
exact package before calling the focus repair verified. Typing, click, Undo,
and full delivery remain separate acceptance outcomes.
