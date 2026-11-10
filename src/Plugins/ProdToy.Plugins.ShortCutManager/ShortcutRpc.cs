using System.Text.Json;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Handlers for the shortcut_* MCP tools: list / launch / create / delete saved
/// shortcuts of ANY profile (terminal commands, URLs, Visual Studio solutions).
/// Distinct from the launcher_* verbs in <see cref="LauncherRpc"/>, which drive
/// the Consolidated Launcher window. All handlers run on the UI thread and
/// return single-line JSON with at least {"ok":bool,...}.
/// </summary>
static class ShortcutRpc
{
    public static Task<string> ListAsync(PipeCommand cmd)
    {
        string? folder = LauncherRpc.ParseString(cmd.PayloadJson, "folder");
        var all = ShortcutStore.Load();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            string norm = ShortcutFolders.Normalize(folder);
            var filtered = all.Where(s => string.Equals(
                ShortcutFolders.Normalize(s.FolderPath), norm, StringComparison.OrdinalIgnoreCase)).ToList();
            // Leaf-name convenience, same as the launcher verbs.
            if (filtered.Count == 0)
                filtered = all.Where(s =>
                {
                    var f = ShortcutFolders.Normalize(s.FolderPath);
                    int i = f.LastIndexOf('/');
                    return string.Equals(i < 0 ? f : f[(i + 1)..], norm, StringComparison.OrdinalIgnoreCase);
                }).ToList();
            all = filtered;
        }

