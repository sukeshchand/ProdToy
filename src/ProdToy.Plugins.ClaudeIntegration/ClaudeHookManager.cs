using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ProdToy.Plugins.ClaudeIntegration;

static class ClaudeHookManager
{
    private const string AutoTitleMarkerStart = "<!-- ProdToy:AutoTitle:Start -->";
    private const string AutoTitleMarkerEnd = "<!-- ProdToy:AutoTitle:End -->";

    public static void SetAutoTitleHook(bool enabled)
    {
        try
        {
            if (enabled) AddInstruction();
            else RemoveInstruction();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to set auto-title: {ex.Message}");
        }
    }

    private static void AddInstruction()
    {
        string path = ClaudePaths.ClaudeMdFile;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string existing = File.Exists(path) ? File.ReadAllText(path) : "";
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
        File.WriteAllText(path, newContent.TrimStart(), Encoding.UTF8);
    }

    private static void RemoveInstruction()
    {
        string path = ClaudePaths.ClaudeMdFile;
        if (!File.Exists(path)) return;

        string content = File.ReadAllText(path);
        int startIdx = content.IndexOf(AutoTitleMarkerStart);
        if (startIdx < 0) return;
        int endIdx = content.IndexOf(AutoTitleMarkerEnd);
        if (endIdx < 0) return;
        endIdx += AutoTitleMarkerEnd.Length;

        string before = content[..startIdx].TrimEnd();
        string after = content[endIdx..].TrimStart();
        string result = string.IsNullOrWhiteSpace(before) && string.IsNullOrWhiteSpace(after)
            ? "" : (before + "\n" + after).Trim() + "\n";
        File.WriteAllText(path, result, Encoding.UTF8);
    }

    public static void CleanupOldHook()
    {
        try
        {
            string settingsPath = ClaudePaths.ClaudeSettingsFile;
            if (!File.Exists(settingsPath)) return;

            string json = File.ReadAllText(settingsPath);
            var root = JsonNode.Parse(json);
            if (root?["hooks"] is not JsonObject hooksNode) return;
            if (hooksNode["SessionStart"] is not JsonArray eventArray) return;

            for (int i = eventArray.Count - 1; i >= 0; i--)
            {
                if (eventArray[i]?["hooks"] is not JsonArray hooksArray) continue;
                for (int j = hooksArray.Count - 1; j >= 0; j--)
                {
                    string? command = hooksArray[j]?["command"]?.GetValue<string>();
                    if (command != null && command.Contains("Set-FolderTitle"))
                        hooksArray.RemoveAt(j);
                }
                if (hooksArray.Count == 0) eventArray.RemoveAt(i);
            }

            if (eventArray.Count == 0) hooksNode.Remove("SessionStart");

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root!.ToJsonString(options), Encoding.UTF8);

            string oldScript = Path.Combine(ClaudePaths.ClaudeHooksDir, "Set-FolderTitle.ps1");
            if (File.Exists(oldScript)) File.Delete(oldScript);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Cleanup old hook failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Updates Claude's settings.json to enable/disable a ProdToy hook for the given event.
    /// </summary>
    public static void UpdateClaudeHook(string eventName, string? matcher, bool enabled)
    {
        try
        {
            string settingsPath = ClaudePaths.ClaudeSettingsFile;
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

            string hookCommand = $"powershell.exe -ExecutionPolicy Bypass -File \"{Path.Combine(ClaudePaths.ClaudeHooksDir, "Show-ProdToy.ps1")}\"";

            if (!enabled)
            {
                // Remove ProdToy hooks from this event
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
                // Add ProdToy hook if not already present
                if (hooksNode[eventName] is not JsonArray eventArray2)
                {
                    eventArray2 = new JsonArray();
                    hooksNode[eventName] = eventArray2;
                }

                // Check if already exists
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

            // Remove hooks object if empty
            if (hooksNode.Count == 0)
                ((JsonObject)root).Remove("hooks");

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(settingsPath, root.ToJsonString(options), Encoding.UTF8);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"UpdateClaudeHook failed: {ex.Message}");
        }
    }

    private static bool IsProdToyHookCommand(string? command) =>
        command != null && (command.Contains("Show-ProdToy") || command.Contains("Show-DevToy"));
}
