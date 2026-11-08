# Capture lifetime and concrete approval repairs

Status: PLAN_APPROVED by independent Advisor v2. Root owns the plan. This repairs
two bounded parts of the existing accepted Boo plan and frozen NAT-007 through
NAT-009 / NAT-015 through NAT-017. Native acceptance remains unchanged.

## Capture lifetime

Keep ScreenCapture as the application service, with one service instance in
MainWindow. Add an injectable `IFrameCaptureBackend` at the actual Windows
capture boundary. Its one asynchronous operation owns exclusion, WGC frame
acquisition, in-memory JPEG conversion, and native cleanup. The production
backend retains the existing WGC implementation; no fallback capture API or
saved frames are added.

ScreenCapture serializes complete backend lifetimes, including cleanup, with
an instance semaphore. Waiting callers honor cancellation. Validate arguments
before entering the backend: every supplied owned handle must be nonzero, at
least one is required, target cannot be owned, and display geometry is finite
and positive. Copy the owned-handle list before waiting.

Use a linked cancellation deadline (five seconds in production). The backend
must return promptly from setup and honor cancellation during asynchronous
frame acquisition. Register frame cancellation before starting capture. Keep
the gate until backend cleanup finishes; do not time out and release the gate
while native capture is still running. Convert deadline cancellation to a
readable CaptureLimitationException; preserve caller cancellation separately.
Check cancellation again after JPEG readback and before returning a frame.
This bounds a missing-first-frame wait; it does not claim that Windows GPU or
COM calls can be forcibly interrupted.

The public OS seam is `IFrameCaptureBackend.CaptureAsync(DisplayInfo display,
WindowIdentity target, IReadOnlyList<long> ownedWindows, long generation,
CancellationToken cancellationToken) : Task<CapturedFrame>` in
`src/Boo.Windows/IFrameCaptureBackend.cs`. The public constructor is
`ScreenCapture(IFrameCaptureBackend backend, TimeSpan? timeout = null)`;
the default constructor uses `WindowsFrameCaptureBackend` and five seconds.
An explicit timeout must be at least one millisecond and at most
4,294,967,294 milliseconds, within CancelAfter's supported range.

Copy owned handles before validating or awaiting anything. Start the deadline
only after acquiring the semaphore. Queued calls use only caller cancellation.
The backend task completes only after frame/session/pool/device cleanup and
affinity restoration. No WhenAny/WaitAsync race may release the semaphore
early. Restore every affinity after WGC disposal, and surface restoration
failure as a limitation rather than returning a successful frame.

Caller cancellation takes precedence over deadline cancellation when handling
OperationCanceledException. Native/cleanup failures remain failures even if
cancellation happened concurrently; they cannot yield a successful frame.
Both the production backend after JPEG conversion and the service immediately
after backend completion check cancellation. Tests use fake hardware only at
this OS boundary; they do not prove native resource disposal or affinity.

Independent first tracer: no first frame arrives, deadline cancels the backend,
cleanup completes, and the caller receives a readable limitation. Follow-up
tests cover serialization through cleanup, caller cancellation while queued,
late-frame rejection, and invalid/zero owned handles without native calls.

## Concrete approval presentation

Add a pure Core `ActionPreviewBuilder.Create(request, authority)` returning an
immutable preview: title/target description, a field for every supplied
argument, an immutable copied ToolRequest, and can-approve/blocked reason.
Fields retain full values, including multiline/empty text; nothing is silently
truncated or omitted. Labels use plain language for the bounded argument set.

For file actions, show the selected root's resolved canonical destination using
the same path precedence and ScopedFiles resolver as execution. Missing root,
rejected paths, denied authority, and unsupported actions produce a disabled
preview with a readable reason. This does not replace execution-time rechecking
or the separately tracked native file-identity repair.

The WPF approval card renders the target and all fields in a bounded scroll
area with readable, selectable values. The pending action is the immutable
request displayed by the preview, so later caller dictionary mutation cannot
change what the user approves. Keep exact digest/generation checks in execution
and do not show internal hashes in the product flow. Use the same builder for
every step in an action chain.

The public preview types in `src/Boo.Core/ActionPreviewBuilder.cs` are
`ActionPreviewField(Key, Label, Value)` with a computed DisplayValue showing
`(empty value)` for empty strings, and `ActionPreviewModel(Title,
RequestedTarget, Destination, ImmutableArray<ActionPreviewField> Fields,
ToolRequest Request, bool CanApprove, string? BlockedReason)`. Fields are
ordinal-sorted by argument key. Request.Arguments is a FrozenDictionary copy.
Show both the raw requested target and the canonical override destination.

Add `src/Boo.Core/FileActionResolver.cs` with a public 4 MiB write limit and
`Resolve(request, authority)` returning `FileActionPlan(CanonicalPath,
Contents, Overwrite)`. Both preview and DesktopTools use it, preserving the
existing nonblank `path` over target and `contents` over `content` precedence.
The preview shows all supplied values and labels any unused content alias
explicitly. Missing content/root/parent, oversized content, path escape,
existing write target without overwrite, directory/device target, and missing
or non-regular delete target are blocked with readable reasons. Resolver
validation is repeated before execution; atomic file identity remains a
separately tracked repair.

Root updates the actual `MainWindow.xaml` and `MainWindow.xaml.cs` files.
Use read-only selectable controls inside a bounded ScrollViewer; a disabled
preview shows its reason, disables Approve, and leaves Cancel usable. The
Approve handler defensively checks the stored preview's CanApprove value.
Every chain step assigns `_pendingAction` from that preview's copied request.

Independent first tracer: a write request with a path override, multiline
contents, and overwrite option exposes the canonical destination and full
values; mutating the original argument dictionary cannot change the preview
or pending request. Follow-up tests cover typed text, empty values, denied
authority, and path escape failures.

## Execution and ownership

After Advisor approval, root adds only minimal public stubs/overloads needed
to compile independent tests. Authors record RED before builders are released.
Root waits for explicit author ownership release before freezing an additive
oracle revision; no existing hashes, contracts, tests, or manifests are edited
by builders.

Capture and approval test authors/builders own disjoint files. Root integrates
the WPF card. Builders do not run live APIs, activate capture/microphone, touch
credentials, or automate the desktop. Fresh reviewers check the repairs;
root reruns affected and full tests, Release build, oracle, and diff checks.
Existing candidates remain immutable. These tests do not prove native WGC
exclusion, actual user approval UX, protected windows, or physical latency.
