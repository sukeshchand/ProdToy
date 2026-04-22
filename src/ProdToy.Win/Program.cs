using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace ProdToy;

static class Program
{
    private const string MutexName = "ProdToy_SingleInstance_Mutex";
    internal const string PipeName = "ProdToy_Pipe";

    [STAThread]
    static void Main(string[] args)
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log.Error("Unhandled domain exception", e.ExceptionObject as Exception);
        Application.ThreadException += (_, e) =>
            Log.Error("Unhandled UI thread exception", e.Exception);

        // No arguments → run if installed, otherwise point user at the installer.
        if (args.Length == 0)
        {
            if (AppRegistry.IsRegistered() && IsRunningFromInstallDir())
            {
                RunInstalledInstance();
            }
            else
            {
                ShowInstallerRequiredMessage();
            }
            return;
        }

        // Phase 5: generic envelope args are the only CLI shape the host
        // recognizes. Claude-specific flags (--title/--message/--session-id/
        // --save-question) are handled entirely by the plugin via its
        // registered pipe commands (claude.notify / claude.save-question).
        string? envelopeCommand = null;
        string? envelopePayload = null;
        string? envelopePayloadFile = null;

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--command" when i + 1 < args.Length:
                    envelopeCommand = args[++i];
                    break;
                case "--payload" when i + 1 < args.Length:
                    envelopePayload = args[++i];
                    break;
                case "--payload-file" when i + 1 < args.Length:
                    envelopePayloadFile = args[++i];
                    break;
                case "--plugin" when i + 1 < args.Length:
                    // Reserved for future multi-plugin routing; currently informational.
                    _ = args[++i];
                    break;
            }
        }

        if (string.IsNullOrEmpty(envelopeCommand))
        {
            // No recognized args — treat like a no-arg launch.
            if (AppRegistry.IsRegistered() && IsRunningFromInstallDir())
                RunInstalledInstance();
            else
                ShowInstallerRequiredMessage();
            return;
        }

        string? payloadJson = envelopePayload;
        if (payloadJson == null && envelopePayloadFile != null
            && File.Exists(envelopePayloadFile) && IsPathSafe(envelopePayloadFile))
        {
            payloadJson = File.ReadAllText(envelopePayloadFile, Encoding.UTF8);
        }

        using var envelopeMutex = new Mutex(true, MutexName, out bool isFirstForEnvelope);
        if (!isFirstForEnvelope)
        {
            SendEnvelopeToPipe(envelopeCommand, payloadJson);
            return;
        }

        // No host running — start one and deliver the envelope once it's up.
        Application.Run(new PopupAppContext(envelopeCommand, payloadJson));
    }

    /// <summary>
    /// Shown when the host exe is launched from outside its install dir and no
    /// registry entry exists. Setup lives in ProdToySetup.exe now — direct the
    /// user there rather than trying to self-install.
    /// </summary>
    private static void ShowInstallerRequiredMessage()
    {
        MessageBox.Show(
            "ProdToy is not installed on this machine.\n\n" +
            "Please run ProdToySetup.exe to install.",
            "ProdToy",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
    }

    private static void RunInstalledInstance()
    {
        using var mutex = new Mutex(true, MutexName, out bool isNew);
        if (!isNew)
        {
            // Already running — tell existing instance to bring itself to front
            SendToPipe("ProdToy", "ProdToy is ready.", NotificationType.Info);
            return;
        }

        Log.Info($"ProdToy v{AppVersion.Current} starting");

        // Keep the Apps & Features DisplayVersion in sync with the running exe.
        // After an auto-update swap, this refreshes the value so "Installed updates"
        // in Windows Settings matches AppVersion.Current.
        AppRegistry.SyncDisplayVersion();

        // Check if we just came back from an auto-update (start hidden, no welcome dialog).
        string updateMarker = Path.Combine(AppPaths.Root, "_updated.marker");
        bool justUpdated = File.Exists(updateMarker);
        if (justUpdated)
        {
            try { File.Delete(updateMarker); } catch { }
        }

        // Check if launched from setup (welcome already shown, just start hidden)
        string hiddenMarker = Path.Combine(AppPaths.Root, "_start_hidden.marker");
        bool startHidden = justUpdated || File.Exists(hiddenMarker);
        if (File.Exists(hiddenMarker))
            try { File.Delete(hiddenMarker); } catch { }

        // Phase 5: the host no longer knows about Claude chat history. On a
        // no-args launch, show a generic welcome. Plugins that care about
        // surfacing their last notification can do so from their own
        // dashboard tiles.
        string title = "ProdToy";
        string message = "No notifications yet. ProdToy will notify you here.";
        string type = NotificationType.Info;

        // If we came back from an update, the PS1 is polling for either
        // _update_ok.marker (we're healthy) or _update_fail.marker (we died
        // during init). Write the fail marker first so any crash in the
        // construction path is observable — we'll overwrite it with OK on
        // successful start, and then let the PS1's Phase 5 clean tmp.
        string okMarker = Path.Combine(AppPaths.Root, "_update_ok.marker");
        string failMarker = Path.Combine(AppPaths.Root, "_update_fail.marker");

        PopupAppContext? ctx = null;
        try
        {
            ctx = new PopupAppContext(title, message, type, startHidden: startHidden);

            if (justUpdated)
            {
                // Signal PS1 that the new host constructed successfully.
                try
                {
                    File.WriteAllText(okMarker, "");
                    Log.Info("Wrote _update_ok.marker — signalling PS1 health check");
                }
                catch (Exception ex)
                {
                    Log.Warn($"Failed to write _update_ok.marker: {ex.Message}");
                }
            }
            else
            {
                // Not a post-update run — opportunistically clean any stale tmp
                // from a previous update. Only delete when there is no
                // update-failed.log inside, so post-mortem data is preserved.
                CleanupStaleUpdateTmp();
            }
        }
        catch (Exception ex)
        {
            Log.Error("PopupAppContext construction failed", ex);
            if (justUpdated)
            {
                try { File.WriteAllText(failMarker, ex.ToString()); } catch { }
            }
            throw;
        }

        Application.Run(ctx!);
    }

    /// <summary>
    /// Removes ~/.prod-toy/tmp if it only contains successful-update leftovers.
    /// If a previous update wrote update-failed.log, the whole tmp dir is left
    /// in place so the user or support can inspect update.log for the failure.
    /// </summary>
    private static void CleanupStaleUpdateTmp()
    {
        try
        {
            if (!Directory.Exists(AppPaths.TmpDir)) return;

            string failLog = Path.Combine(AppPaths.TmpDir, "update-failed.log");
            if (File.Exists(failLog))
            {
                Log.Warn($"Previous update failed — leaving {AppPaths.TmpDir} in place for post-mortem");
                return;
            }

            Directory.Delete(AppPaths.TmpDir, recursive: true);
            Log.Info("Stale update tmp dir removed");
        }
        catch (Exception ex)
        {
            Log.Warn($"Stale tmp cleanup failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Validates that a file path is within safe directories (user profile or temp).
    /// Prevents path traversal attacks via CLI arguments.
    /// </summary>
    private static bool IsPathSafe(string filePath)
    {
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var tempDir = Path.GetTempPath();
            return fullPath.StartsWith(userProfile, StringComparison.OrdinalIgnoreCase)
                || fullPath.StartsWith(tempDir, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static bool IsRunningFromInstallDir()
    {
        try
        {
            var exeDir = Path.GetDirectoryName(Application.ExecutablePath) ?? "";
            return string.Equals(
                Path.GetFullPath(exeDir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                Path.GetFullPath(AppPaths.Root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static void SendToPipe(string title, string message, string type, string sessionId = "", string cwd = "")
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var payload = JsonSerializer.Serialize(new { title, message, type, sessionId, cwd });
            var bytes = Encoding.UTF8.GetBytes(payload);
            client.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Log.Warn($"SendToPipe failed: {ex.Message}");
        }
    }

    /// <summary>Sends a routed envelope {command, payload} to the running host's
    /// pipe. The host's PipeRouter dispatches to the plugin that registered
    /// the matching command name.</summary>
    internal static void SendEnvelopeToPipe(string command, string? payloadJson)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", PipeName, PipeDirection.Out);
            client.Connect(3000);
            var envelope = JsonSerializer.Serialize(new { command, payload = payloadJson });
            var bytes = Encoding.UTF8.GetBytes(envelope);
            client.Write(bytes, 0, bytes.Length);
        }
        catch (Exception ex)
        {
            Log.Warn($"SendEnvelopeToPipe failed: {ex.Message}");
        }
    }
}
