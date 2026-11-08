# Native capture diagnosis

Candidate6 verified a real resize/reopen handoff and then reached a later
failure: `InvalidCastException`, HRESULT `0x80004002`, before a frame was sent.
The exact package and source hashes are in `native-teaching-candidate6.md`.

The type/HRESULT alone does not identify which projected interface failed.
Candidate7 will report fixed capture-stage names around display-item creation,
graphics-device creation, frame-pool/session setup, acquisition, readback, and
cleanup. Caller cancellation and existing capture limitations pass through
unchanged. No exception argument, window title, secret, image, or audio is
logged. This stage instrumentation is diagnostic evidence, not a capture fix.

The strongest source hypothesis is the legacy `Marshal.GetIUnknownForObject`
surface unwrapping in `NativeCaptureDevice.ReadJpeg`; the pinned C#/WinRT guide
instead specifies native-object access. Display-item or graphics-device
projection conversion is a competing explanation until the stage is observed.
See `capture-com-ownership-followup.md` for the corrected source review.

The independent ownership review also found deterministic resource-disposal
gaps. They are separate from the exact current cast failure and must not be
claimed as a measured leak or repaired by speculative extra COM releases.

Candidate7 has now run. Its fixed stage reports `read the captured screen
frame` with the same InvalidCastException/HRESULT. Display item, device,
frame-pool, session, and frame acquisition completed sufficiently to reach
that stage. This narrows the failure to frame.Surface/readback, without
measuring the wrapper's exact runtime identity. The proposed supported native
surface-pointer path passed independent plan review. Candidate7's receipt and
package are preserved as the failing regression evidence.
