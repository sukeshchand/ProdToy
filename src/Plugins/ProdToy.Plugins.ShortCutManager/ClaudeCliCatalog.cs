using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// The Claude CLI options offered in the shortcut editor: a built-in list (the
/// fallback when <c>claude</c> is not installed), merged with the options read from
/// the installed CLI's <c>--help</c> by <see cref="Refresh"/> and cached in
/// <c>claude-options.json</c> in the plugin data directory.
/// </summary>
static class ClaudeCliCatalog
{
    public const string CacheFileName = "claude-options.json";

    public static readonly string[] Groups = { "Permissions", "Session", "Model", "Tools", "Output", "Other" };

    private static CliOption Opt(string group, string token, string desc,
        CliValueKind kind = CliValueKind.None, string[]? choices = null, string[]? aliases = null,
        string hint = "", bool variadic = false, string[]? excludes = null) =>
        new(token, desc, kind, choices)
        {
            Group = group,
            Aliases = aliases ?? Array.Empty<string>(),
            ValueHint = hint,
            Variadic = variadic,
            Excludes = excludes ?? Array.Empty<string>(),
        };

    /// <summary>The template chip / menu item that renames the session tab.</summary>
    public static readonly CliOption Rename = new("/rename <name>",
        "Runs /rename with the shortcut's name when the session starts, so the terminal tab keeps that name.")
    { Group = "Other" };

    public static readonly IReadOnlyList<CliOption> BuiltIn = new[]
    {
        // Permissions
        Opt("Permissions", "--dangerously-skip-permissions",
            "Bypass all permission checks. Recommended only for sandboxes with no internet access.",
            excludes: new[] { "--permission-mode" }),
        Opt("Permissions", "--allow-dangerously-skip-permissions",
            "Enable bypassing all permission checks as an option, without it being enabled by default."),
        Opt("Permissions", "--permission-mode", "Permission mode to use for the session.",
            CliValueKind.Required, new[] { "acceptEdits", "auto", "bypassPermissions", "manual", "dontAsk", "plan" },
            hint: "<mode>", excludes: new[] { "--dangerously-skip-permissions" }),
        Opt("Permissions", "--restricted",
            "Restricted mode: removes the built-in tools that run commands or code and ignores user, project and local settings files."),
        Opt("Permissions", "--safe-mode",
            "Start with all customizations (CLAUDE.md, skills, plugins, hooks, MCP servers, ...) disabled - useful for troubleshooting."),
        Opt("Permissions", "--bare",
            "Minimal mode: skip hooks, LSP, plugin sync, auto-memory and CLAUDE.md auto-discovery."),

        // Session
        Opt("Session", "--continue", "Continue the most recent conversation in the current directory.",
            aliases: new[] { "-c" }),
        Opt("Session", "--resume", "Resume a conversation by session ID or name, or open the interactive picker.",
            CliValueKind.Optional, aliases: new[] { "-r" }, hint: "[value]"),
        Opt("Session", "--fork-session",
            "When resuming, create a new session ID instead of reusing the original (use with --resume or --continue)."),
        Opt("Session", "--name", "Set a display name for this session (prompt box, /resume picker and terminal title).",
            CliValueKind.Required, aliases: new[] { "-n" }, hint: "<name>"),
        Opt("Session", "--worktree", "Create a new git worktree for this session (optionally named).",
            CliValueKind.Optional, aliases: new[] { "-w" }, hint: "[name]"),
        Opt("Session", "--bg", "Start the session in the background and return immediately.",
            aliases: new[] { "--background" }),
        Opt("Session", "--remote-control", "Start an interactive session with Remote Control enabled (optionally named).",
            CliValueKind.Optional, hint: "[name]"),
        Opt("Session", "--desktop", "Open in the Claude Desktop app instead of the terminal."),
        Opt("Session", "--session-id", "Use a specific session ID for the conversation (must be a valid UUID).",
            CliValueKind.Required, hint: "<uuid>"),

        // Model
        Opt("Model", "--model", "Model for the current session: an alias for the latest model or a model's full name.",
            CliValueKind.Required, new[] { "fable", "opus", "sonnet", "haiku" }, hint: "<model>"),
        Opt("Model", "--effort", "Effort level for the current session.",
            CliValueKind.Required, new[] { "low", "medium", "high", "xhigh", "max" }, hint: "<level>"),
        Opt("Model", "--fallback-model", "Automatic fallback to the given model(s) when the default model is overloaded.",
            CliValueKind.Required, hint: "<model>"),
        Opt("Model", "--autocompact", "Auto-compact window size (auto, or a token count).",
            CliValueKind.Required, hint: "<auto|tokens>"),

        // Tools
        Opt("Tools", "--chrome", "Enable Claude in Chrome integration.", excludes: new[] { "--no-chrome" }),
        Opt("Tools", "--no-chrome", "Disable Claude in Chrome integration.", excludes: new[] { "--chrome" }),
        Opt("Tools", "--ide", "Automatically connect to the IDE on startup if exactly one valid IDE is available."),
        Opt("Tools", "--add-dir", "Additional directories to allow tool access to.",
            CliValueKind.Required, hint: "<directories...>", variadic: true),
        Opt("Tools", "--allowed-tools", "Comma or space-separated list of tool names to allow.",
            CliValueKind.Required, aliases: new[] { "--allowedTools" }, hint: "<tools...>", variadic: true),
        Opt("Tools", "--disallowed-tools", "Comma or space-separated list of tool names to deny.",
            CliValueKind.Required, aliases: new[] { "--disallowedTools" }, hint: "<tools...>", variadic: true),
        Opt("Tools", "--tools", "The list of available tools from the built-in set.",
            CliValueKind.Required, hint: "<tools...>", variadic: true),
        Opt("Tools", "--mcp-config", "Load MCP servers from JSON files or strings (space-separated).",
            CliValueKind.Required, hint: "<configs...>", variadic: true),
        Opt("Tools", "--strict-mcp-config", "Only use MCP servers from --mcp-config, ignoring all other MCP configurations."),
        Opt("Tools", "--plugin-dir", "Load a plugin from a directory or .zip for this session only.",
            CliValueKind.Required, hint: "<path>"),

        // Output
        Opt("Output", "-p", "Print the response and exit (useful for pipes).", aliases: new[] { "--print" }),
        Opt("Output", "--output-format", "Output format (only works with --print).",
            CliValueKind.Required, new[] { "text", "json", "stream-json" }, hint: "<format>"),
        Opt("Output", "--verbose", "Override the verbose mode setting from config."),
        Opt("Output", "--debug", "Enable debug mode with optional category filtering (e.g. hooks,mcp).",
            CliValueKind.Optional, aliases: new[] { "-d" }, hint: "[filter]"),
        Opt("Output", "--brief", "Enable the SendUserMessage tool for agent-to-user communication."),

        // Other
        Opt("Other", "--append-system-prompt", "Append a system prompt to the default system prompt.",
            CliValueKind.Required, hint: "<prompt>"),
        Opt("Other", "--system-prompt", "System prompt to use for the session.",
            CliValueKind.Required, hint: "<prompt>"),
        Opt("Other", "--settings", "Path to a settings JSON file or a JSON string to load additional settings from.",
            CliValueKind.Required, hint: "<file-or-json>"),
        Opt("Other", "--agent", "Agent for the current session. Overrides the 'agent' setting.",
            CliValueKind.Required, hint: "<agent>"),
        Rename,
    };

