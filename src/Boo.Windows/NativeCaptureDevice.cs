using System.Runtime.InteropServices;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Windows.Graphics.DirectX.Direct3D11;
using WinRT;

namespace Boo.Windows;

internal sealed class NativeCaptureDevice : IDisposable
{
    private const uint D3d11CreateDeviceBgraSupport = 0x20;
    private const uint D3d11SdkVersion = 7;
    private const uint D3d11DriverTypeHardware = 1;
    private const uint D3d11DriverTypeWarp = 5;
    private const int D3d11UsageStaging = 3;
    private const uint D3d11CpuAccessRead = 0x20000;
    private const int D3d11MapRead = 1;
    private const int DxgiFormatB8G8R8A8Unorm = 87;
    private const int D3d11Texture2DDescVtableIndex = 10;
    private const int D3d11DeviceCreateTexture2DVtableIndex = 5;
    private const int D3d11ContextCopyResourceVtableIndex = 47;
    private const int D3d11ContextMapVtableIndex = 14;
    private const int D3d11ContextUnmapVtableIndex = 15;
    private const int DxgiInterfaceAccessGetInterfaceVtableIndex = 3;

    private static readonly Guid DxgiDeviceGuid = new("54EC77FA-1377-44E6-8C32-88FD5F44C84C");
    private static readonly Guid DxgiInterfaceAccessGuid = new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");
    private static readonly Guid D3d11Texture2DGuid = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");

    private readonly IntPtr _devicePointer;
    private readonly IntPtr _contextPointer;
    private bool _disposed;

    public IDirect3DDevice Device { get; }

    private NativeCaptureDevice(IntPtr devicePointer, IntPtr contextPointer, IDirect3DDevice device)
    {
        _devicePointer = devicePointer;
        _contextPointer = contextPointer;
        Device = device;
    }

