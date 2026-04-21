using System.Diagnostics;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Launches a ClaudeShortcut by building a Windows Terminal (or plain cmd)
/// command line that opens <c>claude</c> in the configured working directory.
///
/// Primary command when WindowsTerminal mode + wt.exe present:
///     wt.exe -p "&lt;WtProfile&gt;" -d "&lt;WorkingDirectory&gt;" cmd /k claude &lt;args&gt;
///
/// Fallback (no wt.exe, or CmdWindow mode):
///     cmd.exe /k "cd /d &lt;WorkingDirectory&gt; && claude &lt;args&gt;"
///
/// If <see cref="ClaudeShortcut.RequireAdmin"/> is set, the process is started
/// with <c>UseShellExecute=true, Verb="runas"</c> — triggers the standard UAC
/// prompt. If the user cancels UAC we surface a friendly error instead of
/// crashing.
/// </summary>
static class ClaudeShortcutLauncher
{
    public readonly record struct LaunchResult(bool Ok, string? ErrorMessage = null);

    public static LaunchResult Launch(ClaudeShortcut s)
    {
        if (string.IsNullOrWhiteSpace(s.WorkingDirectory))
            return new LaunchResult(false, "Working directory is empty.");
        if (!Directory.Exists(s.WorkingDirectory))
            return new LaunchResult(false, $"Working directory doesn't exist: {s.WorkingDirectory}");

        bool useWt = s.LauncherMode == ClaudeLauncherMode.WindowsTerminal && TryFindWindowsTerminal(out _);
        ProcessStartInfo psi = useWt ? BuildWtStartInfo(s) : BuildCmdStartInfo(s);

        if (s.RequireAdmin)
        {
            psi.UseShellExecute = true;
            psi.Verb = "runas";
            // runas + redirected streams doesn't work; callers can't read
            // stdout/stderr when elevating anyway.
            psi.RedirectStandardOutput = false;
            psi.RedirectStandardError = false;
        }

        try
        {
            Process.Start(psi);
            ClaudeShortcutStore.RecordLaunch(s.Id);
            return new LaunchResult(true);
        }
        catch (System.ComponentModel.Win32Exception ex) when ((uint)ex.NativeErrorCode == 0x800704C7u
            || ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user clicked No on the UAC prompt.
            return new LaunchResult(false, "UAC prompt was cancelled.");
        }
        catch (Exception ex)
        {
            return new LaunchResult(false, ex.Message);
        }
    }

    private static ProcessStartInfo BuildWtStartInfo(ClaudeShortcut s)
    {
        // wt.exe resolves via %PATH% on modern Windows (Windows Terminal
        // installs a shim in %LocalAppData%\Microsoft\WindowsApps).
        var psi = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = true,   // no console window for the wt invoker itself
        };
        if (!string.IsNullOrWhiteSpace(s.WtProfile))
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(s.WtProfile);
        }
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(s.WorkingDirectory);
        psi.ArgumentList.Add("cmd");
        psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add(BuildClaudeCmdline(s));
        return psi;
    }

    private static ProcessStartInfo BuildCmdStartInfo(ClaudeShortcut s)
    {
        // Plain cmd window fallback. We wrap the whole thing in one argument
        // so cmd /k gets the combined "cd && claude" line.
        var line = $"cd /d \"{s.WorkingDirectory}\" && {BuildClaudeCmdline(s)}";
        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k {line}",
            UseShellExecute = true,
            WorkingDirectory = s.WorkingDirectory,
        };
    }

    private static string BuildClaudeCmdline(ClaudeShortcut s)
    {
        string args = (s.ClaudeArgs ?? "").Trim();
        if (string.IsNullOrEmpty(args)) return "claude";

        // When the user has --continue (or -c) in their args, Claude exits with
        // "No conversation found to continue" on a fresh project. We auto-retry
        // without that flag via a cmd-level || chain so the shortcut still lands
        // you in a working session instead of a dead terminal.
        string stripped = StripContinueFlag(args);
        if (stripped == args) return $"claude {args}";
        return string.IsNullOrEmpty(stripped)
            ? $"(claude {args} || claude)"
            : $"(claude {args} || claude {stripped})";
    }

    private static string StripContinueFlag(string args)
    {
        var tokens = args.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(t => !t.Equals("--continue", StringComparison.OrdinalIgnoreCase)
                     && !t.Equals("-c", StringComparison.OrdinalIgnoreCase));
        return string.Join(" ", tokens).Trim();
    }

    public static bool TryFindWindowsTerminal(out string? path)
    {
        // Windows Terminal ships a shim under WindowsApps on the PATH. If it's
        // not found, fall back to the user-scope install path just in case.
        foreach (var candidate in EnumerateCandidates())
        {
            if (File.Exists(candidate))
            {
                path = candidate;
                return true;
            }
        }
        path = null;
        return false;
    }

    private static IEnumerable<string> EnumerateCandidates()
    {
        var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        yield return Path.Combine(localApp, "Microsoft", "WindowsApps", "wt.exe");

        // Walk %PATH% too.
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            string candidate;
            try { candidate = Path.Combine(dir.Trim(), "wt.exe"); }
            catch { continue; }
            yield return candidate;
        }
    }
}
