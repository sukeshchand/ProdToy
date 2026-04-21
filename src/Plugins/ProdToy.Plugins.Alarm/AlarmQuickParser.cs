using System.Globalization;
using System.Text.RegularExpressions;

namespace ProdToy.Plugins.Alarm;

/// <summary>
/// Best-effort natural-language parser for quick-add. Understands a small, predictable
/// grammar — not a full date parser. Returns null when input is ambiguous; callers fall
/// back to the inline editor.
/// Supported:
///   "in 15 minutes" / "in 2 hours" / "in 1h 30m"
///   "tomorrow at 8:30" / "tomorrow 8am"
///   "today at 17:00"
///   "at 9am" / "at 14:30"                     → once, today if future else tomorrow
///   "every day at 7"
///   "every weekday at 9am" / "weekdays at 11:00"
///   "every weekend at 10"
///   "every monday at 8" / "monday,wednesday at 7:30"
///   "every 30 minutes"
/// The trailing "<text>" (title) is everything after a leading "to " or the whole
/// remainder if no time fragment was consumed.
/// </summary>
static class AlarmQuickParser
{
    public static AlarmEntry? TryParse(string input, string? fallbackTitle = null)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;
        var text = input.Trim();
        var lower = text.ToLowerInvariant();

        // "in N minutes|hours"
        var rel = Regex.Match(lower, @"\bin\s+(\d+)\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)\b(?:\s+and\s+(\d+)\s*(m|min|mins|minute|minutes))?");
        if (rel.Success)
        {
            int n = int.Parse(rel.Groups[1].Value, CultureInfo.InvariantCulture);
            bool isHour = rel.Groups[2].Value.StartsWith("h");
            var minutes = isHour ? n * 60 : n;
            if (rel.Groups[3].Success)
                minutes += int.Parse(rel.Groups[3].Value, CultureInfo.InvariantCulture);
            if (minutes <= 0) return null;

            var fire = DateTime.Now.AddMinutes(minutes);
            string title = ExtractTitle(text, rel.Index, rel.Length, fallbackTitle)
                ?? $"Timer ({minutes} min)";
            return BuildOnce(title, fire);
        }

        // "every N minutes" → Interval
        var interval = Regex.Match(lower, @"\bevery\s+(\d+)\s*(m|min|mins|minute|minutes|h|hr|hrs|hour|hours)\b");
        if (interval.Success)
        {
            int n = int.Parse(interval.Groups[1].Value, CultureInfo.InvariantCulture);
            bool isHour = interval.Groups[2].Value.StartsWith("h");
            int mins = isHour ? n * 60 : n;
            if (mins <= 0) return null;
            string title = ExtractTitle(text, interval.Index, interval.Length, fallbackTitle)
                ?? $"Every {mins} min";
            return new AlarmEntry
            {
                Title = title,
                Schedule = new AlarmSchedule
                {
                    Type = AlarmScheduleType.Interval,
                    IntervalMinutes = mins,
                },
            };
        }

        // Pull a time-of-day fragment.
        var timeMatch = Regex.Match(lower, @"(?:\bat\s+)?(\d{1,2})(?::(\d{2}))?\s*(am|pm)?\b");
        TimeSpan? time = null;
        int timeStart = -1, timeLen = 0;
        if (timeMatch.Success)
        {
            int hour = int.Parse(timeMatch.Groups[1].Value, CultureInfo.InvariantCulture);
            int min = timeMatch.Groups[2].Success
                ? int.Parse(timeMatch.Groups[2].Value, CultureInfo.InvariantCulture) : 0;
            string ampm = timeMatch.Groups[3].Value;

            // Guard against matching bare numbers that aren't really a time.
            bool hasAnchor = lower.Contains(" at ") || lower.StartsWith("at ")
                || timeMatch.Groups[2].Success || !string.IsNullOrEmpty(ampm);
            if (hasAnchor && hour <= 23 && min <= 59)
            {
                if (ampm == "pm" && hour < 12) hour += 12;
                else if (ampm == "am" && hour == 12) hour = 0;
                time = new TimeSpan(hour, min, 0);
                timeStart = timeMatch.Index;
                timeLen = timeMatch.Length;
            }
        }

        // Day/date anchor
        DateTime? onDate = null;
        AlarmScheduleType? repeatType = null;
        List<DayOfWeek>? repeatDays = null;

