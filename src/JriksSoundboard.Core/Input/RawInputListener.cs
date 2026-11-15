using System.ComponentModel;
using System.Runtime.InteropServices;
using static JriksSoundboard.Core.Input.NativeMethods;

namespace JriksSoundboard.Core.Input;

/// <summary>
/// Passively observes keyboard and mouse input system-wide using Windows Raw Input with
/// RIDEV_INPUTSINK — the same read-only mechanism voice/streaming apps use for global hotkeys.
/// It cannot block, modify or send input, and it never touches the game process.
/// <para>
/// All events are raised on the listener's own background thread, not the UI thread.
/// Buttons are identified by Windows virtual-key codes; mouse buttons use VK 1/2/4/5/6
/// (see <see cref="VirtualKeys"/>).
/// </para>
/// </summary>
public sealed class RawInputListener : IDisposable
{
    private readonly bool[] pressed = new bool[256];
    private readonly ManualResetEventSlim ready = new();
    private Thread? thread;
    private IntPtr hwnd;
    private string? className;
    private IntPtr hInstance;
    private WndProc? wndProc; // must stay referenced while the window exists
    private Exception? startError;
    private IntPtr buffer;
    private uint bufferSize;
    private int disposed;

    /// <summary>A key or mouse button went down (auto-repeat is filtered out).</summary>
    public event Action<int>? ButtonDown;

    /// <summary>A key or mouse button was released.</summary>
    public event Action<int>? ButtonUp;

    /// <summary>Relative mouse movement in raw counts (DPI-dependent, unaffected by Windows pointer speed).</summary>
    public event Action<int, int>? MouseMoved;

    /// <summary>An event handler threw. The listener keeps running.</summary>
    public event Action<Exception>? HandlerError;

    public bool IsRunning => thread is not null && startError is null && Volatile.Read(ref disposed) == 0;

    /// <summary>Whether a button is currently held (safe to call from any thread).</summary>
    public bool IsDown(int virtualKey) =>
        virtualKey is >= 0 and < 256 && Volatile.Read(ref pressed[virtualKey]);

    /// <summary>Creates the hidden window and registers for raw input. Throws if Windows refuses.</summary>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (thread is not null)
        {
            throw new InvalidOperationException("Listener already started.");
        }

        thread = new Thread(ThreadMain) { IsBackground = true, Name = "RawInputListener" };
        thread.Start();
        ready.Wait();

        if (startError is not null)
        {
            thread.Join();
            throw new InvalidOperationException("Could not start the raw input listener: " + startError.Message, startError);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        if (thread is not null && hwnd != IntPtr.Zero)
        {
            PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            thread.Join(TimeSpan.FromSeconds(2));
        }
        ready.Dispose();
    }

    private void ThreadMain()
    {
        try
        {
            CreateWindowAndRegister();
        }
        catch (Exception ex)
        {
            startError = ex;
            Cleanup();
            ready.Set();
            return;
        }

        ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Cleanup();
    }

