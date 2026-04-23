using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Host-side "doctor" — enumerates checks grouped by source (host + each
/// plugin). Each source's checks are run together on demand so the UI can
/// show progress one group at a time.
/// </summary>
static class HostDoctor
{
    private const string Source = "ProdToy";

    public sealed record DoctorCheckSource(string Name, Func<List<DoctorCheck>> Run);

    public static List<DoctorCheckSource> GetSources()
    {
        var sources = new List<DoctorCheckSource>
        {
            new(Source, () => DiagnoseHost().ToList()),
        };

        foreach (var info in PluginManager.Plugins)
        {
            if (!info.Enabled || info.Instance is null) continue;
            if (info.Instance is IDoctor doc)
            {
                var capturedDoc = doc;
                var capturedName = info.Name;
                sources.Add(new DoctorCheckSource(capturedName, () =>
                {
                    try
                    {
                        var checks = capturedDoc.Diagnose();
                        return checks?.ToList() ?? new List<DoctorCheck>();
                    }
                    catch (Exception ex)
                    {
                        return new List<DoctorCheck>
                        {
                            new()
                            {
                                Source = capturedName,
                                Title = $"Doctor check crashed: {ex.GetType().Name}",
                                Passed = false,
                                Severity = DoctorSeverity.Warning,
                                Details = ex.Message,
                            }
                        };
                    }
                }));
            }
        }

        return sources;
    }

    /// <summary>Convenience: run every source and flatten the results.</summary>
    public static List<DoctorCheck> RunAll()
    {
        var all = new List<DoctorCheck>();
        foreach (var s in GetSources()) all.AddRange(s.Run());
        return all;
    }

    private static IEnumerable<DoctorCheck> DiagnoseHost()
    {
        // Required directories — created if missing.
        foreach (var (label, path) in new (string, string)[]
        {
            ("Root",          AppPaths.Root),
            ("Data",          AppPaths.DataDir),
            ("Plugins",       AppPaths.PluginsDir),
            ("Plugins bin",   AppPaths.PluginsBinDir),
            ("Plugins data",  AppPaths.PluginsDataDir),
            ("Logs",          AppPaths.LogsDir),
        })
        {
            if (Directory.Exists(path))
            {
                yield return new DoctorCheck
                {
                    Source = Source,
                    Title = $"{label} directory exists",
                    Passed = true,
                    Details = path,
                };
            }
            else
            {
                string captured = path;
                yield return new DoctorCheck
                {
                    Source = Source,
                    Title = $"{label} directory missing",
                    Passed = false,
                    Severity = DoctorSeverity.Warning,
                    Details = captured,
                    Fix = () => Directory.CreateDirectory(captured),
                };
            }
        }

        // Host executable — if missing, something is very wrong (report only).
        if (File.Exists(AppPaths.ExePath))
        {
            yield return new DoctorCheck
            {
                Source = Source,
                Title = "Host executable present",
                Passed = true,
                Details = AppPaths.ExePath,
            };
        }
        else
        {
            yield return new DoctorCheck
            {
                Source = Source,
                Title = "Host executable not found",
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"Expected at {AppPaths.ExePath}. Run the installer to restore.",
            };
        }

        // Host settings JSON — if unreadable, offer to reset to defaults.
        yield return CheckJsonFile(Source, AppPaths.SettingsFile, "Host settings file valid JSON", requiresRestart: true);

        // Plugins-state file sanity.
        yield return CheckJsonFile(Source, AppPaths.PluginsStateFile, "Plugins state file valid JSON", requiresRestart: true);

        // Discovered plugins: each must have loaded successfully.
        foreach (var info in PluginManager.Plugins)
        {
            if (string.IsNullOrEmpty(info.LoadError))
            {
                yield return new DoctorCheck
                {
                    Source = Source,
                    Title = $"Plugin loaded: {info.Name} v{info.Version}",
                    Passed = true,
                    Details = info.DllPath,
                };
            }
            else
            {
                yield return new DoctorCheck
                {
                    Source = Source,
                    Title = $"Plugin failed to load: {info.Name}",
                    Passed = false,
                    Severity = DoctorSeverity.Error,
                    Details = info.LoadError!,
                };
            }
        }
    }

    /// <summary>
    /// Shared helper: if the file is present, check that it parses as JSON.
    /// If missing, treat as a pass (many JSON files are written on demand).
    /// </summary>
    private static DoctorCheck CheckJsonFile(string source, string path, string title, bool requiresRestart)
    {
        if (!File.Exists(path))
        {
            return new DoctorCheck
            {
                Source = source,
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
                Source = source,
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
                Source = source,
                Title = title.Replace("valid JSON", "is corrupted"),
                Passed = false,
                Severity = DoctorSeverity.Error,
                Details = $"{p}\n{ex.Message}",
                Fix = () =>
                {
                    var backup = p + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Move(p, backup, overwrite: true); } catch { File.Delete(p); }
                },
                RequiresRestart = requiresRestart,
            };
        }
    }
}
