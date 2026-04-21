using System.Text.Json.Serialization;

namespace ProdToy.Plugins.ClaudeIntegration;

enum ClaudeLauncherMode
{
    WindowsTerminal,
    CmdWindow,
}

/// <summary>
/// A saved "launch Claude in a specific project" shortcut. Persisted as JSON in
/// the plugin's data directory (shortcuts.json). Fields are all back-compat —
/// omitting any field in the JSON produces the default.
/// </summary>
sealed record ClaudeShortcut
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; init; } = "";

    [JsonPropertyName("claudeArgs")]
    public string ClaudeArgs { get; init; } = "--dangerously-skip-permissions --continue";

    /// <summary>Windows Terminal profile name, e.g. "Command Prompt". Empty = wt default.</summary>
    [JsonPropertyName("wtProfile")]
    public string WtProfile { get; init; } = "Command Prompt";

    [JsonPropertyName("launcherMode")]
    public ClaudeLauncherMode LauncherMode { get; init; } = ClaudeLauncherMode.WindowsTerminal;

    [JsonPropertyName("requireAdmin")]
    public bool RequireAdmin { get; init; }

    [JsonPropertyName("notes")]
    public string Notes { get; init; } = "";

    /// <summary>
    /// Slash-separated folder path for organizing shortcuts in the list UI.
    /// Empty string = root. Examples: "Work", "Work/Backend", "Personal/Pi Projects".
    /// Existing JSON without this field loads with an empty string (back-compat).
    /// </summary>
    [JsonPropertyName("folderPath")]
    public string FolderPath { get; init; } = "";

    [JsonPropertyName("createdAt")]
    public DateTime CreatedAt { get; init; } = DateTime.Now;

    [JsonPropertyName("updatedAt")]
    public DateTime? UpdatedAt { get; init; }

    [JsonPropertyName("lastLaunchedAt")]
    public DateTime? LastLaunchedAt { get; init; }

    [JsonPropertyName("launchCount")]
    public int LaunchCount { get; init; }
}
