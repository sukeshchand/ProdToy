using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Text;

namespace ProdToy;

static class Updater
{
    public record UpdateResult(bool Success, string Message);

    private static readonly HttpClient _http = new()
    {
        Timeout = TimeSpan.FromMinutes(5),
    };

    public static UpdateResult Apply()
    {
        var sw = Stopwatch.StartNew();
        try
        {
            Log.Info("Updater.Apply started");
            var settings = AppSettings.Load();
            string location = UpdateChecker.ResolveUpdateLocation(settings.UpdateLocation);

            string installDir = Path.GetDirectoryName(Application.ExecutablePath)!;
            string currentExe = Application.ExecutablePath;
            string pluginsInstallDir = AppPaths.PluginsBinDir;
            int currentPid = Environment.ProcessId;

            bool isHttp = location.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                       || location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

            Log.Info($"Update location={location} isHttp={isHttp} installDir={installDir} pid={currentPid}");

            // Re-read the freshest metadata directly (don't trust the cached one).
            Log.Info("Loading fresh metadata.json");
            UpdateMetadata? freshMetadata = LoadFreshMetadata(location, isHttp);
            if (freshMetadata == null)
            {
                Log.Warn("metadata.json not found or invalid");
                return new UpdateResult(false, $"metadata.json not found or invalid at {location}");
            }
            Log.Info($"Fresh metadata loaded: host v{freshMetadata.Version}, {freshMetadata.Plugins?.Length ?? 0} plugin(s), hostZip={freshMetadata.HostZip}");

            // Prepare a clean tmp working dir under ~/.prod-toy/tmp/.
            string tmpRoot = AppPaths.TmpDir;
            if (Directory.Exists(tmpRoot))
            {
                try { Directory.Delete(tmpRoot, recursive: true); Log.Info($"Cleaned tmp dir {tmpRoot}"); }
                catch (Exception ex) { Log.Warn($"Failed to clean tmp: {ex.Message}"); }
            }
            Directory.CreateDirectory(tmpRoot);

            // Stage host zip → tmp/host/ProdToy.exe (only if a newer host is published).
            bool hostNeedsUpdate = !string.IsNullOrWhiteSpace(freshMetadata.Version)
                && IsNewerVersion(freshMetadata.Version, AppVersion.Current);
            Log.Info($"Host update needed: {hostNeedsUpdate} (current={AppVersion.Current}, remote={freshMetadata.Version})");
            string stagedHostExe = "";
            if (hostNeedsUpdate && !string.IsNullOrWhiteSpace(freshMetadata.HostZip))
            {
                string hostStaging = Path.Combine(tmpRoot, "host");
                Directory.CreateDirectory(hostStaging);

                try
                {
                    Log.Info($"Fetching host zip: {freshMetadata.HostZip} (expected sha256={(string.IsNullOrWhiteSpace(freshMetadata.HostSha256) ? "<none>" : freshMetadata.HostSha256.Substring(0, 12) + "...")})");
                    var fetchSw = Stopwatch.StartNew();
                    FetchAndExtractZip(isHttp, location, freshMetadata.ManifestUrl,
                        freshMetadata.HostZip, freshMetadata.HostSha256, hostStaging);
                    Log.Info($"Host zip extracted to {hostStaging} in {fetchSw.ElapsedMilliseconds}ms");
                }
                catch (Exception ex)
                {
                    Log.Error("Host zip fetch failed", ex);
                    return new UpdateResult(false, $"Host zip fetch failed: {ex.Message}");
                }

                stagedHostExe = Path.Combine(hostStaging, "ProdToy.exe");
                if (!File.Exists(stagedHostExe))
                {
                    string? found = FindFileRecursive(hostStaging, "ProdToy.exe");
                    if (found == null)
                    {
                        Log.Error("ProdToy.exe missing inside host zip");
                        return new UpdateResult(false, "ProdToy.exe missing inside host zip.");
                    }
                    stagedHostExe = found;
                }
                Log.Info($"Staged host exe: {stagedHostExe}");
            }

            // Stage plugin zips → tmp/plugins/{id}/ — only those that are newer or missing locally.
            var installedVersions = PluginManager.GetInstalledVersions();
            string pluginsStagingRoot = Path.Combine(tmpRoot, "plugins");
            int pluginsStaged = 0;
            var stagedPluginIds = new List<string>();
            foreach (var p in freshMetadata.Plugins ?? Array.Empty<PluginEntry>())
            {
                if (string.IsNullOrWhiteSpace(p.Id) || string.IsNullOrWhiteSpace(p.Zip))
                    continue;

                bool localExists = installedVersions.TryGetValue(p.Id, out var localVer);
                bool isNewer = localExists && IsNewerVersion(p.Version, localVer ?? "");
                Log.Info($"Plugin {p.Id}: installed={localVer ?? "<none>"} remote={p.Version} willStage={localExists && isNewer}");
                // Only stage plugins that are installed AND newer remotely.
                // New-but-not-installed plugins are not auto-installed by Update.
                if (!localExists || !isNewer) continue;

                string pluginDest = Path.Combine(pluginsStagingRoot, p.Id);
                Directory.CreateDirectory(pluginDest);

                try
                {
                    Log.Info($"Fetching plugin zip: {p.Zip} (expected sha256={(string.IsNullOrWhiteSpace(p.Sha256) ? "<none>" : p.Sha256.Substring(0, 12) + "...")})");
                    var pluginSw = Stopwatch.StartNew();
                    FetchAndExtractZip(isHttp, location, freshMetadata.ManifestUrl,
                        p.Zip, p.Sha256, pluginDest);
                    Log.Info($"Plugin {p.Id} extracted to {pluginDest} in {pluginSw.ElapsedMilliseconds}ms");
                    pluginsStaged++;
                    stagedPluginIds.Add(p.Id);
                }
                catch (Exception ex)
                {
                    Log.Error($"Plugin zip fetch failed for {p.Id}", ex);
                    // Best-effort: skip this plugin, keep going with others.
                }
            }

            Log.Info($"Plugins staged: {pluginsStaged}");
            if (!hostNeedsUpdate && pluginsStaged == 0)
            {
                Log.Warn("Nothing new to update");
                return new UpdateResult(false, "Nothing new to update.");
            }

            // Build a rollback snapshot BEFORE the PS1 swaps anything. The snapshot
            // captures only what this run is about to replace — the current host exe
            // (if being updated) and the current bin dir of every plugin being
            // updated. PS1 restores from here on any Phase 2/3 error.
            string snapshotRoot = Path.Combine(tmpRoot, "snapshot");
            Directory.CreateDirectory(snapshotRoot);
            try
            {
                if (hostNeedsUpdate && File.Exists(currentExe))
                {
                    string snapshotHostDir = Path.Combine(snapshotRoot, "host");
                    Directory.CreateDirectory(snapshotHostDir);
                    File.Copy(currentExe, Path.Combine(snapshotHostDir, "ProdToy.exe"), overwrite: true);
                    Log.Info($"Snapshot: host exe → {snapshotHostDir}");
                }
                foreach (var id in stagedPluginIds)
                {
                    string src = Path.Combine(pluginsInstallDir, id);
                    if (!Directory.Exists(src)) continue;
                    string dst = Path.Combine(snapshotRoot, "plugins", id);
                    Directory.CreateDirectory(dst);
                    foreach (var f in Directory.GetFiles(src))
                        File.Copy(f, Path.Combine(dst, Path.GetFileName(f)), overwrite: true);
                    Log.Info($"Snapshot: plugin {id} → {dst}");
                }
            }
            catch (Exception ex)
            {
                Log.Error("Snapshot build failed — aborting update to avoid non-recoverable state", ex);
                return new UpdateResult(false, $"Snapshot failed: {ex.Message}");
            }

            // Health-check markers live in install dir so the PS1 can poll for them
            // from outside tmp (tmp self-cleans at the end of a successful run).
            string healthOkMarker = Path.Combine(installDir, "_update_ok.marker");
            string healthFailMarker = Path.Combine(installDir, "_update_fail.marker");
            try
            {
                if (File.Exists(healthOkMarker)) File.Delete(healthOkMarker);
                if (File.Exists(healthFailMarker)) File.Delete(healthFailMarker);
            }
            catch { }

            // Write the new orchestration PS1 next to the staged files.
            string ps1Path = Path.Combine(tmpRoot, "_update.ps1");
            string ps1 = BuildLocalPs1(
                installDir: installDir,
                currentExe: currentExe,
                pluginsInstallDir: pluginsInstallDir,
                tmpRoot: tmpRoot,
                stagedHostExe: stagedHostExe,        // empty if host not updated
                pluginsStagingRoot: pluginsStagingRoot,
                snapshotRoot: snapshotRoot,
                healthOkMarker: healthOkMarker,
                healthFailMarker: healthFailMarker,
                targetPid: currentPid);
            File.WriteAllText(ps1Path, ps1, Encoding.UTF8);
            Log.Info($"Wrote orchestrator {ps1Path}");

            try { File.WriteAllText(Path.Combine(installDir, "_updated.marker"), ""); }
            catch (Exception ex) { Log.Warn($"Failed to write _updated.marker: {ex.Message}"); }

            LaunchPs1(ps1Path, tmpRoot);
            Log.Info($"Launched orchestrator, Apply total={sw.ElapsedMilliseconds}ms — host will exit to allow swap");
            return new UpdateResult(true, "Update started. Application will restart.");
        }
        catch (Exception ex)
        {
            Log.Error($"Updater.Apply failed after {sw.ElapsedMilliseconds}ms", ex);
            return new UpdateResult(false, $"Update failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Re-read metadata.json from the update location. Works for both local and HTTP
    /// (unlike relying on the cached UpdateChecker.LatestMetadata which may be stale).
    /// </summary>
    private static UpdateMetadata? LoadFreshMetadata(string location, bool isHttp)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            string json;
            string manifestUrl = "";
            if (isHttp)
            {
                Log.Info($"HTTP GET {location}");
                json = _http.GetStringAsync(location).GetAwaiter().GetResult();
                manifestUrl = location;
                Log.Info($"HTTP GET metadata succeeded in {sw.ElapsedMilliseconds}ms ({json.Length} bytes)");
            }
            else
            {
                string manifestPath = Path.Combine(location, "metadata.json");
                if (!File.Exists(manifestPath))
                {
                    Log.Warn($"metadata.json not found at {manifestPath}");
                    return null;
                }
                json = File.ReadAllText(manifestPath);
            }

            var meta = System.Text.Json.JsonSerializer.Deserialize<UpdateMetadata>(json);
            if (meta == null)
            {
                Log.Warn("metadata.json deserialized to null");
                return null;
            }
            return isHttp ? meta with { ManifestUrl = manifestUrl } : meta;
        }
        catch (Exception ex)
        {
            Log.Error($"LoadFreshMetadata failed after {sw.ElapsedMilliseconds}ms", ex);
            return null;
        }
    }

    /// <summary>
    /// Resolve a relative asset path (from metadata.json) to a local file, verify
    /// its SHA256 if one is advertised, then extract flat into destDir. For local
    /// updates, the source is a file under {location}; for HTTP updates, the zip
    /// is downloaded to a temp file first (AssetDownloader verifies the hash),
    /// then extracted and the temp file deleted.
    /// </summary>
    private static void FetchAndExtractZip(
        bool isHttp, string location, string manifestUrl,
        string relZip, string expectedSha256, string destDir)
    {
        if (isHttp)
        {
            string tempZip = AssetDownloader
                .DownloadRelativeAssetAsync(manifestUrl, relZip, expectedSha256)
                .GetAwaiter().GetResult();
            try
            {
                ZipFile.ExtractToDirectory(tempZip, destDir, overwriteFiles: true);
            }
            finally
            {
                try { File.Delete(tempZip); } catch { }
            }
        }
        else
        {
            string localZip = Path.Combine(location, NormalizeRelative(relZip));
            if (!File.Exists(localZip))
                throw new FileNotFoundException($"Zip not found: {localZip}", localZip);

            if (!string.IsNullOrWhiteSpace(expectedSha256))
            {
                string actual = AssetDownloader.ComputeSha256(localZip);
                if (!actual.Equals(expectedSha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        $"SHA256 mismatch for {Path.GetFileName(localZip)}: " +
                        $"expected {expectedSha256.Trim()}, got {actual}");
                }
                Log.Info($"  local sha256 verified for {Path.GetFileName(localZip)}");
            }

            ZipFile.ExtractToDirectory(localZip, destDir, overwriteFiles: true);
        }
    }

    private static string? FindFileRecursive(string rootDir, string fileName)
    {
        foreach (var file in Directory.GetFiles(rootDir, fileName, SearchOption.AllDirectories))
            return file;
        return null;
    }

    /// <summary>Manifest paths use forward slashes; convert to OS-native.</summary>
    private static string NormalizeRelative(string relPath) =>
        relPath.Replace('/', Path.DirectorySeparatorChar);

    private static bool IsNewerVersion(string remote, string local)
    {
        if (Version.TryParse(remote, out var r) && Version.TryParse(local, out var l))
            return r > l;
        return false;
    }

    private static void LaunchPs1(string scriptPath, string workingDir)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = workingDir,
        });
    }

    /// <summary>
    /// PS1 orchestrator with full logging, snapshot rollback, and a
    /// health-check handshake against the relaunched host.
    ///
    /// Phases:
    ///  1. Wait for old host to exit (1s grace, then 12×5s retry-kill = 60s budget).
    ///     On timeout: abort, no mutation, user retries on next launch.
    ///  2. Swap host exe from tmp\host\. On failure: rollback from snapshot, exit 2.
    ///  3. Deploy each staged plugin dir over PluginsBinDir\{id}\. Track what we
    ///     replaced so rollback can target just those. On failure: rollback, exit 3.
    ///  4. Relaunch host, wait up to 30s for it to write _update_ok.marker. If it
    ///     writes _update_fail.marker (crashed during init) or times out: rollback,
    ///     relaunch the now-restored old version, exit 4.
    ///  5. Self-clean tmp dir.
    ///
    /// Every step writes to tmp\update.log so a failed update leaves a full trail.
    /// </summary>
    private static string BuildLocalPs1(
        string installDir, string currentExe, string pluginsInstallDir,
        string tmpRoot, string stagedHostExe, string pluginsStagingRoot,
        string snapshotRoot, string healthOkMarker, string healthFailMarker,
        int targetPid)
    {
        return $@"
# ProdToy Auto-Updater (manifest flow, v2: logging + snapshot rollback + health check)
# Lives at $tmpRoot\_update.ps1 — self-cleans the entire tmp dir at the end of a
# successful run. On failure, tmp stays in place with update.log and update-failed.log
# for post-mortem.

$exePath           = '{currentExe.Replace("'", "''")}'
$installDir        = '{installDir.Replace("'", "''")}'
$pluginsDest       = '{pluginsInstallDir.Replace("'", "''")}'
$tmpRoot           = '{tmpRoot.Replace("'", "''")}'
$stagedHostExe     = '{stagedHostExe.Replace("'", "''")}'
$pluginsStagingRoot= '{pluginsStagingRoot.Replace("'", "''")}'
$snapshotRoot      = '{snapshotRoot.Replace("'", "''")}'
$healthOkMarker    = '{healthOkMarker.Replace("'", "''")}'
$healthFailMarker  = '{healthFailMarker.Replace("'", "''")}'
$targetPid         = {targetPid}
$updateLog         = Join-Path $tmpRoot 'update.log'
$failLog           = Join-Path $tmpRoot 'update-failed.log'

function Log-Line($msg) {{
    try {{
        $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ss.fffzzz')
        Add-Content -Path $updateLog -Value ""$stamp $msg""
    }} catch {{ }}
}}

function Fail-Log($reason) {{
    Log-Line ""FAIL: $reason""
    try {{
        $stamp = (Get-Date).ToString('yyyy-MM-ddTHH:mm:ssZ')
        Add-Content -Path $failLog -Value ""$stamp $reason""
    }} catch {{ }}
}}

# --- Rollback helper — restores host exe + any plugins we replaced. ---
function Rollback-From-Snapshot {{
    Log-Line ""ROLLBACK: starting restore from $snapshotRoot""
    $snapHostExe = Join-Path $snapshotRoot 'host\ProdToy.exe'
    if (Test-Path $snapHostExe) {{
        try {{
            Copy-Item -Path $snapHostExe -Destination $exePath -Force
            Log-Line ""ROLLBACK: host exe restored""
        }} catch {{
            Log-Line ""ROLLBACK ERROR: host exe restore failed: $($_.Exception.Message)""
        }}
    }}
    $snapPluginsRoot = Join-Path $snapshotRoot 'plugins'
    if (Test-Path $snapPluginsRoot) {{
        foreach ($dir in Get-ChildItem $snapPluginsRoot -Directory) {{
            $dest = Join-Path $pluginsDest $dir.Name
            try {{
                if (Test-Path $dest) {{ Remove-Item ""$dest\*"" -Recurse -Force -ErrorAction SilentlyContinue }}
                else {{ New-Item -ItemType Directory -Path $dest -Force | Out-Null }}
                Copy-Item ""$($dir.FullName)\*"" $dest -Force -Recurse
                Log-Line ""ROLLBACK: plugin $($dir.Name) restored""
            }} catch {{
                Log-Line ""ROLLBACK ERROR: plugin $($dir.Name) restore failed: $($_.Exception.Message)""
            }}
        }}
    }}
    Log-Line ""ROLLBACK: complete""
}}

Log-Line ""=== ProdToy auto-updater starting ===""
Log-Line ""installDir=$installDir pid=$targetPid""
Log-Line ""stagedHostExe=$stagedHostExe""
Log-Line ""pluginsStagingRoot=$pluginsStagingRoot""
Log-Line ""snapshotRoot=$snapshotRoot""

# --- Phase 1: wait for old host to exit. ---
Log-Line ""Phase 1: waiting for pid $targetPid to exit (1s grace)""
Start-Sleep -Milliseconds 1000
$proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
if ($proc) {{
    Log-Line ""Phase 1: pid still alive, starting retry-kill loop (12 * 5s = 60s budget)""
    $killed = $false
    for ($attempt = 1; $attempt -le 12; $attempt++) {{
        try {{ Stop-Process -Id $targetPid -Force -ErrorAction Stop }} catch {{ Log-Line ""  kill attempt $attempt error: $($_.Exception.Message)"" }}
        Start-Sleep -Seconds 5
        $proc = Get-Process -Id $targetPid -ErrorAction SilentlyContinue
        if (-not $proc) {{ $killed = $true; Log-Line ""  kill attempt $attempt succeeded""; break }}
    }}
    if (-not $killed) {{
        Fail-Log ""ABORT Phase 1: PID $targetPid still alive after 60s. Install dir untouched.""
        exit 1
    }}
}} else {{
    Log-Line ""Phase 1: pid already exited""
}}

# --- Phase 2: swap host exe. ---
if ($stagedHostExe -and (Test-Path $stagedHostExe)) {{
    Log-Line ""Phase 2: copying $stagedHostExe → $exePath""
    try {{
        Copy-Item -Path $stagedHostExe -Destination $exePath -Force
        Log-Line ""Phase 2: host exe swapped""
    }} catch {{
        Fail-Log ""Phase 2 ERROR: $($_.Exception.Message)""
        Rollback-From-Snapshot
        exit 2
    }}
}} else {{
    Log-Line ""Phase 2: skipped (no staged host exe)""
}}

# --- Phase 3: deploy plugins. ---
if (Test-Path $pluginsStagingRoot) {{
    if (-not (Test-Path $pluginsDest)) {{ New-Item -ItemType Directory -Path $pluginsDest -Force | Out-Null }}
    foreach ($dir in Get-ChildItem $pluginsStagingRoot -Directory) {{
        $dest = Join-Path $pluginsDest $dir.Name
        Log-Line ""Phase 3: deploying plugin $($dir.Name) → $dest""
        try {{
            if (-not (Test-Path $dest)) {{ New-Item -ItemType Directory -Path $dest -Force | Out-Null }}
            Copy-Item ""$($dir.FullName)\*"" $dest -Force -Recurse
            Log-Line ""  plugin $($dir.Name) deployed""
        }} catch {{
            Fail-Log ""Phase 3 ERROR deploying $($dir.Name): $($_.Exception.Message)""
            Rollback-From-Snapshot
            exit 3
        }}
    }}
}} else {{
    Log-Line ""Phase 3: skipped (no staged plugins)""
}}

# --- Phase 4: relaunch and wait for health-check marker. ---
if (-not (Test-Path $exePath)) {{
    Fail-Log ""Phase 4 ERROR: exe missing at $exePath after swap""
    Rollback-From-Snapshot
    exit 4
}}
Log-Line ""Phase 4: relaunching $exePath and waiting up to 30s for health marker""
try {{ Start-Process -FilePath $exePath }} catch {{
    Fail-Log ""Phase 4 ERROR launching new exe: $($_.Exception.Message)""
    Rollback-From-Snapshot
    try {{ Start-Process -FilePath $exePath }} catch {{ }}
    exit 4
}}

$healthy = $false
for ($i = 0; $i -lt 60; $i++) {{  # 60 * 500ms = 30s
    if (Test-Path $healthFailMarker) {{
        Log-Line ""Phase 4: new host reported fail marker""
        break
    }}
    if (Test-Path $healthOkMarker) {{
        $healthy = $true
        Log-Line ""Phase 4: health OK marker detected at attempt $($i+1)""
        break
    }}
    Start-Sleep -Milliseconds 500
}}

if (-not $healthy) {{
    Fail-Log ""Phase 4: new host did not report healthy within 30s, rolling back""
    # Try to kill the unhealthy new process before we restore its exe.
    foreach ($p in Get-Process -Name 'ProdToy' -ErrorAction SilentlyContinue) {{
        try {{ Stop-Process -Id $p.Id -Force -ErrorAction Stop; Log-Line ""  killed unhealthy pid $($p.Id)"" }} catch {{ }}
    }}
    Start-Sleep -Seconds 2
    Rollback-From-Snapshot
    try {{ Start-Process -FilePath $exePath; Log-Line ""  relaunched rolled-back host"" }} catch {{ }}
    exit 5
}}

Log-Line ""Phase 5: self-cleanup""
Start-Sleep -Seconds 2
try {{
    Remove-Item $tmpRoot -Recurse -Force -ErrorAction Stop
    # Note: this deletes updateLog too; the success path doesn't need it.
}} catch {{ }}
";
    }

    // Phase 5: EnsureHookScript moved to the Claude Integration plugin.
    // The plugin owns its own PS1 template and writes it on Start(). The
    // host no longer knows about Claude hooks.
}
