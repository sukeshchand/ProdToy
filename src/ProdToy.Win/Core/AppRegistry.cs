using Microsoft.Win32;

namespace ProdToy;

/// <summary>
/// Read-mostly view of the Windows "Apps &amp; Features" registration. The
/// installer (ProdToy.Setup) owns Register/Unregister; the host only reads and
/// keeps DisplayVersion fresh after an auto-update.
/// </summary>
static class AppRegistry
{
    private const string UninstallKeyPath =
        @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\ProdToy";

    public static bool IsRegistered()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key != null;
        }
        catch (Exception ex)
        {
            Log.Warn($"AppRegistry.IsRegistered failed: {ex.Message}");
            return false;
        }
    }

    public static string? GetInstalledVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath);
            return key?.GetValue("DisplayVersion") as string;
        }
        catch (Exception ex)
        {
            Log.Warn($"AppRegistry.GetInstalledVersion failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// If the registry key exists AND its DisplayVersion differs from the currently
    /// running exe's AppVersion.Current, update it in place. Does nothing if the key
    /// doesn't exist — creating it is the installer's job.
    /// </summary>
    public static void SyncDisplayVersion()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(UninstallKeyPath, writable: true);
            if (key == null) return;
            var current = key.GetValue("DisplayVersion") as string;
            if (current == AppVersion.Current) return;
            key.SetValue("DisplayVersion", AppVersion.Current);
        }
        catch (Exception ex)
        {
            Log.Warn($"AppRegistry.SyncDisplayVersion failed: {ex.Message}");
        }
    }
}