    // ------------------------------------------------------------------ chips

    private static CliOption Chip(string token, string? value = null)
    {
        var o = BuiltIn.First(x => x.Matches(token));
        return value == null ? o : o with { ChipValue = value };
    }

    /// <summary>The chip row under the args box of a Claude shortcut.</summary>
    public static readonly CliOption[] Chips =
    {
        Chip("--permission-mode", "auto"),
        Chip("--continue"),
        Chip("--resume"),
        Chip("-p"),
        Chip("--model", "fable"),
        Chip("--model", "opus"),
        Chip("--model", "sonnet"),
        Rename,
    };

    // ------------------------------------------------------------------ merge

    /// <summary>Built-in options updated with what the CLI's help says (text, value
    /// kind, choices when it lists any); options the built-in list lacks are added
    /// to "Other". Built-in token, group and exclusions are kept.</summary>
    public static List<CliOption> Merge(IReadOnlyList<CliOption> builtIn, IReadOnlyList<CliOption> parsed)
    {
        var result = new List<CliOption>();
        var used = new HashSet<CliOption>(ReferenceEqualityComparer.Instance);
        foreach (var b in builtIn)
        {
            var p = parsed.FirstOrDefault(x => !used.Contains(x) && x.Names.Any(b.Matches));
            if (p == null) { result.Add(b); continue; }
            used.Add(p);
            result.Add(b with
            {
                Description = p.Description.Length > 0 ? p.Description : b.Description,
                ValueKind = p.ValueKind,
                Choices = p.ChoiceList.Length > 0 ? p.Choices : b.Choices,
                Aliases = b.Aliases.Union(p.Names.Where(n => n != b.Token)).ToArray(),
                Variadic = p.Variadic,
                ValueHint = p.ValueHint.Length > 0 ? p.ValueHint : b.ValueHint,
            });
        }
        foreach (var p in parsed)
            if (!used.Contains(p) && !result.Any(r => p.Names.Any(r.Matches)))
                result.Add(p with { Group = "Other" });
        return result;
    }

    // ------------------------------------------------------------------ cache + state

    private static string _cacheFile = "";
    private static readonly object _lock = new();
    private static List<CliOption>? _current;

    /// <summary>The CLI version the catalog was read from; null = the built-in list.</summary>
    public static string? CliVersion { get; private set; }
    public static DateTime? RefreshedAt { get; private set; }

    public static IReadOnlyList<CliOption> Current
    {
        get { lock (_lock) return _current ?? (IReadOnlyList<CliOption>)BuiltIn; }
    }

