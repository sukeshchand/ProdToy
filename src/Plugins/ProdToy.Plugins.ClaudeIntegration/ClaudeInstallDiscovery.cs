using System.Text.Json.Nodes;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// A single Claude Code CLI installation discovered on this machine.
/// Each installation has its own config directory (typically <c>~/.claude/</c>,
/// but Claude supports <c>CLAUDE_CONFIG_DIR</c> overrides and users may run
/// multiple instances with separate dirs).
/// </summary>
sealed record ClaudeInstall(string ConfigDir)
{
    public string SettingsFile => Path.Combine(ConfigDir, "settings.json");
    public string ClaudeMdFile => Path.Combine(ConfigDir, "CLAUDE.md");
}

/// <summary>
/// Scans the current user's home + roaming + local app data directories for
/// Claude Code CLI installations. A directory is considered a Claude install
/// if its name contains "claude" (case-insensitive) AND it contains a
/// <c>settings.json</c> that parses as a JSON object.
/// </summary>
static class ClaudeInstallDiscovery
{
    /// <summary>
    /// Scan top-level directories of %USERPROFILE%, %APPDATA%, and %LOCALAPPDATA%
    /// for Claude installations. Results are deduplicated by absolute path.
    /// </summary>
    public static List<ClaudeInstall> Scan()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var results = new List<ClaudeInstall>();

        foreach (var root in EnumerateScanRoots())
        {
            if (!Directory.Exists(root)) continue;

            IEnumerable<string> candidates;
            try
            {
                candidates = Directory.EnumerateDirectories(root);
            }
            catch (Exception ex)
            {
                PluginLog.Warn($"ClaudeInstallDiscovery: cannot enumerate {root}: {ex.Message}");
                continue;
            }

            foreach (var dir in candidates)
            {
                try
                {
                    string name = Path.GetFileName(dir);
                    if (!name.Contains("claude", StringComparison.OrdinalIgnoreCase))
                        continue;

                    string settingsFile = Path.Combine(dir, "settings.json");
                    if (!File.Exists(settingsFile))
                        continue;

                    if (!IsValidSettingsJson(settingsFile))
                        continue;

                    string abs = Path.GetFullPath(dir);
                    if (seen.Add(abs))
                        results.Add(new ClaudeInstall(abs));
                }
                catch (Exception ex)
                {
                    PluginLog.Warn($"ClaudeInstallDiscovery: skipped {dir}: {ex.Message}");
                }
            }
        }

        return results;
    }

    private static IEnumerable<string> EnumerateScanRoots()
    {
        yield return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        yield return Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    }

    private static bool IsValidSettingsJson(string settingsFile)
    {
        try
        {
            string json = File.ReadAllText(settingsFile);
            return JsonNode.Parse(json) is JsonObject;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"ClaudeInstallDiscovery: cannot parse {settingsFile}: {ex.Message}");
            return false;
        }
    }
}
