namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// How a profile launches:
///   <see cref="Terminal"/> → run <see cref="LaunchProfile.Command"/> + args in a
///   terminal / captured process (the historical behavior).
///   <see cref="Url"/> → no command at all; the shortcut just opens a URL (the
///   <see cref="Shortcut.Args"/> field holds the URL) in an in-app preview.
/// </summary>
enum LaunchKind
{
    Terminal,
    Url,
    /// <summary>ShellExecute a file/path (no terminal). Used for opening a Visual
    /// Studio solution (.sln) via the VS Version Selector, or any file with its
    /// default handler. The <see cref="Shortcut.Args"/> field holds the path.</summary>
    Open,
}

/// <summary>
/// A "kind" of shortcut — the binary to invoke, its default args, and the
/// hint text shown in the edit form. Lets the feature be generic instead of
/// tightly coupled to <c>claude</c>.
/// </summary>
sealed record LaunchProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>Terminal command (default) vs a URL-opening shortcut.</summary>
    public LaunchKind Kind { get; init; } = LaunchKind.Terminal;

    /// <summary>
    /// Binary invoked by the launcher (e.g. "claude", "npm", "npx", "dotnet").
    /// Empty ⇒ Custom profile: the args string IS the full command.
    /// </summary>
    public required string Command { get; init; }

    public required string DefaultArgs { get; init; }
    public required string ArgsHint { get; init; }

    /// <summary>
    /// Short arg snippets surfaced as clickable chips below the args textbox.
    /// Clicking a chip toggles the snippet in the textbox. Empty array ⇒ no chips.
    /// The plain profiles give only a token (empty description); Claude's chips
    /// come from <see cref="ClaudeCliCatalog"/>.
    /// </summary>
    public CliOption[] SuggestedTokens { get; init; } = Array.Empty<CliOption>();

    /// <summary>The chip row ends with a ⋯ chip that opens the menu of every
    /// option in <see cref="ClaudeCliCatalog"/>. Claude-specific.</summary>
    public bool HasOptionsMenu { get; init; }

    /// <summary>
    /// When true, a trailing <c>--continue</c>/<c>-c</c> in the args triggers
    /// the "(X --continue || X)" retry wrapper so Claude doesn't leave the
    /// user in a dead terminal on a fresh project. Claude-specific.
    /// </summary>
    public bool SupportsContinueFallback { get; init; }
}

static class LaunchProfiles
{
    /// <summary>Args of a NEW Claude shortcut. Existing shortcuts keep theirs.</summary>
    public const string ClaudeDefaultArgs = "--permission-mode auto --continue";

    private static CliOption[] Plain(params string[] tokens) => tokens.Select(t => new CliOption(t)).ToArray();

    public static readonly LaunchProfile[] All = new[]
    {
        new LaunchProfile
        {
            Id = "claude",
            DisplayName = "Claude CLI",
            Command = "claude",
            DefaultArgs = ClaudeDefaultArgs,
            ArgsHint = "Appended to `claude`. Click a chip to toggle a flag; ⋯ (or right-click) lists every option.",
            SuggestedTokens = ClaudeCliCatalog.Chips,
            HasOptionsMenu = true,
            SupportsContinueFallback = true,
        },
        new LaunchProfile
        {
            Id = "npm",
            DisplayName = "npm",
            Command = "npm",
            DefaultArgs = "",
            ArgsHint = "Appended to `npm`. Click a chip below to add a common subcommand.",
            SuggestedTokens = Plain("install", "run dev", "run build", "run start", "test", "start"),
        },
        new LaunchProfile
        {
            Id = "vite",
            DisplayName = "Vite (via npx)",
            Command = "npx",
            DefaultArgs = "",
            ArgsHint = "Appended to `npx`. Click a chip below to add a common invocation.",
            SuggestedTokens = Plain("vite", "vite build", "vite preview"),
        },
        new LaunchProfile
        {
            Id = "dotnet",
            DisplayName = "dotnet",
            Command = "dotnet",
            DefaultArgs = "",
            ArgsHint = "Appended to `dotnet`. Click a chip below to add a common subcommand.",
            SuggestedTokens = Plain("run", "build", "test", "watch run", "restore", "publish"),
        },
        new LaunchProfile
        {
            Id = "pwsh",
            DisplayName = "PowerShell",
            Command = "pwsh",
            DefaultArgs = "",
            ArgsHint = "Appended to `pwsh`. Click a chip below to add a common flag.",
            SuggestedTokens = Plain("-NoExit", "-Command", "-File", "-ExecutionPolicy Bypass"),
        },
        new LaunchProfile
        {
            Id = "custom",
            DisplayName = "Custom command",
            Command = "",
            DefaultArgs = "",
            ArgsHint = "Full command line (no fixed prefix) — e.g. `python -m http.server 8080`",
        },
        new LaunchProfile
        {
            Id = "url",
            DisplayName = "Open URL (browser)",
            Command = "",
            Kind = LaunchKind.Url,
            DefaultArgs = "",
            ArgsHint = "The URL to open in the in-app preview — e.g. https://localhost:5001",
        },
        new LaunchProfile
        {
            Id = "vssln",
            DisplayName = "Visual Studio solution",
            Command = "",
            Kind = LaunchKind.Open,
            DefaultArgs = "",
            ArgsHint = @"Path to a .sln file — opens in Visual Studio (the VS Version Selector picks the version). e.g. C:\source\App\App.sln",
        },
    };

    public static LaunchProfile Default => All[0];

    public static LaunchProfile GetOrDefault(string? id)
    {
        if (string.IsNullOrEmpty(id)) return Default;
        foreach (var p in All)
            if (string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase))
                return p;
        return Default;
    }
}
