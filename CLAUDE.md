# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Overview

ProdToy is a plugin-based Windows developer utility application. The host app is a lightweight plugin runner that provides tray icon, notification popup, settings UI, and named-pipe IPC. All features (alarms, screenshots, Claude integration) are independently developed DLL-based plugins loaded at runtime via `AssemblyLoadContext`.

## Build & Run Commands

```bash
# Build entire solution (host + all plugins)
dotnet build src/ProdToy.sln

# Build Release
dotnet build -c Release src/ProdToy.sln

# Build individual plugin
dotnet build src/ProdToy.Plugins.Alarm
dotnet build src/ProdToy.Plugins.Screenshot
dotnet build src/ProdToy.Plugins.ClaudeIntegration

# Run setup form (no arguments)
dotnet run --project src/ProdToy.Win

# Run popup with notification
dotnet run --project src/ProdToy.Win -- --title "Title" --message "Message" --type "info"

# Publish single-file executable + plugins
dotnet publish -c Release src/ProdToy.Win

# Full publish with version bump, plugin packaging, and optional deploy
.\publish.ps1
.\publish.ps1 -DeployPath "I:\ProdToy\_beta" -ReleaseNotes "Feature description"
```

There are no tests or linting configured.

## Architecture

**Solution** (`src/ProdToy.sln`) with 5 projects targeting .NET 8.0 (net8.0-windows):

| Project | Type | Purpose |
|---------|------|---------|
| `ProdToy.Sdk` | Class library | Plugin contracts (IPlugin, IPluginHost, IPluginContext) |
| `ProdToy.Win` | WinExe | Host application — tray icon, popup, settings, plugin manager |
| `ProdToy.Plugins.Alarm` | Class library | Alarm scheduling and notifications |
| `ProdToy.Plugins.Screenshot` | Class library | Screen capture and annotation editor |
| `ProdToy.Plugins.ClaudeIntegration` | Class library | Claude Code hooks, status line, auto-title |

Only external NuGet dependency is `Microsoft.Web.WebView2` (host only). Plugin projects reference only `ProdToy.Sdk`.

### Solution Structure

```
src/
  ProdToy.sln
  ProdToy.Sdk/                          Plugin contracts
    IPlugin.cs                          Lifecycle: Initialize, Start, Stop, Dispose
    IPluginHost.cs                      Host services: theme, hotkeys, notifications, UI thread
    IPluginContext.cs                    Per-plugin: data dir, settings, logging
    PluginAttribute.cs                  Metadata: id, name, version, author, priority
    PluginTheme.cs                      Theme colors exposed to plugins
    MenuContribution.cs                 Tray menu item contribution
    SettingsPageContribution.cs         Settings tab contribution
    IHotkeyRegistration.cs             Hotkey handle
  ProdToy.Win/                          Host application
    Program.cs                          Entry point, CLI args, mutex, pipe client
    Core/
      AppVersion.cs                     Central version constant
      AppSettings.cs                    Host settings (theme, font, notifications, update)
      AppPaths.cs                       All path definitions including plugins dir
      NativeMethods.cs                  P/Invoke declarations
      GlobalHotkey.cs                   System-wide hotkey registration
      TripleCtrlDetector.cs             Triple Ctrl-tap detection
      UpdateChecker.cs                  Host update checker
      Updater.cs                        Download + apply host updates
    Plugin/
      PluginManager.cs                  Discovery, loading, lifecycle, enable/disable
      PluginLoadContext.cs              AssemblyLoadContext per plugin (isolation)
      PluginHostImpl.cs                 IPluginHost implementation
      PluginContextImpl.cs              IPluginContext implementation
      PluginInfo.cs                     Runtime plugin state
      PluginCatalog.cs                  Remote catalog fetch, download, install, update
      PluginCatalogForm.cs              Catalog browser UI
      PluginManifest.cs                 Catalog data records
    Popup/
      PopupAppContext.cs                Tray icon, plugin init, pipe server, update checker
      PopupForm.cs                      Notification window (WebView2, history nav)
    Settings/
      SettingsForm.cs                   Settings dialog (General, Appearance, Notifications, Plugins, About + plugin tabs)
    Setup/                              Installation wizard, registry, uninstaller
    Rendering/                          Markdown-to-HTML, funny quotes
    Theme/                              PopupTheme record + 12 built-in themes
    Controls/                           RoundedButton, ColorPickerPopup
    Data/                               ResponseHistory (daily JSON)
  Plugins/
    ProdToy.Plugins.Alarm/             Alarm plugin
    ProdToy.Plugins.Screenshot/        Screenshot plugin
    ProdToy.Plugins.ClaudeIntegration/ Claude integration plugin
```

