using System.Runtime.InteropServices;
using System.Text;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Thin P/Invoke wrapper around <c>EnumWindows</c> + <c>SendMessage(WM_CLOSE)</c>
/// used by <see cref="GroupLauncherForm"/> to track and close the windows it
/// launched. Identifies windows by title rather than process id so we don't
/// have to track child processes spawned by <c>wt</c>/<c>cmd</c>.
/// </summary>
static class WindowFinder
{
    private const int WM_CLOSE = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    /// <summary>Resolve the owning process id of <paramref name="hWnd"/>.
    /// For a Windows Terminal tab this returns the WT process pid (one per
    /// WT window), which can then be walked via <see cref="ProcessTree"/>.</summary>
    public static bool TryGetWindowPid(IntPtr hWnd, out int pid)
    {
        pid = 0;
        if (hWnd == IntPtr.Zero) return false;
        try
        {
            GetWindowThreadProcessId(hWnd, out uint upid);
            pid = (int)upid;
            return pid > 0;
        }
        catch { return false; }
    }

    public readonly record struct WindowInfo(IntPtr Handle, string Title);

    /// <summary>Enumerate visible top-level windows with non-empty titles.</summary>
    public static IReadOnlyList<WindowInfo> EnumerateTopLevel()
    {
        var list = new List<WindowInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            int len = GetWindowTextLength(h);
            if (len <= 0) return true;
            var sb = new StringBuilder(len + 1);
            GetWindowText(h, sb, sb.Capacity);
            string title = sb.ToString();
            if (!string.IsNullOrEmpty(title))
                list.Add(new WindowInfo(h, title));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>Find every visible top-level window whose title starts with <paramref name="prefix"/>.</summary>
    public static IReadOnlyList<WindowInfo> FindByPrefix(string prefix)
    {
        if (string.IsNullOrEmpty(prefix)) return Array.Empty<WindowInfo>();
        var hits = new List<WindowInfo>();
        foreach (var w in EnumerateTopLevel())
        {
            if (w.Title.StartsWith(prefix, StringComparison.Ordinal))
                hits.Add(w);
        }
        return hits;
    }

    /// <summary>True if any visible top-level window's title contains <paramref name="needle"/>.
    /// We match on Contains (not exact) because <c>wt</c> often appends "- Windows
    /// Terminal" or the active profile name to the tab title we set via <c>--title</c>.</summary>
    public static bool AnyWindowTitleContains(string needle)
    {
        if (string.IsNullOrEmpty(needle)) return false;
        foreach (var w in EnumerateTopLevel())
        {
            if (w.Title.IndexOf(needle, StringComparison.Ordinal) >= 0)
                return true;
        }
        return false;
    }

    /// <summary>Find every visible top-level window whose title contains <paramref name="needle"/>.</summary>
    public static IReadOnlyList<WindowInfo> FindByTitleContains(string needle)
    {
        if (string.IsNullOrEmpty(needle)) return Array.Empty<WindowInfo>();
        var hits = new List<WindowInfo>();
        foreach (var w in EnumerateTopLevel())
        {
            if (w.Title.IndexOf(needle, StringComparison.Ordinal) >= 0)
                hits.Add(w);
        }
        return hits;
    }

    /// <summary>Politely close the window via <c>WM_CLOSE</c>. Returns to the
    /// caller immediately — the message is queued, so callers should poll for
    /// the title's disappearance rather than assume the window is gone.</summary>
    public static void CloseWindow(IntPtr hWnd)
    {
        try { SendMessage(hWnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); }
        catch { }
    }
}
