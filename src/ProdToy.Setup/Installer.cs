using System.Diagnostics;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace ProdToy.Setup;

/// <summary>
/// Performs the actual install/repair/update work. Extracts bundled zips next
/// to the running installer into the user's .prod-toy directory and registers
/// the app in Windows "Apps &amp; Features". As of Phase 6, Claude hooks and
/// the Show-ProdToy.ps1 script are owned by the Claude Integration plugin and
/// installed on its first Start() after the host launches post-install.
/// </summary>
static class Installer
{
    public record InstallResult(bool Success, string Message);

    /// <summary>
    /// Default bundle location: next to the running installer exe. Used when
    /// Run() is called without a bundleDir (e.g. offline install with zips
    /// shipped alongside ProdToySetup.exe).
    /// </summary>
    public static string DefaultBundleDir => Path.GetDirectoryName(Application.ExecutablePath)!;

    public static string DefaultMetadataPath => Path.Combine(DefaultBundleDir, "metadata.json");

    /// <summary>
    /// Returns the version of the bundled host (from metadata.json next to the
    /// installer if present, otherwise falls back to the installer's own
    /// AppVersion.Current). Used by SetupForm for display BEFORE bootstrap
    /// download runs, so it can only see a sibling metadata.json.
    /// </summary>
    public static string ReadBundledVersion()
    {
        try
        {
            if (File.Exists(DefaultMetadataPath))
            {
                var json = JsonNode.Parse(File.ReadAllText(DefaultMetadataPath));
                var v = json?["version"]?.GetValue<string>();
                if (!string.IsNullOrWhiteSpace(v)) return v;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"ReadBundledVersion failed: {ex.Message}");
        }
        return AppVersion.Current;
    }

    /// <summary>
    /// Runs the install against the given bundle directory. The directory must
    /// contain ProdToy.zip, metadata.json, and a plugins\*.zip subdir — either
    /// shipped alongside the installer or assembled by BootstrapDownloader.
    /// </summary>
    public static InstallResult Run(string bundleDir, Action<string> onProgress,
        bool createDesktopShortcut = false, bool createStartMenuShortcut = false)
    {
        string hostZipPath = Path.Combine(bundleDir, "ProdToy.zip");
        string pluginsBundleDir = Path.Combine(bundleDir, "plugins");
        string metadataPath = Path.Combine(bundleDir, "metadata.json");

        var log = new StringBuilder();
        void Report(string msg)
        {
            log.AppendLine(msg);
            try { onProgress(msg); } catch { }
        }

        try
        {
            // Step 1: Kill any running ProdToy instances (except this installer).
            Report("Stopping any running ProdToy instances...");
            int currentPid = Environment.ProcessId;
            foreach (var proc in Process.GetProcessesByName("ProdToy"))
            {
                if (proc.Id == currentPid) continue;
                try
                {
                    proc.Kill();
                    proc.WaitForExit(3000);
                    Report($"  Closed ProdToy PID {proc.Id}");
                }
                catch (Exception ex)
                {
                    Report($"  Warning: could not kill PID {proc.Id}: {ex.Message}");
                }
            }

            // Step 2: Ensure install dir exists.
            Directory.CreateDirectory(AppPaths.Root);
            Report($"Install directory: {AppPaths.Root}");

            // Step 3: Extract ProdToy.zip → Root\ProdToy.exe
            if (!File.Exists(hostZipPath))
                return new InstallResult(false, $"ProdToy.zip not found at {hostZipPath}.");

            Report($"Extracting host exe from {Path.GetFileName(hostZipPath)}...");
            ExtractZipFlat(hostZipPath, AppPaths.Root);
            Report($"  Host exe → {AppPaths.ExePath}");

            // Step 4: Extract each plugin zip → Root\plugins\bin\{PluginId}\
            int pluginCount = 0;
            if (Directory.Exists(pluginsBundleDir))
            {
                Directory.CreateDirectory(AppPaths.PluginsBinDir);
                foreach (var zipPath in Directory.GetFiles(pluginsBundleDir, "*.zip"))
                {
                    string pluginId = Path.GetFileNameWithoutExtension(zipPath);
                    string destDir = Path.Combine(AppPaths.PluginsBinDir, pluginId);
                    Directory.CreateDirectory(destDir);
                    ExtractZipFlat(zipPath, destDir);
                    Report($"  Plugin {pluginId} → {destDir}");
                    pluginCount++;
                }
                Report($"Installed {pluginCount} plugin package(s).");
            }
            else
            {
                Report("No bundled plugins directory found (skipping plugin install).");
            }

            // Step 5: Copy the installer exe itself to install dir so Windows
            //         Add/Remove Programs can find it for uninstall.
            try
            {
                string runningSetup = Application.ExecutablePath;
                if (!string.Equals(runningSetup, AppPaths.SetupExePath, StringComparison.OrdinalIgnoreCase))
                {
                    File.Copy(runningSetup, AppPaths.SetupExePath, overwrite: true);
                    Report($"Copied installer to {AppPaths.SetupExePath}");
                }
            }
            catch (Exception ex)
            {
                Report($"Warning: could not copy installer: {ex.Message}");
            }

            // Phase 6: hook script + Claude settings merge are now the
            // ClaudeIntegration plugin's job. The plugin's Start() writes
            // ~/.claude/hooks/Show-ProdToy.ps1 and merges the hook entries
            // into ~/.claude/settings.json on first launch after install.
            // The installer no longer touches ~/.claude/.

            // Step 8: Register in Windows Apps & Features using the version from
            //         the bundle's metadata.json (not AppVersion.Current — they
            //         could differ when Setup bootstraps a newer release).
            try
            {
                string bundledVersion = AppVersion.Current;
                try
                {
                    if (File.Exists(metadataPath))
                    {
                        var json = JsonNode.Parse(File.ReadAllText(metadataPath));
                        var v = json?["version"]?.GetValue<string>();
                        if (!string.IsNullOrWhiteSpace(v)) bundledVersion = v;
                    }
                }
                catch { }
                AppRegistry.Register(bundledVersion);
                Report($"Registered v{bundledVersion} in Apps & Features.");
            }
            catch (Exception ex)
            {
                Report($"Warning: could not register in Apps & Features: {ex.Message}");
            }

            // Step 8b: Register "Start with Windows" so ProdToy launches at
            //          login. Matches SettingsForm.SetStartWithWindows — the
            //          user can disable it later from General settings.
            try
            {
                using var runKey = Registry.CurrentUser.OpenSubKey(
                    @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", writable: true);
                if (runKey != null)
                {
                    runKey.SetValue("ProdToy", $"\"{AppPaths.ExePath}\"");
                    Report("Registered ProdToy to start with Windows.");
                }
            }
            catch (Exception ex)
            {
                Report($"Warning: could not register startup entry: {ex.Message}");
            }

            // Step 9: Optionally create shortcuts.
            if (createDesktopShortcut)
            {
                try
                {
                    string shortcutPath = CreateShortcut(
                        Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
                        "ProdToy.lnk");
                    Report($"Created desktop shortcut: {shortcutPath}");
                }
                catch (Exception ex)
                {
                    Report($"Warning: could not create desktop shortcut: {ex.Message}");
                }
            }

            if (createStartMenuShortcut)
            {
                try
                {
                    string startMenuDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                        "Microsoft", "Windows", "Start Menu", "Programs");
                    Directory.CreateDirectory(startMenuDir);
                    string shortcutPath = CreateShortcut(startMenuDir, "ProdToy.lnk");
                    Report($"Created Start Menu shortcut: {shortcutPath}");
                }
                catch (Exception ex)
                {
                    Report($"Warning: could not create Start Menu shortcut: {ex.Message}");
                }
            }

            Report("Installation complete.");
            return new InstallResult(true, log.ToString());
        }
        catch (Exception ex)
        {
            Report($"Error: {ex.Message}");
            return new InstallResult(false, log.ToString());
        }
    }

