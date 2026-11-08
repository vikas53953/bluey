# Capture COM/WinRT ownership review

Date: 2026-11-07  
Workspace: `C:\Users\vikasmit\Downloads\blue--fo`  
Scope: read-only source and local projection inspection. No capture, GUI, live-AI,
credential, or native soak probe was run.

## Local projection facts

- `global.json` pins the SDK to `.NET 10.0.403`; `Boo.Windows` targets
  `net10.0-windows10.0.19041.0`.
- The target imports the implicit Microsoft Windows SDK framework reference. The
  local targeting pack is `Microsoft.Windows.SDK.NET.Ref 10.0.19041.57`; its
  `data/FrameworkList.xml` identifies `WinRT.Runtime.dll` assembly version
  `2.2.0.0`, file `2.2.0.48161`.
- Reflection of the local `Microsoft.Windows.SDK.NET.dll` reports
  `Windows.Graphics.DirectX.Direct3D11.IDirect3DDevice` as an interface inheriting
  `System.IDisposable`. `GraphicsCaptureItem` implements `WinRT.IWinRTObject` but
  does not implement `System.IDisposable`; the local projection has no
  `Windows.Foundation.IClosable` type.

The CsWinRT 2.2.0 source is the relevant ownership contract. Its interop guide
states that `ObjectReference<T>.Attach` transfers a pointer without `AddRef`, while
`ObjectReference<T>.FromAbi` creates a new reference with `AddRef`:

- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/docs/interop.md#ref-counting>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L542-L600>

`IObjectReference.Dispose` releases that owned pointer, and the finalizer is the
fallback if the projection is not disposed:

- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L145-L148>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L318-L393>

## Path-by-path findings

### 1. `CaptureInterop` activation-factory RCW — confirmed deterministic-release gap

Source: `src/Boo.Windows/CaptureInterop.cs:24-35,53-60`.

`RoGetActivationFactory` returns `factoryPointer` with a caller-owned COM
reference. The existing `Marshal.Release(factoryPointer)` correctly balances that
pointer. However, `Marshal.GetObjectForIUnknown(factoryPointer)` also creates a
classic RCW and increments the native reference; the local `interop` RCW is never
released explicitly. Microsoft documents that this reference otherwise remains
until GC:

- <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.getobjectforiunknown?view=net-10.0>

This is a real source-level lifetime gap, not proof that native memory grows by a
specific amount. The narrow repair direction is to balance this private, local RCW
with exactly one `Marshal.ReleaseComObject(interop)` in an inner `finally`, while
retaining the separate `Marshal.Release(factoryPointer)`. Do not use
`FinalReleaseComObject`: Microsoft warns that it can invalidate another managed
reference sharing the RCW. A single `ReleaseComObject` balances this method's one
RCW acquisition; it must not be used on the returned `GraphicsCaptureItem`.

- <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.marshal.releasecomobject?view=net-10.0>

The longer-term C#/WinRT direction is a generated/projection interop interface or
direct ABI call. The current private `[ComImport]` interface is supported by the
CLR, so a one-for-one local RCW release is the bounded repair.

### 2. `ReadJpeg` `IDirect3DDxgiInterfaceAccess` RCW — confirmed deterministic-release gap

Source: `src/Boo.Windows/NativeCaptureDevice.cs:133-175`.

`Marshal.GetIUnknownForObject(surface)` adds one pointer reference and the existing
`Marshal.Release(surfaceUnknown)` correctly balances it. The subsequent
`Marshal.GetObjectForIUnknown(surfaceUnknown)` creates a separate classic RCW for
`access`; no code releases that RCW. `sourceTexture` (returned by
`GetInterface`), `stagingTexture`, and `surfaceUnknown` each have matching releases.

The bounded repair is an `access`-local `finally` that calls exactly one
`Marshal.ReleaseComObject(access)` after the last ABI call, then releases
`surfaceUnknown` as today. Do not call `FinalReleaseComObject`; do not call an
additional `Marshal.Release(surfaceUnknown)` to try to release the RCW. Both the
official .NET guidance and the C#/WinRT source show why these are distinct
references:

- <https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices/marshal.getobjectforiunknown?view=net-10.0>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/ObjectReference.cs#L161-L174>