    public static string CacheFile => _cacheFile;

    public static void Initialize(string dataDirectory)
    {
        lock (_lock)
        {
            _cacheFile = Path.Combine(dataDirectory, CacheFileName);
            _current = null;
            CliVersion = null;
            RefreshedAt = null;
            var cache = LoadCache(_cacheFile);
            if (cache != null && cache.Options.Count > 0)
            {
                _current = Merge(BuiltIn, cache.Options.Select(c => c.ToOption()).ToList());
                CliVersion = cache.CliVersion;
                RefreshedAt = cache.RefreshedAt;
            }
        }
    }

    /// <summary>Re-reads the installed CLI's help. <paramref name="runClaude"/> runs
    /// <c>claude &lt;args&gt;</c> and returns its output, or null when it cannot run
    /// (the default starts the real CLI). On failure the catalog stays as it was.</summary>
    public static RefreshResult Refresh(Func<string, string?>? runClaude = null)
    {
        runClaude ??= RunClaude;
        string? help = runClaude("--help");
        var parsed = ClaudeHelpParser.Parse(help);
        if (parsed.Count == 0)
            return new RefreshResult(false, help == null
                ? "Could not run 'claude --help' - is the Claude CLI installed and on PATH? The built-in list stays in use."
                : "'claude --help' printed no options block - the catalog was not changed.", 0);

        string? version = ClaudeHelpParser.ParseVersion(runClaude("--version"));
        var before = Current;
        var merged = Merge(BuiltIn, parsed);
        int added = merged.Count(m => !before.Any(b => m.Names.Any(b.Matches)));
        lock (_lock)
        {
            _current = merged;
            CliVersion = version ?? "unknown";
            RefreshedAt = DateTime.Now;
            if (_cacheFile.Length > 0)
            {
                try
                {
                    var dto = new CacheDto
                    {
                        CliVersion = CliVersion,
                        RefreshedAt = RefreshedAt,
                        Options = parsed.Select(CachedOption.From).ToList(),
                    };
                    Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile)!);
                    File.WriteAllText(_cacheFile, JsonSerializer.Serialize(dto, _json));
                }
                catch (Exception ex) { PluginLog.Error("Could not save the Claude options cache", ex); }
            }
        }
        return new RefreshResult(true,
            $"Read {parsed.Count} options from Claude CLI {CliVersion}" + (added > 0 ? $" - {added} new." : "."), added);
    }

    public sealed record RefreshResult(bool Ok, string Message, int Added);

    private static string? RunClaude(string args)
    {
        try
        {
            // Through cmd so a claude.cmd / claude.exe on PATH both resolve.
            var psi = new ProcessStartInfo("cmd.exe", "/d /c claude " + args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            psi.Environment["NO_COLOR"] = "1";
            using var p = Process.Start(psi);
            if (p == null) return null;
            var outTask = p.StandardOutput.ReadToEndAsync();
            var errTask = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000))
            {
                try { p.Kill(true); } catch { }
                return null;
            }
            string output = outTask.Result;
            return p.ExitCode == 0 && output.Length > 0 ? output : null;
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"claude {args} failed: {ex.Message}");
            return null;
        }
    }

    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    internal static CacheDto? LoadCache(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            return JsonSerializer.Deserialize<CacheDto>(File.ReadAllText(file), _json);
        }
        catch (Exception ex)
        {
            PluginLog.Warn($"Claude options cache unreadable, using the built-in list: {ex.Message}");
            return null;
        }
    }

    internal sealed class CacheDto
    {
        [JsonPropertyName("cliVersion")] public string? CliVersion { get; set; }
        [JsonPropertyName("refreshedAt")] public DateTime? RefreshedAt { get; set; }
        [JsonPropertyName("options")] public List<CachedOption> Options { get; set; } = new();
    }

    internal sealed class CachedOption
    {
        [JsonPropertyName("token")] public string Token { get; set; } = "";
        [JsonPropertyName("aliases")] public string[] Aliases { get; set; } = Array.Empty<string>();
        [JsonPropertyName("description")] public string Description { get; set; } = "";
        [JsonPropertyName("valueKind")] public CliValueKind ValueKind { get; set; }
        [JsonPropertyName("valueHint")] public string ValueHint { get; set; } = "";
        [JsonPropertyName("variadic")] public bool Variadic { get; set; }
        [JsonPropertyName("choices")] public string[]? Choices { get; set; }

        public static CachedOption From(CliOption o) => new()
        {
            Token = o.Token, Aliases = o.Aliases, Description = o.Description, ValueKind = o.ValueKind,
            ValueHint = o.ValueHint, Variadic = o.Variadic, Choices = o.Choices,
        };

        public CliOption ToOption() => new(Token, Description ?? "", ValueKind, Choices)
        {
            Aliases = Aliases ?? Array.Empty<string>(),
            ValueHint = ValueHint ?? "",
            Variadic = Variadic,
        };
    }
}
