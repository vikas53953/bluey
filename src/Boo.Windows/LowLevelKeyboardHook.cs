using System.ComponentModel;
using System.Runtime.InteropServices;
using Boo.Core;

namespace Boo.Windows;

/// <summary>
/// Global WH_KEYBOARD_LL hook for the hold-to-talk chord (Ctrl+Alt+Space). Runs on its own thread with a message
/// loop so a busy UI thread can never stall system input. The handler is called on that thread for every
/// Ctrl/Alt/Space transition and returns whether to swallow the event; it must be fast (&lt;1 ms) and must marshal
/// any UI work asynchronously. Injected events (e.g. Boo's own SendInput) are ignored entirely.
/// </summary>
public sealed class LowLevelKeyboardHook : IDisposable
{
    /// <summary>Returns true to swallow the key event.</summary>
    public delegate bool KeyHandler(TalkKey key, bool down, bool isRepeat);

    private const int WhKeyboardLl = 13;
    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;
    private const int WmQuit = 0x0012;
    private const uint LlkhfInjected = 0x10;
    private const uint PmNoRemove = 0x0000;
    private const uint AltGrFakeCtrlScanCode = 0x21D;
    private const ushort MenuMaskVk = 0xE8; // unassigned virtual key (same default mask key AutoHotkey recommends)
    private const uint InputKeyboard = 1;
    private const uint KeyEventFKeyUp = 0x0002;

    private readonly KeyHandler _handler;
    private readonly HookProc _proc;
    private readonly HashSet<uint> _down = new();
    private readonly HashSet<uint> _swallowed = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private IntPtr _hook;
    private uint _threadId;
    private int _installError;
    private bool _disposed;

    public LowLevelKeyboardHook(KeyHandler handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _proc = HookCallback; // keep the delegate alive for the lifetime of the hook
        _thread = new Thread(Run) { IsBackground = true, Name = "Boo hold-to-talk hook" };
    }

    /// <summary>True once the hook is installed.</summary>
    public bool IsInstalled => _hook != IntPtr.Zero;

