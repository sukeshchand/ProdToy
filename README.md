# ProdToy

A Windows developer utility that integrates with Claude Code CLI. Provides rich notifications, a full-featured screenshot editor, alarm scheduling, and a system tray experience.

## Features

### Claude Code Integration
- **Rich Notifications** -- popup notifications for task completions, errors, and pending questions with themed UI and markdown rendering
- **Response History** -- daily JSON-based chat history with session filtering and date navigation
- **Hook Management** -- configurable hooks for Stop, Notification, and UserPromptSubmit events
- **Status Line** -- custom status bar for Claude CLI showing model, branch, context usage, edit stats
- **Named-pipe IPC** -- single-instance architecture with pipe-based communication

### Screenshot Editor
- **Screen Capture** -- global hotkey to capture screen regions
- **Annotation Tools** -- pen, marker, line, arrow, rectangle, ellipse, text, mask box
- **Perspective Crop** -- drag-to-select crop with 4 independently draggable corners, bilinear warp, and shift-constrained edge adjustment
- **Bitmap Eraser** -- erase pixels from base image or image layers with dual-color cursor
- **Rotation** -- rotate any annotation via drag handle with full undo/redo
- **Mask Box** -- redact areas with solid fill and asterisk pattern overlay
- **Non-destructive Editing** -- all operations stored as undo/redo actions; base image never modified
- **Auto-save** -- debounced save on every edit (state.json, flattened PNG, preview)
- **Session Persistence** -- full editing state serialized to JSON; resume editing across sessions
- **Singleton Form** -- editor window reused across captures for fast response
- **Image Library** -- recent screenshots panel with double-click to open and restore edit history
- **Export** -- copy image, copy file, copy path, save as (PNG/JPG/BMP)
- **Segoe Fluent Icons** -- native Windows 11 icon font for all toolbar buttons
- **Border Settings** -- split toggle/dropdown button with 5 border styles
- **Canvas Resize** -- drag handles on canvas edges with undo/redo

### Alarms
- **Alarm Scheduling** -- create, snooze, and manage alarms with popup and Windows notifications
- **Alarm History** -- persistent alarm log with configurable retention

### Settings
- **Themes** -- 7 built-in themes with live preview
- **Global Font** -- configurable font family
- **Hook Toggles** -- enable/disable individual Claude Code hooks from the UI
- **Triple Ctrl** -- triple Ctrl-tap to instantly open last screenshot editor
- **Status Line Config** -- toggle individual status line items

## Requirements

- Windows 10/11
- .NET 8.0 Runtime
- Microsoft Edge WebView2 Runtime (for notification rendering)

## Build

```bash
dotnet build src/ProdToy.sln
```

## Publish

```bash
dotnet publish -c Release src/ProdToy.Win
```

Or use the publish script which automates version bump, build, metadata generation, and network deploy:

```powershell
.\publish.ps1
```

## Install

Run the published `ProdToy.exe` without arguments to open the setup wizard. It copies the executable, writes the Claude Code hook script, and merges settings into Claude's `settings.json`.

## Architecture

Single WinForms project targeting `net8.0-windows`. External dependency: `Microsoft.Web.WebView2`. All classes in the `ProdToy` namespace. Feature-folder organization:

```
src/ProdToy.Win/
  Program.cs              Entry point, CLI parsing, single-instance mutex
  Core/                   Settings, versioning, updates, hotkeys, P/Invoke
  Popup/                  Tray icon, notification popup, pipe server
  Screenshot/             Full screenshot editor (canvas, toolbar, annotations, crop, export)
  Settings/               Settings dialog with themed tabs
  Setup/                  Installation wizard
  Rendering/              Markdown-to-HTML, funny quotes
  Theme/                  Theme records and built-in palette
  Alarm/                  Alarm scheduling and notifications
  Controls/               Custom UI controls
  Data/                   Response history persistence
```

## License

MIT
