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
            DefaultArgs = "--dangerously-skip-permissions --continue",
            ArgsHint = "Appended to `claude` — e.g. --continue, --model, -p, --dangerously-skip-permissions",
            SupportsContinueFallback = true,
        },
        new LaunchProfile
        {
            Id = "npm",
            DisplayName = "npm",
            Command = "npm",
            DefaultArgs = "run dev",
            ArgsHint = "Appended to `npm` — e.g. install, run dev, run build, test, start",
        },
        new LaunchProfile
        {
            Id = "vite",
            DisplayName = "Vite (via npx)",
            Command = "npx",
            DefaultArgs = "vite",
            ArgsHint = "Appended to `npx` — e.g. vite, vite build, vite preview",
        },
        new LaunchProfile
        {
            Id = "dotnet",
            DisplayName = "dotnet",
            Command = "dotnet",
            DefaultArgs = "run",
            ArgsHint = "Appended to `dotnet` — e.g. run, build, test, watch run",
        },
        new LaunchProfile
        {
            Id = "pwsh",
            DisplayName = "PowerShell",
            Command = "pwsh",
            DefaultArgs = "",
            ArgsHint = "Appended to `pwsh` — e.g. -NoExit -Command ..., -File script.ps1",
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
