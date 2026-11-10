using System.Drawing;

namespace ProdToy.Plugins.ClaudeIntegration;

enum SnoozeUnit { Minutes, Hours }

/// <summary>
/// The snooze lengths and what the snooze button says. One rule for the popup's Snooze menu, the More… dialog and the settings
/// page; <see cref="ClaudePluginSettings.SnoozeUntil"/> stays the single source of whether notifications are snoozed.
/// </summary>
static class SnoozeMath
{
    public const int MinMinutes = 1;

    /// <summary>One day.</summary>
    public const int MaxMinutes = 1440;

    /// <summary>The menu's fixed lengths, in minutes.</summary>
    public static readonly IReadOnlyList<int> Presets = new[] { 10, 20, 30, 60 };

    /// <summary>The More… dialog's quick choices, in minutes: 2 h, 4 h, 8 h, 1 day.</summary>
    public static readonly IReadOnlyList<int> QuickChoices = new[] { 120, 240, 480, 1440 };

    /// <summary>Not snoozed: the value <see cref="ClaudePluginSettings.SnoozeUntil"/> holds after Wake now.</summary>
    public static readonly DateTime Awake = DateTime.MinValue;

    public static int Clamp(int minutes) => Math.Clamp(minutes, MinMinutes, MaxMinutes);

    /// <summary>A typed length in minutes; false when it is not a whole number of minutes from 1 minute to 1 day.</summary>
    public static bool TryMinutes(decimal value, SnoozeUnit unit, out int minutes)
    {
        decimal total = unit == SnoozeUnit.Hours ? value * 60 : value;
        minutes = 0;
        if (total < MinMinutes || total > MaxMinutes || total != decimal.Truncate(total)) return false;
        minutes = (int)total;
        return true;
    }

    /// <summary>The same from the dialog's text box; a blank or non-number is refused.</summary>
    public static bool TryParse(string? text, SnoozeUnit unit, out int minutes)
    {
        minutes = 0;
        return decimal.TryParse((text ?? "").Trim(), System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.CurrentCulture, out var value)
            && TryMinutes(value, unit, out minutes);
    }

    public static DateTime Until(DateTime now, int minutes) => now.AddMinutes(Clamp(minutes));

    public static bool IsSnoozed(DateTime until, DateTime now) => until > now;

    /// <summary>Whole minutes left, rounded up; 0 when not snoozed.</summary>
    public static int MinutesLeft(DateTime until, DateTime now)
        => until > now ? (int)Math.Ceiling((until - now).TotalMinutes) : 0;

    /// <summary>A length in words: "10 min", "1 h", "1 h 30 min", "1 day".</summary>
    public static string Words(int minutes)
    {
        if (minutes >= MaxMinutes) return "1 day";
        if (minutes < 60) return $"{minutes} min";
        int h = minutes / 60, m = minutes % 60;
        return m == 0 ? $"{h} h" : $"{h} h {m} min";
    }

    /// <summary>A menu item's words for a fixed length: "10 minutes" … "60 minutes".</summary>
    public static string PresetText(int minutes) => $"{minutes} minutes";

    /// <summary>The button: "Snooze" when awake, "Snoozed 27 min" while snoozed.</summary>
    public static string ButtonText(DateTime until, DateTime now)
        => IsSnoozed(until, now) ? $"Snoozed {Words(MinutesLeft(until, now))}" : "Snooze";

    /// <summary>The menu's status line while snoozed: "Snoozed until 15:42 - Wake now"; null when awake.</summary>
    public static string? WakeText(DateTime until, DateTime now)
        => IsSnoozed(until, now) ? $"Snoozed until {until:HH:mm}{(until.Date != now.Date ? $" ({until:ddd})" : "")} - Wake now" : null;

    /// <summary>The menu's "Last: 4 h" item, once a custom length was used; null before.</summary>
    public static string? LastText(int lastMinutes)
        => lastMinutes >= MinMinutes && !Presets.Contains(lastMinutes) ? $"Last: {Words(Clamp(lastMinutes))}" : null;
}

/// <summary>
/// The popup's footer: one row - Snooze on the left of OK, OK centred, ⋯ on its right. Pure, so the row can be checked
/// without a window.
/// </summary>
static class FooterLayout
{
    public const int Gap = 10;

    public readonly record struct Result(Rectangle Snooze, Rectangle Ok, Rectangle More);

    public static Result Place(int width, int height, Size snooze, Size ok, Size more)
    {
        int okX = (width - ok.Width) / 2;
        int y(Size s) => Math.Max(0, (height - s.Height) / 2);
        var okBox = new Rectangle(okX, y(ok), ok.Width, ok.Height);
        var moreBox = new Rectangle(okBox.Right + Gap, y(more), more.Width, more.Height);
        var snoozeBox = new Rectangle(Math.Max(0, okBox.Left - Gap - snooze.Width), y(snooze), snooze.Width, snooze.Height);
        return new Result(snoozeBox, okBox, moreBox);
    }
}
