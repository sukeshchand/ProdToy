using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ProdToy.Sdk;

namespace ProdToy.Plugins.Alarm;

/// <summary>
/// MCP tools exposed by the Alarm plugin: list/get/create/update/delete alarms,
/// pause/resume/snooze, preview the alarm window, and read the trigger history.
/// Lets a Claude instance set reminders for the user ("remind me in 20 minutes to
/// check the deploy"), give them a markdown or html body, and inspect why an alarm
/// did or didn't fire. All handlers run on the UI thread (RpcRouter contract) and
/// return single-line JSON.
/// </summary>
static class AlarmMcpTools
{
    private const string Section = "Alarms";
    private const int GetBodyLimitChars = 256 * 1024;

    private static readonly (string Name, string Type, string Desc, bool Required) NameArg =
        ("name", "string", "The alarm's title (or id).", true);

    private static readonly (string Name, string Type, string Desc, bool Required)[] ScheduleArgs =
    {
        ("inMinutes", "integer", "Shortcut: fire once N minutes from now (overrides time/schedule/date).", false),
        ("time", "string", "Time of day \"HH:mm\" (default 09:00).", false),
        ("schedule", "string", "once | daily | weekdays | weekend | weekly | monthly | interval | custom (default once).", false),
        ("date", "string", "For once: \"yyyy-MM-dd\" (default: today if the time is still ahead, else tomorrow).", false),
        ("days", "array", "For weekly/custom: day names, e.g. [\"Monday\",\"Thursday\"].", false),
        ("dayOfMonth", "integer", "For monthly: 1-31.", false),
        ("intervalMinutes", "integer", "For interval: fire every N minutes.", false),
        ("startDate", "string", "Do not fire before this date (\"yyyy-MM-dd\"); empty string clears it.", false),
        ("endDate", "string", "Stop firing after this date (\"yyyy-MM-dd\"); empty string clears it.", false),
    };

    private static readonly (string Name, string Type, string Desc, bool Required)[] OptionArgs =
    {
        ("note", "string", "A private note kept with the alarm (not shown when it fires).", false),
        ("category", "string", "Optional grouping label.", false),
        ("priority", "string", "low | normal | high (default normal).", false),
        ("snoozeMinutes", "integer", "Snooze length offered by the window (default 5).", false),
        ("notification", "string", "popup | balloon | both (default both).", false),
        ("sound", "boolean", "Play a sound when it fires (default true).", false),
        ("fireAndForget", "boolean", "Complete the alarm after its first firing.", false),
    };

    private static readonly (string Name, string Type, string Desc, bool Required)[] BodyArgs =
    {
        ("bodyKind", "string", "text | markdown | html. Text shows the message; markdown and html are files in the alarm's own folder.", false),
        ("body", "string", "Inline body content, written to the alarm's body file (body.md / body.html). For text it becomes the message.", false),
        ("bodyFile", "string", "Absolute path of a .md / .html file to copy in instead of 'body'. The folders beside it and the relative files it references are copied too.", false),
        ("resources", "array", "Absolute paths of extra files or folders to copy into the alarm's folder (images, css).", false),
    };

    private const string BodyHelp =
        "Body: an alarm shows its message (text), a markdown page or an html page. Markdown and html live in " +
        "the alarm's own folder `alarms/<id>/` (body.md or body.html) with their images beside them; reference them " +
        "relatively, e.g. `![chart](images/chart.png)` or `<img src=\"images/chart.png\">`. Pass the content inline " +
        "with `body`, or point `bodyFile` at a file on disk (its sibling folders come along). `bodyKind` defaults to " +
        "the file's extension, else markdown on create and the alarm's current kind on update. Without a `message`, " +
        "the first line of the body becomes the one-line summary used by the balloon and the list.";

