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

    public static LaunchResult Launch(Shortcut s) => Launch(s, null, forceNewWindow: false);

    /// <summary>
    /// Group Launcher entry point. <paramref name="titleOverride"/> replaces
    /// the shortcut's saved <see cref="Shortcut.WindowTitle"/> for this run
    /// only — used to stamp a batch id into the window title so the launcher
    /// can find/close it later. When <paramref name="forceNewWindow"/> is true,
    /// the shortcut's saved WT-existing-window/tab-group settings are ignored
    /// so the window appears as its own top-level (required for title-scan
    /// tracking to work).
    /// </summary>
    /// <summary>True when the shortcut's profile is the URL kind (opens a URL
    /// instead of running a command). In-app callers open the URL in a WebView2
    /// preview; this static path is the headless fallback (desktop .lnk / Explorer
    /// menu) which opens the system default browser.</summary>
    internal static bool IsUrl(Shortcut s) =>
        LaunchProfiles.GetOrDefault(s.Profile).Kind == LaunchKind.Url;

    public static LaunchResult Launch(Shortcut s, string? titleOverride, bool forceNewWindow)
    {
        // URL shortcut: no command/terminal, no working directory — just open the
        // URL. Reached from desktop .lnk / Explorer (no UI to host a preview), so
        // fall back to the system default browser.
        if (IsUrl(s))
        {
            var url = (s.Args ?? "").Trim();
            if (string.IsNullOrWhiteSpace(url))
                return new LaunchResult(false, "No URL set for this shortcut.");
            try
            {
                Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                ShortcutStore.RecordLaunch(s.Id);
                AutoLoginRunner.RunInBackground(s);   // no-op unless enabled + HomeUrl set
                return new LaunchResult(true);
            }
            catch (Exception ex) { return new LaunchResult(false, ex.Message); }
        }

        if (string.IsNullOrWhiteSpace(s.WorkingDirectory))
            return new LaunchResult(false, "Working directory is empty.");
        if (!Directory.Exists(s.WorkingDirectory))
            return new LaunchResult(false, $"Working directory doesn't exist: {s.WorkingDirectory}");

        if (titleOverride != null || forceNewWindow)
        {
            s = s with
            {
                WindowTitle = titleOverride ?? s.WindowTitle,
                WtWindowTarget = forceNewWindow ? WtWindowTarget.NewWindow : s.WtWindowTarget,
                WtWindowName = forceNewWindow ? "" : s.WtWindowName,
            };
        }

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
            AutoLoginRunner.RunInBackground(s);
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
        // wt sets the tab title via --title above, so the script must NOT
        // touch the console title (it would clobber the tab name the Group
        // Launcher keys off when closing tabs).
        AppendShellInvocation(psi.ArgumentList, s, includeTitleInScript: false);
        return psi;
    }

    private static ProcessStartInfo BuildCmdStartInfo(Shortcut s)
    {
        // Plain shell window fallback (no Windows Terminal). The shell's
        // working directory is set via ProcessStartInfo.WorkingDirectory, and
        // the window title is set inside the generated script (no --title flag
        // here as there is for wt).
        var psi = new ProcessStartInfo
        {
            UseShellExecute = true,
            WorkingDirectory = s.WorkingDirectory,
        };
        AppendShellInvocation(psi.ArgumentList, s, includeTitleInScript: true);
        // ArgumentList holds the program + args (e.g. "cmd","/k",<script> or
        // "powershell","-NoExit",…). Pull the program name into FileName.
        psi.FileName = psi.ArgumentList[0];
        psi.ArgumentList.RemoveAt(0);
        return psi;
    }

    /// <summary>
    /// Appends the shell program and its arguments to <paramref name="args"/>:
    /// cmd → <c>cmd /k &lt;script.cmd&gt;</c>, PowerShell →
    /// <c>powershell -NoExit -ExecutionPolicy Bypass -File &lt;script.ps1&gt;</c>.
    /// The setup steps + command are written to a throwaway script (see
    /// <see cref="WriteLaunchScript"/>) rather than crammed onto the command
    /// line — that sidesteps cmd/PowerShell/wt quoting and delimiter rules
    /// (notably wt treating <c>;</c> as a tab separator), and lets each setup
    /// step be a plain line in its native shell syntax.
    /// </summary>
    private static void AppendShellInvocation(IList<string> args, Shortcut s, bool includeTitleInScript)
    {
        string script = WriteLaunchScript(s, includeTitleInScript);
        if (s.Shell == LaunchShell.PowerShell)
        {
            args.Add("powershell.exe");
            args.Add("-NoExit");
            args.Add("-ExecutionPolicy");
            args.Add("Bypass");
            args.Add("-File");
            args.Add(script);
        }
        else
        {
            args.Add("cmd.exe");
            args.Add("/k");
            args.Add(script);
        }
    }

    /// <summary>
    /// Writes a throwaway launch script (.cmd or .ps1, per the shortcut's
    /// shell) into a temp folder and returns its path. The script optionally
    /// sets the window title, then runs each non-blank
    /// <see cref="Shortcut.SetupSteps"/> line, then the profile command — one
    /// statement per line in the shell's native syntax. The caller invokes it
    /// via <c>cmd /k</c> or <c>powershell -NoExit -File</c>, so the window
    /// stays open and any env vars the setup set persist for the command.
    /// </summary>
    private static string WriteLaunchScript(Shortcut s, bool includeTitle)
    {
        bool ps = s.Shell == LaunchShell.PowerShell;

        string dir = Path.Combine(Path.GetTempPath(), "ProdToyShortcuts");
        Directory.CreateDirectory(dir);
        PruneOldScripts(dir);

        string idSafe = new string((s.Id ?? "").Where(char.IsLetterOrDigit).ToArray());
        if (idSafe.Length == 0) idSafe = "x";
        string path = Path.Combine(dir,
            $"launch-{idSafe}-{DateTime.Now:yyyyMMddHHmmssfff}.{(ps ? "ps1" : "cmd")}");

        var lines = new List<string>();

        if (includeTitle && !string.IsNullOrWhiteSpace(s.WindowTitle))
        {
            lines.Add(ps
                ? $"$host.UI.RawUI.WindowTitle = '{s.WindowTitle.Replace("'", "''")}'"
                : $"title {s.WindowTitle}");
        }

        foreach (var step in (s.SetupSteps ?? "")
            .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0))
        {
            lines.Add(step);
        }

        string main = BuildProfileCmdline(s);
        if (!string.IsNullOrEmpty(main)) lines.Add(main);

        File.WriteAllText(path, string.Join(Environment.NewLine, lines) + Environment.NewLine);
        return path;
    }

    /// <summary>Best-effort delete of launch scripts older than an hour so the
    /// temp folder doesn't grow without bound. A stale script never hurts a
    /// future launch, so failures here are swallowed.</summary>
    private static void PruneOldScripts(string dir)
    {
        try
        {
            var cutoff = DateTime.Now.AddHours(-1);
            foreach (var f in Directory.EnumerateFiles(dir, "launch-*"))
            {
                try { if (File.GetLastWriteTime(f) < cutoff) File.Delete(f); }
                catch (Exception ex) { Debug.WriteLine($"PruneOldScripts delete: {ex.Message}"); }
            }
        }
        catch (Exception ex) { Debug.WriteLine($"PruneOldScripts: {ex.Message}"); }
    }

    /// <summary>
    /// Builds the effective inner command string for a shortcut (e.g.
    /// <c>dotnet run</c>, <c>npm run dev</c>, or a custom command). This is the
    /// exact command the WT/cmd launchers run via <c>cmd /k …</c>; the
    /// Consolidated Launcher reuses it to run the same command as a captured
    /// child process via <c>cmd /c …</c>. <c>internal</c> so the consolidated
    /// launcher can share one source of truth for the command.
    /// </summary>
    internal static string BuildProfileCmdline(Shortcut s)
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
        // The wrapper is cmd syntax (|| and parens), so it's only emitted for
        // the cmd shell — PowerShell gets the plain command.
        if (profile.SupportsContinueFallback && s.Shell == LaunchShell.Cmd)
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
