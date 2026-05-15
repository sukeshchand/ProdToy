namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// One folder's Group-Launcher state. Holds the current batch id and the
/// PID we attributed to each shortcut at launch time, keyed by the
/// shortcut's stable <see cref="Shortcut.Id"/>. PIDs are OS-level so they
/// stay valid until the cmd.exe actually exits — even if the UI tab for
/// the folder isn't currently showing.
/// </summary>
sealed class GroupSession
{
    public int BatchId { get; set; }
    public Dictionary<string, int> PidByShortcutId { get; } = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// Process-wide store of <see cref="GroupSession"/> instances keyed by
/// folder path. Survives the user navigating between folders inside
/// <see cref="ShortcutsForm"/> so a backgrounded batch keeps its PID
/// tracking when the user comes back to its folder.
/// </summary>
static class GroupLauncherSessions
{
    private static readonly Dictionary<string, GroupSession> _byFolder =
        new(StringComparer.OrdinalIgnoreCase);

    public static GroupSession GetOrCreate(string folderPath)
    {
        if (!_byFolder.TryGetValue(folderPath, out var s))
            _byFolder[folderPath] = s = new GroupSession();
        return s;
    }

    public static IEnumerable<KeyValuePair<string, GroupSession>> All => _byFolder;

    /// <summary>True if any session has at least one PID we recorded that is
    /// still alive — used by <c>ShortcutsForm</c> to gate its close prompt.</summary>
    public static bool AnyLive()
    {
        foreach (var kv in _byFolder)
        {
            foreach (var pid in kv.Value.PidByShortcutId.Values)
            {
                if (pid > 0 && IsPidAlive(pid)) return true;
            }
        }
        return false;
    }

    private static bool IsPidAlive(int pid)
    {
        try
        {
            using var p = System.Diagnostics.Process.GetProcessById(pid);
            return !p.HasExited;
        }
        catch { return false; }
    }
}