`IObjectReference.AsInterface<T>` itself uses `QueryInterface`,
`Marshal.GetObjectForIUnknown`, and then releases only the temporary QI pointer;
the returned ComImport RCW still needs its own lifetime treatment. Therefore merely
rewriting this cast as `AsInterface<IDirect3DDxgiInterfaceAccess>()` would not by
itself fix the lifetime gap.

### 3. Projected `IDirect3DDevice` — confirmed missing deterministic disposal

Source: `src/Boo.Windows/NativeCaptureDevice.cs:88-109,178-188`.

`CreateDirect3D11DeviceFromDXGIDevice` returns an owned `IInspectable` pointer.
`MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePointer)` creates the
projected object and an independent native reference; the existing
`Marshal.Release(inspectablePointer)` correctly drops only the bridge's returned
ABI pointer. It must remain.

The stored `Device` is a projected interface that locally inherits
`System.IDisposable`. `NativeCaptureDevice.Dispose` currently releases only the raw
`ID3D11Device*` and immediate-context pointers, so the projected device reference
is left to its finalizer. The real deterministic repair is to call `Device.Dispose()`
once in `NativeCaptureDevice.Dispose`, before releasing the raw device/context (or
in a `try/finally` that guarantees both raw releases). Do not call
`Marshal.Release` on the `inspectablePointer` a second time, and do not apply
`FinalReleaseComObject` to the projected device.

The pinned CsWinRT implementation shows that the `MarshalInterface<T>` interface
path delegates to `MarshalInspectable<T>.FromAbi`, which creates the projected RCW,
while `IObjectReference.Dispose` performs the native release:

- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/Marshalers.cs#L1535-L1585>
- <https://github.com/microsoft/CsWinRT/blob/2.2.0.241111.1/src/WinRT.Runtime/Marshalers.cs#L1797-L1833>

### 4. Projected `GraphicsCaptureItem` — ownership is understood; use lifetime is
separate from the two RCW defects

Source: `src/Boo.Windows/CaptureInterop.cs:41-50` and
`src/Boo.Windows/ScreenCapture.cs:52-65`.

`MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer)` owns an independent
C#/WinRT reference, so the current `Marshal.Release(itemPointer)` is correct and
must remain. `GraphicsCaptureItem` has no public `IDisposable` surface in the local
projection; its `IWinRTObject.NativeObject` is an `IObjectReference` with the normal
finalizer fallback. It is therefore not correct to add a raw `Marshal.Release` for
the returned managed item or to treat the item as a classic RCW.

If strict per-capture teardown is required, the owning capture scope may dispose
`((IWinRTObject)item).NativeObject` only after the capture session and frame pool
have been disposed and no item member is used. That ordering is not proven by this
read-only review and should be implemented and tested by the capture owner. Until
then, item finalization is safe GC ownership, while a measurable native growth
claim remains unproven.

## Correctly balanced paths / non-issues

- `factoryPointer`, `itemPointer`, `inspectablePointer`, `dxgiDevice`,
  `surfaceUnknown`, `sourceTexture`, `stagingTexture`, and the raw D3D device/context
  each have an evident matching release for the ABI reference acquired at that
  point.
- `FromAbi` does not consume the caller's ABI pointer. Releasing the ABI pointer
  after conversion is required and is not a double release; the projection owns its
  own `AddRef` reference.
- `NativeCaptureDevice.Device` is not a WinRT `IClosable` object in the local
  metadata. Its `System.IDisposable` is the C#/WinRT projection's reference-lifetime
  API and is the correct deterministic release call.
- No source-only review can establish a native handle/device-memory leak rate,
  device-removal behavior, or 15-minute soak result. Those remain native proof
  gaps, not reasons to add speculative `FinalReleaseComObject` or extra raw
  `Marshal.Release` calls.

## Handoff

Confirmed implementation targets before any native acceptance work:

1. Release the private activation-factory RCW once (`ReleaseComObject`, not
   `FinalReleaseComObject`) while retaining its pointer release.
2. Release the private `IDirect3DDxgiInterfaceAccess` RCW once, independently of
   `surfaceUnknown`.
3. Dispose the projected `IDirect3DDevice` once in `NativeCaptureDevice.Dispose`,
   independently of raw device/context release.

The returned `GraphicsCaptureItem` needs an explicit post-session
`IWinRTObject.NativeObject.Dispose()` decision only if deterministic item teardown
is a requirement; it must not be handled as a classic RCW.
