# Give requested actions actual Windows control evidence

This implements the next part of the accepted companion plan's requested
actions flow. It does not declare the edit/Undo demo complete: verified click
postconditions and remaining target-lifetime defects are subsequent work.
Continue under existing autonomous authorization with tests before builders.

## Evidence and outcome

ResponsesClient sends an image and window/display identity but no UI Automation
control descriptors. DesktopTools uses `automationId` for UIA targeting;
`FindElement` otherwise returns the window root. The model therefore cannot
honestly obtain IDs such as the fixture's `button-text` from its current input.

Add an independently observed, memory-only control snapshot to explicit task
requests. Supply real IDs, names, roles, bounds, supported operations and safe
text values. Bind UIA proposals to that snapshot rather than accepting invented
IDs. Keep the existing effect-argument schema and frozen Contracts.cs unchanged.
Do not encode unrelated metadata in file-content arguments.

## Public Core seam

Add `ControlCandidate` as a public sealed record with positional fields:
`string AutomationId, string Name, string ControlType, string? Value,
RectD Bounds, IReadOnlyList<int> RuntimeId, bool IsEnabled, bool IsOffscreen,
bool IsPassword, bool CanInvoke, bool CanSetValue`.

Add `ObservedControl` as a public sealed record with the first five fields in
the same order, then `ImmutableArray<int> RuntimeId, bool CanInvoke,
bool CanSetValue`. Runtime IDs stay local; they are opaque comparison data,
not permanent identities and not strings to show or send to the model.

Add a public sealed `ControlObservation` with private construction, read-only
`ScreenContext Context` and `IReadOnlyList<ObservedControl> Controls`, and
`static Create(ScreenContext context, IEnumerable<ControlCandidate> candidates)`.
Create validates context geometry (finite positive display/target bounds,
including finite computed right/bottom edges, and positive frame dimensions).
Display ID and target process name must be non-whitespace, display scale finite
and positive, target handle nonzero and process ID positive. The target window
may cross the display boundary. It then consumes at most 512 input
candidates and publishes at most 128 controls in input order. It copies runtime
ID arrays and returns a collection callers cannot mutate.

Eligible candidates are enabled, on-screen, non-password, have finite positive
bounds fully inside both the selected display and target window, an actual
non-whitespace ID of at most 256 characters, a non-null name of at most 512
characters (empty is allowed), a non-whitespace role of at most 128 characters,
and 1–64 runtime-ID integers. Never trim or truncate selector identity. Exclude
every eligible candidate whose ordinal ID occurs more than once among the
examined eligible candidates, before applying the 128-control publication cap.
Values over 1,024 characters become unavailable
(`null`); preserve exact shorter values, including empty strings. Do not require
invoke/value support to publish a control: read-only result labels will matter
for later postcondition verification. Null inputs fail with ArgumentNullException;
invalid context fails with ArgumentException. Individual invalid candidates
are omitted. No screenshot/audio persistence or logging of control text.

## Windows read-only boundary

Add `ControlObservationReader(TargetObserver targets)` with
`Task<ControlObservation> ReadAsync(ScreenContext context, CancellationToken token)`.
Perform UIA work on a windowless MTA worker; return plain copied data only.
Root and worker do not use this service as an alternative desktop automation
tool. Real native QA still drives Boo through the supported Sky route.

Use only AutomationElement.FromHandle for the independently retained target.
Verify TargetObserver.IsCurrent before and after reading. Walk the target's
ControlView subtree with at most 512 visited elements, checking cancellation
between native calls. Do not enumerate the whole desktop, use FindAll over an
unbounded subtree, save text, or marshal live AutomationElements into WPF.
Cache ordinary properties where supported; check password/visibility before
reading ValuePattern.Value. A read-only ValuePattern can still supply an
observed value, but CanSetValue must be false. Capability flags require the
actual supported patterns. Feed copied candidates to the Core factory.

Cancellation remains cancellation. Root/target loss, navigation failure, or
unavailable UIA produces a fixed unavailable error without raw provider text.
A disappearing individual element may be omitted. Await the worker through
completion; do not detach it behind a timeout. The traversal count is bounded,
but this does not claim that one blocked Windows provider call is interruptible.
That native-lifetime limitation remains a separate delivery blocker.

## Provider handoff

Extend RespondAsync with an optional third parameter `ControlObservation?
controls = null`, preserving its existing two-argument calls. When supplied,
require a frame and exact equality with its ScreenContext, and matching request
generation, before credential/transport use. Include a bounded JSON control
list as a separate untrusted `input_text` JSON object in user input, not
higher-priority instructions. Its exact shape is
`{"observedControls":[{"automationId":"...","name":"...","controlType":"...","value":null,"bounds":{"x":0,"y":0,"width":1,"height":1},"canInvoke":false,"canSetValue":true}]}`.
Send only those properties;
never runtime IDs or rejected candidate data. Keep models, schema argument
keys, tools, endpoint, cancellation and existing screen-context binding intact.

Tell the model to use supplied IDs for UIA click/type proposals and copy their
observed names. At decode, when controls were supplied, discard click/type
proposals naming an unknown ID or a provided name that differs ordinally from
the observed name. If name is absent, fill it from the observation before the
request reaches the immutable approval preview. A type_text proposal also
requires a matched observed automation ID and CanSetValue; never allow a
no-ID type_text fallback when observation was supplied. Preserve other proposal
order and existing coordinate-only click behavior. Missing optional observation keeps the existing
transport-decoder contract; it does not certify any native action.

## WPF integration and current limits

