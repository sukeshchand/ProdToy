using System.Diagnostics;
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

        string pluginsBinDir = AppPaths.PluginsBinDir;
        if (!Directory.Exists(pluginsBinDir))
        {
            Directory.CreateDirectory(pluginsBinDir);
            return;
        }

        var enabledState = LoadEnabledState();

        // Each subdirectory under plugins/bin/ is a plugin
        foreach (var dir in Directory.GetDirectories(pluginsBinDir))
        {
            var info = DiscoverPlugin(dir);
            if (info == null) continue;

            // Default to enabled for newly discovered plugins
            if (enabledState.TryGetValue(info.Id, out var state))
                info.Enabled = state.Enabled;
            else
                info.Enabled = true;

            _plugins.Add(info);

            if (info.Enabled)
                LoadAndInitialize(info);
        }

        SaveEnabledState();
    }

    /// <summary>
    /// Call Start() on all loaded and enabled plugins.
    /// </summary>
    public static void StartAll()
    {
        foreach (var info in _plugins)
        {
            if (info.Enabled && info.Instance != null)
            {
                try
                {
                    info.Instance.Start();
                }
                catch (Exception ex)
                {
                    info.LoadError = $"Start failed: {ex.Message}";
                    Debug.WriteLine($"Plugin {info.Id} Start failed: {ex}");
                    LogPluginError(info.Id, "Start", ex);
                }
            }
        }
    }

    /// <summary>
    /// Stop and dispose all plugins. Called at app exit.
    /// </summary>
    public static void StopAll()
    {
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
                    Debug.WriteLine($"Plugin {info.Id} Stop/Dispose failed: {ex}");
                }

                info.Instance = null;
            }

            if (info.LoadContext != null)
            {
                info.LoadContext.Unload();
                info.LoadContext = null;
            }
        }
    }

    /// <summary>
    /// Discover a newly installed plugin by ID, load, initialize, and start it.
    /// Called after copying plugin DLL to the plugins directory.
    /// </summary>
    public static bool DiscoverAndLoad(string pluginId)
    {
        // Check if already known
        var existing = _plugins.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            // Already loaded — just enable if disabled
            if (!existing.Enabled)
                return EnablePlugin(pluginId);
            return true;
        }

        // Discover from the plugins directory
        string pluginDir = Path.Combine(AppPaths.PluginsBinDir, pluginId);
        if (!Directory.Exists(pluginDir)) return false;

        var info = DiscoverPlugin(pluginDir);
        if (info == null) return false;

        info.Enabled = true;
        _plugins.Add(info);
        LoadAndInitialize(info);

        if (info.Instance != null)
        {
            try
            {
                info.Instance.Start();
            }
            catch (Exception ex)
            {
                info.LoadError = $"Start failed: {ex.Message}";
                Debug.WriteLine($"Plugin {info.Id} Start failed: {ex}");
                LogPluginError(info.Id, "Start", ex);
            }
        }

        SaveEnabledState();
        PluginsChanged?.Invoke();
        return info.Instance != null;
    }

    /// <summary>
    /// Enable a disabled plugin at runtime (load + init + start).
    /// </summary>
    public static bool EnablePlugin(string pluginId)
    {
        var info = _plugins.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (info == null || info.Enabled) return false;

        info.Enabled = true;
        info.LoadError = null;
        LoadAndInitialize(info);

        if (info.Instance != null)
        {
            try
            {
                info.Instance.Start();
            }
            catch (Exception ex)
            {
                info.LoadError = $"Start failed: {ex.Message}";
                Debug.WriteLine($"Plugin {info.Id} Start failed: {ex}");
            }
        }

        SaveEnabledState();
        PluginsChanged?.Invoke();
        return info.Instance != null;
    }

    /// <summary>
    /// Disable an enabled plugin at runtime (stop + dispose + unload).
    /// </summary>
    public static bool DisablePlugin(string pluginId, bool notify = true)
    {
        var info = _plugins.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (info == null || !info.Enabled) return false;

        info.Enabled = false;

        if (info.Instance != null)
        {
            try
            {
                info.Instance.Stop();
                info.Instance.Dispose();
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Plugin {info.Id} Stop/Dispose failed: {ex}");
            }

            info.Instance = null;
        }

        if (info.LoadContext != null)
        {
            info.LoadContext.Unload();
            info.LoadContext = null;
        }

        SaveEnabledState();
        if (notify) PluginsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Uninstall a plugin: stop it, unload it, delete its folder.
    /// </summary>
    public static bool UninstallPlugin(string pluginId)
    {
        var info = _plugins.Find(p => p.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));
        if (info == null) return false;

        // Disable without firing PluginsChanged (we'll fire once at the end)
        if (info.Enabled)
            DisablePlugin(pluginId, notify: false);

        _plugins.Remove(info);
        SaveEnabledState();

        // Delete plugin folder — files are not locked since we use LoadFromStream
        try
        {
            if (Directory.Exists(info.PluginDirectory))
                Directory.Delete(info.PluginDirectory, recursive: true);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to delete plugin dir: {ex.Message}");
        }

        // Fire once after everything is fully torn down
        PluginsChanged?.Invoke();
        return true;
    }

    /// <summary>
    /// Install a plugin from a DLL or zip path.
    /// Copies to ~/.prod-toy/plugins/{PluginId}/.
    /// </summary>
    public static bool InstallPlugin(string sourceDllPath)
    {
        try
        {
            // Probe the DLL to read its PluginAttribute
            var tempContext = new PluginLoadContext(sourceDllPath);
            var assembly = tempContext.LoadFromStream(new MemoryStream(File.ReadAllBytes(sourceDllPath)));
            var (pluginType, attr) = FindPluginType(assembly);
            tempContext.Unload();

            if (pluginType == null || attr == null)
                return false;

            // Copy to plugins bin directory
            string destDir = Path.Combine(AppPaths.PluginsBinDir, attr.Id);
            Directory.CreateDirectory(destDir);

            string sourceDir = Path.GetDirectoryName(sourceDllPath)!;
            foreach (var file in Directory.GetFiles(sourceDir))
            {
                string destFile = Path.Combine(destDir, Path.GetFileName(file));
                File.Copy(file, destFile, overwrite: true);
            }

            // Discover and register
            var info = DiscoverPlugin(destDir);
            if (info != null)
            {
                info.Enabled = true;
                _plugins.Add(info);
                SaveEnabledState();
            }

            return info != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Plugin install failed: {ex}");
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
                    Debug.WriteLine($"Plugin {info.Id} GetMenuItems failed: {ex}");
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
                    Debug.WriteLine($"Plugin {info.Id} GetMenuItems failed: {ex}");
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
                    Debug.WriteLine($"Plugin {info.Id} GetDashboardItems failed: {ex}");
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
                    Debug.WriteLine($"Plugin {info.Id} GetSettingsPage failed: {ex}");
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
            Debug.WriteLine($"Failed to discover plugin in {pluginDir}: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            info.LoadError = $"Load failed: {ex.Message}";
            Debug.WriteLine($"Plugin {info.Id} load failed: {ex}");
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

    private static Dictionary<string, PluginState> LoadEnabledState()
    {
        try
        {
            string path = AppPaths.PluginsStateFile;
            if (File.Exists(path))
            {
                string json = File.ReadAllText(path);
                return JsonSerializer.Deserialize<Dictionary<string, PluginState>>(json)
                       ?? new Dictionary<string, PluginState>();
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to load plugin state: {ex.Message}");
        }

        return new Dictionary<string, PluginState>();
    }

    private static void SaveEnabledState()
    {
        try
        {
            var state = new Dictionary<string, PluginState>();
            foreach (var info in _plugins)
                state[info.Id] = new PluginState { Enabled = info.Enabled };

            Directory.CreateDirectory(AppPaths.PluginsDataDir);
            string json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(AppPaths.PluginsStateFile, json);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to save plugin state: {ex.Message}");
        }
    }

    private static void LogPluginError(string pluginId, string phase, Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppPaths.LogsDir);
            string logPath = Path.Combine(AppPaths.LogsDir, "plugins.log");
            string line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [ERROR] [{pluginId}] {phase}: {ex.Message}";
            File.AppendAllText(logPath, line + Environment.NewLine);
        }
        catch { }
    }

    private sealed class PluginState
    {
        [JsonPropertyName("enabled")]
        public bool Enabled { get; set; } = true;
    }
}