        if (Regex.IsMatch(lower, @"\btomorrow\b"))
            onDate = DateTime.Today.AddDays(1);
        else if (Regex.IsMatch(lower, @"\btoday\b"))
            onDate = DateTime.Today;

        if (Regex.IsMatch(lower, @"\bevery\s+day\b") || Regex.IsMatch(lower, @"\bdaily\b"))
            repeatType = AlarmScheduleType.Daily;
        else if (Regex.IsMatch(lower, @"\b(every\s+)?weekday(s)?\b"))
            repeatType = AlarmScheduleType.Weekdays;
        else if (Regex.IsMatch(lower, @"\b(every\s+)?weekend(s)?\b"))
            repeatType = AlarmScheduleType.Weekend;
        else
        {
            var days = ParseDays(lower);
            if (days.Count > 0)
            {
                repeatDays = days;
                repeatType = days.Count == 1 ? AlarmScheduleType.Weekly : AlarmScheduleType.Custom;
            }
        }

        if (time == null) return null;

        string inferredTitle = ExtractTitle(text, timeStart, timeLen, fallbackTitle)
            ?? fallbackTitle
            ?? "Alarm";

        if (repeatType is AlarmScheduleType rt)
        {
            return new AlarmEntry
            {
                Title = inferredTitle,
                Schedule = new AlarmSchedule
                {
                    Type = rt,
                    TimeOfDay = time.Value.ToString(@"hh\:mm"),
                    CustomDays = repeatDays?.ToArray(),
                },
            };
        }

        // One-time: use onDate if set, else today-if-future-else-tomorrow.
        var dateOnly = onDate ?? DateTime.Today;
        var fireAt = dateOnly + time.Value;
        if (onDate == null && fireAt <= DateTime.Now) fireAt = fireAt.AddDays(1);
        return BuildOnce(inferredTitle, fireAt);
    }

    private static AlarmEntry BuildOnce(string title, DateTime when) => new()
    {
        Title = title,
        Schedule = new AlarmSchedule
        {
            Type = AlarmScheduleType.Once,
            TimeOfDay = when.ToString("HH:mm"),
            OneTimeDate = when.ToString("yyyy-MM-dd"),
        },
    };

    private static List<DayOfWeek> ParseDays(string lower)
    {
        var result = new List<DayOfWeek>();
        var names = new (string Key, DayOfWeek Day)[]
        {
            ("sunday", DayOfWeek.Sunday), ("sun", DayOfWeek.Sunday),
            ("monday", DayOfWeek.Monday), ("mon", DayOfWeek.Monday),
            ("tuesday", DayOfWeek.Tuesday), ("tue", DayOfWeek.Tuesday), ("tues", DayOfWeek.Tuesday),
            ("wednesday", DayOfWeek.Wednesday), ("wed", DayOfWeek.Wednesday),
            ("thursday", DayOfWeek.Thursday), ("thu", DayOfWeek.Thursday), ("thur", DayOfWeek.Thursday), ("thurs", DayOfWeek.Thursday),
            ("friday", DayOfWeek.Friday), ("fri", DayOfWeek.Friday),
            ("saturday", DayOfWeek.Saturday), ("sat", DayOfWeek.Saturday),
        };
        foreach (var (key, day) in names)
        {
            if (Regex.IsMatch(lower, $@"\b{key}\b") && !result.Contains(day))
                result.Add(day);
        }
        return result;
    }

    private static string? ExtractTitle(string original, int consumedStart, int consumedLen, string? fallback)
    {
        if (consumedStart < 0) return Clean(original) ?? fallback;

        var before = original[..consumedStart].Trim();
        var after = consumedStart + consumedLen < original.Length
            ? original[(consumedStart + consumedLen)..].Trim()
            : "";

        var combined = string.IsNullOrWhiteSpace(before) ? after : before;
        return Clean(combined) ?? fallback;
    }

    private static string? Clean(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return null;
        // Strip common leading connectors and trailing punctuation.
        var cleaned = Regex.Replace(s.Trim(), @"^(to|for|about|\-|—|:)\s+", "", RegexOptions.IgnoreCase).Trim();
        cleaned = cleaned.TrimEnd('.', ',', ';', ':').Trim();
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
    }
}
