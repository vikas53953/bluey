# Native surface readback repair build

- Baseline source commit: `f82c17dabab4129c9804c11d1861f2fbfde3a02a`
- Scope: `src/Boo.Windows/NativeCaptureDevice.cs` only, plus this receipt.
- Source SHA-256 after repair: `161D7DE5A36D75958867C1BA9CE481B53DD6BBA9AE5FF33837EE41DFFB502839`

## Build

- Restore: `scripts/build.ps1 -Action restore -Project src/Boo.Windows/Boo.Windows.csproj` — PASS.
- Release build: `scripts/build.ps1 -Action build -Configuration Release -Project src/Boo.Windows/Boo.Windows.csproj -NoRestore` — PASS.
- Build result: 0 warnings, 0 errors.
- Built assembly: `src/Boo.Windows/bin/Release/net10.0-windows10.0.19041.0/Boo.Windows.dll`
- Built assembly SHA-256: `722C44D73B06E1DAEA92E38E5D7BD9A6EB82003CCC4B860660BA9FAABAFC983B`

## Repair and ownership

The projected surface now unwraps through `IWinRTObject.NativeObject.GetRef()`,
queries `IDirect3DDxgiInterfaceAccess` with native QI, and invokes vtable slot 3
with the requested StdCall HRESULT ABI. The QI helper releases a nonzero failed
output before reporting failure. A nonzero failed texture output is likewise
released before the capture limitation is raised. The GetRef pointer, access
interface, source texture, and staging texture each have one owning finally
scope. Device disposal and activation-factory ownership were not changed.

## Verification boundary

This scoped executor build did not run native WGC capture, provider requests,
GUI interaction, or tests. Candidate7's actual projected-surface readback RED
(`InvalidCastException`, `0x80004002`) remains the regression target; root owns
candidate packaging and native verification.
