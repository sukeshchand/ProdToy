using System.Text.Json.Nodes;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// MCP tool registrations for the Shortcuts plugin — the plugin-owned
/// descriptors (names, schemas, help text) that the host aggregates into the
/// MCP server's tools/list and the mcp-info guide. Handlers delegate to
/// <see cref="LauncherRpc"/> (Consolidated Launcher verbs) and
/// <see cref="ShortcutRpc"/> (saved-shortcut CRUD/launch).
/// </summary>
static class ShortcutMcpTools
{
    private const string LauncherSection = "Consolidated Launcher";
    private const string ShortcutSection = "Shortcuts";

    private static readonly (string Name, string Type, string Desc, bool Required) FolderArg =
        ("folder", "string",
         "Shortcut folder to target (e.g. \"Work/Backend\" or just the leaf \"Backend\"). " +
         "Optional when only one folder exists or one launcher window is open.", false);

    private static readonly (string Name, string Type, string Desc, bool Required) NameArg =
        ("name", "string", "The shortcut's name (or id).", true);

    public static List<IDisposable> RegisterAll(IPluginContext ctx)
    {
        var host = ctx.Host;
        var regs = new List<IDisposable>
        {
            // ── Consolidated Launcher: folder-wide ──
            host.RegisterMcpTool(new McpTool("launcher_restart_all",
                "Stop all processes in a ProdToy Consolidated Launcher folder, wait for them to exit, then launch " +
                "them all again. Use this after making code changes to restart the apps under development.",
                Schema(FolderArg), LauncherSection,
                "Launching is asynchronous — poll launcher_status until every shortcut reports \"Running\" " +
                "(and healthy urlHealth) before testing. Shortcuts marked 'Keep running on Stop All' are not stopped."),
                cmd => LauncherRpc.HandleAsync(ctx, "restart-all", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_launch_all",
                "Launch every shortcut in a ProdToy Consolidated Launcher folder. Does not stop already-running " +
                "processes first — prefer launcher_restart_all after code changes.",
                Schema(FolderArg), LauncherSection,
                "Launching is asynchronous — poll launcher_status for progress."),
                cmd => LauncherRpc.HandleAsync(ctx, "launch-all", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_stop_all",
                "Stop all running processes in a ProdToy Consolidated Launcher folder. Shortcuts marked " +
                "'Keep running on Stop All' are left running.",
                Schema(FolderArg), LauncherSection),
                cmd => LauncherRpc.HandleAsync(ctx, "stop-all", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_status",
                "Live status of a ProdToy Consolidated Launcher folder: per-shortcut state " +
                "(Running/Stopped/Building/Exited/Failed), pid, memory, uptime, and Status-URL health.",
                Schema(FolderArg), LauncherSection,
                "Reports live process state only while the launcher window is open (it never force-opens one)."),
                cmd => LauncherRpc.HandleAsync(ctx, "status", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_folders",
                "List the ProdToy shortcut folders that can be controlled (name + shortcut count + whether a " +
                "launcher window is open). Use when a folder name is needed for the other launcher tools.",
                null, LauncherSection),
                cmd => LauncherRpc.HandleAsync(ctx, "folders", cmd)),

            // ── Consolidated Launcher: single row ──
            host.RegisterMcpTool(new McpTool("launcher_launch_one",
                "Launch a single shortcut in the Consolidated Launcher (a unique shortcut name needs no folder).",
                Schema(NameArg, FolderArg), LauncherSection),
                cmd => LauncherRpc.HandleAsync(ctx, "launch-one", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_stop_one",
                "Stop a single shortcut's process in the Consolidated Launcher (works even for 'Keep running on " +
                "Stop All' shortcuts — this is the explicit per-row stop).",
                Schema(NameArg, FolderArg), LauncherSection),
                cmd => LauncherRpc.HandleAsync(ctx, "stop-one", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_restart_one",
                "Stop then relaunch a single shortcut in the Consolidated Launcher. Use after editing only that " +
                "app's code.",
                Schema(NameArg, FolderArg), LauncherSection,
                "Asynchronous — poll launcher_status."),
                cmd => LauncherRpc.HandleAsync(ctx, "restart-one", cmd)),

            host.RegisterMcpTool(new McpTool("launcher_logs",
                "Read the tail of a shortcut's console output (stdout/stderr) from the current Consolidated " +
                "Launcher session — build errors, runtime exceptions, request logs.",
                Schema(NameArg,
                    ("lines", "integer", "How many trailing lines to return (default 100, max 2000).", false),
                    FolderArg), LauncherSection,
                "stderr lines are prefixed with \"[err] \". Logs exist only while the launcher window is open " +
                "and only for shortcuts that produced output this session."),
                cmd => LauncherRpc.HandleAsync(ctx, "logs", cmd)),

            // ── Saved shortcuts (any profile) ──
            host.RegisterMcpTool(new McpTool("shortcut_list",
                "List the user's saved ProdToy shortcuts (all profiles — terminal commands, URLs, Visual Studio " +
                "solutions) with folder, profile, command, working directory, URLs, and flags.",
                Schema(("folder", "string", "Only list this folder (full path or leaf name).", false)),
                ShortcutSection),
                ShortcutRpc.ListAsync),

            host.RegisterMcpTool(new McpTool("shortcut_launch",
                "Launch any saved shortcut exactly as its Launch button would: terminal profiles open Windows " +
                "Terminal/cmd, URL profiles open the browser, solution profiles open Visual Studio.",
                Schema(NameArg, ("folder", "string", "Disambiguate when the name exists in several folders.", false)),
                ShortcutSection),
                ShortcutRpc.LaunchAsync),

            host.RegisterMcpTool(new McpTool("shortcut_create",
                "Create a new saved shortcut.",
                Schema(NameArg,
                    ("profile", "string", "Launch profile id (default \"custom\"). Common: claude, custom, vssln, url.", false),
                    ("args", "string", "Profile arguments: the command line (custom), CLI args (claude; default \"--permission-mode auto --continue\"), URL (url), or .sln path (vssln).", false),
                    ("workingDirectory", "string", "Required for terminal profiles; the directory to run in.", false),
                    ("folder", "string", "Folder path to file it under (created if new). Default: root.", false)),
                ShortcutSection,
                "Fails when a shortcut with the same name already exists in the target folder."),
                ShortcutRpc.CreateAsync),

            host.RegisterMcpTool(new McpTool("shortcut_delete",
                "Delete a saved shortcut by name (or id). Permanent — confirm with the user before deleting " +
                "anything you did not create yourself.",
                Schema(NameArg, ("folder", "string", "Disambiguate when the name exists in several folders.", false)),
                ShortcutSection),
                ShortcutRpc.DeleteAsync),
        };
        return regs;
    }

    private static string Schema(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            properties[p.Name] = new JsonObject { ["type"] = p.Type, ["description"] = p.Desc };
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        }.ToJsonString();
    }
}