    public static List<IDisposable> RegisterAll(IPluginContext ctx)
    {
        var host = ctx.Host;
        return new List<IDisposable>
        {
            host.RegisterMcpTool(new McpTool("alarm_list",
                "List the user's alarms: title, schedule, live status (Active/Paused/Snoozed/Disabled/…), " +
                "next trigger time, category, priority and body kind.",
                Schema(("status", "string", "Filter by display status, e.g. Active, Paused, Disabled.", false)),
                Section),
                cmd => Task.FromResult(List(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_get",
                "Every field of one alarm, plus its body kind, body file path, folder, the files in the folder " +
                "and the body content - read this before changing a body.",
                Schema(NameArg), Section),
                cmd => Task.FromResult(Get(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_create",
                "Create an alarm/reminder. Simplest: {title, inMinutes} for a one-shot reminder N minutes from " +
                "now. Or give time (\"HH:mm\") plus a schedule: once (with date), daily, weekdays, weekend, " +
                "weekly/custom (with days), monthly (with dayOfMonth), interval (with intervalMinutes). " +
                "Optional markdown or html body (body / bodyFile) for a rich alarm window.",
                Schema(new[]
                    {
                        ("title", "string", "Alarm title (shown in the window).", true),
                        ("message", "string", "Short message: the text body, or the one-line summary of a markdown/html alarm.", false),
                    }
                    .Concat(ScheduleArgs).Concat(OptionArgs).Concat(BodyArgs).ToArray()),
                Section,
                "The response includes the computed next trigger so you can confirm the schedule was understood.\n\n" + BodyHelp),
                cmd => Task.FromResult(Create(ctx, cmd))),

            host.RegisterMcpTool(new McpTool("alarm_update",
                "Update an existing alarm: any of title, message, status (active/disabled), the schedule fields " +
                "(schedule, time, date, days, dayOfMonth, intervalMinutes, inMinutes, startDate, endDate), note, " +
                "category, priority, snoozeMinutes, notification, sound, fireAndForget, and the body " +
                "(bodyKind, body or bodyFile, resources). Fields you leave out stay as they are.",
                Schema(new[]
                    {
                        NameArg,
                        ("title", "string", "New title.", false),
                        ("message", "string", "New message.", false),
                        ("status", "string", "active | disabled.", false),
                    }
                    .Concat(ScheduleArgs).Concat(OptionArgs).Concat(BodyArgs).ToArray()),
                Section,
                BodyHelp),
                cmd => Task.FromResult(Update(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_preview",
                "Open the alarm window for an alarm now, with its current content, without firing it: no sound, " +
                "no snooze and nothing written to the history. Use it to check a markdown or html body.",
                Schema(NameArg), Section),
                cmd => Task.FromResult(Preview(cmd, a => AlarmRingLauncher.ShowPreview(a)))),

            host.RegisterMcpTool(new McpTool("alarm_delete",
                "Delete an alarm permanently, with its folder. Confirm with the user before deleting an alarm you did not create.",
                Schema(NameArg), Section),
                cmd => Task.FromResult(Delete(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_pause",
                "Pause an alarm so it stays silent for a while (default 60 minutes) without disabling it.",
                Schema(NameArg, ("minutes", "integer", "How long to pause (default 60).", false)), Section),
                cmd => Task.FromResult(Pause(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_resume",
                "Resume an alarm: clears any pause/snooze and re-activates it if it was disabled.",
                Schema(NameArg), Section),
                cmd => Task.FromResult(Resume(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_snooze",
                "Snooze an alarm (default: its configured snooze length).",
                Schema(NameArg, ("minutes", "integer", "Snooze length in minutes.", false)), Section),
                cmd => Task.FromResult(Snooze(cmd))),

            host.RegisterMcpTool(new McpTool("alarm_history",
                "Recent alarm events (Triggered/Missed/Snoozed/Dismissed/…), newest first — useful to check " +
                "whether and when an alarm actually fired.",
                Schema(("name", "string", "Only events for this alarm (title or id).", false),
                       ("limit", "integer", "Max events to return (default 30).", false)),
                Section),
                cmd => Task.FromResult(History(cmd))),
        };
    }

    // ─────────────────────────── handlers ───────────────────────────

    internal static string List(PipeCommand cmd)
    {
        var args = Args.Parse(cmd);
        string? statusFilter = args.String("status");
        var items = AlarmStore.LoadAlarms()
            .Select(a => new { Alarm = a, Display = a.GetDisplayStatus() })
            .Where(x => statusFilter == null
                || string.Equals(x.Display.ToString(), statusFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.Alarm.GetNextTrigger() ?? DateTime.MaxValue)
            .Select(x => new
            {
                id = x.Alarm.Id,
                title = x.Alarm.Title,
                message = string.IsNullOrWhiteSpace(x.Alarm.Message) ? null : x.Alarm.Message,
                bodyKind = KindName(x.Alarm.BodyKind),
                schedule = x.Alarm.GetScheduleDescription(),
                time = x.Alarm.Schedule.TimeOfDay,
                status = x.Display.ToString(),
                nextTrigger = x.Alarm.GetNextTrigger(),
                category = x.Alarm.Category,
                priority = x.Alarm.Priority.ToString(),
                lastTriggeredAt = x.Alarm.LastTriggeredAt,
                triggerCount = x.Alarm.TriggerCount,
            })
            .ToList();
        return JsonSerializer.Serialize(new { ok = true, count = items.Count, alarms = items });
    }

    private static readonly JsonSerializerOptions GetJson = new()
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    internal static string Get(PipeCommand cmd)
    {
        var (alarm, error) = Resolve(Args.Parse(cmd));
        if (error != null) return Fail(error);
        var a = alarm!;
        var bodies = AlarmStore.Bodies;

        var node = JsonSerializer.SerializeToNode(a, GetJson)!.AsObject();
        node["bodyKind"] = KindName(a.BodyKind);
        node["displayStatus"] = a.GetDisplayStatus().ToString();
        node["scheduleDescription"] = a.GetScheduleDescription();
        node["nextTrigger"] = a.GetNextTrigger()?.ToString("s");

        string? folder = bodies.HasFolder(a.Id) ? bodies.FolderFor(a.Id) : null;
        node["folder"] = folder;
        node["bodyFile"] = bodies.BodyFileFor(a);
        if (folder != null)
        {
            var files = new JsonArray();
            foreach (var f in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Take(200))
                files.Add(Path.GetRelativePath(folder, f).Replace('\\', '/'));
            node["files"] = files;
        }

        string? body = null;
        try { body = bodies.ReadBody(a); }
        catch (Exception ex) { node["bodyError"] = ex.Message; }
        if (body != null && body.Length > GetBodyLimitChars)
        {
            node["body"] = body[..GetBodyLimitChars];
            node["bodyTruncated"] = true;
        }
        else node["body"] = body;

        return new JsonObject { ["ok"] = true, ["alarm"] = node }.ToJsonString();
    }

    private static string Create(IPluginContext ctx, PipeCommand cmd) =>
        Create(cmd, ctx.LoadSettings<AlarmPluginSettings>().AlarmsEnabled);

    internal static string Create(PipeCommand cmd, bool alarmsEnabled)
    {
        var args = Args.Parse(cmd);
        string? title = args.String("title");
        if (string.IsNullOrWhiteSpace(title)) return Fail("Missing 'title'.");

        var (schedule, schedError) = ApplySchedule(new AlarmSchedule(), args, create: true);
        if (schedError != null) return Fail(schedError);

        var alarm = new AlarmEntry
        {
            Title = title!.Trim(),
            Message = args.String("message") ?? "",
            Schedule = schedule!,
        };
        var (withOptions, optError) = ApplyOptions(alarm, args);
        if (optError != null) return Fail(optError);
        alarm = withOptions!;

        var (withBody, bodyError) = ApplyBody(alarm, args, create: true);
        if (bodyError != null)
        {
            try { AlarmStore.Bodies.DeleteFolder(alarm.Id); } catch { }
            return Fail(bodyError);
        }
        alarm = withBody!;

        AlarmStore.AddAlarm(alarm);
        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
        {
            AlarmId = alarm.Id,
            AlarmTitle = alarm.Title,
            EventType = AlarmHistoryEventType.Created,
            Detail = "Created via MCP",
        });

        string? folder = AlarmStore.Bodies.HasFolder(alarm.Id) ? AlarmStore.Bodies.FolderFor(alarm.Id) : null;
        return JsonSerializer.Serialize(new
        {
            ok = true,
            id = alarm.Id,
            message = $"Created alarm '{alarm.Title}' — {alarm.GetScheduleDescription()} at {alarm.Schedule.TimeOfDay}."
                + (alarm.BodyKind != AlarmBodyKind.Text ? $" Body: {KindName(alarm.BodyKind)}." : "")
                + (alarmsEnabled ? "" : " WARNING: alarms are disabled in ProdToy settings — it will not fire until enabled."),
            nextTrigger = alarm.GetNextTrigger(),
            bodyKind = KindName(alarm.BodyKind),
            folder,
        });
    }

    internal static string Update(PipeCommand cmd)
    {
        var args = Args.Parse(cmd);
        var (alarm, error) = Resolve(args);
        if (error != null) return Fail(error);

        var updated = alarm!;
        if (args.String("title") is { Length: > 0 } t) updated = updated with { Title = t.Trim() };
        if (args.String("message") is string m) updated = updated with { Message = m };
        if (args.String("status") is string st)
        {
            if (string.Equals(st, "active", StringComparison.OrdinalIgnoreCase))
                updated = updated with { Status = AlarmStatus.Active };
            else if (string.Equals(st, "disabled", StringComparison.OrdinalIgnoreCase))
                updated = updated with { Status = AlarmStatus.Disabled };
            else return Fail($"Bad 'status' \"{st}\" — use active or disabled.");
        }

        if (TouchesSchedule(args))
        {
            var (schedule, schedError) = ApplySchedule(updated.Schedule, args, create: false);
            if (schedError != null) return Fail(schedError);
            updated = updated with { Schedule = schedule! };
        }

        var (withOptions, optError) = ApplyOptions(updated, args);
        if (optError != null) return Fail(optError);
        updated = withOptions!;

        var (withBody, bodyError) = ApplyBody(updated, args, create: false);
        if (bodyError != null) return Fail(bodyError);
        updated = withBody! with { UpdatedAt = DateTime.Now };

        AlarmStore.UpdateAlarm(updated);
        LogEvent(updated, AlarmHistoryEventType.Edited, "Edited via MCP");
        return Ok($"Updated alarm '{updated.Title}' ({updated.GetScheduleDescription()}, body {KindName(updated.BodyKind)}). " +
                  $"Next trigger: {updated.GetNextTrigger()?.ToString() ?? "none"}.");
    }

    /// <summary>Shows the alarm window without firing: <paramref name="show"/> opens it; no history row is written.</summary>
    internal static string Preview(PipeCommand cmd, Action<AlarmEntry> show)
    {
        var (alarm, error) = Resolve(Args.Parse(cmd));
        if (error != null) return Fail(error);
        try { show(alarm!); }
        catch (Exception ex) { return Fail($"Could not open the preview: {ex.Message}"); }
        return Ok($"Opened a preview of '{alarm!.Title}' ({KindName(alarm.BodyKind)} body).");
    }

    internal static string Delete(PipeCommand cmd)
    {
        var (alarm, error) = Resolve(Args.Parse(cmd));
        if (error != null) return Fail(error);
        AlarmStore.DeleteAlarm(alarm!.Id);
        LogEvent(alarm, AlarmHistoryEventType.Deleted, "Deleted via MCP");
        return Ok($"Deleted alarm '{alarm.Title}'.");
    }

    private static string Pause(PipeCommand cmd)
    {
        var args = Args.Parse(cmd);
        var (alarm, error) = Resolve(args);
        if (error != null) return Fail(error);
        int minutes = Math.Clamp(args.Int("minutes") ?? 60, 1, 7 * 24 * 60);
        var until = DateTime.Now.AddMinutes(minutes);
        AlarmStore.UpdateAlarm(alarm! with { PausedUntil = until, UpdatedAt = DateTime.Now });
        LogEvent(alarm, AlarmHistoryEventType.Paused, $"Paused {minutes} min via MCP");
        return Ok($"Paused '{alarm.Title}' until {until:HH:mm} ({minutes} min).");
    }

    private static string Resume(PipeCommand cmd)
    {
        var (alarm, error) = Resolve(Args.Parse(cmd));
        if (error != null) return Fail(error);
        var updated = alarm! with
        {
            PausedUntil = null,
            SnoozedUntil = null,
            Status = alarm.Status == AlarmStatus.Disabled ? AlarmStatus.Active : alarm.Status,
            UpdatedAt = DateTime.Now,
        };
        AlarmStore.UpdateAlarm(updated);
        LogEvent(updated, AlarmHistoryEventType.Resumed, "Resumed via MCP");
        return Ok($"Resumed '{updated.Title}'. Next trigger: {updated.GetNextTrigger()?.ToString() ?? "none"}.");
    }

    private static string Snooze(PipeCommand cmd)
    {
        var args = Args.Parse(cmd);
        var (alarm, error) = Resolve(args);
        if (error != null) return Fail(error);
        int minutes = Math.Clamp(args.Int("minutes") ?? alarm!.SnoozeMinutes, 1, 24 * 60);
        var until = DateTime.Now.AddMinutes(minutes);
        AlarmStore.UpdateAlarm(alarm! with { SnoozedUntil = until, UpdatedAt = DateTime.Now });
        LogEvent(alarm!, AlarmHistoryEventType.Snoozed, $"Snoozed {minutes} min via MCP");
        return Ok($"Snoozed '{alarm!.Title}' until {until:HH:mm}.");
    }

    private static string History(PipeCommand cmd)
    {
        var args = Args.Parse(cmd);
        int limit = Math.Clamp(args.Int("limit") ?? 30, 1, 500);
        List<AlarmHistoryEntry> events;
        if (args.String("name") is { Length: > 0 })
        {
            var (alarm, error) = Resolve(args);
            if (error != null) return Fail(error);
            events = AlarmStore.LoadHistory(alarm!.Id);
        }
        else
        {
            events = AlarmStore.LoadHistory();
        }

        var items = events
            .OrderByDescending(e => e.Timestamp)
            .Take(limit)
            .Select(e => new
            {
                alarm = e.AlarmTitle,
                @event = e.EventType.ToString(),
                at = e.Timestamp,
                detail = e.Detail,
                bodyKind = e.BodyKind is AlarmBodyKind k ? KindName(k) : null,
                bodyCopy = e.BodyCopy,
            });
        return JsonSerializer.Serialize(new { ok = true, events = items });
    }

    // ─────────────────────────── mapping ───────────────────────────

    private static readonly string[] ScheduleFieldNames =
        { "inMinutes", "time", "schedule", "date", "days", "dayOfMonth", "intervalMinutes" };

    private static bool TouchesSchedule(Args args) => ScheduleFieldNames.Any(args.Has);

    /// <summary>Applies the schedule fields to <paramref name="current"/>. On create the defaults are
    /// once at 09:00; on update every field left out keeps its value. A field that does not fit the
    /// resulting schedule type is an error.</summary>
    internal static (AlarmSchedule? Schedule, string? Error) ApplySchedule(AlarmSchedule current, Args args, bool create)
    {
        if (args.Int("inMinutes") is int mins)
        {
            if (mins < 1) return (null, "'inMinutes' must be at least 1.");
            var when = DateTime.Now.AddMinutes(mins);
            return (new AlarmSchedule
            {
                Type = AlarmScheduleType.Once,
                OneTimeDate = when.ToString("yyyy-MM-dd"),
                TimeOfDay = when.ToString("HH:mm"),
            }, null);
        }
        if (args.Has("inMinutes")) return (null, "'inMinutes' must be a whole number.");

        string time = args.String("time") ?? (create ? "09:00" : current.TimeOfDay);
        if (!TimeSpan.TryParse(time, out var tod)) return (null, $"Bad 'time' \"{time}\" — use \"HH:mm\".");

        var type = create ? AlarmScheduleType.Once : current.Type;
        if (args.String("schedule") is string schedName
            && !Enum.TryParse(schedName, ignoreCase: true, out type))
            return (null, $"Bad 'schedule' \"{schedName}\". Use once, daily, weekdays, weekend, weekly, monthly, interval, or custom.");
        bool typeChanged = type != current.Type;

        string? Misfit(string field, string kinds) =>
            args.Has(field) ? $"'{field}' only applies to {kinds} schedules (this one is {type.ToString().ToLowerInvariant()})." : null;

        var schedule = new AlarmSchedule { Type = type, TimeOfDay = time };
        switch (type)
        {
            case AlarmScheduleType.Once:
            {
                string? date = args.String("date");
                if (date != null && !DateTime.TryParse(date, out _))
                    return (null, $"Bad 'date' \"{date}\" — use \"yyyy-MM-dd\".");
                if (date == null && !create && !typeChanged) date = current.OneTimeDate;
                date ??= (DateTime.Now.Date + tod > DateTime.Now
                    ? DateTime.Now : DateTime.Now.AddDays(1)).ToString("yyyy-MM-dd");
                schedule = schedule with { OneTimeDate = date };
                break;
            }
            case AlarmScheduleType.Weekly:
            case AlarmScheduleType.Custom:
            {
                var days = args.Has("days") ? args.Days() : (create ? null : current.CustomDays);
                if (days == null || days.Length == 0)
                    return (null, "Weekly/custom schedules need 'days', e.g. [\"Monday\",\"Thursday\"].");
                schedule = schedule with { CustomDays = days };
                break;
            }
            case AlarmScheduleType.Monthly:
            {
                int? dom = args.Has("dayOfMonth") ? args.Int("dayOfMonth") : (create ? null : current.DayOfMonth);
                if (dom is not (>= 1 and <= 31)) return (null, "Monthly schedules need 'dayOfMonth' (1–31).");
                schedule = schedule with { DayOfMonth = dom };
                break;
            }
            case AlarmScheduleType.Interval:
            {
                int? iv = args.Has("intervalMinutes") ? args.Int("intervalMinutes") : (create ? null : current.IntervalMinutes);
                if (iv is not > 0) return (null, "Interval schedules need 'intervalMinutes' (> 0).");
                schedule = schedule with { IntervalMinutes = iv };
                break;
            }
        }

        string? misfit =
            (type != AlarmScheduleType.Once ? Misfit("date", "once") : null)
            ?? (type is not (AlarmScheduleType.Weekly or AlarmScheduleType.Custom) ? Misfit("days", "weekly/custom") : null)
            ?? (type != AlarmScheduleType.Monthly ? Misfit("dayOfMonth", "monthly") : null)
            ?? (type != AlarmScheduleType.Interval ? Misfit("intervalMinutes", "interval") : null);
        return misfit != null ? (null, misfit) : (schedule, null);
    }

    /// <summary>startDate, endDate, note, category, priority, snoozeMinutes, notification, sound, fireAndForget.</summary>
    internal static (AlarmEntry? Alarm, string? Error) ApplyOptions(AlarmEntry alarm, Args args)
    {
        var a = alarm;
        foreach (var field in new[] { "startDate", "endDate" })
        {
            if (args.String(field) is not string value) continue;
            if (value.Length > 0 && !DateTime.TryParse(value, out _))
                return (null, $"Bad '{field}' \"{value}\" — use \"yyyy-MM-dd\".");
            string? v = value.Length == 0 ? null : value;
            a = field == "startDate" ? a with { StartDate = v } : a with { EndDate = v };
        }
        if (args.String("note") is string note) a = a with { Note = note };
        if (args.String("category") is string cat) a = a with { Category = cat.Length == 0 ? null : cat };
        if (args.String("priority") is string prio)
        {
            if (!Enum.TryParse<AlarmPriority>(prio, ignoreCase: true, out var p))
                return (null, $"Bad 'priority' \"{prio}\" — use low, normal, or high.");
            a = a with { Priority = p };
        }
        if (args.Int("snoozeMinutes") is int sn) a = a with { SnoozeMinutes = Math.Clamp(sn, 1, 24 * 60) };
        if (args.String("notification") is string mode)
        {
            AlarmNotificationMode? parsed = mode.Trim().ToLowerInvariant() switch
            {
                "popup" or "window" => AlarmNotificationMode.Popup,
                "balloon" or "windows" or "toast" => AlarmNotificationMode.Windows,
                "both" => AlarmNotificationMode.Both,
                _ => null,
            };
            if (parsed == null) return (null, $"Bad 'notification' \"{mode}\" — use popup, balloon, or both.");
            a = a with { Notification = parsed.Value };
        }
        if (args.Bool("sound") is bool sound) a = a with { SoundEnabled = sound };
        if (args.Bool("fireAndForget") is bool ff) a = a with { FireAndForget = ff };
        return (a, null);
    }

    /// <summary>bodyKind, body / bodyFile and resources: writes the alarm's folder and returns the
    /// alarm with BodyKind / BodyPath (and Message for a text body or a missing summary).</summary>
    internal static (AlarmEntry? Alarm, string? Error) ApplyBody(AlarmEntry alarm, Args args, bool create)
    {
        string? kindText = args.String("bodyKind");
        string? body = args.String("body");
        string? bodyFile = args.String("bodyFile");
        string[]? resources = args.Strings("resources");
        if (args.Has("resources") && resources == null) return (null, "'resources' must be an array of absolute paths.");

        AlarmBodyKind? kind = null;
        if (kindText != null)
        {
            if (!AlarmBodyFiles.TryParseKind(kindText, out var k))
                return (null, $"Bad 'bodyKind' \"{kindText}\" — use text, markdown, or html.");
            kind = k;
        }
        if (body != null && bodyFile != null) return (null, "Pass either 'body' or 'bodyFile', not both.");

        var files = AlarmStore.Bodies;
        var a = alarm;
        try
        {
            if (body != null)
            {
                // No kind given: markdown for a new alarm, the alarm's own kind on update.
                var k = kind ?? (create ? AlarmBodyKind.Markdown : alarm.BodyKind);
                a = files.WriteBody(a, k, body);
            }
            else if (bodyFile != null)
            {
                a = files.ImportBodyFile(a, kind, bodyFile);
            }
            else if (kind != null && kind != alarm.BodyKind)
            {
                if (kind == AlarmBodyKind.Text) a = files.WriteBody(a, AlarmBodyKind.Text, a.Message);
                else return (null, $"Changing the body to {KindName(kind.Value)} needs 'body' or 'bodyFile'.");
            }

            if (resources is { Length: > 0 })
            {
                if (a.BodyKind == AlarmBodyKind.Text)
                    return (null, "'resources' need a markdown or html body - a text alarm has no folder.");
                files.CopyResources(a.Id, resources);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException)
        {
            return (null, ex.Message);
        }

        // A markdown / html alarm still needs a one-line Message for the balloon, the list and the history.
        if (a.BodyKind != AlarmBodyKind.Text && string.IsNullOrWhiteSpace(a.Message) && args.String("message") == null)
        {
            string? content = body ?? (bodyFile != null ? files.ReadBody(a) : null);
            if (content != null) a = a with { Message = AlarmBodyFiles.SummaryOf(a.BodyKind, content) };
        }
        return (a, null);
    }

    internal static string KindName(AlarmBodyKind kind) => kind.ToString().ToLowerInvariant();

    // ─────────────────────────── helpers ───────────────────────────

    private static (AlarmEntry? Alarm, string? Error) Resolve(Args args)
    {
        string? name = args.String("name");
        if (string.IsNullOrWhiteSpace(name)) return (null, "Missing 'name' — the alarm's title (or id).");

        var all = AlarmStore.LoadAlarms();
        var matches = all.Where(a =>
            string.Equals(a.Id, name, StringComparison.OrdinalIgnoreCase)
            || string.Equals(a.Title, name, StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 1) return (matches[0], null);
        if (matches.Count > 1)
            return (null, $"Alarm '{name}' is ambiguous ({matches.Count} share that title) — use the id from alarm_list.");
        var titles = string.Join(", ", all.Select(a => a.Title)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(x => x, StringComparer.OrdinalIgnoreCase));
        return (null, $"No alarm named '{name}'." + (titles.Length > 0 ? $" Available: {titles}." : ""));
    }

    private static void LogEvent(AlarmEntry alarm, AlarmHistoryEventType type, string detail) =>
        AlarmStore.AddHistoryEntry(new AlarmHistoryEntry
        {
            AlarmId = alarm.Id,
            AlarmTitle = alarm.Title,
            EventType = type,
            Detail = detail,
        });

    /// <summary>The tool's arguments object, parsed once.</summary>
    internal sealed class Args
    {
        private readonly JsonObject? _obj;

        private Args(JsonObject? obj) { _obj = obj; }

        public static Args Parse(PipeCommand cmd)
        {
            if (string.IsNullOrWhiteSpace(cmd.PayloadJson)) return new Args(null);
            try { return new Args(JsonNode.Parse(cmd.PayloadJson) as JsonObject); }
            catch (Exception) { return new Args(null); }
        }

        public bool Has(string prop) => _obj != null && _obj.TryGetPropertyValue(prop, out var n) && n != null;

        public string? String(string prop)
        {
            try { return _obj?[prop]?.GetValue<string>(); }
            catch (Exception) { return null; }
        }

        public int? Int(string prop)
        {
            try { return _obj?[prop]?.GetValue<int>(); }
            catch (Exception) { return null; }
        }

        public bool? Bool(string prop)
        {
            var n = _obj?[prop];
            if (n == null) return null;
            try { return n.GetValue<bool>(); }
            catch (Exception)
            {
                try { return bool.TryParse(n.GetValue<string>(), out var b) ? b : null; }
                catch (Exception) { return null; }
            }
        }

        public string[]? Strings(string prop)
        {
            if (_obj?[prop] is not JsonArray arr) return null;
            try { return arr.Select(n => n!.GetValue<string>()).ToArray(); }
            catch (Exception) { return null; }
        }

        public DayOfWeek[]? Days()
        {
            var names = Strings("days");
            if (names == null) return null;
            var days = new List<DayOfWeek>();
            foreach (var n in names)
            {
                if (Enum.TryParse<DayOfWeek>(n, ignoreCase: true, out var d)) days.Add(d);
                else return null;
            }
            return days.Distinct().ToArray();
        }
    }

    private static string Schema(params (string Name, string Type, string Desc, bool Required)[] props)
    {
        var properties = new JsonObject();
        var required = new JsonArray();
        foreach (var p in props)
        {
            var prop = new JsonObject { ["type"] = p.Type, ["description"] = p.Desc };
            if (p.Type == "array") prop["items"] = new JsonObject { ["type"] = "string" };
            properties[p.Name] = prop;
            if (p.Required) required.Add(p.Name);
        }
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = required,
        }.ToJsonString();
    }

    private static string Ok(string message) =>
        JsonSerializer.Serialize(new { ok = true, message });

    private static string Fail(string message) =>
        JsonSerializer.Serialize(new { ok = false, message });
}
