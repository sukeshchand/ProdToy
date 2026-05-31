using System.Text.Json.Serialization;

namespace ProdToy.Plugins.ShortCutManager;

enum LauncherMode
{
    WindowsTerminal,
    CmdWindow,
}

/// <summary>
/// Where a Windows Terminal-launched shortcut should open:
///   <see cref="NewWindow"/> → a brand-new WT window (the historical default,
///   passes no -w flag so WT uses its own new-window behavior).
///   <see cref="ExistingWindow"/> → reuse the active/last-focused WT window as
///   a new tab. Maps to <c>wt.exe -w 0 …</c>.
/// Only consulted when <see cref="Shortcut.LauncherMode"/> is
/// <see cref="LauncherMode.WindowsTerminal"/>.
/// </summary>
enum WtWindowTarget
{
    NewWindow,
    ExistingWindow,
}

/// <summary>
/// A saved "launch a CLI in a specific project" shortcut. Persisted as JSON in
/// the plugin's data directory (shortcuts.json). Fields are all back-compat —
/// omitting any field in the JSON produces the default. JSON property names
/// preserve the historical "claude" prefix where present so shortcuts saved
/// under the ClaudeIntegration plugin keep loading unchanged.
/// </summary>
sealed record Shortcut
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = Guid.NewGuid().ToString("N");

    [JsonPropertyName("name")]
    public string Name { get; init; } = "";

    /// <summary>
    /// Launch profile id — selects the binary invoked by the launcher and
    /// the hint/defaults shown in the edit form. Defaults to "claude" so
    /// shortcuts saved before this field existed keep their behavior.
    /// </summary>
    [JsonPropertyName("profile")]
    public string Profile { get; init; } = "claude";

    [JsonPropertyName("workingDirectory")]
    public string WorkingDirectory { get; init; } = "";

    // JSON property name stays "claudeArgs" for back-compat with existing
    // shortcut data; the C# property is the generic "Args".
    [JsonPropertyName("claudeArgs")]
    public string Args { get; init; } = "--dangerously-skip-permissions --continue";

    /// <summary>Windows Terminal profile name, e.g. "Command Prompt". Empty = wt default.</summary>
    [JsonPropertyName("wtProfile")]
    public string WtProfile { get; init; } = "Command Prompt";

    [JsonPropertyName("launcherMode")]
    public LauncherMode LauncherMode { get; init; } = LauncherMode.WindowsTerminal;

    /// <summary>
    /// For WindowsTerminal launcher only — whether to reuse an existing WT
    /// window (new tab) or open a brand-new window. Defaults to NewWindow for
    /// back-compat with shortcuts saved before this field existed.
    /// </summary>
    [JsonPropertyName("wtWindowTarget")]
    public WtWindowTarget WtWindowTarget { get; init; } = WtWindowTarget.NewWindow;

    /// <summary>
    /// Optional named window / "tab group" for WT. Only consulted when
    /// <see cref="WtWindowTarget"/> is ExistingWindow. Empty = use wt's
    /// most-recent window (<c>-w 0</c>). Non-empty = <c>-w &lt;name&gt;</c>,
    /// which routes the tab into a specific named window (creating it if it
    /// doesn't exist yet) so related shortcuts cluster together.
    /// </summary>
    [JsonPropertyName("wtWindowName")]
    public string WtWindowName { get; init; } = "";

    /// <summary>
    /// Optional custom title for the launched tab/window. Empty string means
    /// "don't rename" — the terminal keeps its default title. Passed to
    /// <c>wt.exe</c> as <c>--title</c>, or prepended as <c>title ...</c> on
    /// the cmd fallback.
    /// </summary>
    [JsonPropertyName("windowTitle")]
    public string WindowTitle { get; init; } = "";

    /// <summary>
    /// Optional keystroke string dispatched via <c>SendKeys.SendWait</c> after
    /// launch. Use this for apps that reset the tab title on startup (e.g.
    /// claude) — you can send the keybinding that re-applies your title.
    /// Empty = do nothing. Syntax matches <see cref="SendKeys"/>: {ENTER},
    /// {TAB}, ^+p for Ctrl+Shift+P, etc.
    /// </summary>
    [JsonPropertyName("postLaunchSendKeys")]
    public string PostLaunchSendKeys { get; init; } = "";

    /// <summary>
    /// Delay in milliseconds before dispatching
    /// <see cref="PostLaunchSendKeys"/>. Gives the terminal + app time to
    /// finish starting so the keystrokes land in the right window.
    /// </summary>
    [JsonPropertyName("postLaunchDelayMs")]
    public int PostLaunchDelayMs { get; init; } = 3000;

    [JsonPropertyName("requireAdmin")]
    public bool RequireAdmin { get; init; }

    /// <summary>
    /// When true, the plugin registers a Windows Explorer right-click verb
    /// scoped to <see cref="WorkingDirectory"/>: right-clicking inside that
    /// folder shows a "Run …" menu item that launches this shortcut. Off
    /// by default — opt-in per shortcut so the shell stays clean.
    /// </summary>
    [JsonPropertyName("showInExplorerContextMenu")]
    public bool ShowInExplorerContextMenu { get; init; }

    /// <summary>
    /// When true, the plugin maintains a <c>.lnk</c> on the user's Desktop
    /// that launches this shortcut (same as clicking Launch in the
    /// Shortcuts window). The .lnk is created on save, refreshed on rename,
    /// and deleted when this flag is turned off or the shortcut is deleted.
    /// </summary>
    [JsonPropertyName("addToDesktop")]
    public bool AddToDesktop { get; init; }

    /// <summary>
    /// Filename (without extension) used for the desktop .lnk when
    /// <see cref="AddToDesktop"/> is on. Set by the edit form — defaults
    /// to "&lt;directory-name&gt; &lt;profile-display-name&gt;" the first
    /// time the user picks a directory / profile, but the user can override.
    /// Mandatory when <see cref="AddToDesktop"/> is on.
    /// </summary>
    [JsonPropertyName("desktopShortcutName")]
    public string DesktopShortcutName { get; init; } = "";

    /// <summary>
    /// Optional URL the Group Launcher pings to surface a live/down health
    /// indicator per row. Typical use: an HTTP server the launched command
    /// exposes (e.g. <c>http://localhost:5000</c> for <c>dotnet run</c> on an
    /// ASP.NET project). Empty = no probe.
    /// </summary>
    [JsonPropertyName("statusUrl")]
    public string StatusUrl { get; init; } = "";

    /// <summary>
    /// Home/landing URL the auto-login flow opens first, reusing the cached
    /// cookies from the per-shortcut browser profile. If it loads without being
    /// redirected to <see cref="LoginUrl"/> (and no login form appears), the
    /// session is still valid and no sign-in is performed. Empty = fall back to
    /// <see cref="StatusUrl"/>.
    /// </summary>
    [JsonPropertyName("homeUrl")]
    public string HomeUrl { get; init; } = "";

    /// <summary>
    /// Sign-in page URL. When opening <see cref="HomeUrl"/> redirects here (or a
    /// login form is detected), the auto-login fills the credentials here, submits,
    /// and waits to be returned to the home page — caching cookies for next time.
    /// Empty = detect/fill the login form on whatever page loads after Home URL.
    /// </summary>
    [JsonPropertyName("loginUrl")]
    public string LoginUrl { get; init; } = "";

    /// <summary>
    /// When true, the launcher kicks off a Playwright session that opens
    /// <see cref="HomeUrl"/> (cached cookies first) and, if needed, signs in at
    /// <see cref="LoginUrl"/> with <see cref="LoginUsername"/> +
    /// <see cref="LoginPasswordEncrypted"/> so the browser is ready for the user.
    /// Requires a home target (HomeUrl or StatusUrl) to be set.
    /// </summary>
    [JsonPropertyName("autoLoginEnabled")]
    public bool AutoLoginEnabled { get; init; }

    [JsonPropertyName("loginUsername")]
    public string LoginUsername { get; init; } = "";

    /// <summary>
    /// Base64 of the DPAPI-protected password bytes (CurrentUser scope).
    /// Decryptable only by the same Windows user on the same machine —
    /// safe to ship alongside the per-envId data folder because that's
    /// already machine-local.
    /// </summary>
    [JsonPropertyName("loginPasswordEncrypted")]
    public string LoginPasswordEncrypted { get; init; } = "";

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