        var items = all.OrderBy(s => ShortcutFolders.Normalize(s.FolderPath), StringComparer.OrdinalIgnoreCase)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s =>
            {
                var profile = LaunchProfiles.GetOrDefault(s.Profile);
                return new
                {
                    id = s.Id,
                    name = s.Name,
                    folder = ShortcutFolders.Normalize(s.FolderPath) is { Length: > 0 } f ? f : "(root)",
                    profile = s.Profile,
                    profileName = profile.DisplayName,
                    kind = profile.Kind.ToString(),
                    command = profile.Kind == LaunchKind.Terminal
                        ? ShortcutLauncher.BuildProfileCmdline(s)
                        : (s.Args ?? "").Trim(),
                    workingDirectory = string.IsNullOrWhiteSpace(s.WorkingDirectory) ? null : s.WorkingDirectory,
                    statusUrl = string.IsNullOrWhiteSpace(s.StatusUrl) ? null : s.StatusUrl,
                    homeUrl = string.IsNullOrWhiteSpace(s.HomeUrl) ? null : s.HomeUrl,
                    ignoredInConsolidated = s.ExcludeFromConsolidated,
                    keepRunningOnStopAll = s.ExcludeFromStopAll,
                    launchCount = s.LaunchCount,
                    lastLaunchedAt = s.LastLaunchedAt,
                };
            });
        return Task.FromResult(JsonSerializer.Serialize(new { ok = true, shortcuts = items }));
    }

    public static Task<string> LaunchAsync(PipeCommand cmd)
    {
        var rs = Resolve(cmd);
        if (rs.Error != null) return Task.FromResult(Fail(rs.Error));
        var s = rs.Shortcut!;

        var result = ShortcutLauncher.Launch(s);
        return Task.FromResult(result.Ok
            ? Ok($"Launched '{s.Name}' ({LaunchProfiles.GetOrDefault(s.Profile).DisplayName}).")
            : Fail($"Launch of '{s.Name}' failed: {result.ErrorMessage}"));
    }

    public static Task<string> CreateAsync(PipeCommand cmd)
    {
        string? name = LauncherRpc.ParseString(cmd.PayloadJson, "name");
        if (string.IsNullOrWhiteSpace(name)) return Task.FromResult(Fail("Missing 'name'."));
        string profileId = LauncherRpc.ParseString(cmd.PayloadJson, "profile") ?? "custom";
        if (!LaunchProfiles.All.Any(p => string.Equals(p.Id, profileId, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(Fail($"Unknown profile '{profileId}'. "
                + $"Available: {string.Join(", ", LaunchProfiles.All.Select(p => $"{p.Id} ({p.DisplayName})"))}."));
        var profile = LaunchProfiles.GetOrDefault(profileId);

        // No args given = the profile's default (a new Claude shortcut gets --permission-mode auto --continue).
        string args = LauncherRpc.ParseString(cmd.PayloadJson, "args") ?? profile.DefaultArgs;
        string workDir = LauncherRpc.ParseString(cmd.PayloadJson, "workingDirectory") ?? "";
        string folder = ShortcutFolders.Normalize(LauncherRpc.ParseString(cmd.PayloadJson, "folder"));

        // Same validation shape as the edit form, per profile kind.
        if (profile.Kind == LaunchKind.Terminal)
        {
            if (string.IsNullOrWhiteSpace(workDir))
                return Task.FromResult(Fail("Terminal shortcuts need 'workingDirectory'."));
            if (!Directory.Exists(workDir))
                return Task.FromResult(Fail($"Working directory doesn't exist: {workDir}"));
            if (profile.Command.Length == 0 && string.IsNullOrWhiteSpace(args))
                return Task.FromResult(Fail("The 'custom' profile needs 'args' (the full command line)."));
        }
        else if (string.IsNullOrWhiteSpace(args))
        {
            return Task.FromResult(Fail(profile.Kind == LaunchKind.Url
                ? "URL shortcuts need 'args' (the URL)."
                : "This profile needs 'args' (the file/solution path)."));
        }

        if (ShortcutStore.Load().Any(s => string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(ShortcutFolders.Normalize(s.FolderPath), folder, StringComparison.OrdinalIgnoreCase)))
            return Task.FromResult(Fail($"A shortcut named '{name}' already exists in that folder."));

        var shortcut = new Shortcut
        {
            Name = name!.Trim(),
            Profile = profile.Id,
            Args = args,
            WorkingDirectory = workDir,
            FolderPath = folder,
        };
        ShortcutStore.Add(shortcut);
        if (folder.Length > 0) ShortcutFolders.Add(folder);
        PluginLog.Info($"MCP shortcut_create: '{shortcut.Name}' ({profile.Id}) in '{folder}'");
        return Task.FromResult(JsonSerializer.Serialize(new
        {
            ok = true,
            id = shortcut.Id,
            message = $"Created shortcut '{shortcut.Name}' ({profile.DisplayName})"
                + (folder.Length > 0 ? $" in folder '{folder}'." : " in the root folder."),
        }));
    }

    public static Task<string> DeleteAsync(PipeCommand cmd)
    {
        var rs = Resolve(cmd);
        if (rs.Error != null) return Task.FromResult(Fail(rs.Error));
        var s = rs.Shortcut!;
        ShortcutStore.Delete(s.Id);
        PluginLog.Info($"MCP shortcut_delete: '{s.Name}' ({s.Id})");
        return Task.FromResult(Ok($"Deleted shortcut '{s.Name}'."));
    }

    /// <summary>Resolve payload {name, folder?} to one shortcut across ALL
    /// saved shortcuts (any profile — unlike LauncherRpc's consolidated-only view).</summary>
    private static (Shortcut? Shortcut, string? Error) Resolve(PipeCommand cmd)
    {
        string? name = LauncherRpc.ParseString(cmd.PayloadJson, "name");
        if (string.IsNullOrWhiteSpace(name)) return (null, "Missing 'name' — the shortcut's name (or id).");
        string? folder = LauncherRpc.ParseString(cmd.PayloadJson, "folder");

        var all = ShortcutStore.Load();
        if (!string.IsNullOrWhiteSpace(folder))
        {
            string norm = ShortcutFolders.Normalize(folder);
            all = all.Where(s => string.Equals(
                ShortcutFolders.Normalize(s.FolderPath), norm, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        var matches = all.Where(s =>
            string.Equals(s.Id, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(s.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return (matches[0], null);
        if (matches.Count > 1)
        {
            var homes = matches.Select(m =>
            {
                var f = ShortcutFolders.Normalize(m.FolderPath);
                return $"{m.Name} ({(f.Length == 0 ? "(root)" : f)})";
            });
            return (null, $"Shortcut '{name}' is ambiguous: {string.Join(", ", homes)}. Pass a folder.");
        }
        return (null, $"No shortcut named '{name}'. Use shortcut_list to see what exists.");
    }

    private static string Ok(string message) =>
        JsonSerializer.Serialize(new { ok = true, message });

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { ok = false, message });
}
