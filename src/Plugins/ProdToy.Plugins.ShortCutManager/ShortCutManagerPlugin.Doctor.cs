using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

public partial class ShortCutManagerPlugin
{
    private const string DoctorSource = "Shortcuts";

    public IReadOnlyList<DoctorCheck> Diagnose()
    {
        var checks = new List<DoctorCheck>();
        var dataDir = _context.DataDirectory;

        // Data directory.
        if (Directory.Exists(dataDir))
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource, Title = "Plugin data directory exists",
                Passed = true, Details = dataDir,
            });
        }
        else
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource, Title = "Plugin data directory missing",
                Passed = false, Severity = DoctorSeverity.Warning, Details = dataDir,
                Fix = () => Directory.CreateDirectory(dataDir),
            });
        }

        // All JSON data files.
        CheckJsonFile(checks, Path.Combine(dataDir, "shortcuts.json"),            "shortcuts.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "shortcut-folders.json"),     "shortcut-folders.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "owned-wt-profiles.json"),    "owned-wt-profiles.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "owned-wt-schemes.json"),     "owned-wt-schemes.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "shortcuts-recyclebin.json"), "shortcuts-recyclebin.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "settings.json"),             "Plugin settings is valid JSON", expectArray: false);

        // Stale WT profile tracking.
        try
        {
            var wtSettings = WindowsTerminalProfiles.FindSettingsPath();
            if (wtSettings != null && File.Exists(wtSettings))
            {
                var ownedProfiles = OwnedWtProfilesStore.Load();
                var stale = new List<string>();
                foreach (var name in ownedProfiles)
                {
                    if (WindowsTerminalProfiles.ReadProfile(name) == null)
                        stale.Add(name);
                }
                if (stale.Count > 0)
                {
                    var captured = stale;
                    checks.Add(new DoctorCheck
                    {
                        Source = DoctorSource,
                        Title = $"Stale tracked WT profiles: {stale.Count}",
                        Passed = false,
                        Severity = DoctorSeverity.Info,
                        Details = string.Join(", ", stale) + "\n(Profile no longer exists in WT — click Fix to prune.)",
                        Fix = () => { foreach (var n in captured) OwnedWtProfilesStore.Remove(n); },
                    });
                }
                else
                {
                    checks.Add(new DoctorCheck
                    {
                        Source = DoctorSource,
                        Title = $"Tracked WT profiles match WT settings ({ownedProfiles.Count} entry(s))",
                        Passed = true,
                        Details = wtSettings,
                    });
                }
            }
            else
            {
                checks.Add(new DoctorCheck
                {
                    Source = DoctorSource,
                    Title = "Windows Terminal settings.json located",
                    Passed = true,
                    Details = "(WT not installed — stale-profile check skipped)",
                });
            }
        }
        catch (Exception ex)
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Could not verify WT profile tracking",
                Passed = false,
                Severity = DoctorSeverity.Info,
                Details = ex.Message,
            });
        }

        return checks;
    }

    private static void CheckJsonFile(List<DoctorCheck> checks, string path, string title, bool expectArray)
    {
        if (!File.Exists(path))
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource, Title = title, Passed = true,
                Details = $"{path} (not present — will be recreated on first write)",
            });
            return;
        }
        try
        {
            var txt = File.ReadAllText(path);
            if (!string.IsNullOrWhiteSpace(txt))
            {
                using var doc = JsonDocument.Parse(txt);
                if (expectArray && doc.RootElement.ValueKind != JsonValueKind.Array)
                    throw new InvalidDataException($"Expected JSON array, got {doc.RootElement.ValueKind}");
            }
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource, Title = title, Passed = true, Details = path,
            });
        }
        catch (Exception ex)
        {
            string captured = path;
            string emptyValue = expectArray ? "[]" : "{}";
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = title.Replace("is valid JSON", "is corrupted"),
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"{path}\n{ex.Message}",
                Fix = () =>
                {
                    var backup = captured + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Move(captured, backup, overwrite: true); } catch { }
                    File.WriteAllText(captured, emptyValue);
                },
                RequiresRestart = true,
            });
        }
    }
}
