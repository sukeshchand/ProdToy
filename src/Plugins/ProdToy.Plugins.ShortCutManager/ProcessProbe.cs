using System.Diagnostics;
using System.Management;
using System.Runtime.InteropServices;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Finds and inspects the OS process behind a shortcut — whether the
/// Consolidated Launcher started it or someone else did. Matching is layered:
///   1. By the Status-URL <b>port</b> → the PID owning that listening TCP socket
///      (most reliable for servers; <see cref="GetListenerPids"/>).
///   2. Fallback by <b>command line / working directory</b> via WMI
///      (<see cref="SnapshotCommandLines"/>) for shortcuts without a port.
/// Once matched, live info (memory, uptime, CPU sample) comes from
/// <see cref="System.Diagnostics.Process"/>, and <see cref="ResolveRoot"/> climbs
/// the dotnet/node parent chain so Stop can take down a <c>dotnet watch</c> parent
/// (otherwise it just respawns the child) without killing the user's shell.
/// </summary>
static class ProcessProbe
{
    public readonly record struct Info(int Pid, string Name, long MemoryBytes, DateTime StartTimeUtc, TimeSpan TotalCpu);

    // ───────────────────────── port → owning PID ─────────────────────────

    public static Dictionary<int, int> GetListenerPids()
    {
        var result = new Dictionary<int, int>();
        const int AF_INET = 2;
        const int TCP_TABLE_OWNER_PID_LISTENER = 3;
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0);
        if (size <= 0) return result;
        IntPtr buf = Marshal.AllocHGlobal(size);
        try
        {
            if (GetExtendedTcpTable(buf, ref size, false, AF_INET, TCP_TABLE_OWNER_PID_LISTENER, 0) != 0)
                return result;
            int count = Marshal.ReadInt32(buf);
            int rowSize = Marshal.SizeOf<MIB_TCPROW_OWNER_PID>();
            IntPtr row = buf + 4;
            for (int i = 0; i < count; i++)
            {
                var r = Marshal.PtrToStructure<MIB_TCPROW_OWNER_PID>(row);
                // dwLocalPort holds the port in network byte order within its low 2 bytes.
                int port = ((int)(r.localPort & 0xFF) << 8) | (int)((r.localPort >> 8) & 0xFF);
                if (port > 0 && !result.ContainsKey(port)) result[port] = (int)r.owningPid;
                row += rowSize;
            }
        }
        catch { /* return what we have */ }
        finally { Marshal.FreeHGlobal(buf); }
        return result;
    }

    // ───────────────────────── command lines (WMI) ─────────────────────────

    /// <summary>pid → full command line, via WMI. ~100-300ms; call on a background
    /// thread and only when a port match isn't available.</summary>
    public static Dictionary<int, string> SnapshotCommandLines()
    {
        var map = new Dictionary<int, string>();
        try
        {
            using var searcher = new ManagementObjectSearcher("SELECT ProcessId, CommandLine FROM Win32_Process");
            foreach (ManagementObject mo in searcher.Get())
            {
                try
                {
                    int pid = Convert.ToInt32(mo["ProcessId"]);
                    var cmd = mo["CommandLine"] as string;
                    if (!string.IsNullOrEmpty(cmd)) map[pid] = cmd;
                }
                catch { }
                finally { mo.Dispose(); }
            }
        }
        catch (Exception ex) { PluginLog.Warn($"ProcessProbe WMI snapshot failed: {ex.Message}"); }
        return map;
    }

    // ───────────────────────── per-process info ─────────────────────────

    public static bool TryGetInfo(int pid, out Info info)
    {
        info = default;
        if (pid <= 0) return false;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (p.HasExited) return false;
            DateTime start;
            try { start = p.StartTime.ToUniversalTime(); } catch { start = DateTime.UtcNow; }
            TimeSpan cpu;
            try { cpu = p.TotalProcessorTime; } catch { cpu = TimeSpan.Zero; }
            info = new Info(pid, p.ProcessName, p.WorkingSet64, start, cpu);
            return true;
        }
        catch { return false; }
    }

    /// <summary>Climb the parent chain through dotnet/node ancestors and return the
    /// topmost — so killing its tree takes down a <c>dotnet watch</c> / <c>npm</c>
    /// supervisor along with the leaf, but stops at cmd/terminal/explorer so the
    /// user's shell survives.</summary>
    public static int ResolveRoot(int pid, IReadOnlyDictionary<int, (int Parent, string Name)> snap)
    {
        int cur = pid, guard = 0;
        while (guard++ < 30 && snap.TryGetValue(cur, out var node) && node.Parent > 0
               && snap.TryGetValue(node.Parent, out var parent) && IsClimbable(parent.Name))
        {
            cur = node.Parent;
        }
        return cur;
    }

    private static bool IsClimbable(string name) =>
        name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
        || name.Equals("node.exe", StringComparison.OrdinalIgnoreCase);

    public static void KillTree(int pid)
    {
        if (pid <= 0) return;
        try
        {
            using var p = Process.GetProcessById(pid);
            if (!p.HasExited) p.Kill(entireProcessTree: true);
        }
        catch (Exception ex) { Debug.WriteLine($"ProcessProbe.KillTree {pid}: {ex.Message}"); }
    }

    // ───────────────────────── interop ─────────────────────────

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern uint GetExtendedTcpTable(IntPtr pTcpTable, ref int pdwSize, bool bOrder,
        int ulAf, int tableClass, uint reserved);

    [StructLayout(LayoutKind.Sequential)]
    private struct MIB_TCPROW_OWNER_PID
    {
        public uint state;
        public uint localAddr;
        public uint localPort;
        public uint remoteAddr;
        public uint remotePort;
        public uint owningPid;
    }
}
