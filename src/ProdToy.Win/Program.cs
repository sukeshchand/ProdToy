using System.Diagnostics;
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

        string title = "ProdToy";
        string message = "Task completed.";
        string type = NotificationType.Info;
        string? messageFile = null;
        string? saveQuestion = null;
        string sessionId = "";
        string cwd = "";

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i].ToLowerInvariant())
            {
                case "--title" or "-t" when i + 1 < args.Length:
                    title = args[++i];
                    break;
                case "--message" or "-m" when i + 1 < args.Length:
                    message = args[++i];
                    break;
                case "--message-file" when i + 1 < args.Length:
                    messageFile = args[++i];
                    break;
                case "--save-question" when i + 1 < args.Length:
                    saveQuestion = args[++i];
                    break;
                case "--type" when i + 1 < args.Length:
                    type = args[++i].ToLowerInvariant();
                    break;
                case "--session-id" when i + 1 < args.Length:
                    sessionId = args[++i];
                    break;
                case "--cwd" when i + 1 < args.Length:
                    cwd = args[++i];
                    break;
            }
        }

        // Save question to history and exit (UserPromptSubmit hook)
        if (saveQuestion != null)
        {
            // Read from file if it's a file path (validate it's within safe directories)
            if (File.Exists(saveQuestion) && IsPathSafe(saveQuestion))
                saveQuestion = File.ReadAllText(saveQuestion, Encoding.UTF8);
            ResponseHistory.SaveQuestion(saveQuestion.Replace("\\n", "\n").Replace("\\t", "\t").Trim(), sessionId, cwd);
            return;
        }

        // Read message from file if specified (avoids command-line length limits)
        if (messageFile != null && File.Exists(messageFile) && IsPathSafe(messageFile))
            message = File.ReadAllText(messageFile, Encoding.UTF8);

        message = message.Replace("\\n", "\n").Replace("\\t", "\t");

        // Save response to history (completes pending question entry)
        ResponseHistory.SaveResponse(title, message, type, sessionId, cwd);

        using var mutex = new Mutex(true, MutexName, out bool isNewInstance);

        if (!isNewInstance)
        {
            SendToPipe(title, message, type, sessionId, cwd);
            return;
        }

        Application.Run(new PopupAppContext(title, message, type, sessionId, cwd));
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

        // Load last history entry or show a default welcome
        var latest = ResponseHistory.GetLatest();
        string title = latest?.Title ?? "ProdToy";
        string message = latest?.Message ?? "No notifications yet. ProdToy will notify you here.";
        string type = latest?.Type ?? NotificationType.Info;
        string sessionId = latest?.SessionId ?? "";
        string cwd = latest?.Cwd ?? "";

        Application.Run(new PopupAppContext(title, message, type, sessionId, cwd, startHidden: startHidden));
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
            Debug.WriteLine($"SendToPipe failed: {ex.Message}");
        }
    }
}