### Plugin System

Plugins are DLLs placed in `~/.prod-toy/plugins/{PluginId}/`. Each plugin:
- References only `ProdToy.Sdk` (not the host)
- Implements `IPlugin` with `[Plugin]` attribute
- Is loaded in its own `AssemblyLoadContext` (collectible, isolatable)
- Can contribute tray menu items and settings tabs
- Has its own data directory at `~/.prod-toy/plugins/data/{PluginId}/` (survives uninstall/reinstall)
- Has its own `settings.json` independent of host settings

Plugin lifecycle: `Initialize(context)` → `Start()` → (running) → `Stop()` → `Dispose()`

### Runtime File Structure

```
~/.prod-toy/
  ProdToy.exe                           Host executable
  settings.json                         Host settings
  plugins/
    plugins-state.json                  Enable/disable state
    bin/                                Plugin DLLs (deleted on uninstall)
      ProdToy.Plugin.Alarm/
        ProdToy.Plugins.Alarm.dll
      ProdToy.Plugin.Screenshot/
        ProdToy.Plugins.Screenshot.dll
      ProdToy.Plugin.ClaudeIntegration/
        ProdToy.Plugins.ClaudeIntegration.dll
    data/                               Plugin data (survives uninstall)
      ProdToy.Plugin.Alarm/
        settings.json
      ProdToy.Plugin.Screenshot/
        settings.json, screenshots/
      ProdToy.Plugin.ClaudeIntegration/
        settings.json
  history/claude/chats/                 Response history (host-managed)
  scripts/                             Status line script (plugin-managed)
  logs/prod-toy-yyyyMMdd.log           Unified daily log (host + plugins, 30-day retention)
```

### Execution Flow

1. **First instance with no args from non-install dir** → opens `SetupForm`
2. **First instance with no args from install dir** → opens popup with last history entry
3. **First instance with args** → creates `PopupAppContext`, initializes `PluginManager`, starts plugins, shows popup
4. **Subsequent instances** → detect mutex, send args via named pipe, exit

### Conventions

- Notification type strings are centralized in `NotificationType` constants.
- P/Invoke declarations live in `NativeMethods` — do not duplicate in individual forms.
- `AppSettingsData` is an immutable record — use `with` expressions to create modified copies.
- Plugin settings use per-plugin `settings.json` via `IPluginContext.LoadSettings<T>()`/`SaveSettings<T>()`.
- HTML encoding uses `System.Net.WebUtility.HtmlEncode`.
- Empty `catch` blocks should include `Debug.WriteLine` for diagnostics.
- Files are organized by feature folder, not by type.
- Plugins include their own copies of shared controls (RoundedButton, IconHelper) since they can't reference host internals.

### Creating a New Plugin

1. Create a new class library project referencing `ProdToy.Sdk`
2. Set `<EnableDynamicLoading>true</EnableDynamicLoading>` and `<Private>false</Private>` on SDK reference
3. Create a class implementing `IPlugin` with `[Plugin("Id", "Name", "Version")]` attribute
4. Build and place DLL in `~/.prod-toy/plugins/{PluginId}/`
5. The host discovers and loads it automatically on next startup
