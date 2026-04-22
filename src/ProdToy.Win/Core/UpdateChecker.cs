using System.Text;
using System.Text.Json;

namespace ProdToy;

static class UpdateChecker
{
    private static System.Threading.Timer? _timer;
    private static UpdateMetadata? _latestMetadata;
    private static readonly HttpClient _http = CreateHttpClient();

    public static event Action<UpdateMetadata>? UpdateAvailable;

    public static UpdateMetadata? LatestMetadata => _latestMetadata;

    public static void Start()
    {
        // Check immediately, then every hour
        _timer = new System.Threading.Timer(_ => CheckForUpdate(), null, TimeSpan.Zero, TimeSpan.FromHours(1));
    }

    public static void Stop()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public static void CheckNow() => CheckForUpdate();

    private static HttpClient CreateHttpClient()
    {
        var client = new HttpClient();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("ProdToy/" + AppVersion.Current);
        client.Timeout = TimeSpan.FromSeconds(15);
        return client;
    }

    /// <summary>
    /// Resolves the effective update location. If empty, returns the default GitHub URL.
    /// </summary>
    internal static string ResolveUpdateLocation(string updateLocation)
    {
        return string.IsNullOrWhiteSpace(updateLocation)
            ? AppSettingsData.DefaultUpdateLocation
            : updateLocation.Trim();
    }

    private static bool IsHttpUrl(string location)
    {
        return location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    private static void CheckForUpdate()
    {
        try
        {
            var settings = AppSettings.Load();
            string location = ResolveUpdateLocation(settings.UpdateLocation);

            UpdateMetadata? metadata;

            if (IsHttpUrl(location))
                metadata = CheckFromUrl(location);
            else
                metadata = CheckFromLocalPath(location);

            if (metadata == null || string.IsNullOrWhiteSpace(metadata.Version))
                return;

            bool hostNewer = IsNewerVersion(metadata.Version, AppVersion.Current);
            bool anyPluginNewer = HasPluginUpdate(metadata);
            bool updateAvailable = hostNewer || anyPluginNewer;

            // Fire-and-forget: log the enquiry silently (only for local paths)
            if (!IsHttpUrl(location))
                _ = LogUpdateEnquiryAsync(location, metadata.Version, updateAvailable);

            if (updateAvailable)
            {
                _latestMetadata = metadata;
                UpdateAvailable?.Invoke(metadata);
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Returns true if any plugin in the manifest is a newer version than what's
    /// installed, OR is a brand-new plugin not present locally. Returns false if
    /// the manifest has no plugins[] (old format / HTTP).
    /// </summary>
    private static bool HasPluginUpdate(UpdateMetadata metadata)
    {
        if (metadata.Plugins == null || metadata.Plugins.Length == 0)
            return false;

        var installed = PluginManager.GetInstalledVersions();
        // If the plugin manager hasn't initialized yet, don't claim updates we can't verify.
        if (installed.Count == 0)
            return false;

        foreach (var p in metadata.Plugins)
        {
            if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Version))
                continue;
            if (!installed.TryGetValue(p.Id, out var localVer))
                continue; // plugin not installed locally → not an update for this user
            if (IsNewerVersion(p.Version, localVer))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Load metadata.json from a local/UNC path. Returns null if the file is missing.
    /// </summary>
    private static UpdateMetadata? CheckFromLocalPath(string location)
    {
        string metadataPath = Path.Combine(location, "metadata.json");
        if (!File.Exists(metadataPath))
            return null;

        string json = File.ReadAllText(metadataPath);
        var meta = JsonSerializer.Deserialize<UpdateMetadata>(json);
        // ManifestUrl stays empty for local paths — Updater uses `location` directly.
        return meta;
    }

    /// <summary>
    /// Fetch metadata.json over HTTP. The URL is expected to point directly at the
    /// JSON file (e.g. https://github.com/.../releases/latest/download/metadata.json).
    /// Stores the URL on the returned record so the Updater can resolve relative
    /// asset paths against it.
    /// </summary>
    private static UpdateMetadata? CheckFromUrl(string url)
    {
        string json = _http.GetStringAsync(url).GetAwaiter().GetResult();
        var meta = JsonSerializer.Deserialize<UpdateMetadata>(json);
        if (meta == null) return null;
        return meta with { ManifestUrl = url };
    }

    private static async Task LogUpdateEnquiryAsync(string updateLocation, string remoteVersion, bool updateAvailable)
    {
        try
        {
            string logsDir = Path.Combine(updateLocation, "_logs");
            Directory.CreateDirectory(logsDir);

            string fileName = $"{DateTime.Now:yyyyMMdd}_UpdateEnquiry.log";
            string logPath = Path.Combine(logsDir, fileName);

            string user = Environment.UserName;
            string machine = Environment.MachineName;
            string timestamp = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss");
            string status = updateAvailable ? "Update Available" : "Up to Date";
            string logLine = $"{timestamp} | User: {user} | PC: {machine} | Current: {AppVersion.Current} | Remote: {remoteVersion} | {status}{Environment.NewLine}";

            byte[] data = Encoding.UTF8.GetBytes(logLine);

            // Retry with delay to handle concurrent file access from multiple instances
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    using var fs = new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
                    await fs.WriteAsync(data);
                    await fs.FlushAsync();
                    return;
                }
                catch (IOException)
                {
                    // File likely locked by another instance, wait and retry
                    await Task.Delay(500 * (attempt + 1));
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn($"Update enquiry log failed: {ex.Message}");
        }
    }

    internal static bool IsNewerVersion(string remote, string local)
    {
        if (Version.TryParse(remote, out var remoteVer) && Version.TryParse(local, out var localVer))
            return remoteVer > localVer;
        return false;
    }
}
