using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

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

            bool updateAvailable = IsNewerVersion(metadata.Version, AppVersion.Current);

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
            Debug.WriteLine($"Update check failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Checks for updates from a local/network path containing metadata.json and ProdToy.exe.
    /// </summary>
    private static UpdateMetadata? CheckFromLocalPath(string location)
    {
        string metadataPath = Path.Combine(location, "metadata.json");
        if (!File.Exists(metadataPath))
            return null;

        string json = File.ReadAllText(metadataPath);
        return JsonSerializer.Deserialize<UpdateMetadata>(json);
    }

    /// <summary>
    /// Checks for updates from an HTTP URL.
    /// Supports GitHub API format (tag_name, assets[]) and plain metadata.json format.
    /// </summary>
    private static UpdateMetadata? CheckFromUrl(string url)
    {
        string json = _http.GetStringAsync(url).GetAwaiter().GetResult();
        var root = JsonNode.Parse(json);
        if (root == null) return null;

        // GitHub API response: has "tag_name" and "assets" array
        var tagName = root["tag_name"]?.GetValue<string>();
        if (tagName != null)
            return ParseGitHubRelease(root, tagName);

        // Plain metadata.json format served over HTTP
        return JsonSerializer.Deserialize<UpdateMetadata>(json);
    }

    /// <summary>
    /// Parses a GitHub releases API response into UpdateMetadata.
    /// </summary>
    private static UpdateMetadata ParseGitHubRelease(JsonNode root, string tagName)
    {
        // Strip leading 'v' from tag (e.g. "v1.0.196" -> "1.0.196")
        string version = tagName.StartsWith('v') ? tagName[1..] : tagName;

        string releaseNotes = root["body"]?.GetValue<string>() ?? "";
        string publishedAt = root["published_at"]?.GetValue<string>() ?? "";

        // Find ProdToy.exe and ProdToy-v*.zip in assets
        string downloadUrl = "";
        string bundleDownloadUrl = "";
        var assets = root["assets"]?.AsArray();
        if (assets != null)
        {
            foreach (var asset in assets)
            {
                var name = asset?["name"]?.GetValue<string>();
                if (name == null) continue;

                if (string.Equals(name, "ProdToy.exe", StringComparison.OrdinalIgnoreCase))
                    downloadUrl = asset?["browser_download_url"]?.GetValue<string>() ?? "";

                if (name.StartsWith("ProdToy-v", StringComparison.OrdinalIgnoreCase)
                    && name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    bundleDownloadUrl = asset?["browser_download_url"]?.GetValue<string>() ?? "";
            }
        }

        return new UpdateMetadata
        {
            Version = version,
            ReleaseNotes = releaseNotes,
            PublishedAt = publishedAt,
            DownloadUrl = downloadUrl,
            BundleDownloadUrl = bundleDownloadUrl,
        };
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
            Debug.WriteLine($"Update enquiry log failed: {ex.Message}");
        }
    }

    internal static bool IsNewerVersion(string remote, string local)
    {
        if (Version.TryParse(remote, out var remoteVer) && Version.TryParse(local, out var localVer))
            return remoteVer > localVer;
        return false;
    }
}
