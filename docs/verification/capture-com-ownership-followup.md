# Capture COM/WinRT ownership follow-up

Date: 2026-11-07  
Scope: narrow follow-up to the original ownership receipt. No implementation,
test, capture, GUI, or native pointer probe was run.

## Runtime status boundary

The current candidate-4 observation is **NULL TARGET BEFORE WGC**. There is no
observed COM cast failure, HRESULT, pointer identity, or native leak measurement.
The conclusions below are source/projection evidence and proposed implementation
direction; they do not attribute candidate 4's current failure to COM interop.

## Confirmed projected-device ownership

The local Microsoft.Windows.SDK.NET projection reports
IDirect3DDevice as System.IDisposable. Microsoft documents that the WinRT
interface implements IClosable and that .NET exposes IClosable as IDisposable:

- <https://learn.microsoft.com/en-us/uwp/api/windows.graphics.directx.direct3d11.idirect3ddevice?view=winrt-28000>
- <https://learn.microsoft.com/en-us/uwp/api/windows.foundation.iclosable?view=winrt-28000>

Therefore NativeCaptureDevice.Device is disposable. Device.Dispose() is the
projected IClosable.Close operation; it is not an additional raw
Marshal.Release on the native ID3D11Device pointer. It must be called only after
the capture pool/session and any other user have stopped using that projected
device. It is separate from releasing the raw D3D device and context pointers.

The pinned CsWinRT FromAbi path creates an owning projected reference. It does not
consume the caller's inspectablePointer. Releasing inspectablePointer once after
FromAbi is therefore correct, while the stored projected Device needs its own
deterministic Dispose when NativeCaptureDevice owns the end of its lifetime:

- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/Marshalers.cs#L1577-L1585>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/Marshalers.cs#L1797-L1831>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L288-L293>

This supports a real source-level deterministic-disposal gap in
NativeCaptureDevice.Dispose: the current method releases raw pointers but does
not dispose Device. It does not justify FinalReleaseComObject or a second raw
release. Shared projected ownership and teardown ordering still require the
capture owner to establish that no active pool/session uses Device.

## Surface pointer provenance

ReadJpeg currently calls Marshal.GetIUnknownForObject(surface) where surface is
a C#/WinRT projected IDirect3DSurface. The pinned CsWinRT interop guidance says
legacy Marshal RCW/CCW operations should not be used to unwrap projected objects;
the supported native access is IWinRTObject.NativeObject, with GetRef() when a
new owned native reference is required:

- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/docs/interop.md>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L288-L293>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L318-L374>

Accordingly, the supported proposed direction is:

1. Obtain one temporary owned pointer from
   IWinRTObject.NativeObject.GetRef(). That reference has one AddRef and must be
   released exactly once, or transferred to an owning ObjectReference and
   disposed.
2. Query that native pointer for IDirect3DDxgiInterfaceAccess and release the
   resulting QI pointer exactly once.
3. Invoke the known native interface ABI directly, or through an owning
   ObjectReference/vtable wrapper, and release the GetInterface result as today.

This corrects the source-level pointer provenance risk: the current
GetIUnknownForObject call does not establish that the pointer passed to the
private interface is the underlying native surface identity. It may be a legacy
wrapper. The exact CCW/native identity and any resulting cast failure remain
unresolved without the prohibited runtime probe.

AsInterface<T> for a ComImport interface is a supported CsWinRT mechanism, but
the pinned implementation QIs and then uses Marshal.GetObjectForIUnknown, which
returns a classic ComImport RCW before the temporary QI pointer is released.
Thus AsInterface<ComImport> is an alternative with its own RCW lifetime; it does
not by itself prove or remove the current surface provenance risk. GetRef plus
QI/direct ABI is the preferred proposed direction for this path.

## ABI non-issue

The current IntPtr-returning ComImport declarations for
IDirect3DDxgiInterfaceAccess.GetInterface and
IGraphicsCaptureItemInterop.CreateForMonitor/CreateForWindow are not established
ABI defects. .NET's default COM signature translation hides HRESULT and maps a
final [out, retval] pointer to the managed return. Microsoft documents this
translation and the corresponding native signatures:

- <https://learn.microsoft.com/en-us/dotnet/standard/native-interop/preserve-sig>
- <https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.directx.direct3d11.interop/nf-windows-graphics-directx-direct3d11-interop-idirect3ddxgiinterfaceaccess-getinterface>
- <https://learn.microsoft.com/en-us/windows/win32/api/windows.graphics.capture.interop/nf-windows-graphics-capture-interop-igraphicscaptureiteminterop-createformonitor>

If a future implementation uses a direct vtable call, it should spell the native
shape explicitly as HRESULT plus out pointer and check the HRESULT. That is an
implementation choice, not a finding against the current implicit ComImport
translation.

## Surface close remains an ownership question

Microsoft also projects IDirect3DSurface as System.IDisposable/IClosable:

- <https://learn.microsoft.com/en-us/uwp/api/windows.graphics.directx.direct3d11.idirect3dsurface?view=winrt-28000>

WindowsFrameCaptureBackend passes frame.Surface inline and disposes the frame.
A proposed deterministic owner could retain the projected surface for the
ReadJpeg scope and dispose it after readback, before frame teardown. This review
does not establish whether frame disposal already closes that exact projected
reference, so it is not reported as a confirmed runtime leak or candidate-4
failure.