    /// <summary>Installs the hook. Throws <see cref="Win32Exception"/> if Windows refuses it.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _thread.Start();
        _ready.Wait();
        if (_hook == IntPtr.Zero)
        {
            throw new Win32Exception(_installError, "The hold-to-talk keyboard hook could not be installed.");
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_thread.IsAlive && _threadId != 0)
        {
            PostThreadMessage(_threadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        _ready.Dispose();
    }

    /// <summary>Maps a virtual-key code to a chord key. Left/right variants map to the same key.</summary>
    public static TalkKey? Map(uint vk) => vk switch
    {
        0x11 or 0xA2 or 0xA3 => TalkKey.Ctrl,  // VK_CONTROL, VK_LCONTROL, VK_RCONTROL
        0x12 or 0xA4 or 0xA5 => TalkKey.Alt,   // VK_MENU, VK_LMENU, VK_RMENU
        0x20 => TalkKey.Space,                 // VK_SPACE
        _ => null,
    };

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        PeekMessage(out _, IntPtr.Zero, 0, 0, PmNoRemove); // create the message queue so Dispose's WM_QUIT can't be lost
        _hook = SetWindowsHookEx(WhKeyboardLl, _proc, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero)
        {
            _installError = Marshal.GetLastWin32Error();
            _ready.Set();
            return;
        }

        _ready.Set();
        try
        {
            while (GetMessage(out var message, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        finally
        {
            UnhookWindowsHookEx(_hook);
            _hook = IntPtr.Zero;
        }
    }

    private IntPtr HookCallback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            try
            {
                var message = wParam.ToInt32();
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var isUp = message is WmKeyUp or WmSysKeyUp;
                if (isDown || isUp)
                {
                    var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                    // AltGr is reported as a fake LCtrl (scan code 0x21D) + RAlt; ignore the fake Ctrl so AltGr+Space
                    // on international layouts is not taken as Ctrl+Alt+Space.
                    var fakeCtrl = data.VkCode == 0xA2 && data.ScanCode == AltGrFakeCtrlScanCode;
                    if ((data.Flags & LlkhfInjected) == 0 && !fakeCtrl && Map(data.VkCode) is { } key && Translate(key, data.VkCode, isDown))
                    {
                        return (IntPtr)1;
                    }
                }
            }
            catch (Exception)
            {
                // Never let a failure here eat or stall system input.
            }
        }

        return CallNextHookEx(IntPtr.Zero, code, wParam, lParam);
    }

    private bool Translate(TalkKey key, uint vk, bool isDown)
    {
        ReleaseStaleKeys(vk);
        var otherVariantDown = AnyDown(key, except: vk);
        if (isDown)
        {
            // LL hooks don't flag auto-repeat: a down for a key that is already down is a repeat. Pressing the
            // second Ctrl while the first is held is the same logical key, so it is reported as a repeat too.
            var fresh = _down.Add(vk);
            var isRepeat = !fresh || otherVariantDown;
            var swallow = _handler(key, true, isRepeat);
            if (swallow)
            {
                _swallowed.Add(vk);
                if (fresh && AnyDown(TalkKey.Alt, except: uint.MaxValue))
                {
                    // The foreground app saw Alt go down but will never see the swallowed key, so releasing Alt
                    // would open its menu bar. A harmless injected key in between cancels that menu activation.
                    SendMenuMask();
                }
            }
            return swallow;
        }

        _down.Remove(vk);
        _swallowed.Remove(vk);
        return !otherVariantDown && _handler(key, false, false);
    }

    /// <summary>
    /// Key-ups can be missed (Ctrl+Alt+Del and Win+L switch to the secure desktop, where LL hooks see nothing).
    /// A key we let through updates the async key state, so if Windows says it is up we report the missed up now;
    /// otherwise the next real press would look like a repeat and the chord would never fire again.
    /// Swallowed keys never reach the key state, so they are left alone.
    /// </summary>
    private void ReleaseStaleKeys(uint current)
    {
        List<uint>? stale = null;
        foreach (var vk in _down)
        {
            if (vk != current && !_swallowed.Contains(vk) && (GetAsyncKeyState((int)vk) & 0x8000) == 0)
            {
                (stale ??= new List<uint>()).Add(vk);
            }
        }

        if (stale is null)
        {
            return;
        }

        foreach (var vk in stale)
        {
            _down.Remove(vk);
            if (Map(vk) is { } key && !AnyDown(key, except: vk))
            {
                _handler(key, false, false);
            }
        }
    }

    private static void SendMenuMask()
    {
        var inputs = new[]
        {
            new Input { Type = InputKeyboard, U = new InputUnion { Ki = new KeybdInput { Vk = MenuMaskVk } } },
            new Input { Type = InputKeyboard, U = new InputUnion { Ki = new KeybdInput { Vk = MenuMaskVk, Flags = KeyEventFKeyUp } } },
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
    }

    private bool AnyDown(TalkKey key, uint except)
    {
        foreach (var vk in _down)
        {
            if (vk != except && Map(vk) == key)
            {
                return true;
            }
        }

        return false;
    }

    private delegate IntPtr HookProc(int code, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint VkCode;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeybdInput
    {
        public ushort Vk;
        public ushort Scan;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    // INPUT is a tagged union whose largest member is MOUSEINPUT; the pad keeps Marshal.SizeOf equal to
    // sizeof(INPUT) (40 bytes on x64, 28 on x86), otherwise SendInput rejects the call.
    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputUnion U;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)]
        public KeybdInput Ki;

        [FieldOffset(0)]
        public MouseInputPad Pad;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInputPad
    {
        public int Dx;
        public int Dy;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public UIntPtr ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public IntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int X;
        public int Y;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage(ref Msg lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PeekMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax, uint wRemoveMsg);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, Input[] pInputs, int cbSize);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
