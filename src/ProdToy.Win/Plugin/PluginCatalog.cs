using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

namespace ProdToy;

/// <summary>
/// Fetches plugin catalog from a remote URL, downloads and installs plugins.
/// </summary>
static class PluginCatalog
{
    private static readonly HttpClient _http = new();
    private static List<CatalogEntry>? _cachedCatalog;

    /// <summary>
    /// Fetches the catalog manifest from the configured or default URL.
    /// </summary>
    public static async Task<List<CatalogEntry>> FetchCatalogAsync(string? catalogUrl = null)
    {
        try
        {
            string url = catalogUrl;
            if (string.IsNullOrEmpty(url))
            {
                var settings = AppSettings.Load();
                url = string.IsNullOrEmpty(settings.PluginCatalogUrl)
                    ? AppSettingsData.DefaultPluginCatalogUrl
                    : settings.PluginCatalogUrl;
            }

            string json = await _http.GetStringAsync(url);
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
    /// Returns the last fetched catalog without making a network call.
    /// </summary>
    public static List<CatalogEntry> GetCachedCatalog() => _cachedCatalog ?? new List<CatalogEntry>();

    /// <summary>
    /// Downloads a plugin zip from the catalog and installs it.
    /// </summary>
    public static async Task<(bool Success, string Message)> InstallFromCatalogAsync(
        CatalogEntry entry, IProgress<int>? progress = null)
    {
        try
        {
            // Validate host version compatibility
            if (!string.IsNullOrEmpty(entry.MinHostVersion))
            {
                if (!IsVersionCompatible(AppVersion.Current, entry.MinHostVersion))
                    return (false, $"Requires ProdToy v{entry.MinHostVersion} or later (current: v{AppVersion.Current})");
            }

            string destDir = Path.Combine(AppPaths.PluginsDir, entry.Id);

            // Download to temp
            progress?.Report(10);
            string tempPath = Path.Combine(Path.GetTempPath(), $"prodtoy_plugin_{entry.Id}.zip");

            using (var response = await _http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead))
            {
                response.EnsureSuccessStatusCode();
                using var stream = await response.Content.ReadAsStreamAsync();
                using var fileStream = File.Create(tempPath);

                long totalBytes = response.Content.Headers.ContentLength ?? -1;
                long bytesRead = 0;
                var buffer = new byte[8192];
                int read;
                while ((read = await stream.ReadAsync(buffer)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read));
                    bytesRead += read;
                    if (totalBytes > 0)
                        progress?.Report(10 + (int)(70.0 * bytesRead / totalBytes));
                }
            }

            progress?.Report(80);

            // If it's a zip, extract; if it's a single DLL, just copy
            if (tempPath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                IsZipFile(tempPath))
            {
                if (Directory.Exists(destDir))
                    Directory.Delete(destDir, recursive: true);
                Directory.CreateDirectory(destDir);
                ZipFile.ExtractToDirectory(tempPath, destDir);
            }
            else
            {
                // Assume it's a single DLL
                Directory.CreateDirectory(destDir);
                string destDll = Path.Combine(destDir, Path.GetFileName(entry.DownloadUrl));
                File.Copy(tempPath, destDll, overwrite: true);
            }

            // Clean up temp
            try { File.Delete(tempPath); } catch { }

            progress?.Report(100);
            return (true, $"Installed {entry.Name} v{entry.Version}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Plugin install from catalog failed: {ex}");
            return (false, $"Install failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks if any installed plugins have updates available in the catalog.
    /// </summary>
    public static async Task<List<(PluginInfo Installed, CatalogEntry Available)>> CheckForUpdatesAsync()
    {
        var catalog = _cachedCatalog ?? await FetchCatalogAsync();
        var updates = new List<(PluginInfo, CatalogEntry)>();

        foreach (var installed in PluginManager.Plugins)
        {
            var catalogEntry = catalog.FirstOrDefault(c =>
                c.Id.Equals(installed.Id, StringComparison.OrdinalIgnoreCase));

            if (catalogEntry != null && IsNewerVersion(catalogEntry.Version, installed.Version))
                updates.Add((installed, catalogEntry));
        }

        return updates;
    }

    /// <summary>
    /// Updates an installed plugin to the latest catalog version.
    /// </summary>
    public static async Task<(bool Success, string Message)> UpdatePluginAsync(
        string pluginId, IProgress<int>? progress = null)
    {
        var catalog = _cachedCatalog ?? await FetchCatalogAsync();
        var entry = catalog.FirstOrDefault(c => c.Id.Equals(pluginId, StringComparison.OrdinalIgnoreCase));

        if (entry == null)
            return (false, "Plugin not found in catalog");

        // Disable the plugin before updating
        PluginManager.DisablePlugin(pluginId);

        var result = await InstallFromCatalogAsync(entry, progress);

        // Re-enable after install
        if (result.Success)
            PluginManager.EnablePlugin(pluginId);

        return result;
    }

    private static bool IsNewerVersion(string catalogVersion, string installedVersion)
    {
        if (Version.TryParse(catalogVersion, out var cv) && Version.TryParse(installedVersion, out var iv))
            return cv > iv;
        return string.Compare(catalogVersion, installedVersion, StringComparison.Ordinal) > 0;
    }

    private static bool IsVersionCompatible(string hostVersion, string minRequired)
    {
        if (Version.TryParse(hostVersion, out var hv) && Version.TryParse(minRequired, out var mr))
            return hv >= mr;
        return true;
    }

    private static bool IsZipFile(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            var header = new byte[4];
            if (fs.Read(header, 0, 4) == 4)
                return header[0] == 0x50 && header[1] == 0x4B && header[2] == 0x03 && header[3] == 0x04;
        }
        catch { }
        return false;
    }
}
