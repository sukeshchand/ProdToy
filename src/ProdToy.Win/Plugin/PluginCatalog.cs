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
    /// Fetches the plugin catalog from the configured update location by
    /// reading {UpdateLocation}/metadata.json and mapping each PluginEntry
    /// into a CatalogEntry for the UI. Works for both local/UNC paths and
    /// HTTP update locations.
    /// </summary>
    public static async Task<List<CatalogEntry>> FetchCatalogAsync()
    {
        try
        {
            string location = GetUpdateLocation();
            string metadataJson = await ReadTextAsync(location, "metadata.json");
            if (string.IsNullOrWhiteSpace(metadataJson))
            {
                _cachedCatalog = new List<CatalogEntry>();
                return _cachedCatalog;
            }

            var meta = JsonSerializer.Deserialize<UpdateMetadata>(metadataJson);
            if (meta?.Plugins == null || meta.Plugins.Length == 0)
            {
                _cachedCatalog = new List<CatalogEntry>();
                return _cachedCatalog;
            }

            var mapped = meta.Plugins
                .Where(p => !string.IsNullOrWhiteSpace(p.Id))
                .Select(p => new CatalogEntry
                {
                    Id = p.Id,
                    Name = string.IsNullOrWhiteSpace(p.Name) ? p.Id : p.Name,
                    Version = p.Version,
                    Description = p.ReleaseNotes ?? "",
                    Author = "",
                    DownloadUrl = p.Zip,
                })
                .ToList();
            _cachedCatalog = mapped;
            return mapped;
        }
        catch (Exception ex)
        {
            Log.Error("Failed to fetch plugin catalog", ex);
            return new List<CatalogEntry>();
        }
    }

    /// <summary>
    /// Reads a file from the configured update location.
    /// For HTTP: `location` IS the manifest URL (points directly at metadata.json),
    /// so we fetch it as-is when fileName is "metadata.json".
    /// For local paths: `location` is a directory, append fileName and read the file.
    /// </summary>
    private static async Task<string> ReadTextAsync(string location, string fileName)
    {
        try
        {
            if (IsHttpUrl(location))
            {
                // The configured URL is the direct manifest URL for HTTP updates.
                // Any other fileName isn't served from this flow; caller shouldn't ask.
                return await _http.GetStringAsync(location);
            }
            else
            {
                string path = Path.Combine(location, fileName);
                if (!File.Exists(path)) return "";
                return await File.ReadAllTextAsync(path);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"ReadTextAsync failed for {fileName}: {ex.Message}");
            return "";
        }
    }

    /// <summary>
    /// Installs a plugin by resolving its zip from the configured update location —
    /// local copy for local/UNC paths, HTTP download via AssetDownloader for URLs —
    /// then extracting into PluginsBinDir\{id}\ and hot-loading it via PluginManager.
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallPluginAsync(CatalogEntry entry)
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

            // Resolve the zip path relative to the manifest. For HTTP we treat
            // "location" as the manifest URL and download; for local we join paths.
            string relZip = string.IsNullOrWhiteSpace(entry.DownloadUrl)
                ? $"plugins/{entry.Id}.zip"
                : entry.DownloadUrl;

            string zipToExtract;
            string? tempFileToDelete = null;
            if (IsHttpUrl(location))
            {
                try
                {
                    // No ConfigureAwait(false): the post-await work touches
                    // WinForms via PluginManager.DiscoverAndLoad, so we need
                    // the UI SyncContext preserved for the continuation.
                    tempFileToDelete = await AssetDownloader
                        .DownloadRelativeAssetAsync(location, relZip);
                    zipToExtract = tempFileToDelete;
                }
                catch (Exception ex)
                {
                    return (false, $"Plugin download failed: {ex.Message}");
                }
            }
            else
            {
                zipToExtract = Path.Combine(location, relZip.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(zipToExtract))
                    return (false, $"Plugin zip not found: {zipToExtract}");
            }

            try
            {
                // Extract the zip into the plugin's bin directory.
                string destDir = Path.Combine(AppPaths.PluginsBinDir, entry.Id);
                Directory.CreateDirectory(destDir);
                using (var archive = ZipFile.OpenRead(zipToExtract))
                {
                    foreach (var zipEntry in archive.Entries)
                    {
                        if (string.IsNullOrEmpty(zipEntry.Name)) continue; // skip directory entries
                        string destPath = Path.Combine(destDir, zipEntry.Name);
                        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
                        zipEntry.ExtractToFile(destPath, overwrite: true);
                    }
                }

                // Discover, load, initialize, and start the plugin
                bool loaded = PluginManager.DiscoverAndLoad(entry.Id);

                return loaded
                    ? (true, $"Installed {entry.Name} v{entry.Version}")
                    : (false, "Files extracted but plugin failed to load. Restart app to activate.");
            }
            finally
            {
                if (tempFileToDelete != null)
                {
                    try { File.Delete(tempFileToDelete); } catch { }
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Plugin install failed", ex);
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
            Log.Error("Plugin uninstall failed", ex);
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