    private void CreateWindowAndRegister()
    {
        wndProc = WindowProc;
        hInstance = GetModuleHandle(null);
        className = "JriksSoundboard.RawInput." + Guid.NewGuid().ToString("N");

        var wc = new WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProc),
            hInstance = hInstance,
            lpszClassName = className,
        };
        if (RegisterClassEx(ref wc) == 0)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterClassEx failed");
        }

        // Message-only window: invisible, never focusable, exists only to receive WM_INPUT.
        hwnd = CreateWindowEx(0, className, string.Empty, 0, 0, 0, 0, 0, HWND_MESSAGE, IntPtr.Zero, hInstance, IntPtr.Zero);
        if (hwnd == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateWindowEx failed");
        }

        RAWINPUTDEVICE[] devices =
        [
            new() { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_KEYBOARD, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
            new() { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_INPUTSINK, hwndTarget = hwnd },
        ];
        if (!RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>()))
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), "RegisterRawInputDevices failed");
        }
    }

    private void Cleanup()
    {
        if (hwnd != IntPtr.Zero)
        {
            RAWINPUTDEVICE[] remove =
            [
                new() { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_KEYBOARD, dwFlags = RIDEV_REMOVE },
                new() { usUsagePage = HID_USAGE_PAGE_GENERIC, usUsage = HID_USAGE_GENERIC_MOUSE, dwFlags = RIDEV_REMOVE },
            ];
            RegisterRawInputDevices(remove, (uint)remove.Length, (uint)Marshal.SizeOf<RAWINPUTDEVICE>());
            DestroyWindow(hwnd);
            hwnd = IntPtr.Zero;
        }
        if (className is not null)
        {
            UnregisterClass(className, hInstance);
            className = null;
        }
        if (buffer != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(buffer);
            buffer = IntPtr.Zero;
            bufferSize = 0;
        }
    }

    private IntPtr WindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_INPUT:
                try
                {
                    HandleRawInput(lParam);
                }
                catch (Exception ex)
                {
                    // Never let an exception unwind into native code (it would kill the process).
                    RaiseHandlerError(ex);
                }
                break; // WM_INPUT must still go to DefWindowProc so Windows can clean up.

            case WM_CLOSE:
                DestroyWindow(hWnd);
                hwnd = IntPtr.Zero;
                return IntPtr.Zero;

            case WM_DESTROY:
                PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void HandleRawInput(IntPtr hRawInput)
    {
        uint headerSize = (uint)Marshal.SizeOf<RAWINPUTHEADER>();
        uint size = 0;
        GetRawInputData(hRawInput, RID_INPUT, IntPtr.Zero, ref size, headerSize);
        if (size == 0)
        {
            return;
        }

        if (size > bufferSize)
        {
            if (buffer != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(buffer);
            }
            buffer = Marshal.AllocHGlobal((int)size);
            bufferSize = size;
        }

        if (GetRawInputData(hRawInput, RID_INPUT, buffer, ref size, headerSize) != size)
        {
            return;
        }

        var header = Marshal.PtrToStructure<RAWINPUTHEADER>(buffer);
        IntPtr data = buffer + (int)headerSize;

        if (header.dwType == RIM_TYPEMOUSE)
        {
            HandleMouse(Marshal.PtrToStructure<RAWMOUSE>(data));
        }
        else if (header.dwType == RIM_TYPEKEYBOARD)
        {
            HandleKeyboard(Marshal.PtrToStructure<RAWKEYBOARD>(data));
        }
    }

    private void HandleMouse(RAWMOUSE m)
    {
        // Absolute positioning comes from tablets / remote desktop; the wheel needs relative deltas.
        if ((m.usFlags & MOUSE_MOVE_ABSOLUTE) == 0 && (m.lLastX != 0 || m.lLastY != 0))
        {
            Raise(MouseMoved, m.lLastX, m.lLastY);
        }

        ushort f = m.usButtonFlags;
        if (f == 0)
        {
            return;
        }

        MouseButton(f, RI_MOUSE_LEFT_BUTTON_DOWN, RI_MOUSE_LEFT_BUTTON_UP, VirtualKeys.MouseLeft);
        MouseButton(f, RI_MOUSE_RIGHT_BUTTON_DOWN, RI_MOUSE_RIGHT_BUTTON_UP, VirtualKeys.MouseRight);
        MouseButton(f, RI_MOUSE_MIDDLE_BUTTON_DOWN, RI_MOUSE_MIDDLE_BUTTON_UP, VirtualKeys.MouseMiddle);
        MouseButton(f, RI_MOUSE_BUTTON_4_DOWN, RI_MOUSE_BUTTON_4_UP, VirtualKeys.Mouse4);
        MouseButton(f, RI_MOUSE_BUTTON_5_DOWN, RI_MOUSE_BUTTON_5_UP, VirtualKeys.Mouse5);
    }

    private void MouseButton(ushort flags, ushort downFlag, ushort upFlag, int vk)
    {
        if ((flags & downFlag) != 0)
        {
            SetState(vk, down: true);
        }
        if ((flags & upFlag) != 0)
        {
            SetState(vk, down: false);
        }
    }

    private void HandleKeyboard(RAWKEYBOARD k)
    {
        int vk = k.VKey;
        if (vk is 0 or 0xFF) // 0xFF = fake key Windows inserts in escaped sequences
        {
            return;
        }

        vk = VirtualKeys.Normalize(vk, k.MakeCode, (k.Flags & RI_KEY_E0) != 0);
        SetState(vk, down: (k.Flags & RI_KEY_BREAK) == 0);
    }

    private void SetState(int vk, bool down)
    {
        if (vk is < 0 or >= 256)
        {
            return;
        }

        bool wasDown = pressed[vk];
        if (down == wasDown)
        {
            return; // key auto-repeat, or an up for a key that went down before we started
        }

        Volatile.Write(ref pressed[vk], down);
        Raise(down ? ButtonDown : ButtonUp, vk);
    }

    private void Raise(Action<int>? handler, int vk)
    {
        try
        {
            handler?.Invoke(vk);
        }
        catch (Exception ex)
        {
            RaiseHandlerError(ex);
        }
    }

    private void Raise(Action<int, int>? handler, int dx, int dy)
    {
        try
        {
            handler?.Invoke(dx, dy);
        }
        catch (Exception ex)
        {
            RaiseHandlerError(ex);
        }
    }

    private void RaiseHandlerError(Exception ex)
    {
        try
        {
            HandlerError?.Invoke(ex);
        }
        catch
        {
            // Last line of defence: an error handler that throws must not crash the input thread.
        }
    }
}
