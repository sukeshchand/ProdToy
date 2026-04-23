namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// A "kind" of shortcut — the binary to invoke, its default args, and the
/// hint text shown in the edit form. Lets the feature be generic instead of
/// tightly coupled to <c>claude</c>.
/// </summary>
sealed record LaunchProfile
{
    public required string Id { get; init; }
    public required string DisplayName { get; init; }

    /// <summary>
    /// Binary invoked by the launcher (e.g. "claude", "npm", "npx", "dotnet").
    /// Empty ⇒ Custom profile: the args string IS the full command.
    /// </summary>
    public required string Command { get; init; }

    public required string DefaultArgs { get; init; }
    public required string ArgsHint { get; init; }

    /// <summary>
    /// Short arg snippets surfaced as clickable chips below the args textbox.
    /// Clicking a chip appends the snippet to the textbox. Empty array ⇒ no chips.
    /// </summary>
    public string[] SuggestedTokens { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, a trailing <c>--continue</c>/<c>-c</c> in the args triggers
    /// the "(X --continue || X)" retry wrapper so Claude doesn't leave the
    /// user in a dead terminal on a fresh project. Claude-specific.
    /// </summary>
    public bool SupportsContinueFallback { get; init; }
}

static class LaunchProfiles
{
    public static readonly LaunchProfile[] All = new[]
    {
        new LaunchProfile
        {
            Id = "claude",
            DisplayName = "Claude CLI",
            Command = "claude",
            DefaultArgs = "",
            ArgsHint = "Appended to `claude`. Click a chip below to add common flags.",
            SuggestedTokens = new[]
            {
                "--dangerously-skip-permissions",
                "--continue",
                "-p",
                "--model sonnet",
                "--model opus",
                "--model haiku",
            },
            SupportsContinueFallback = true,
        },
        new LaunchProfile
        {
            Id = "npm",
            DisplayName = "npm",
            Command = "npm",
            DefaultArgs = "",
            ArgsHint = "Appended to `npm`. Click a chip below to add a common subcommand.",
            SuggestedTokens = new[] { "install", "run dev", "run build", "run start", "test", "start" },
        },
        new LaunchProfile
        {
            Id = "vite",
            DisplayName = "Vite (via npx)",
            Command = "npx",
            DefaultArgs = "",
            ArgsHint = "Appended to `npx`. Click a chip below to add a common invocation.",
            SuggestedTokens = new[] { "vite", "vite build", "vite preview" },
        },
        new LaunchProfile
        {
            Id = "dotnet",
            DisplayName = "dotnet",
            Command = "dotnet",
            DefaultArgs = "",
            ArgsHint = "Appended to `dotnet`. Click a chip below to add a common subcommand.",
            SuggestedTokens = new[] { "run", "build", "test", "watch run", "restore", "publish" },
        },
        new LaunchProfile
        {
            Id = "pwsh",
            DisplayName = "PowerShell",
            Command = "pwsh",
            DefaultArgs = "",
            ArgsHint = "Appended to `pwsh`. Click a chip below to add a common flag.",
            SuggestedTokens = new[] { "-NoExit", "-Command", "-File", "-ExecutionPolicy Bypass" },
        },
        new LaunchProfile
        {
            Id = "custom",
            DisplayName = "Custom command",
            Command = "",
            DefaultArgs = "",
            ArgsHint = "Full command line (no fixed prefix) — e.g. `python -m http.server 8080`",
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
