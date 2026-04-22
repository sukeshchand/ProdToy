using System.Diagnostics;
using System.Windows.Forms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Launches a <see cref="Shortcut"/> by building a Windows Terminal (or plain
/// cmd) command line that runs the shortcut's <see cref="LaunchProfile"/>
/// command in the configured working directory.
///
/// Primary command when WindowsTerminal mode + wt.exe present:
///     wt.exe -p "&lt;WtProfile&gt;" -d "&lt;WorkingDirectory&gt;" cmd /k &lt;command&gt; &lt;args&gt;
///
/// Fallback (no wt.exe, or CmdWindow mode):
///     cmd.exe /k "cd /d &lt;WorkingDirectory&gt; && &lt;command&gt; &lt;args&gt;"
///
/// If <see cref="Shortcut.RequireAdmin"/> is set, the process is started
/// with <c>UseShellExecute=true, Verb="runas"</c> — triggers the standard UAC
/// prompt. If the user cancels UAC we surface a friendly error instead of
/// crashing.
/// </summary>
static class ShortcutLauncher
{
    public readonly record struct LaunchResult(bool Ok, string? ErrorMessage = null);

    public static LaunchResult Launch(Shortcut s)
    {
        if (string.IsNullOrWhiteSpace(s.WorkingDirectory))
            return new LaunchResult(false, "Working directory is empty.");
        if (!Directory.Exists(s.WorkingDirectory))
            return new LaunchResult(false, $"Working directory doesn't exist: {s.WorkingDirectory}");

        bool useWt = s.LauncherMode == LauncherMode.WindowsTerminal && TryFindWindowsTerminal(out _);
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
            ShortcutStore.RecordLaunch(s.Id);
            SchedulePostLaunchKeys(s);
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

    private static ProcessStartInfo BuildWtStartInfo(Shortcut s)
    {
        // wt.exe resolves via %PATH% on modern Windows (Windows Terminal
        // installs a shim in %LocalAppData%\Microsoft\WindowsApps).
        var psi = new ProcessStartInfo
        {
            FileName = "wt.exe",
            UseShellExecute = true,   // no console window for the wt invoker itself
        };
        // -w routes the tab into an existing WT window:
        //   empty name → "-w 0" (most-recent window)
        //   named       → "-w <name>" (specific named window; created on demand)
        // Without -w, WT uses its own "new window" default.
        if (s.WtWindowTarget == WtWindowTarget.ExistingWindow)
        {
            psi.ArgumentList.Add("-w");
            psi.ArgumentList.Add(string.IsNullOrWhiteSpace(s.WtWindowName)
                ? "0"
                : s.WtWindowName.Trim());
        }
        if (!string.IsNullOrWhiteSpace(s.WtProfile))
        {
            psi.ArgumentList.Add("-p");
            psi.ArgumentList.Add(s.WtProfile);
        }
        if (!string.IsNullOrWhiteSpace(s.WindowTitle))
        {
            // --title is a new-tab option on wt; without an explicit subcommand
            // wt defaults to new-tab so this names the created tab/window.
            psi.ArgumentList.Add("--title");
            psi.ArgumentList.Add(s.WindowTitle);
        }
        psi.ArgumentList.Add("-d");
        psi.ArgumentList.Add(s.WorkingDirectory);
        psi.ArgumentList.Add("cmd");
        psi.ArgumentList.Add("/k");
        psi.ArgumentList.Add(BuildProfileCmdline(s));
        return psi;
    }

    private static ProcessStartInfo BuildCmdStartInfo(Shortcut s)
    {
        // Plain cmd window fallback. We wrap the whole thing in one argument
        // so cmd /k gets the combined "cd && [title &&] claude" line.
        var line = $"cd /d \"{s.WorkingDirectory}\"";
        if (!string.IsNullOrWhiteSpace(s.WindowTitle))
        {
            // cmd's built-in `title` command sets the console window title.
            // No quoting needed — `title` consumes the rest of the line.
            line += $" && title {s.WindowTitle}";
        }
        line += $" && {BuildProfileCmdline(s)}";
        return new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/k {line}",
            UseShellExecute = true,
            WorkingDirectory = s.WorkingDirectory,
        };
    }

    private static string BuildProfileCmdline(Shortcut s)
    {
        var profile = LaunchProfiles.GetOrDefault(s.Profile);
        string args = (s.Args ?? "").Trim();

        // Custom profile has no fixed binary — args is the full command.
        if (string.IsNullOrEmpty(profile.Command))
            return args;

        string cmd = profile.Command;
        if (string.IsNullOrEmpty(args)) return cmd;

        // For profiles that opt in (Claude today), a trailing --continue/-c in
        // the args gets a "(X args || X stripped)" retry wrapper so the user
        // doesn't land in a dead terminal when there's no prior conversation.
        if (profile.SupportsContinueFallback)
        {
            string stripped = StripContinueFlag(args);
            if (stripped != args)
            {
                return string.IsNullOrEmpty(stripped)
                    ? $"({cmd} {args} || {cmd})"
                    : $"({cmd} {args} || {cmd} {stripped})";
            }
        }

        return $"{cmd} {args}";
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

    /// <summary>
    /// Fires <see cref="SendKeys.SendWait"/> with the shortcut's configured
    /// keystrokes after the configured delay, on the UI thread. Uses a
    /// WinForms Timer (fires on the thread that started it) so SendKeys'
    /// message-pump requirement is satisfied. Exceptions are swallowed —
    /// these keystrokes are an optional convenience, never load-bearing.
    /// </summary>
    private static void SchedulePostLaunchKeys(Shortcut s)
    {
        if (string.IsNullOrWhiteSpace(s.PostLaunchSendKeys)) return;
        int delay = Math.Clamp(s.PostLaunchDelayMs, 100, 60_000);
        string keys = s.PostLaunchSendKeys;

        var timer = new System.Windows.Forms.Timer { Interval = delay };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            timer.Dispose();
            try { SendKeys.SendWait(keys); }
            catch (Exception ex) { PluginLog.Warn($"PostLaunchSendKeys failed: {ex.Message}"); }
        };
        timer.Start();
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