Read controls only for an explicitly authorized task with a captured frame,
after capture and before the Responses request. Do not add this latency to
ordinary teaching, research, focus checks or typed realtime transport. Keep
the capture activity alive through the read, and recheck generation, lease,
target-reader identity, target freshness, and frame/context equality after the
await. On read failure, use an empty observation for the actual frame
so IDs cannot be guessed; show a fixed control-evidence limitation while the
image remains usable. Stop/replacement must prevent a late read from sending
another provider request or restoring UI state. Retain the observation alongside
pending actions and check its generation/target binding before approval. Clear
it on Stop, replacement, cancellation, preview reset, or queue completion.
This binding check does not substitute for fresh native control validation.
No microphone activation.

This slice supplies evidence and binds proposals. It does not yet replace
DesktopTools' native effect implementation, add click success claims, solve
same-connection voice authority, or prove stale-control invalidation at commit.
Those remain explicit required work before final delivery. Runtime-ID data is
retained in the host snapshot for that next commit-validation slice, not used
to claim permanent identity now.

## Test-first ownership and evidence

Independent Core and provider test authors work in separate worktrees. Root
adds compile-time stubs only, retains RED evidence, freezes the new tests and
then releases separate Core, provider and Windows builders with disjoint files.
Root integrates WPF. All existing frozen tests and Contracts.cs stay unchanged.

Core tests cover published immutable snapshots, duplicate IDs, exact/oversized
values, password/disabled/offscreen omission, invalid bounds including negative
display origins, limits, identity preservation, context and null rejection.
Provider tests use the public HttpMessageHandler seam to inspect actual request
data, reject mismatched contexts without sending, suppress unknown IDs/name
mismatches, fill observed names and preserve the old two-argument contract.
They do not pretend to test Windows property reads.

Native cases are written before builder release: on the existing disposable
QA workspace, ask Boo to type a unique short value into the Button text field
without clicking Apply. Inspect the exact approval fields, approve only that
bounded fixture edit, then verify the actual field value and Boo's readback
result. Restore the disposable field through Sky. This is the typing substep,
not the full edit-and-Undo demo. No private apps or credential values are used.

Fresh source review must examine UIA threading, password read order, snapshot
binding and all late-result paths. Run oracle verification, Release build and
applicable suites; package exact source before native QA. Never promote a
fixture or model response to native privacy, geometry, or complete-demo proof.

## Primary references

- Microsoft UIA threading: https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-threading
- TreeWalker cache overload: https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.treewalker.getfirstchild?view=windowsdesktop-10.0
- Runtime IDs can be reused and are comparison-only: https://learn.microsoft.com/en-us/dotnet/api/system.windows.automation.automationelement.getruntimeid?view=windowsdesktop-10.0

Independent Advisor review is required before releasing test authors/builders.

## Follow-up unit: bound concurrent native reads

Source review found that a new request can start another MTA worker while an
earlier UIA call has not returned. This unit addresses the worker accumulation
portion of the native-lifetime requirement in `review-residuals.md`. It does
not claim to interrupt a blocked UIA call or repair action execution lifetime.
Product choices and the existing RequestCoordinator Stop contract are unchanged.

Add a public sealed `ControlReadCoordinator` in `src/Boo.Core/ControlReadCoordinator.cs`.
Its public seam is `Task<ControlObservation> ReadAsync(Func<Task<ControlObservation>>
read, CancellationToken cancellationToken)`. Each coordinator admits at most
one callback at a time and owns the callback's returned task through completion.
A waiting canceled caller must complete canceled without starting its callback.
A pre-canceled caller likewise never starts native work. Cancellation of an
already-started read must not release admission while its task is unfinished.
If that task returns a successful value after cancellation, discard the value
and complete canceled. Preserve callback faults and cancellation as failures,
release admission after every settled callback, and permit the next live caller.
Null callbacks fail with ArgumentNullException. No timeout detachment, dedicated
idle thread, public mutable gate, disposal while reads are live, or background
retry is introduced. SemaphoreSlim's existing asynchronous admission pattern in
`src/Boo.Windows/ScreenCapture.cs` is the relevant repository precedent.

`ControlObservationReader` uses one static coordinator for all reader instances
and constructs/starts its MTA worker only inside the admitted callback. Existing
context and token validation, native worker behavior, fixed error messages,
target checks, privacy filtering, and snapshot copying remain intact. Waiting
callers do not start threads. Caller cancellation is still checked between native
calls; no claim is made that Windows aborts a provider call already entered.

Independent tests in `tests/Boo.Core.Tests/ControlReadCoordinatorTests.cs` use
controlled callback tasks at this public seam. Before builder release, demonstrate
RED for serial admission and cancellation ownership. Cover exact result forwarding,
pre-cancellation, canceled waiters never running, held admission after in-flight
cancellation, discarded late success, fault/throw/cancel release, and subsequent
successful use. Use deterministic entry/release signals; bounded test waits are
failure guards, not evidence from sleeps. Freeze the new tests additively without
changing existing oracle files. A separate builder owns the Core implementation
and Windows integration. Root inspects integration and reruns focused tests, full
Release checks and the oracle. Fresh source review must confirm the coordinator
is process-shared and Thread construction occurs only after admission.

The deterministic checks prove coordination through the returned task; native
blocked-provider behavior remains unmeasured. Native typing acceptance uses an
exact package and remains separate from this follow-up. The existing failing
format check on untouched Core files remains reported. This unit is complete
when its independently frozen tests pass, wiring is reviewed, and evidence and
residual limits are recorded. A package or final delivery is not implied.
