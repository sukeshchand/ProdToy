using System.Runtime.InteropServices;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Thin wrapper around a Windows <i>Job Object</i> with the
/// <c>JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE</c> flag set. Any process assigned to the job
/// (and every process those processes spawn — recursively) is terminated when the job
/// handle is closed.
/// </summary>
/// <remarks>
/// Why this exists: <see cref="System.Diagnostics.Process.Kill(bool)"/> with
/// <c>entireProcessTree: true</c> walks parent-PID relationships, which doesn't catch
/// grandchildren that npm / dotnet-watch detach from their original parent chain. The
/// result was that "Stop" killed our wrapper <c>cmd.exe</c> + <c>npm</c> but left
/// <c>node</c>, <c>dotnet watch</c>, and the actual app process running. Job Objects don't
/// care about parentage — every process ever assigned (directly or as a descendant of one
/// already in the job) is in the job and dies when the job closes.
///
/// Ported verbatim from NordPilot.DeveloperTools for the Consolidated Launcher, which runs
/// each shortcut as a captured child process (cmd /c &lt;command&gt;) and needs a reliable
/// Stop that takes the whole tree (dotnet/npm/node) with it.
///
/// Usage:
///   var job = new ProcessJobObject();
///   process.Start();
///   job.AssignProcess(process);
///   ...
///   job.Dispose();   // kills process + every descendant it spawned
/// </remarks>
internal sealed class ProcessJobObject : IDisposable
{
    private IntPtr _handle;

    public ProcessJobObject()
    {
        _handle = CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
            throw new InvalidOperationException("CreateJobObject failed: " + Marshal.GetLastWin32Error());

        var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        int length = Marshal.SizeOf(info);
        var ptr = Marshal.AllocHGlobal(length);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            if (!SetInformationJobObject(_handle, JobObjectInfoType.ExtendedLimitInformation, ptr, (uint)length))
                throw new InvalidOperationException("SetInformationJobObject failed: " + Marshal.GetLastWin32Error());
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    /// <summary>
    /// Adds the given running process to this job. Any process the assigned process spawns
    /// after this call is automatically part of the job too. Safe to call once per process;
    /// re-assigning a process that's already in this job is a no-op.
    /// </summary>
    public void AssignProcess(System.Diagnostics.Process process)
    {
        if (_handle == IntPtr.Zero) throw new ObjectDisposedException(nameof(ProcessJobObject));
        if (process.HasExited) return;
        if (!AssignProcessToJobObject(_handle, process.Handle))
        {
            var err = Marshal.GetLastWin32Error();
            // ERROR_ACCESS_DENIED (5) happens when the process already belongs to a job and
            // the job (or the parent process's job) isn't a nested-jobs-allowed setup. In
            // Windows 10+ that's rare, but if it happens we degrade gracefully: the process
            // simply won't be auto-killed and we'll fall back to Process.Kill in Stop().
            throw new InvalidOperationException(
                $"AssignProcessToJobObject failed (error {err}). The process will not be auto-killed via job.");
        }
    }

    public void Dispose()
    {
        if (_handle == IntPtr.Zero) return;
        // Closing the handle triggers KILL_ON_JOB_CLOSE — every process in the job dies.
        CloseHandle(_handle);
        _handle = IntPtr.Zero;
    }

    // ---- Win32 interop --------------------------------------------------------------

    private const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;

    private enum JobObjectInfoType
    {
        AssociateCompletionPortInformation = 7,
        BasicLimitInformation = 2,
        BasicUIRestrictions = 4,
        EndOfJobTimeInformation = 6,
        ExtendedLimitInformation = 9,
        SecurityLimitInformation = 5,
        GroupInformation = 11,
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObject(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(
        IntPtr hJob, JobObjectInfoType infoType, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }
}
