using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Screenshot;

public partial class ScreenshotPlugin
{
    private const string DoctorSource = "Screenshot";

    public IReadOnlyList<DoctorCheck> Diagnose()
    {
        var checks = new List<DoctorCheck>();
        var dataDir = _context.DataDirectory;

        // Data directory.
        checks.Add(DirCheck("Plugin data directory", dataDir, severity: DoctorSeverity.Warning));

        // screenshots/ and _edits/ — info-level (lazy-created when user takes a shot).
        var shotsDir = Path.Combine(dataDir, "screenshots");
        checks.Add(DirCheck("Screenshots directory", shotsDir, severity: DoctorSeverity.Info,
            missingSuffix: " (normal if you haven't taken any screenshots yet)"));

        var editsDir = Path.Combine(shotsDir, "_edits");
        if (Directory.Exists(shotsDir))
        {
            checks.Add(DirCheck("Edit-sessions directory", editsDir, severity: DoctorSeverity.Info));
        }

        // Settings file.
        checks.Add(JsonCheck(Path.Combine(dataDir, "settings.json"), "Plugin settings is valid JSON", requiresRestart: true));

        // Per-session state.json files.
        if (Directory.Exists(editsDir))
        {
            int good = 0;
            foreach (var dir in Directory.EnumerateDirectories(editsDir))
            {
                var state = Path.Combine(dir, "state.json");
                if (!File.Exists(state)) continue;
                try
                {
                    var txt = File.ReadAllText(state);
                    if (!string.IsNullOrWhiteSpace(txt)) JsonDocument.Parse(txt);
                    good++;
                }
                catch
                {
                    string p = state;
                    checks.Add(new DoctorCheck
                    {
                        Source = DoctorSource,
                        Title = $"Edit session state corrupted: {Path.GetFileName(dir)}",
                        Passed = false,
                        Severity = DoctorSeverity.Warning,
                        Details = p,
                        Fix = () =>
                        {
                            var backup = p + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                            try { File.Move(p, backup, overwrite: true); } catch { }
                        },
                    });
                }
            }
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = $"Edit session states valid ({good} session(s))",
                Passed = true,
                Details = editsDir,
            });
        }

        return checks;
    }

    private static DoctorCheck DirCheck(string label, string path, DoctorSeverity severity, string missingSuffix = "")
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
            Details = path + missingSuffix,
            Fix = () => Directory.CreateDirectory(path),
        };
    }

    private static DoctorCheck JsonCheck(string path, string title, bool requiresRestart)
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
                Fix = () =>
                {
                    var backup = p + ".broken-" + DateTime.Now.ToString("yyyyMMddHHmmss");
                    try { File.Move(p, backup, overwrite: true); } catch { }
                },
                RequiresRestart = requiresRestart,
            };
        }
    }
}
