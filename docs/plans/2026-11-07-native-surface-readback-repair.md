# Native screen-surface readback repair

Status: PLAN_APPROVED by independent Advisor on 2026-11-07; isolated executor
dispatched. No user approval gate; the user authorized autonomous repair and
native verification.

## Evidence and scope

Candidate7, exact source `f82c17dabab4129c9804c11d1861f2fbfde3a02a`, reached
frame readback and reported `InvalidCastException`, HRESULT `0x80004002`.
The retained-target failure is already separately repaired. The native request
and package evidence are in `docs/verification/native-teaching-candidate7.md`.

`NativeCaptureDevice.ReadJpeg` uses `Marshal.GetIUnknownForObject(surface)` and
casts the object returned by `Marshal.GetObjectForIUnknown` to a private
ComImport interface. The pinned C#/WinRT guide identifies that legacy
projected-object unwrapping as incompatible with its ComWrappers path and
specifies `IWinRTObject.NativeObject` instead. This is the documented mismatch
in the observed failing readback path. The exact runtime CCW identity has not
been measured; native verification must confirm this repair resolves that path.

Primary contracts:

- https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/docs/interop.md
- https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess-getinterface

Keep this repair in `src/Boo.Windows/NativeCaptureDevice.cs`. Do not change
capture authority, WDA exclusion, timeouts, target validity, annotation
geometry, API contracts, tests, or provider settings. The old implicit COM
HRESULT/retval declaration is valid; it is not the diagnosed defect.

## Implementation

1. Obtain a temporary owned native pointer from
   `((IWinRTObject)surface).NativeObject.GetRef()`. If the supplied surface is
   not a projected WinRT object, return a readable capture limitation; do not
   fall back to legacy Marshal conversion.
2. Query that pointer for the existing IDirect3DDxgiInterfaceAccess GUID
   `A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1` using the existing native QI helper.
3. Invoke its first method after IUnknown (vtable slot3) through a private
   StdCall delegate with native signature
   `int GetInterface(IntPtr self, in Guid iid, out IntPtr result)`. Request the
   existing ID3D11Texture2D GUID. Check failure HRESULT and zero results before
   use; a nonzero output on failure must be released before throwing.
4. Keep the existing texture validation, staging/readback, and JPEG conversion.
   Balance the GetRef pointer, QI pointer, source texture, and staging texture
   exactly once in their owning finally scopes. Do not use ReleaseComObject or
   FinalReleaseComObject on a projected object. Remove only the private
   ComImport surface interface made unused by this path.

Device.Close/Dispose and activation-factory RCW lifetime are separate known
ownership repairs. Do not bundle them into this cast repair, and do not claim a
native leak measurement. Keep the staged error messages for usable failure
reporting.

## Verification and ownership

The failure needs WGC plus an actual projected screen surface. Existing Core
and integration fake-backend tests do not exercise this chain. Use the already
written native capture/teaching acceptance cases and candidate7 RED as the
regression check. Do not invent a production hook or static-source assertion
to obtain a synthetic green result.

After technical approval, an executor works in a fresh isolated worktree,
owning only NativeCaptureDevice.cs and its build receipt. No live capture,
credentials, API, GUI, test/oracle edits, or commits in that executor. Root
inspects and integrates the result. A separate reviewer checks ABI and pointer
ownership. Root runs affected tests, full Release build, oracle, and diff check.

Publish an exact new native package. Sole Sky QA driver uses the maximized
disposable fixture, opens Boo, verifies exact typed input before submission,
and makes one screen-help request. Success requires actual captured context
reaching the provider; a different failure is partial progress only. Ring
geometry, exclusion, latency, and full teaching acceptance remain separate
checks. Preserve the old failing package and receipts. If the same cast error
remains, invalidate this hypothesis and inspect the exact failing projection
boundary rather than adding speculative releases.
