using System.Text.Json;
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

        // Status-line script (context-bar.ps1 or context-bar-v{n}.ps1).
        string? statusScriptFound = null;
        if (Directory.Exists(ClaudePaths.ScriptsDir))
        {
            statusScriptFound = Directory.EnumerateFiles(ClaudePaths.ScriptsDir, "context-bar*.ps1").FirstOrDefault();
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
                Title = "Status-line script (context-bar.ps1) missing",
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"Expected under {ClaudePaths.ScriptsDir}. Click Fix to re-extract from embedded resources.",
                Fix = () => Install(_context),
                RequiresRestart = true,
            });
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

        // Registered Claude installs.
        try
        {
            var settings = _context.LoadSettings<ClaudePluginSettings>();
            var dirs = settings.ClaudeConfigDirs ?? new List<string>();

            if (dirs.Count == 0)
            {
                checks.Add(new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = "No Claude CLI installs registered",
                    Passed = false,
                    Severity = DoctorSeverity.Info,
                    Details = "Hooks/status line are not wired into any Claude install. Click Fix to scan and register.",
                    Fix = () => Install(_context),
                    RequiresRestart = true,
                });
            }
            else
            {
                foreach (var dir in dirs)
                {
                    if (Directory.Exists(dir))
                    {
                        checks.Add(new DoctorCheck
                        {
                            Source = DoctorSource,
                            Title = "Registered Claude install present",
                            Passed = true,
                            Details = dir,
                        });

                        var claudeSettings = Path.Combine(dir, "settings.json");
                        if (File.Exists(claudeSettings))
                        {
                            try
                            {
                                var txt = File.ReadAllText(claudeSettings);
                                if (!string.IsNullOrWhiteSpace(txt)) JsonDocument.Parse(txt);
                                checks.Add(new DoctorCheck
                                {
                                    Source = DoctorSource,
                                    Title = "Claude CLI settings.json is valid JSON",
                                    Passed = true,
                                    Details = claudeSettings,
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
                                    Details = $"{claudeSettings}\n{ex.Message}\n(Not auto-fixed — open the file manually or reinstall Claude CLI.)",
                                });
                            }
                        }
                    }
                    else
                    {
                        checks.Add(new DoctorCheck
                        {
                            Source = DoctorSource,
                            Title = "Registered Claude install no longer exists",
                            Passed = false,
                            Severity = DoctorSeverity.Warning,
                            Details = dir + "\nClick Fix to remove it from the integration list.",
                            Fix = () =>
                            {
                                var s = _context.LoadSettings<ClaudePluginSettings>();
                                var pruned = (s.ClaudeConfigDirs ?? new List<string>()).Where(Directory.Exists).ToList();
                                _context.SaveSettings(s with { ClaudeConfigDirs = pruned });
                            },
                        });
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
