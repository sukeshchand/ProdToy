using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// ToolHelp32-based process-tree walker used by Stop All's escalation step.
/// When the per-row PID kill misses (PID never captured, or the cmd child
/// already exited leaving npm/node descendants reparented), we fall back to
/// resolving the Windows Terminal process behind a still-open tab and
/// killing every process under it.
/// </summary>
static class ProcessTree
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Auto)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32First(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto)]
    private static extern bool Process32Next(IntPtr hSnapshot, ref PROCESSENTRY32 lppe);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    /// <summary>pid → (parentPid, exeName) for every process, via one ToolHelp32
    /// snapshot. Used to climb the parent chain when resolving a launch root.</summary>
    public static Dictionary<int, (int Parent, string Name)> SnapshotByPid()
    {
        var map = new Dictionary<int, (int, string)>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return map;
            do
            {
                map[(int)entry.th32ProcessID] = ((int)entry.th32ParentProcessID, entry.szExeFile ?? "");
            } while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    private static Dictionary<int, List<(int Pid, string Name)>> BuildChildMap()
    {
        var map = new Dictionary<int, List<(int, string)>>();
        IntPtr snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return map;
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snap, ref entry)) return map;
            do
            {
                int parent = (int)entry.th32ParentProcessID;
                int pid = (int)entry.th32ProcessID;
                string name = entry.szExeFile ?? "";
                if (!map.TryGetValue(parent, out var list))
                {
                    list = new List<(int, string)>();
                    map[parent] = list;
                }
                list.Add((pid, name));
            } while (Process32Next(snap, ref entry));
        }
        finally { CloseHandle(snap); }
        return map;
    }

    /// <summary>Every descendant PID of <paramref name="rootPid"/> via BFS over
    /// the parent→child map. The root itself is not included.</summary>
    public static List<(int Pid, string Name)> GetDescendants(int rootPid)
    {
        var result = new List<(int, string)>();
        if (rootPid <= 0) return result;
        var map = BuildChildMap();
        var queue = new Queue<int>();
        queue.Enqueue(rootPid);
        var seen = new HashSet<int> { rootPid };
        while (queue.Count > 0)
        {
            int parent = queue.Dequeue();
            if (!map.TryGetValue(parent, out var kids)) continue;
            foreach (var k in kids)
            {
                if (!seen.Add(k.Pid)) continue;
                result.Add(k);
                queue.Enqueue(k.Pid);
            }
        }
        return result;
    }

    /// <summary>Forcibly terminate every descendant of <paramref name="rootPid"/>.
    /// The root process is left alone so the hosting WindowsTerminal.exe
    /// survives — only its panes/tabs die. Returns the count actually killed.</summary>
    public static int KillDescendants(int rootPid)
    {
        int killed = 0;
        foreach (var (pid, _) in GetDescendants(rootPid))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                if (p.HasExited) continue;
                p.Kill(entireProcessTree: false);
                killed++;
            }
            catch (Exception ex) { Debug.WriteLine($"KillDescendants pid={pid}: {ex.Message}"); }
        }
        return killed;
    }
}
