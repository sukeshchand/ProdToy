using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Discovers, loads, and manages the lifecycle of all plugins.
/// </summary>
static class PluginManager
{
    private static readonly List<PluginInfo> _plugins = new();
    private static PluginHostImpl? _host;

    public static IReadOnlyList<PluginInfo> Plugins => _plugins;

    /// <summary>
    /// Snapshot of installed plugin id → version (from each PluginInfo's discovered
    /// version attribute). Returns an empty dict if Initialize has not run yet.
    /// </summary>
    public static IReadOnlyDictionary<string, string> GetInstalledVersions()
    {
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in _plugins)
            dict[info.Id] = info.Version;
        return dict;
    }

    /// <summary>Fires when plugins are installed, uninstalled, enabled, or disabled.</summary>
    public static event Action? PluginsChanged;

    /// <summary>
    /// Discover all plugin DLLs in the plugins directory and load enabled ones.
    /// Called once at startup.
    /// </summary>
    public static void Initialize(PluginHostImpl host)
    {
        _host = host;
        _installedIds = LoadInstalledIds();

        // In --dev mode, scan each plugin project's build output directly; in
        // installed mode, scan the single installed plugins-bin folder.
        List<string> scanDirs;
        if (DevMode.IsEnabled)
        {
            scanDirs = DevMode.GetPluginDiscoveryDirs();
            Log.Info($"PluginManager.Initialize (dev): {scanDirs.Count} build-output dir(s)");
        }
        else
        {
            string pluginsBinDir = AppPaths.PluginsBinDir;
            if (!Directory.Exists(pluginsBinDir))
            {
                Directory.CreateDirectory(pluginsBinDir);
                Log.Info($"PluginManager.Initialize: created empty plugins bin dir at {pluginsBinDir}");
                return;
            }
            Log.Info($"PluginManager.Initialize: scanning {pluginsBinDir}");
            scanDirs = Directory.GetDirectories(pluginsBinDir).ToList();
        }

        foreach (var dir in scanDirs)
        {
            var info = DiscoverPlugin(dir);
            if (info == null) continue;

            info.Enabled = true;
            _plugins.Add(info);

            LoadAndInitialize(info);

            // First-time install: if this plugin ID hasn't been installed on
            // this machine yet (or its state entry was lost), call Install()
            // now so it can register hook scripts, third-party config entries,
            // etc. Install() runs exactly once; subsequent host launches just
            // call Start().
            if (info.Instance != null && !_installedIds.Contains(info.Id))
            {
                TryInstall(info);
            }
        }

        SaveInstalledIds();

        int loaded = _plugins.Count(p => p.Instance != null);
        int failed = _plugins.Count - loaded;
        Log.Info($"PluginManager.Initialize: {loaded} loaded, {failed} failed to load");
    }

    /// <summary>
    /// Runs Install() on a freshly-installed plugin and records it as installed
    /// in plugins-state.json. Errors are logged but do not throw — Install() is
    /// best-effort (the plugin may still function without its external-system
    /// state if, e.g., the user denies a permission prompt).
    /// </summary>
    private static void TryInstall(PluginInfo info)
    {
        if (info.Instance == null) return;
        try
        {
            var context = new PluginContextImpl(_host!, info.Metadata!);
            info.Instance.Install(context);
            _installedIds.Add(info.Id);
            Log.Tagged("INFO", info.Id, "Install() completed");
        }
        catch (Exception ex)
        {
            LogPluginError(info.Id, "Install", ex);
        }
    }

    /// <summary>
    /// Call Start() on all loaded and enabled plugins.
    /// </summary>
    public static void StartAll()
    {
        int started = 0, failed = 0;
        foreach (var info in _plugins)
        {
            if (info.Enabled && info.Instance != null)
            {
                try
                {
                    info.Instance.Start();
                    started++;
                }
                catch (Exception ex)
                {
                    info.LoadError = $"Start failed: {ex.Message}";
                    LogPluginError(info.Id, "Start", ex);
                    failed++;
                }
            }
        }
        Log.Info($"PluginManager.StartAll: started {started}, failed {failed}");
    }

    /// <summary>
    /// Stop and dispose all plugins. Called at app exit.
    /// </summary>
    public static void StopAll()
    {
        Log.Info($"PluginManager.StopAll: stopping {_plugins.Count} plugin(s)");
        foreach (var info in _plugins)
        {
            if (info.Instance != null)
            {
                try
                {
                    info.Instance.Stop();
                    info.Instance.Dispose();
                }
                catch (Exception ex)
                {
                    Log.Tagged("ERROR", info.Id, $"Stop/Dispose failed: {ex}");
                }

                info.Instance = null;
            }

            if (info.LoadContext != null)
            {
                try
                {
                    info.LoadContext.Unload();
                }
                catch (Exception ex)
                {
                    Log.Tagged("WARN", info.Id, $"AssemblyLoadContext.Unload failed: {ex.Message}");
                }
                info.LoadContext = null;
            }
        }
    }

    /// <summary>
    /// Discover a newly installed plugin by ID, load, initialize, and start it.
    /// Called after copying plugin DLL to the plugins directory. Runs Install()
    /// on first discovery so the plugin can register its external-system state.
    /// </summary>
    public static bool DiscoverAndLoad(string pluginId)
    {
        var existing = _plugins.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (existing != null) return true;

        string pluginDir = Path.Combine(AppPaths.PluginsBinDir, pluginId);
        if (!Directory.Exists(pluginDir)) return false;

        var info = DiscoverPlugin(pluginDir);
        if (info == null) return false;

        info.Enabled = true;
        _plugins.Add(info);
        LoadAndInitialize(info);

        if (info.Instance != null && !_installedIds.Contains(info.Id))
            TryInstall(info);

        if (info.Instance != null)
        {
            try
            {
                info.Instance.Start();
            }
            catch (Exception ex)
            {
                info.LoadError = $"Start failed: {ex.Message}";
                LogPluginError(info.Id, "Start", ex);
            }
        }

        SaveInstalledIds();
        PluginsChanged?.Invoke();
        return info.Instance != null;
    }

    /// <summary>Internal: stop + dispose + unload without deleting files.
    /// Used by UninstallPlugin before removing the bin directory.</summary>
    private static void TearDownInstance(PluginInfo info)
    {
        if (info.Instance != null)
        {
            try
            {
                info.Instance.Stop();
                info.Instance.Dispose();
            }
            catch (Exception ex)
            {
                Log.Tagged("ERROR", info.Id, $"Stop/Dispose failed: {ex}");
            }
            info.Instance = null;
        }

        if (info.LoadContext != null)
        {
            try
            {
                info.LoadContext.Unload();
            }
            catch (Exception ex)
            {
                Log.Tagged("WARN", info.Id, $"AssemblyLoadContext.Unload failed: {ex.Message}");
            }
            info.LoadContext = null;
        }

        info.Enabled = false;
    }

    /// <summary>
    /// Uninstall a plugin: call its Uninstall() hook, stop it, unload it,
    /// delete its bin folder, remove it from installed-ids. Plugin data
    /// under data/plugins/{PluginId}/ is preserved (survives reinstall).
    /// </summary>
    public static bool UninstallPlugin(string pluginId)
    {
        var info = _plugins.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (info == null) return false;

        // Call Uninstall() so the plugin can strip its external-system state
        // (hook scripts, third-party config entries, etc.) before we tear it down.
        if (info.Instance != null && info.Metadata != null && _host != null)
        {
            try
            {
                var context = new PluginContextImpl(_host, info.Metadata);
                info.Instance.Uninstall(context);
                Log.Tagged("INFO", info.Id, "Uninstall() completed");
            }
            catch (Exception ex)
            {
                LogPluginError(info.Id, "Uninstall", ex);
            }
        }

        TearDownInstance(info);
        _plugins.Remove(info);
        _installedIds.Remove(info.Id);
        SaveInstalledIds();

        // Delete plugin bin folder — files are not locked since we use LoadFromStream.
        try
        {
            if (Directory.Exists(info.PluginDirectory))
                Directory.Delete(info.PluginDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Log.Tagged("WARN", info.Id, $"Failed to delete plugin dir: {ex.Message}");
        }

        PluginsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Install a plugin from a DLL path. Copies files to
    /// ~/.prod-toy/plugins/bin/{PluginId}/ and defers discovery/lifecycle
    /// to <see cref="DiscoverAndLoad"/>, which runs Initialize+Install+Start.
    /// </summary>
    public static bool InstallPlugin(string sourceDllPath)
    {
        try
        {
            var tempContext = new PluginLoadContext(sourceDllPath);
            var assembly = tempContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(sourceDllPath)));
            var (pluginType, attr) = FindPluginType(assembly);
            tempContext.Unload();

            if (pluginType == null || attr == null)
                return false;

            string destDir = Path.Combine(AppPaths.PluginsBinDir, attr.Id);
            Directory.CreateDirectory(destDir);

            string sourceDir = Path.GetDirectoryName(sourceDllPath)!;
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            return DiscoverAndLoad(attr.Id);
        }
        catch (Exception ex)
        {
            Log.Error("PluginManager.InstallPlugin (from dll path) failed", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns all menu contributions from enabled plugins, sorted by priority.
    /// </summary>
    public static List<MenuContribution> GetAllMenuItems()
    {
        var items = new List<MenuContribution>();
        foreach (var info in _plugins)
        {
            if (info.Enabled && info.Instance != null)
            {
                try
                {
                    items.AddRange(info.Instance.GetMenuItems());
                }
                catch (Exception ex)
                {
                    Log.Tagged("ERROR", info.Id, $"GetMenuItems failed: {ex}");
                }
            }
        }

        items.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        return items;
    }

    /// <summary>
    /// Returns menu items grouped by plugin, only for plugins that have menu items.
    /// </summary>
    public static List<(PluginInfo Plugin, List<MenuContribution> Items)> GetGroupedMenuItems()
    {
        var result = new List<(PluginInfo, List<MenuContribution>)>();
        foreach (var info in _plugins)
        {
            if (info.Enabled && info.Instance != null)
            {
                try
                {
                    var items = info.Instance.GetMenuItems();
                    var visible = items.Where(i => i.Visible).ToList();
                    if (visible.Count > 0)
                        result.Add((info, visible));
                }
                catch (Exception ex)
                {
                    Log.Tagged("ERROR", info.Id, $"GetMenuItems failed: {ex}");
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Returns dashboard items grouped by plugin, only for plugins that have dashboard items.
    /// </summary>
    public static List<(PluginInfo Plugin, List<MenuContribution> Items)> GetAllDashboardItems()
    {
        var result = new List<(PluginInfo, List<MenuContribution>)>();
        foreach (var info in _plugins)
        {
            if (info.Enabled && info.Instance != null)
            {
                try
                {
                    var items = info.Instance.GetDashboardItems();
                    var visible = items.Where(i => i.Visible).ToList();
                    if (visible.Count > 0)
                        result.Add((info, visible));
                }
                catch (Exception ex)
                {
                    Log.Tagged("ERROR", info.Id, $"GetDashboardItems failed: {ex}");
                }
            }
        }
        return result;
    }

    /// <summary>
    /// Returns all settings page contributions from enabled plugins.
    /// </summary>
    public static List<(PluginInfo Plugin, SettingsPageContribution Page)> GetAllSettingsPages()
    {
        var pages = new List<(PluginInfo, SettingsPageContribution)>();
        foreach (var info in _plugins)
        {
            if (info.Enabled && info.Instance != null)
            {
                try
                {
                    var page = info.Instance.GetSettingsPage();
                    if (page != null)
                        pages.Add((info, page));
                }
                catch (Exception ex)
                {
                    Log.Tagged("ERROR", info.Id, $"GetSettingsPage failed: {ex}");
                }
            }
        }

        pages.Sort((a, b) => a.Item2.TabOrder.CompareTo(b.Item2.TabOrder));
        return pages;
    }

    // --- Private helpers ---

    private static PluginInfo? DiscoverPlugin(string pluginDir)
    {
        try
        {
            // Look for a DLL matching the directory name, or any DLL with [Plugin] attribute
            string dirName = Path.GetFileName(pluginDir);
            string? dllPath = Path.Combine(pluginDir, $"{dirName}.dll");

            if (!File.Exists(dllPath))
            {
                // Try ProdToy.Plugins.{dirName}.dll
                dllPath = Path.Combine(pluginDir, $"ProdToy.Plugins.{dirName}.dll");
            }

            if (!File.Exists(dllPath))
            {
                // Fallback: find any DLL in the directory
                var dlls = Directory.GetFiles(pluginDir, "*.dll");
                dllPath = dlls.FirstOrDefault(d =>
                    !Path.GetFileName(d).Equals("ProdToy.Sdk.dll", StringComparison.OrdinalIgnoreCase));
            }

            if (dllPath == null || !File.Exists(dllPath))
                return null;

            // Peek at metadata without fully loading
            var tempContext = new PluginLoadContext(dllPath);
            var assembly = tempContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(dllPath)));
            var (_, attr) = FindPluginType(assembly);
            tempContext.Unload();

            if (attr == null)
                return null;

            return new PluginInfo
            {
                Id = attr.Id,
                Name = attr.Name,
                Version = attr.Version,
                Description = attr.Description,
                Author = attr.Author,
                DllPath = dllPath,
                PluginDirectory = pluginDir,
            };
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to discover plugin in {pluginDir}: {ex.Message}");
            return null;
        }
    }

    private static void LoadAndInitialize(PluginInfo info)
    {
        if (_host == null) return;

        try
        {
            var loadContext = new PluginLoadContext(info.DllPath);
            var assembly = loadContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(info.DllPath)));
            var (pluginType, attr) = FindPluginType(assembly);

            if (pluginType == null || attr == null)
            {
                info.LoadError = "No IPlugin implementation with [Plugin] attribute found";
                loadContext.Unload();
                return;
            }

            var instance = (IPlugin)Activator.CreateInstance(pluginType)!;
            var context = new PluginContextImpl(_host, attr);

            instance.Initialize(context);

            info.Instance = instance;
            info.LoadContext = loadContext;
            info.Metadata = attr;
            info.LoadError = null;
            Log.Tagged("INFO", info.Id, $"Loaded v{info.Version} from {info.DllPath}");
        }
        catch (Exception ex)
        {
            info.LoadError = $"Load failed: {ex.Message}";
            LogPluginError(info.Id, "Load", ex);
        }
    }

    private static (Type? PluginType, PluginAttribute? Attr) FindPluginType(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (!typeof(IPlugin).IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                continue;

            var attr = type.GetCustomAttribute<PluginAttribute>();
            if (attr != null)
                return (type, attr);
        }

        return (null, null);
    }

    // --- State persistence ---
    //
    // plugins-state.json tracks which plugins have had Install() called on
    // this machine. A plugin is "installed" once its external-system state
    // (hook scripts, third-party config entries, etc.) has been written.
    // Install() runs exactly once per plugin on the machine; Uninstall()
    // runs exactly once at removal time. Start()/Stop() are unrelated to
    // this file — they run on every host launch regardless.

    private sealed class PluginsStateFile
    {
        [JsonPropertyName("installedIds")]
        public List<string> InstalledIds { get; set; } = new();
    }

    private static HashSet<string> _installedIds = new(StringComparer.OrdinalIgnoreCase);

    private static HashSet<string> LoadInstalledIds()
    {
        try
        {
            string path = AppPaths.PluginsStateFile;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                var state = JsonSerializer.Deserialize<PluginsStateFile>(json);
                if (state?.InstalledIds != null)
                    return new HashSet<string>(state.InstalledIds, StringComparer.OrdinalIgnoreCase);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Failed to load plugin state: {ex.Message}");
        }
        return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    }

    private static void SaveInstalledIds()
    {
        try
        {
            var state = new PluginsStateFile { InstalledIds = _installedIds.ToList() };
            state.InstalledIds.Sort(StringComparer.OrdinalIgnoreCase);
            Directory.CreateDirectory(AppPaths.PluginsDataDir);
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.PluginsStateFile, json);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save plugin state", ex);
        }
    }

    private static void LogPluginError(string pluginId, string phase, Exception ex)
        => Log.Tagged("ERROR", pluginId, $"{phase}: {ex}");

}
