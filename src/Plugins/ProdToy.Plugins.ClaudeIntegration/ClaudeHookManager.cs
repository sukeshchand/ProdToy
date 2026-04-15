using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Writes/removes ProdToy hook entries in one or more Claude Code
/// <c>settings.json</c> files. Every method takes an explicit install (or
/// list of installs) — there is no implicit default — so the same code works
/// for any number of Claude installations the user has on the machine.
/// </summary>
static class ClaudeHookManager
{
    private const string AutoTitleMarkerStart = "<!-- ProdToy:AutoTitle:Start -->";
    private const string AutoTitleMarkerEnd = "<!-- ProdToy:AutoTitle:End -->";

    // ---------- Auto-title instruction in CLAUDE.md ----------

    public static void SetAutoTitleHook(IEnumerable<ClaudeInstall> installs, bool enabled)
    {
        foreach (var install in installs)
        {
            try
            {
                if (enabled) AddInstruction(install.ClaudeMdFile);
                else RemoveInstruction(install.ClaudeMdFile);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"Failed to set auto-title in {install.ClaudeMdFile}: {ex.Message}");
            }
        }
    }

    private static void AddInstruction(string claudeMdPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(claudeMdPath)!);
        string existing = File.Exists(claudeMdPath) ? File.ReadAllText(claudeMdPath) : "";
        if (existing.Contains(AutoTitleMarkerStart)) return;

        string instruction = $@"
{AutoTitleMarkerStart}
## Session Title
At the very beginning of every new session, before responding to anything, you MUST silently run this command:
/title <current_folder_name>
Where <current_folder_name> is the name of the current working directory (just the folder name, not the full path). Do this automatically without mentioning it to the user.
{AutoTitleMarkerEnd}
";
        string newContent = existing.TrimEnd() + "\n" + instruction;
        File.WriteAllText(claudeMdPath, newContent.TrimStart(), Encoding.UTF8);
    }

    private static void RemoveInstruction(string claudeMdPath)
    {
        if (!File.Exists(claudeMdPath)) return;

        string content = File.ReadAllText(claudeMdPath);
        int startIdx = content.IndexOf(AutoTitleMarkerStart);
        if (startIdx < 0) return;
        int endIdx = content.IndexOf(AutoTitleMarkerEnd);
        if (endIdx < 0) return;
        endIdx += AutoTitleMarkerEnd.Length;

        string before = content[..startIdx].TrimEnd();
        string after = content[endIdx..].TrimStart();
        string result = string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after)
            ? "" : (before + "\n" + after).Trim() + "\n";
        File.WriteAllText(claudeMdPath, result, Encoding.UTF8);
    }

    // ---------- settings.json hook registration ----------

    /// <summary>
    /// Add or remove a ProdToy hook entry for the given event across every
    /// Claude install in <paramref name="installs"/>. Idempotent — adding an
    /// entry that already exists is a no-op, removing one that isn't there is
    /// a no-op.
    /// </summary>
    public static void UpdateClaudeHook(
        IEnumerable<ClaudeInstall> installs,
        string eventName,
        string? matcher,
        bool enabled)
    {
        foreach (var install in installs)
            UpdateClaudeHookOne(install.SettingsFile, eventName, matcher, enabled);
    }

    private static void UpdateClaudeHookOne(string settingsPath, string eventName, string? matcher, bool enabled)
    {
        try
        {
            JsonNode root;
            if (File.Exists(settingsPath))
            {
                string existing = File.ReadAllText(settingsPath);
                root = JsonNode.Parse(existing) ?? new JsonObject();
            }
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
                root = new JsonObject();
            }

            if (root["hooks"] is not JsonObject hooksNode)
            {
                hooksNode = new JsonObject();
                root["hooks"] = hooksNode;
            }

            string hookCommand = $"powershell.exe -ExecutionPolicy Bypass -File \"{ClaudePaths.ShowProdToyScript}\"";

            if (!enabled)
            {
                if (hooksNode[eventName] is JsonArray eventArray)
                {
                    for (int i = eventArray.Count - 1; i >= 0; i--)
                    {
                        if (eventArray[i]?["hooks"] is JsonArray ha)
                        {
                            for (int j = ha.Count - 1; j >= 0; j--)
                            {
                                string? cmd = ha[j]?["command"]?.GetValue<string>();
                                if (cmd != null && IsProdToyHookCommand(cmd))
                                    ha.RemoveAt(j);
                            }
                            if (ha.Count == 0) eventArray.RemoveAt(i);
                        }
                    }
                    if (eventArray.Count == 0) hooksNode.Remove(eventName);
                }
            }
            else
            {
                if (hooksNode[eventName] is not JsonArray eventArray2)
                {
                    eventArray2 = new JsonArray();
                    hooksNode[eventName] = eventArray2;
                }

                bool exists = false;
                foreach (var ruleSet in eventArray2.AsArray())
                {
                    if (ruleSet?["hooks"] is JsonArray ha)
                    {
                        foreach (var h in ha.AsArray())
                        {
                            if (IsProdToyHookCommand(h?["command"]?.GetValue<string>()))
                            { exists = true; break; }
                        }
                    }
                    if (exists) break;
                }

                if (!exists)
                {
                    var hookEntry = new JsonObject { ["type"] = "command", ["command"] = hookCommand };
                    var ruleSet = new JsonObject { ["hooks"] = new JsonArray { hookEntry } };
                    if (matcher != null)
                        ruleSet["matcher"] = matcher;
                    eventArray2.Add(ruleSet);
                }
            }

            if (hooksNode.Count == 0)
                ((JsonObject)root).Remove("hooks");

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateClaudeHook({settingsPath}) failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Remove ALL ProdToy hook entries from every event in every install's
    /// settings.json. Called from <c>Uninstall()</c> to fully clean up.
    /// </summary>
    public static void RemoveAllProdToyHooks(IEnumerable<ClaudeInstall> installs)
    {
        foreach (var install in installs)
        {
            try
            {
                string settingsPath = install.SettingsFile;
                if (!File.Exists(settingsPath)) continue;

                string json = File.ReadAllText(settingsPath);
                var root = JsonNode.Parse(json);
                if (root?["hooks"] is not JsonObject hooksNode) continue;

                var eventNames = hooksNode.Select(kv => kv.Key).ToList();
                foreach (var eventName in eventNames)
                {
                    if (hooksNode[eventName] is not JsonArray eventArray) continue;
                    for (int i = eventArray.Count - 1; i >= 0; i--)
                    {
                        if (eventArray[i]?["hooks"] is not JsonArray ha) continue;
                        for (int j = ha.Count - 1; j >= 0; j--)
                        {
                            string? cmd = ha[j]?["command"]?.GetValue<string>();
                            if (cmd != null && IsProdToyHookCommand(cmd))
                                ha.RemoveAt(j);
                        }
                        if (ha.Count == 0) eventArray.RemoveAt(i);
                    }
                    if (eventArray.Count == 0) hooksNode.Remove(eventName);
                }

                if (hooksNode.Count == 0)
                    ((JsonObject)root!).Remove("hooks");

                var options = new JsonSerializerOptions { WriteIndented = true };
                File.WriteAllText(settingsPath, root!.ToJsonString(options), Encoding.UTF8);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"RemoveAllProdToyHooks({install.SettingsFile}) failed: {ex.Message}");
            }
        }
    }

    private static bool IsProdToyHookCommand(string? command) =>
        command != null && (command.Contains("Show-ProdToy") || command.Contains("Show-DevToy"));
}
