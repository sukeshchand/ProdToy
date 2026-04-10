using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace ProdToy;

/// <summary>
/// Plugin catalog: fetches available plugins, installs from update path, uninstalls.
/// Uses the same UpdateLocation as the host updater for plugin source.
/// </summary>
static class PluginCatalog
{
    private static readonly HttpClient _http = new();
    private static List<CatalogEntry>? _cachedCatalog;

    /// <summary>
    /// Returns the resolved update location (local path or HTTP URL).
    /// </summary>
    private static string GetUpdateLocation()
    {
        var settings = AppSettings.Load();
        return UpdateChecker.ResolveUpdateLocation(settings.UpdateLocation);
    }

    private static bool IsHttpUrl(string location) =>
        location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Fetches the catalog from the update location.
    /// Local path: reads {UpdateLocation}/plugin-catalog.json
    /// HTTP: fetches from configured catalog URL or default GitHub URL
    /// </summary>
    public static async Task<List<CatalogEntry>> FetchCatalogAsync()
    {
        try
        {
            string location = GetUpdateLocation();

            string json;
            if (IsHttpUrl(location))
            {
                // For HTTP, use dedicated catalog URL
                var settings = AppSettings.Load();
                string catalogUrl = string.IsNullOrEmpty(settings.PluginCatalogUrl)
                    ? AppSettingsData.DefaultPluginCatalogUrl
                    : settings.PluginCatalogUrl;
                json = await _http.GetStringAsync(catalogUrl);
            }
            else
            {
                // Local/network: read plugin-catalog.json from update path
                string catalogPath = Path.Combine(location, "plugin-catalog.json");
                if (!File.Exists(catalogPath))
                {
                    _cachedCatalog = new List<CatalogEntry>();
                    return _cachedCatalog;
                }
                json = await File.ReadAllTextAsync(catalogPath);
            }

            var manifest = JsonSerializer.Deserialize<PluginCatalogManifest>(json);
            _cachedCatalog = manifest?.Plugins ?? new List<CatalogEntry>();
            return _cachedCatalog;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to fetch plugin catalog: {ex.Message}");
            return new List<CatalogEntry>();
        }
    }

    /// <summary>
    /// Installs a plugin by copying from the update location's plugins directory,
    /// then loads and starts it via PluginManager.
    /// </summary>
    public static (bool Success, string Message) InstallPlugin(CatalogEntry entry)
    {
        try
        {
            // Validate host version
            if (!string.IsNullOrEmpty(entry.MinHostVersion))
            {
                if (!IsVersionCompatible(AppVersion.Current, entry.MinHostVersion))
                    return (false, $"Requires ProdToy v{entry.MinHostVersion}+");
            }

            string location = GetUpdateLocation();
            string sourceDir = Path.Combine(location, "plugins", entry.Id);

            if (!Directory.Exists(sourceDir))
                return (false, $"Plugin source not found: {sourceDir}");

            // Copy to install directory (plugins/bin/{id}/)
            string destDir = Path.Combine(AppPaths.PluginsBinDir, entry.Id);
            Directory.CreateDirectory(destDir);
            foreach (var file in Directory.GetFiles(sourceDir))
                File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: true);

            // Discover, load, initialize, and start the plugin
            bool loaded = PluginManager.DiscoverAndLoad(entry.Id);

            return loaded
                ? (true, $"Installed {entry.Name} v{entry.Version}")
                : (false, $"Files copied but plugin failed to load. Restart app to activate.");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Plugin install failed: {ex}");
            return (false, $"Install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Uninstalls a plugin: stops, unloads, and deletes from disk.
    /// </summary>
    public static (bool Success, string Message) UninstallPlugin(string pluginId, string pluginName)
    {
        try
        {
            PluginManager.UninstallPlugin(pluginId);
            return (true, $"Uninstalled {pluginName}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Plugin uninstall failed: {ex}");
            return (false, $"Uninstall failed: {ex.Message}");
        }
    }

    private static bool IsVersionCompatible(string hostVersion, string minRequired)
    {
        if (Version.TryParse(hostVersion, out var hv) && Version.TryParse(minRequired, out var mr))
            return hv >= mr;
        return true;
    }

    public static bool IsNewerVersion(string catalogVersion, string installedVersion)
    {
        if (Version.TryParse(catalogVersion, out var cv) && Version.TryParse(installedVersion, out var iv))
            return cv > iv;
        return string.Compare(catalogVersion, installedVersion, StringComparison.Ordinal) > 0;
    }
}
