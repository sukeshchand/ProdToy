using System.Runtime.InteropServices;

namespace ProdToy;

static class NativeMethods
{
    internal const int SW_RESTORE = 9;

    [DllImport("user32.dll")]
    internal static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    internal static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    internal static extern bool SetWindowText(IntPtr hWnd, string lpString);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern bool AttachConsole(int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    internal static extern IntPtr GetStdHandle(int nStdHandle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern bool WriteConsole(IntPtr hConsoleOutput, string lpBuffer, int nNumberOfCharsToWrite, out int lpNumberOfCharsWritten, IntPtr lpReserved);

    internal const int STD_OUTPUT_HANDLE = -11;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    internal static extern int GetConsoleTitle(System.Text.StringBuilder lpConsoleTitle, int nSize);

    // Global hotkey
    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;
    internal const uint MOD_SHIFT = 0x0004;
    internal const uint MOD_WIN = 0x0008;
    internal const uint MOD_NOREPEAT = 0x4000;

    internal const int WM_HOTKEY = 0x0312;
    internal const int HOTKEY_ID_DEFAULT = 1;

    // Low-level keyboard hook
    internal const int WH_KEYBOARD_LL = 13;
    internal const int WM_KEYUP = 0x0101;
    internal const int VK_LCONTROL = 0xA2;
    internal const int VK_RCONTROL = 0xA3;

    internal delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    internal static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    internal static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    internal static extern IntPtr GetModuleHandle(string? lpModuleName);
}
