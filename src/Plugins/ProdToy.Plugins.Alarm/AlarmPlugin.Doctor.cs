using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

public partial class AlarmPlugin
{
    private const string DoctorSource = "Alarms";

    public IReadOnlyList<DoctorCheck> Diagnose()
    {
        var checks = new List<DoctorCheck>();
        var dataDir = _context.DataDirectory;

        checks.Add(DataDirCheck(dataDir));

        CheckJsonFile(checks, Path.Combine(dataDir, "alarms.json"),        "alarms.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "alarm-history.json"), "alarm-history.json is valid JSON", expectArray: true);
        CheckJsonFile(checks, Path.Combine(dataDir, "settings.json"),      "Plugin settings is valid JSON", expectArray: false);

        return checks;
    }

    private static DoctorCheck DataDirCheck(string dataDir)
    {
        if (Directory.Exists(dataDir))
        {
            return new DoctorCheck
            {
                Source = DoctorSource,
                Title = "Plugin data directory exists",
                Passed = true,
                Details = dataDir,
            };
        }
        return new DoctorCheck
        {
            Source = DoctorSource,
            Title = "Plugin data directory missing",
            Passed = false,
            Severity = DoctorSeverity.Warning,
            Details = dataDir,
            Fix = () => Directory.CreateDirectory(dataDir),
        };
    }

    private static void CheckJsonFile(List<DoctorCheck> checks, string path, string title, bool expectArray)
    {
        if (!File.Exists(path))
        {
            checks.Add(new DoctorCheck
            {
                Source = DoctorSource,
                Title = title,
                Passed = true,
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
                Source = DoctorSource,
                Title = title,
                Passed = true,
                Details = path,
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
