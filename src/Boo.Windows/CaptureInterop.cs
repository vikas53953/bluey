using System.Runtime.InteropServices;
using Windows.Graphics.Capture;

namespace Boo.Windows;

internal static class CaptureInterop
{
    private static readonly Guid GraphicsCaptureItemGuid = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    public static GraphicsCaptureItem CreateItemForMonitor(IntPtr monitor)
    {
        if (monitor == IntPtr.Zero)
        {
            throw new ArgumentException("A monitor handle is required.", nameof(monitor));
        }

        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hstring = WindowsCreateString(className, className.Length, out var hstringHandle);
        if (hstring != 0)
        {
            throw new CaptureLimitationException($"Windows could not create the GraphicsCaptureItem class name (HRESULT 0x{hstring:X8}).");
        }

        IntPtr factoryPointer = IntPtr.Zero;
        try
        {
            var interopGuid = typeof(IGraphicsCaptureItemInterop).GUID;
            var activation = RoGetActivationFactory(hstringHandle, in interopGuid, out factoryPointer);
            if (activation != 0 || factoryPointer == IntPtr.Zero)
            {
                throw new CaptureLimitationException($"Windows could not activate GraphicsCaptureItem (HRESULT 0x{activation:X8}).");
            }

            var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
            var itemPointer = interop.CreateForMonitor(monitor, in GraphicsCaptureItemGuid);
            if (itemPointer == IntPtr.Zero)
            {
                throw new CaptureLimitationException("Windows could not create a GraphicsCaptureItem for the selected display.");
            }

            try
            {
                // C#/WinRT's interface marshaler is required here. A raw COM
                // RCW and the generated ABI helper can remain System.__ComObject
                // for this WinRT runtime class on modern .NET.
                return WinRT.MarshalInterface<GraphicsCaptureItem>.FromAbi(itemPointer);
            }
            finally
            {
                Marshal.Release(itemPointer);
            }
        }
        finally
        {
            if (factoryPointer != IntPtr.Zero)
            {
                Marshal.Release(factoryPointer);
            }

            WindowsDeleteString(hstringHandle);
        }
    }

    [ComImport]
    [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    [ComVisible(true)]
    private interface IGraphicsCaptureItemInterop
    {
        IntPtr CreateForWindow([In] IntPtr window, [In] in Guid iid);
        IntPtr CreateForMonitor([In] IntPtr monitor, [In] in Guid iid);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString, int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, in Guid iid, out IntPtr factory);
}
