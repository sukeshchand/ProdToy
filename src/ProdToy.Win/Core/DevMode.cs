namespace ProdToy;

/// <summary>
/// Developer mode — toggled via the <c>--dev</c> CLI flag. Bypasses the
/// "must be installed + running from install dir" gate in Program.cs and
/// redirects plugin discovery to each plugin project's <c>bin/{Config}/</c>
/// build output so F5 from Visual Studio Just Works without copying DLLs.
///
/// <para>Data directories still resolve to <see cref="AppPaths.Root"/>
/// (<c>%USERPROFILE%\.prod-toy\</c>). That's intentional — the debugger
/// runs against real user data.</para>
/// </summary>
static class DevMode
{
    public static bool IsEnabled { get; private set; }

    public static void Enable()
    {
        IsEnabled = true;
        Log.Info("DevMode enabled — plugins will be discovered from build outputs");
    }

    /// <summary>
    /// Returns one directory per plugin project, each pointing at its build
    /// output ({proj}/bin/Debug/net8.0-windows/ preferred, falls back to
    /// Release). Used by <see cref="PluginManager"/> as the scan root list in
    /// place of the single installed <see cref="AppPaths.PluginsBinDir"/>.
    /// </summary>
    public static List<string> GetPluginDiscoveryDirs()
    {
        var root = FindRepoRoot();
        if (root == null)
        {
            Log.Warn("DevMode: couldn't locate repo root from running exe — no plugins will load");
            return new List<string>();
        }

        var pluginsRoot = Path.Combine(root, "src", "Plugins");
        if (!Directory.Exists(pluginsRoot))
        {
            Log.Warn($"DevMode: {pluginsRoot} doesn't exist — no plugins will load");
            return new List<string>();
        }

        var result = new List<string>();
        foreach (var proj in Directory.GetDirectories(pluginsRoot))
        {
            // Prefer Debug (fresh dev build), fall back to Release.
            foreach (var config in new[] { "Debug", "Release" })
            {
                var dir = Path.Combine(proj, "bin", config, "net8.0-windows");
                if (Directory.Exists(dir))
                {
                    result.Add(dir);
                    break;
                }
            }
        }
        Log.Info($"DevMode: discovered {result.Count} plugin build output dir(s)");
        return result;
    }

    /// <summary>
    /// Walk up from the running exe's directory looking for the repo root
    /// (a parent that contains <c>src/Plugins/</c>). Returns null if not
    /// found — happens when running from a published copy, in which case
    /// DevMode is meaningless.
    /// </summary>
    private static string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 10 && !string.IsNullOrEmpty(dir); i++)
        {
            if (Directory.Exists(Path.Combine(dir, "src", "Plugins")))
                return dir;
            dir = Path.GetDirectoryName(dir);
        }
        return null;
    }
}
