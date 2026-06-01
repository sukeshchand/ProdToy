using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Closes individual Windows Terminal tabs whose title carries a given
/// marker (our group prefix) by simulating keystrokes against the WT
/// window. We cycle through tabs with Ctrl+Tab and close the matching
/// ones with Ctrl+Shift+W (WT's default closePane binding).
///
/// Why this, instead of:
///   * WM_CLOSE on the WT hwnd — pops "Do you want to close all tabs?".
///   * Killing the WT process — also closes tabs that belong to other
///     groups sharing the same WT window.
///   * UI Automation via UIAutomationClient — requires &lt;UseWPF&gt; on .NET 8
///     desktop SDK which clashed with this plugin's implicit usings.
///
/// Ctrl+Shift+W closes a single tab without confirmation. The only WT
/// prompt is on closing a *multi-tab window*, which we never trigger.
///
/// All work runs on a background thread (caller wraps in Task.Run) so
/// the UI doesn't freeze during the cycle.
/// </summary>
static class TerminalTabCloser
{
    private const int WM_GETTEXT = 0x000D;
    private const int WM_GETTEXTLENGTH = 0x000E;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, StringBuilder lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint MAPVK_VK_TO_VSC = 0;
    private const int SW_RESTORE = 9;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hWnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion U;
    }
    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    private const uint INPUT_KEYBOARD = 1;
    private const uint KEYEVENTF_KEYUP = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const ushort VK_CONTROL = 0x11;
    private const ushort VK_SHIFT = 0x10;
    private const ushort VK_TAB = 0x09;
    private const ushort VK_W = 0x57;

    /// <summary>Close every tab in the WT window <paramref name="wtHwnd"/>
    /// whose title contains <paramref name="titleNeedle"/>. Cycles tabs with
    /// Ctrl+Tab to discover each in turn and uses Ctrl+Shift+W to close
    /// matches. Returns the number of tabs we believe were closed.
    ///
    /// Call this from a background thread (Task.Run) — it Thread.Sleeps
    /// between keystrokes to give WT time to react.</summary>
    public static int CloseTabsContaining(IntPtr wtHwnd, string titleNeedle)
    {
        if (wtHwnd == IntPtr.Zero || string.IsNullOrEmpty(titleNeedle)) return 0;
        if (!IsWindow(wtHwnd)) return 0;

        int closed = 0;
        const int maxIterations = 64;
        var seenTitlesSinceLastClose = new HashSet<string>(StringComparer.Ordinal);

        for (int i = 0; i < maxIterations; i++)
        {
            if (!IsWindow(wtHwnd)) break;

            // Re-foreground each iteration. WT briefly loses focus when a
            // tab closes (its hwnd may flicker), and another app can steal
            // focus between iterations. Bailing the first time foreground
            // fails would leave half the tabs open.
            if (!ForceForeground(wtHwnd))
            {
                Debug.WriteLine($"TerminalTabCloser: could not foreground WT (iter {i}).");
                break;
            }

            string title = GetWindowTitle(wtHwnd);
            if (string.IsNullOrEmpty(title)) break;

            if (title.IndexOf(titleNeedle, StringComparison.Ordinal) >= 0)
            {
                SendCloseTab();
                Thread.Sleep(200);
                closed++;
                seenTitlesSinceLastClose.Clear();
                continue;
            }

            // Not ours. If we've already cycled past this exact title since
            // the last close, we've made a full lap without finding more
            // matches — done.
            if (!seenTitlesSinceLastClose.Add(title)) break;

            SendCtrlTab();
            Thread.Sleep(120);
        }

        return closed;
    }

    /// <summary>Foreground a window from a background process despite the
    /// foreground-lock rules. Standard trick: attach our input thread to the
    /// target's input thread, then SetForegroundWindow + BringWindowToTop
    /// succeed, then detach.</summary>
    private static bool ForceForeground(IntPtr hWnd)
    {
        try
        {
            if (GetForegroundWindow() == hWnd) return true;

            ShowWindow(hWnd, SW_RESTORE);
            uint targetThread = GetWindowThreadProcessId(hWnd, out _);
            uint ourThread = GetCurrentThreadId();
            if (targetThread == 0) return false;

            bool attached = false;
            if (targetThread != ourThread)
                attached = AttachThreadInput(ourThread, targetThread, true);

            try
            {
                BringWindowToTop(hWnd);
                SetForegroundWindow(hWnd);
            }
            finally
            {
                if (attached) AttachThreadInput(ourThread, targetThread, false);
            }

            for (int i = 0; i < 12; i++)
            {
                if (GetForegroundWindow() == hWnd) return true;
                Thread.Sleep(25);
            }
            return GetForegroundWindow() == hWnd;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"TerminalTabCloser.ForceForeground: {ex.Message}");
            return false;
        }
    }

    private static string GetWindowTitle(IntPtr hWnd)
    {
        try
        {
            int len = (int)SendMessage(hWnd, WM_GETTEXTLENGTH, IntPtr.Zero, IntPtr.Zero);
            if (len <= 0) return string.Empty;
            var sb = new StringBuilder(len + 1);
            SendMessage(hWnd, WM_GETTEXT, (IntPtr)sb.Capacity, sb);
            return sb.ToString();
        }
        catch { return string.Empty; }
    }

    private static void SendCloseTab() => SendChord(VK_CONTROL, VK_SHIFT, VK_W);
    private static void SendCtrlTab() => SendChord(VK_CONTROL, VK_TAB);

    /// <summary>Press modifiers + key (with scan codes for max compatibility),
    /// release in reverse order. SendInput with KEYEVENTF_SCANCODE mimics a
    /// real hardware keypress, which WT respects more reliably than pure-VK
    /// synthesis on some Windows builds.</summary>
    private static void SendChord(ushort modifier1, ushort modifier2, ushort key)
    {
        var inputs = new INPUT[]
        {
            MakeKey(modifier1, down: true),
            MakeKey(modifier2, down: true),
            MakeKey(key, down: true),
            MakeKey(key, down: false),
            MakeKey(modifier2, down: false),
            MakeKey(modifier1, down: false),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static void SendChord(ushort modifier, ushort key)
    {
        var inputs = new INPUT[]
        {
            MakeKey(modifier, down: true),
            MakeKey(key, down: true),
            MakeKey(key, down: false),
            MakeKey(modifier, down: false),
        };
        SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    private static INPUT MakeKey(ushort vk, bool down)
    {
        ushort scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
        uint flags = KEYEVENTF_SCANCODE;
        if (!down) flags |= KEYEVENTF_KEYUP;
        return new INPUT
        {
            type = INPUT_KEYBOARD,
            U = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk = 0, // ignored when KEYEVENTF_SCANCODE is set
                    wScan = scan,
                    dwFlags = flags,
                    time = 0,
                    dwExtraInfo = IntPtr.Zero,
                }
            }
        };
    }
}
