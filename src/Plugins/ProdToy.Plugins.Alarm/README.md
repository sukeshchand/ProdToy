# Alarms plugin

Schedules alarms and reminders for ProdToy: one-shot, daily, weekdays, weekend, weekly, custom days,
monthly and interval schedules, with a window, a tray balloon and a sound when they fire.

## Alarm bodies

Every alarm has a title, a one-line message and a body. The body has one of three kinds:

| Kind | What the window shows | Where it is stored |
|------|-----------------------|--------------------|
| `text` | the message, in a scrolling text box | in `alarms.json` (no folder) |
| `markdown` | the rendered markdown: headings, lists, tables, code blocks, images, links | `alarms/<id>/body.md` |
| `html` | the html page, with its images and css | `alarms/<id>/body.html` |

- `text` is the default. Alarms saved before body kinds existed load as `text`, unchanged.
- For `markdown` and `html` the message stays the short summary used by the balloon, the list and the
  history. Without a message, the first line of the markdown (or the html `<title>`) is used.
- Markdown uses the same styles as the Claude notification popup.
- Links in a body open in the default browser.

## Folder layout

All paths are under the plugin's data folder:

```
alarms.json                    the alarms
alarm-history.json             the history
ring-window.json               the alarm window's size and position per screen
alarms/<id>/                   one folder per markdown / html alarm
  body.md  or  body.html       the body
  images/...                   anything the body references, by relative path
alarm-history-bodies/<id>/     copies of the body at each firing (2 MB or less)
```

- The folder is created with the alarm, copied by Duplicate and deleted with the alarm.
- Reference resources relatively: `![chart](images/chart.png)` or `<img src="images/chart.png">`.
- The window serves only the files inside the alarm's folder.

## The alarm window

- Resizable, at least 420 x 320; the size and position are remembered per screen.
- Header: title, a colour strip for the priority, and the schedule line (for example
  `Every day - 02:00 - 3rd firing`).
- Body: the text, markdown or html; long content scrolls while the footer stays.
- Footer: snooze length and **Snooze**, **Open folder** (when the alarm has one) and **Dismiss**.
- A window left alone for 5 minutes closes and is recorded as missed.

## Editing

The edit dialog has two tabs:

- **General**: title, message, category, priority, schedule, notification, diagnostics.
- **Body**: the kind, an editor for markdown or html, **Choose file...** (the file and the folders beside
  it are copied in on save), **Open folder** and **Preview**.

## MCP tools

| Tool | What it does |
|------|--------------|
| `alarm_list` | all alarms with schedule, status, next trigger, priority and body kind |
| `alarm_get` | every field of one alarm, the body content, the body file, the folder and its files |
| `alarm_create` | creates an alarm |
| `alarm_update` | changes any field; fields left out stay as they are |
| `alarm_preview` | opens the alarm window now without firing it (no sound, no history row) |
| `alarm_delete` | deletes an alarm and its folder |
| `alarm_pause` / `alarm_resume` / `alarm_snooze` | pause, resume or snooze |
| `alarm_history` | recent events, with the body kind of each firing |

Parameters of `alarm_create` and `alarm_update`:

- `title`, `message`, `status` (update only: `active` or `disabled`).
- Schedule: `inMinutes`, `time` (`HH:mm`), `schedule` (`once`, `daily`, `weekdays`, `weekend`,
  `weekly`, `monthly`, `interval`, `custom`), `date`, `days`, `dayOfMonth`, `intervalMinutes`,
  `startDate`, `endDate`. On update, a field that does not fit the schedule type is refused.
- Options: `note`, `category`, `priority` (`low`, `normal`, `high`), `snoozeMinutes`,
  `notification` (`popup`, `balloon`, `both`), `sound`, `fireAndForget`.
- Body:
  - `bodyKind`: `text`, `markdown` or `html`.
  - `body`: the content inline. Without `bodyKind` it is markdown on create and the alarm's current kind
    on update. For `text` it becomes the message.
  - `bodyFile`: an absolute path to a `.md` or `.html` file. It is copied in as the body together with the
    folders beside it and the relative files it references (up to 50 MB; `.git`, `node_modules`, `bin`
    and `obj` are skipped). The kind comes from the extension unless `bodyKind` is given.
  - `resources`: absolute paths of extra files or folders to copy into the alarm's folder.
  - `body` and `bodyFile` cannot be used together. Setting `bodyKind` to `text` removes the folder.

Example:

```json
{
  "title": "Release checklist",
  "time": "09:00",
  "schedule": "custom",
  "days": ["Monday", "Thursday"],
  "body": "# Before the release\n\n| Step | Owner |\n|---|---|\n| Build | CI |\n\n![board](board.png)",
  "resources": ["C:\\path\\to\\board.png"]
}
```