    /// <summary>
    /// Creates a .lnk file at <paramref name="directory"/>/<paramref name="fileName"/>
    /// pointing at the installed host exe. Uses WScript.Shell via late-bound
    /// COM so the Setup project doesn't need an interop reference. Overwrites
    /// any existing shortcut at the target path.
    /// </summary>
    private static string CreateShortcut(string directory, string fileName)
    {
        string shortcutPath = Path.Combine(directory, fileName);

        Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null)
            throw new InvalidOperationException("WScript.Shell COM type unavailable on this system.");

        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic shortcut = shell.CreateShortcut(shortcutPath);
            try
            {
                shortcut.TargetPath = AppPaths.ExePath;
                shortcut.WorkingDirectory = AppPaths.Root;
                shortcut.IconLocation = AppPaths.ExePath + ",0";
                shortcut.Description = "ProdToy";
                shortcut.Save();
            }
            finally
            {
                if (Marshal.IsComObject(shortcut)) Marshal.FinalReleaseComObject(shortcut);
            }
        }
        finally
        {
            if (Marshal.IsComObject(shell)) Marshal.FinalReleaseComObject(shell);
        }

        return shortcutPath;
    }

    /// <summary>
    /// Extract a zip into destDir. Assumes flat zip layout (entries at the root).
    /// Overwrites existing files.
    /// </summary>
    private static void ExtractZipFlat(string zipPath, string destDir)
    {
        using var archive = ZipFile.OpenRead(zipPath);
        foreach (var entry in archive.Entries)
        {
            if (string.IsNullOrEmpty(entry.Name)) continue; // skip directories
            string destPath = Path.Combine(destDir, entry.Name);
            Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);
            entry.ExtractToFile(destPath, overwrite: true);
        }
    }

}
