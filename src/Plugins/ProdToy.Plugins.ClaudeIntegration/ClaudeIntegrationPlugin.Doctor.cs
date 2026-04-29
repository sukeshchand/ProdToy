using System.Text.Json;
using System.Text.Json.Nodes;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

public partial class ClaudeIntegrationPlugin
{
    private const string DoctorSource = "Claude Integration";

    public IReadOnlyList<DoctorCheck> Diagnose()
    {
        var checks = new List<DoctorCheck>();
        var dataDir = _context.DataDirectory;

        checks.Add(DirCheck("Plugin data directory", dataDir, DoctorSeverity.Warning));

        if (string.IsNullOrEmpty(ClaudePaths.ScriptsDir))
            ClaudePaths.Initialize(dataDir);

        checks.Add(DirCheck("Scripts directory", ClaudePaths.ScriptsDir, DoctorSeverity.Error,
            fix: () => { Directory.CreateDirectory(ClaudePaths.ScriptsDir); Install(_context); },
            requiresRestart: true));

        // ---- Environment ID ----
        // launchSettings.json holds a stable envId written by the installer.
        // Read from disk directly (not the cached static) so the check reflects
        // the actual file state and the fix can apply in-session without restart.
        string launchSettingsPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prod-toy", "launchSettings.json");
        string? diskEnvId = null;
        if (File.Exists(launchSettingsPath))
        {
            try
            {
                var jn = JsonNode.Parse(File.ReadAllText(launchSettingsPath));
                diskEnvId = jn?["envId"]?.GetValue<string>();
            }
            catch { }
        }

        bool envIdConfigured = !string.IsNullOrWhiteSpace(diskEnvId);
        if (envIdConfigured)
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Environment ID configured",
                Passed = true,
                Details = $"envId: {diskEnvId}",
            });
        }
        else
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Environment ID not configured",
                Passed = false,
                Severity = DoctorSeverity.Warning,
                Details = "launchSettings.json is missing or has no envId. Fix generates an ID and updates the status-line script immediately.",
                Fix = () =>
                {
                    string newId = Guid.NewGuid().ToString("N")[..8];
                    WriteEnvId(newId);
                    ClaudePaths.SetEnvId(newId);
                    Install(_context);
                },
            });
        }

        // ---- Status-line script: must be env-id qualified ----
        // Search for the script using the current EnvId (which may have just been
        // updated by SetEnvId above if the user ran the envId fix).
        string? statusScriptFound = null;
        if (Directory.Exists(ClaudePaths.ScriptsDir))
        {
            statusScriptFound = Directory
                .EnumerateFiles(ClaudePaths.ScriptsDir, $"context-bar--{ClaudePaths.EnvId}-v*.ps1")
                .FirstOrDefault();
        }
        if (statusScriptFound != null)
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Status-line script extracted",
                Passed = true,
                Details = statusScriptFound,
            });
        }
        else
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = $"Status-line script (context-bar--{ClaudePaths.EnvId}-v*.ps1) missing",
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"Expected under {ClaudePaths.ScriptsDir}. Click Fix to re-extract from embedded resources.",
                Fix = () => Install(_context),
                RequiresRestart = true,
            });
        }

        // ---- Migration: old machine-name script detected ----
        // When envId is a proper hex id (different from the sanitized machine
        // name), an old machine-name script means Claude settings.json still
        // points to the wrong path. Fix bumps the version under the envId so
        // Claude sees the new filename immediately — no restart needed.
        if (envIdConfigured && diskEnvId != ClaudePaths.MachineId && Directory.Exists(ClaudePaths.ScriptsDir))
        {
            string? machineScript = Directory
                .EnumerateFiles(ClaudePaths.ScriptsDir, $"context-bar--{ClaudePaths.MachineId}-v*.ps1")
                .FirstOrDefault();

            if (machineScript != null && statusScriptFound == null)
            {
                string capturedId = diskEnvId!;
                checks.Add(new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = "Status-line script uses old machine name (migration needed)",
                    Passed = false,
                    Severity = DoctorSeverity.Warning,
                    Details = $"Found: {Path.GetFileName(machineScript)}\n"
                            + $"Expected env-id qualified: context-bar--{diskEnvId}-v*.ps1\n"
                            + "Fix renames the script and updates Claude settings.json.",
                    Fix = () =>
                    {
                        ClaudePaths.SetEnvId(capturedId);
                        var s = _context.LoadSettings<ClaudePluginSettings>();
                        var installs = s.ClaudeConfigDirs
                            .Where(Directory.Exists)
                            .Select(d => new ClaudeInstall(d))
                            .ToList();
                        if (installs.Count == 0) installs = ClaudeInstallDiscovery.Scan();
                        string pluginSettingsPath = Path.Combine(_context.DataDirectory, "settings.json");
                        ClaudeStatusLine.BumpScriptVersion(installs, pluginSettingsPath);
                    },
                });
            }
        }

        // Show-ProdToy hook script.
        if (File.Exists(ClaudePaths.ShowProdToyScript))
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Hook script (Show-ProdToy.ps1) extracted",
                Passed = true,
                Details = ClaudePaths.ShowProdToyScript,
            });
        }
        else
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Hook script (Show-ProdToy.ps1) missing",
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = ClaudePaths.ShowProdToyScript,
                Fix = () => Install(_context),
                RequiresRestart = true,
            });
        }

        // status-line-config.json — optional but if present must be valid JSON.
        checks.Add(JsonCheck(ClaudePaths.StatusLineConfigFile,
            "status-line-config.json is valid JSON",
            requiresRestart: false,
            fixOverride: () =>
            {
                try { File.Move(ClaudePaths.StatusLineConfigFile, ClaudePaths.StatusLineConfigFile + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss"), overwrite: true); } catch { }
                Install(_context);
            }));

        // Plugin settings.json.
        checks.Add(JsonCheck(Path.Combine(dataDir, "settings.json"),
            "Plugin settings is valid JSON", requiresRestart: true));

        // Claude installs — always live-scan so Doctor reflects this machine only,
        // regardless of what's stored in ClaudeConfigDirs (which may contain paths
        // from another machine sharing a synced data folder).
        try
        {
            var scanned = ClaudeInstallDiscovery.Scan();

            if (scanned.Count == 0)
            {
                checks.Add(new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = "No Claude CLI installs found on this machine",
                    Passed = false,
                    Severity = DoctorSeverity.Info,
                    Details = "No directory containing 'claude' with a valid settings.json was found under %USERPROFILE%, %APPDATA%, or %LOCALAPPDATA%. Click Fix to scan and register once Claude CLI is installed.",
                    Fix = () => Install(_context),
                    RequiresRestart = true,
                });
            }
            else
            {
                foreach (var install in scanned)
                {
                    checks.Add(new DoctorCheck
                    {
                        Source = DoctorSource,
                        Title = "Claude CLI install found",
                        Passed = true,
                        Details = install.ConfigDir,
                    });

                    if (File.Exists(install.SettingsFile))
                    {
                        bool jsonValid = false;
                        try
                        {
                            var txt = File.ReadAllText(install.SettingsFile);
                            if (!string.IsNullOrWhiteSpace(txt)) JsonDocument.Parse(txt);
                            jsonValid = true;
                            checks.Add(new DoctorCheck
                            {
                                Source = DoctorSource,
                                Title = "Claude CLI settings.json is valid JSON",
                                Passed = true,
                                Details = install.SettingsFile,
                            });
                        }
                        catch (Exception ex)
                        {
                            checks.Add(new DoctorCheck
                            {
                                Source = DoctorSource,
                                Title = "Claude CLI settings.json is corrupted",
                                Passed = false,
                                Severity = DoctorSeverity.Error,
                                Details = $"{install.SettingsFile}\n{ex.Message}\n(Not auto-fixed — open the file manually or reinstall Claude CLI.)",
                            });
                        }

                        // Hook-path integrity: the registered hook command's -File path
                        // must match the current plugin data dir and point at an
                        // existing file. Stale paths happen after the user moves
                        // the data folder on one machine — the *other* machine's
                        // Claude settings.json still points at the old absolute path
                        // until it's repaired here.
                        //
                        // Only gated on NotificationsEnabled: if the user disabled
                        // notifications entirely, a stale hook isn't causing any
                        // user-visible breakage so we skip the noise.
                        if (jsonValid)
                        {
                            var notifEnabled = _context.LoadSettings<ClaudePluginSettings>().NotificationsEnabled;
                            if (notifEnabled)
                                checks.Add(InspectHookPaths(install));
                        }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Could not inspect registered Claude installs",
                Passed = false,
                Severity = DoctorSeverity.Warning,
                Details = ex.Message,
            });
        }

        // Chat history directory — must exist so chat saves never fail silently
        // on the first write. If absent, offer a one-click mkdir.
        var historyDir = Path.Combine(dataDir, "history");
        if (!Directory.Exists(historyDir))
        {
            string captured = historyDir;
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Chat history directory missing",
                Passed = false,
                Severity = DoctorSeverity.Warning,
                Details = captured,
                Fix = () => Directory.CreateDirectory(captured),
            });
        }
        else
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Chat history directory exists",
                Passed = true,
                Details = historyDir,
            });

            // Per-file integrity. Three outcomes:
            //   • Clean — file parses as an array and every entry deserializes.
            //   • Recoverable — file is an array but some entries can't be parsed.
            //     Fix: rewrite keeping only the valid entries.
            //   • Unrecoverable — file doesn't parse as JSON, or root isn't an array.
            //     Fix: move the file into history/_archive/ so it no longer
            //     breaks the index scan but remains available for inspection.
            int cleanCount = 0;
            foreach (var f in Directory.EnumerateFiles(historyDir, "*.json"))
            {
                var outcome = InspectHistoryFile(f);
                switch (outcome.Kind)
                {
                    case HistoryFileKind.Clean:
                        cleanCount++;
                        break;

                    case HistoryFileKind.Recoverable:
                    {
                        string p = f;
                        var validEntries = outcome.ValidEntries!;
                        int invalid = outcome.InvalidEntryCount;
                        checks.Add(new DoctorCheck
                        {
                            Source = DoctorSource,
                            Title = $"History file has corrupt entries: {Path.GetFileName(p)}",
                            Passed = false,
                            Severity = DoctorSeverity.Warning,
                            Details = $"{p}\n{invalid} invalid entry(s); {validEntries.Count} valid. Fix keeps only the valid entries.",
                            Fix = () =>
                            {
                                try
                                {
                                    // Archive the pre-fix file first so nothing is lost.
                                    ArchiveHistoryFile(p, historyDir);
                                    var json = JsonSerializer.Serialize(validEntries, new JsonSerializerOptions { WriteIndented = true });
                                    File.WriteAllText(p, json);
                                }
                                catch { }
                            },
                        });
                        break;
                    }

                    case HistoryFileKind.Unrecoverable:
                    {
                        string p = f;
                        string err = outcome.Error ?? "unknown";
                        checks.Add(new DoctorCheck
                        {
                            Source = DoctorSource,
                            Title = $"History file unrecoverable: {Path.GetFileName(p)}",
                            Passed = false,
                            Severity = DoctorSeverity.Error,
                            Details = $"{p}\n{err}\nFix moves the file to history/_archive/.",
                            Fix = () => ArchiveHistoryFile(p, historyDir),
                        });
                        break;
                    }
                }
            }

            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = $"Chat history files clean ({cleanCount} file(s))",
                Passed = true,
                Details = historyDir,
            });
        }

        return checks;
    }

    private static void WriteEnvId(string envId)
    {
        try
        {
            string root = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".prod-toy");
            string launchSettingsPath = Path.Combine(root, "launchSettings.json");
            var opts = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var launchSettings = new JsonObject { ["envId"] = envId };
            File.WriteAllText(launchSettingsPath, launchSettings.ToJsonString(opts));

            string dataDir = Path.Combine(root, "data");
            Directory.CreateDirectory(dataDir);
            var config = new JsonObject
            {
                ["envId"]       = envId,
                ["machineName"] = Environment.MachineName,
                ["installPath"] = root,
                ["installedAt"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"),
            };
            File.WriteAllText(Path.Combine(dataDir, $"env_{envId}.config"), config.ToJsonString(opts));
        }
        catch (Exception ex)
        {
            PluginLog.Error("WriteEnvId failed", ex);
        }
    }

    private DoctorCheck DirCheck(string label, string path, DoctorSeverity severity, Action? fix = null, bool requiresRestart = false)
    {
        if (Directory.Exists(path))
        {
            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = $"{label} exists",
                Passed = true,
                Details = path,
            };
        }
        return new DoctorCheck
        {
            Source = DoctorSource,
            Title = $"{label} missing",
            Passed = false,
            Severity = severity,
            Details = path,
            Fix = fix ?? (() => Directory.CreateDirectory(path)),
            RequiresRestart = requiresRestart,
        };
    }

    private enum HistoryFileKind { Clean, Recoverable, Unrecoverable }

    private sealed record HistoryInspection(
        HistoryFileKind Kind,
        List<HistoryEntry>? ValidEntries,
        int InvalidEntryCount,
        string? Error);

    /// <summary>
    /// Try to parse a single history file. Returns classification + the recoverable
    /// entries (if any). Shape: top-level JSON array of HistoryEntry. Entries that
    /// can't deserialize are counted and dropped.
    /// </summary>
    private static HistoryInspection InspectHistoryFile(string path)
    {
        string txt;
        try { txt = File.ReadAllText(path); }
        catch (Exception ex) { return new(HistoryFileKind.Unrecoverable, null, 0, $"read failed: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(txt))
            return new(HistoryFileKind.Clean, new List<HistoryEntry>(), 0, null);

        JsonElement root;
        try
        {
            using var doc = JsonDocument.Parse(txt);
            root = doc.RootElement.Clone();
        }
        catch (Exception ex)
        {
            return new(HistoryFileKind.Unrecoverable, null, 0, $"not valid JSON: {ex.Message}");
        }

        if (root.ValueKind != JsonValueKind.Array)
            return new(HistoryFileKind.Unrecoverable, null, 0, $"expected array at root, got {root.ValueKind}");

        var valid = new List<HistoryEntry>();
        int invalid = 0;
        foreach (var el in root.EnumerateArray())
        {
            try
            {
                var entry = el.Deserialize<HistoryEntry>();
                if (entry == null || string.IsNullOrWhiteSpace(entry.Type))
                {
                    invalid++;
                    continue;
                }
                valid.Add(entry);
            }
            catch { invalid++; }
        }

        return invalid == 0
            ? new(HistoryFileKind.Clean, valid, 0, null)
            : new(HistoryFileKind.Recoverable, valid, invalid, null);
    }

    /// <summary>
    /// Move a history file into history/_archive/ so it's preserved for
    /// inspection but no longer scanned as a live history file.
    /// </summary>
    private static void ArchiveHistoryFile(string filePath, string historyDir)
    {
        try
        {
            var archiveDir = Path.Combine(historyDir, "_archive");
            Directory.CreateDirectory(archiveDir);
            var name = Path.GetFileNameWithoutExtension(filePath);
            var ext  = Path.GetExtension(filePath);
            var stamp = DateTime.Now.ToString("yyyyMMddHHmmss");
            var dest = Path.Combine(archiveDir, $"{name}.broken-{stamp}{ext}");
            File.Move(filePath, dest, overwrite: true);
        }
        catch { /* best-effort — archiving failures are non-fatal */ }
    }

    /// <summary>
    /// Inspect every ProdToy hook entry inside an install's settings.json and
    /// report whether each points at the expected Show-ProdToy.ps1 path (and
    /// whether that file actually exists on disk). Catches the scenario that
    /// prompted this check: stop-hook fires with <c>-File "..."</c> referencing
    /// a plugin data dir that no longer exists after a data-folder move.
    /// Fix repairs all stale commands in-place across every registered install.
    /// </summary>
    private DoctorCheck InspectHookPaths(ClaudeInstall install)
    {
        string settingsPath = install.SettingsFile;
        string expectedScript = ClaudePaths.ShowProdToyScript;

        try
        {
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(settingsPath));
            if (root?["hooks"] is not System.Text.Json.Nodes.JsonObject hooksNode)
            {
                return new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = "No ProdToy hooks registered in this Claude install",
                    Passed = false,
                    Severity = DoctorSeverity.Warning,
                    Details = $"{settingsPath}\nClick Fix to register hook entries.",
                    Fix = () => Install(_context),
                    RequiresRestart = true,
                };
            }

            var issues = new List<string>();
            int prodToyHookCount = 0;

            foreach (var kv in hooksNode)
            {
                if (kv.Value is not System.Text.Json.Nodes.JsonArray eventArray) continue;
                foreach (var ruleSet in eventArray)
                {
                    if (ruleSet?["hooks"] is not System.Text.Json.Nodes.JsonArray ha) continue;
                    foreach (var h in ha)
                    {
                        string? cmd = h?["command"]?.GetValue<string>();
                        if (cmd == null) continue;
                        if (!(cmd.Contains("Show-ProdToy") || cmd.Contains("Show-DevToy"))) continue;
                        prodToyHookCount++;

                        var scriptPath = ClaudeHookManager.ExtractScriptPath(cmd);
                        if (scriptPath == null)
                        {
                            issues.Add($"[{kv.Key}] Unparseable hook command: {cmd}");
                            continue;
                        }

                        bool pathMatches = string.Equals(
                            Path.GetFullPath(scriptPath),
                            Path.GetFullPath(expectedScript),
                            StringComparison.OrdinalIgnoreCase);
                        bool fileExists = File.Exists(scriptPath);

                        if (!pathMatches && !fileExists)
                            issues.Add($"[{kv.Key}] Stale path (file missing): {scriptPath}");
                        else if (!pathMatches)
                            issues.Add($"[{kv.Key}] Wrong path (expected current plugin dir): {scriptPath}");
                        else if (!fileExists)
                            issues.Add($"[{kv.Key}] Correct path but file is missing on disk: {scriptPath}");
                    }
                }
            }

            if (prodToyHookCount == 0)
            {
                return new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = "No ProdToy hooks registered in this Claude install",
                    Passed = false,
                    Severity = DoctorSeverity.Warning,
                    Details = $"{settingsPath}\nClick Fix to register hook entries.",
                    Fix = () => Install(_context),
                    RequiresRestart = true,
                };
            }

            if (issues.Count == 0)
            {
                return new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = $"Hook paths valid ({prodToyHookCount} entry(s))",
                    Passed = true,
                    Details = settingsPath,
                };
            }

            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = $"Hook paths out of date ({issues.Count} of {prodToyHookCount})",
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"{settingsPath}\nExpected: {expectedScript}\n\n" + string.Join("\n", issues) +
                          "\n\nFix rewrites the -File path in every ProdToy hook entry across all installs.",
                Fix = () =>
                {
                    // Ensure the target script actually exists before pointing
                    // hooks at it — Install() extracts Show-ProdToy.ps1 as part
                    // of its flow, so re-running it is the safe belt-and-suspenders
                    // path. Then surgically rewrite any lingering stale paths
                    // (which Install doesn't touch because it short-circuits on
                    // "command already starts with Show-ProdToy").
                    try { Install(_context); } catch { }
                    var scanned = ClaudeInstallDiscovery.Scan();
                    ClaudeHookManager.FixStaleHookPaths(scanned);
                },
                RequiresRestart = false,
            };
        }
        catch (Exception ex)
        {
            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Could not inspect hook paths",
                Passed = false,
                Severity = DoctorSeverity.Warning,
                Details = $"{settingsPath}\n{ex.Message}",
            };
        }
    }

    private static DoctorCheck JsonCheck(string path, string title, bool requiresRestart, Action? fixOverride = null)
    {
        if (!File.Exists(path))
        {
            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = title,
                Passed = true,
                Details = $"{path} (not present — will be recreated on first write)",
            };
        }
        try
        {
            var txt = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(txt)) JsonDocument.Parse(txt);
            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = title,
                Passed = true,
                Details = path,
            };
        }
        catch (Exception ex)
        {
            string p = path;
            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = title.Replace("is valid JSON", "is corrupted"),
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"{p}\n{ex.Message}",
                Fix = fixOverride ?? (() =>
                {
                    var backup = p + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Move(p, backup, overwrite: true); } catch { }
                }),
                RequiresRestart = requiresRestart,
            };
        }
    }
}