    public static NativeCaptureDevice Create()
    {
        var hr = D3D11CreateDevice(
            IntPtr.Zero,
            D3d11DriverTypeHardware,
            IntPtr.Zero,
            D3d11CreateDeviceBgraSupport,
            IntPtr.Zero,
            0,
            D3d11SdkVersion,
            out var devicePointer,
            out _,
            out var contextPointer);

        if (hr < 0)
        {
            if (devicePointer != IntPtr.Zero)
            {
                Marshal.Release(devicePointer);
            }

            if (contextPointer != IntPtr.Zero)
            {
                Marshal.Release(contextPointer);
            }

            hr = D3D11CreateDevice(
                IntPtr.Zero,
                D3d11DriverTypeWarp,
                IntPtr.Zero,
                D3d11CreateDeviceBgraSupport,
                IntPtr.Zero,
                0,
                D3d11SdkVersion,
                out devicePointer,
                out _,
                out contextPointer);
        }

        if (hr < 0 || devicePointer == IntPtr.Zero || contextPointer == IntPtr.Zero)
        {
            throw new CaptureLimitationException($"Windows could not create the Direct3D 11 capture device (HRESULT 0x{hr:X8}).");
        }

        try
        {
            var dxgiDevice = QueryInterface(devicePointer, DxgiDeviceGuid);
            try
            {
                var winRtHr = CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice, out var inspectablePointer);
                if (winRtHr != 0 || inspectablePointer == IntPtr.Zero)
                {
                    throw new CaptureLimitationException($"Windows could not bridge the Direct3D 11 device to Windows Graphics Capture (HRESULT 0x{winRtHr:X8}).");
                }

                try
                {
                    // The bridge returns an IInspectable ABI pointer. Use the
                    // generated C#/WinRT interface marshaler rather than a raw
                    // __ComObject cast so the projected IDirect3DDevice is
                    // valid on .NET 10.
                    var device = MarshalInterface<IDirect3DDevice>.FromAbi(inspectablePointer);
                    return new NativeCaptureDevice(devicePointer, contextPointer, device);
                }
                finally
                {
                    Marshal.Release(inspectablePointer);
                }
            }
            finally
            {
                Marshal.Release(dxgiDevice);
            }
        }
        catch
        {
            Marshal.Release(contextPointer);
            Marshal.Release(devicePointer);
            throw;
        }
    }

    public byte[] ReadJpeg(IDirect3DSurface surface, int width, int height)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(surface);
        if (width <= 0 || height <= 0)
        {
            throw new CaptureLimitationException("The captured frame dimensions are invalid.");
        }

        if (surface is not IWinRTObject winRtSurface || !winRtSurface.HasUnwrappableNativeObject)
        {
            throw new CaptureLimitationException("Windows Graphics Capture returned a surface that Boo cannot unwrap for native readback.");
        }

        IntPtr surfaceUnknown = IntPtr.Zero;
        IntPtr accessPointer = IntPtr.Zero;
        IntPtr sourceTexture = IntPtr.Zero;
        IntPtr stagingTexture = IntPtr.Zero;
        try
        {
            surfaceUnknown = winRtSurface.NativeObject.GetRef();
            if (surfaceUnknown == IntPtr.Zero)
            {
                throw new CaptureLimitationException("Windows Graphics Capture returned a surface without a native object for readback.");
            }

            accessPointer = QueryInterface(surfaceUnknown, DxgiInterfaceAccessGuid);
            var textureInterface = D3d11Texture2DGuid;
            var getInterfaceVtable = GetVtableEntry(accessPointer, DxgiInterfaceAccessGetInterfaceVtableIndex);
            var getInterface = Marshal.GetDelegateForFunctionPointer<GetInterfaceDelegate>(getInterfaceVtable);
            var getInterfaceHr = getInterface(accessPointer, in textureInterface, out sourceTexture);
            if (getInterfaceHr < 0 || sourceTexture == IntPtr.Zero)
            {
                if (sourceTexture != IntPtr.Zero)
                {
                    Marshal.Release(sourceTexture);
                    sourceTexture = IntPtr.Zero;
                }

                if (getInterfaceHr < 0)
                {
                    throw new CaptureLimitationException($"Windows Graphics Capture did not expose a Direct3D texture for the frame (HRESULT 0x{getInterfaceHr:X8}).");
                }

                throw new CaptureLimitationException("Windows Graphics Capture did not expose a Direct3D texture for the frame.");
            }

            var description = ReadTextureDescription(sourceTexture);
            if (description.Width != width || description.Height != height || description.Format != DxgiFormatB8G8R8A8Unorm)
            {
                throw new CaptureLimitationException("Windows Graphics Capture returned a frame format or size that Boo cannot encode safely.");
            }

            description.Usage = D3d11UsageStaging;
            description.BindFlags = 0;
            description.CpuAccessFlags = D3d11CpuAccessRead;
            description.MiscFlags = 0;

            stagingTexture = CreateTexture2D(description);
            CopyResource(stagingTexture, sourceTexture);
            var pixels = ReadMappedTexture(stagingTexture, width, height);
            return EncodeJpeg(pixels, width, height);
        }
        finally
        {
            if (stagingTexture != IntPtr.Zero)
            {
                Marshal.Release(stagingTexture);
            }

            if (sourceTexture != IntPtr.Zero)
            {
                Marshal.Release(sourceTexture);
            }

            if (accessPointer != IntPtr.Zero)
            {
                Marshal.Release(accessPointer);
            }

            if (surfaceUnknown != IntPtr.Zero)
            {
                Marshal.Release(surfaceUnknown);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Marshal.Release(_contextPointer);
        Marshal.Release(_devicePointer);
        GC.SuppressFinalize(this);
    }

    private static D3d11Texture2DDescription ReadTextureDescription(IntPtr texture)
    {
        var vtable = GetVtableEntry(texture, D3d11Texture2DDescVtableIndex);
        var getDescription = Marshal.GetDelegateForFunctionPointer<GetTextureDescription>(vtable);
        getDescription(texture, out var description);
        return description;
    }

    private IntPtr CreateTexture2D(D3d11Texture2DDescription description)
    {
        var vtable = GetVtableEntry(_devicePointer, D3d11DeviceCreateTexture2DVtableIndex);
        var create = Marshal.GetDelegateForFunctionPointer<CreateTexture2DDelegate>(vtable);
        var hr = create(_devicePointer, ref description, IntPtr.Zero, out var texture);
        if (hr < 0 || texture == IntPtr.Zero)
        {
            throw new CaptureLimitationException($"Direct3D could not create a CPU-readable frame texture (HRESULT 0x{hr:X8}).");
        }

        return texture;
    }

    private void CopyResource(IntPtr destination, IntPtr source)
    {
        var vtable = GetVtableEntry(_contextPointer, D3d11ContextCopyResourceVtableIndex);
        var copy = Marshal.GetDelegateForFunctionPointer<CopyResourceDelegate>(vtable);
        copy(_contextPointer, destination, source);
    }

    private byte[] ReadMappedTexture(IntPtr texture, int width, int height)
    {
        var vtable = GetVtableEntry(_contextPointer, D3d11ContextMapVtableIndex);
        var map = Marshal.GetDelegateForFunctionPointer<MapDelegate>(vtable);
        var hr = map(_contextPointer, texture, 0, D3d11MapRead, 0, out var mapped);
        if (hr < 0 || mapped.Data == IntPtr.Zero)
        {
            throw new CaptureLimitationException($"Direct3D could not map the captured frame for encoding (HRESULT 0x{hr:X8}).");
        }

        try
        {
            var rowBytes = checked(width * 4);
            var pixels = new byte[checked(rowBytes * height)];
            for (var row = 0; row < height; row++)
            {
                Marshal.Copy(IntPtr.Add(mapped.Data, row * mapped.RowPitch), pixels, row * rowBytes, rowBytes);
            }

            return pixels;
        }
        finally
        {
            var unmapVtable = GetVtableEntry(_contextPointer, D3d11ContextUnmapVtableIndex);
            var unmap = Marshal.GetDelegateForFunctionPointer<UnmapDelegate>(unmapVtable);
            unmap(_contextPointer, texture, 0);
        }
    }

    private static byte[] EncodeJpeg(byte[] pixels, int width, int height)
    {
        var bitmap = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        bitmap.Freeze();
        var encoder = new JpegBitmapEncoder { QualityLevel = 90 };
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    private static IntPtr QueryInterface(IntPtr source, Guid iid)
    {
        var hr = Marshal.QueryInterface(source, in iid, out var result);
        if (hr < 0 || result == IntPtr.Zero)
        {
            if (result != IntPtr.Zero)
            {
                Marshal.Release(result);
            }

            throw new CaptureLimitationException($"Direct3D did not expose the required interface (HRESULT 0x{hr:X8}).");
        }

        return result;
    }

    private static IntPtr GetVtableEntry(IntPtr instance, int index)
    {
        if (instance == IntPtr.Zero)
        {
            throw new ArgumentException("A COM object pointer is required.", nameof(instance));
        }

        var vtable = Marshal.ReadIntPtr(instance);
        return Marshal.ReadIntPtr(vtable, index * IntPtr.Size);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SampleDescription
    {
        public uint Count;
        public uint Quality;

        public SampleDescription(uint count, uint quality)
        {
            Count = count;
            Quality = quality;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct D3d11Texture2DDescription
    {
        public int Width;
        public int Height;
        public uint MipLevels;
        public uint ArraySize;
        public int Format;
        public SampleDescription SampleDescription;
        public int Usage;
        public uint BindFlags;
        public uint CpuAccessFlags;
        public uint MiscFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MappedSubresource
    {
        public IntPtr Data;
        public int RowPitch;
        public int DepthPitch;
    }

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void GetTextureDescription(IntPtr texture, out D3d11Texture2DDescription description);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateTexture2DDelegate(IntPtr device, ref D3d11Texture2DDescription description, IntPtr initialData, out IntPtr texture);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void CopyResourceDelegate(IntPtr context, IntPtr destination, IntPtr source);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int MapDelegate(IntPtr context, IntPtr resource, uint subresource, int mapType, uint mapFlags, out MappedSubresource mapped);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate void UnmapDelegate(IntPtr context, IntPtr resource, uint subresource);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int GetInterfaceDelegate(IntPtr self, in Guid iid, out IntPtr result);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(
        IntPtr adapter,
        uint driverType,
        IntPtr software,
        uint flags,
        IntPtr featureLevels,
        uint featureLevelCount,
        uint sdkVersion,
        out IntPtr device,
        out uint featureLevel,
        out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
