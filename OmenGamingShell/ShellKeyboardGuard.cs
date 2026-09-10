using System.Diagnostics;
using System.Runtime.InteropServices;

namespace OmenGamingShell;

public sealed class ShellKeyboardGuard : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int VkLeftWindows = 0x5B;
    private const int VkRightWindows = 0x5C;
    private const int VkTab = 0x09;
    private const int VkMenu = 0x12;
    private const int VkLeftMenu = 0xA4;
    private const int VkRightMenu = 0xA5;
    private readonly IntPtr _windowHandle;
    private readonly HookProcedure _procedure;
    private IntPtr _hook;
    private bool _windowsKeyDown;
    public event Action? WindowsKeyPressed;
    public event Action? AltTabPressed;
    public event Action? AltTabRepeated;
    public event Action? AltReleased;
    private bool _altTabSessionActive;
    private bool _altHeld;

    public ShellKeyboardGuard(IntPtr windowHandle)
    {
        _windowHandle = windowHandle;
        _procedure = ProcessKeyboardMessage;
        using var process = Process.GetCurrentProcess();
        using var module = process.MainModule;
        _hook = SetWindowsHookEx(WhKeyboardLl, _procedure,
            module is null ? IntPtr.Zero : GetModuleHandle(module.ModuleName), 0);
    }

    private IntPtr ProcessKeyboardMessage(int code, IntPtr message, IntPtr data)
    {
        if (code >= 0)
        {
            var key = Marshal.ReadInt32(data);
            var isDown = message.ToInt32() is 0x0100 or 0x0104;
            if (key is VkMenu or VkLeftMenu or VkRightMenu)
            {
                _altHeld = isDown;
                if (!isDown)
                {
                    _altTabSessionActive = false;
                    AltReleased?.Invoke();
                }
            }
            if (key == VkTab && (GetAsyncKeyState(VkMenu) & 0x8000) != 0)
            {
                if (isDown)
                {
                    if (!_altTabSessionActive)
                    {
                        _altTabSessionActive = true;
                        AltTabPressed?.Invoke();
                    }
                    else
                    {
                        AltTabRepeated?.Invoke();
                    }
                }
                return new IntPtr(1);
            }
            if (key is VkLeftWindows or VkRightWindows)
            {
                if (isDown && !_windowsKeyDown) WindowsKeyPressed?.Invoke();
                _windowsKeyDown = isDown;
                return new IntPtr(1);
            }
        }
        return CallNextHookEx(_hook, code, message, data);
    }

    public void Dispose()
    {
        if (_hook == IntPtr.Zero) return;
        UnhookWindowsHookEx(_hook);
        _hook = IntPtr.Zero;
    }

    private delegate IntPtr HookProcedure(int code, IntPtr message, IntPtr data);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, HookProcedure callback, IntPtr module, uint threadId);
    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int key);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? moduleName);
}
