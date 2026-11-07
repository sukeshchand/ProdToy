using System.Drawing;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>
/// Where the info bar's controls go: the pager from the right edge, the scope chip left of it, the subtitle stopping before both.
/// Pure: it reads only what it is given, never a control's Visible (that reads false while the window is hidden), so a layout
/// pass on a hidden window places the same bounds as one on a shown window.
/// </summary>
static class InfoBarLayout
{
    public const int EdgeGap = 12;
    public const int ButtonGap = 4;
    public const int CounterGap = 8;
    public const int ChipGap = 10;
    public const int HeadingGap = 12;
    public const int SubtitleGap = 8;

    /// <param name="Width">The bar's client width.</param>
    /// <param name="BarHeight">The bar's height; the pager and the chip are centred in it.</param>
    /// <param name="HeadingRight">The right edge of the heading (title or subtitle, the wider): the chip never reaches it.</param>
    /// <param name="Button">Previous / Next.</param>
    /// <param name="EndButton">First / Last.</param>
    /// <param name="Counter">The counter at its widest text (count / count): the buttons never move as the position changes.</param>
    /// <param name="Chip">The scope chip; Size.Empty = no chip.</param>
    /// <param name="ShowPager">More than one item in the scope.</param>
    public readonly record struct Input(int Width, int BarHeight, int HeadingRight, Size Button, Size EndButton, Size Counter, Size Chip, bool ShowPager);

    public readonly record struct Result(
        bool ShowPager, Rectangle First, Rectangle Previous, Rectangle Counter, Rectangle Next, Rectangle Last,
        bool ShowChip, Rectangle Chip, int SubtitleRight);

    public static Result Place(Input i)
    {
        int x = i.Width - EdgeGap;
        Rectangle first = default, previous = default, counter = default, next = default, last = default;
        if (i.ShowPager)
        {
            int y = (i.BarHeight - i.Button.Height) / 2;
            x -= i.EndButton.Width;
            last = new Rectangle(x, (i.BarHeight - i.EndButton.Height) / 2, i.EndButton.Width, i.EndButton.Height);
            x -= ButtonGap + i.Button.Width;
            next = new Rectangle(x, y, i.Button.Width, i.Button.Height);
            x -= CounterGap + i.Counter.Width;
            counter = new Rectangle(x, (i.BarHeight - i.Counter.Height) / 2, i.Counter.Width, i.Counter.Height);
            x -= CounterGap + i.Button.Width;
            previous = new Rectangle(x, y, i.Button.Width, i.Button.Height);
            x -= ButtonGap + i.EndButton.Width;
            first = new Rectangle(x, (i.BarHeight - i.EndButton.Height) / 2, i.EndButton.Width, i.EndButton.Height);
            x -= ChipGap;
        }

        // The chip sits left of the pager and gives way before it reaches the heading (or the bar's left edge).
        int chipLeft = x - i.Chip.Width;
        bool showChip = i.Chip.Width > 0 && chipLeft >= Math.Max(0, i.HeadingRight + HeadingGap);
        var chip = showChip ? new Rectangle(chipLeft, (i.BarHeight - i.Chip.Height) / 2, i.Chip.Width, i.Chip.Height) : Rectangle.Empty;

        int stop = showChip ? chipLeft : i.ShowPager ? first.Left : i.Width - EdgeGap;
        return new Result(i.ShowPager, first, previous, counter, next, last, showChip, chip, stop - SubtitleGap);
    }
}

/// <summary>
/// The pager's numbering: the scope's list is latest first (index 0 = the newest), the pager runs oldest to newest - the oldest
/// is position 1, the newest is position n. The list itself is never copied or reversed.
/// </summary>
static class PagerPosition
{
    /// <summary>The shown position, 1 = the oldest, count = the newest; 0 when the item is not in the scope.</summary>
    public static int Shown(int index, int count) => index < 0 || index >= count ? 0 : count - index;

    public static int Oldest(int count) => count - 1;

    public static int Newest(int count) => 0;

    /// <summary>Previous: one older; stays on the oldest.</summary>
    public static int Older(int index, int count) => Math.Clamp(index + 1, 0, count - 1);

    /// <summary>Next: one newer; stays on the newest. An item not in the scope goes to the newest.</summary>
    public static int Newer(int index, int count) => index < 0 ? 0 : Math.Clamp(index - 1, 0, count - 1);

    /// <summary>First and Previous: something older exists.</summary>
    public static bool CanOlder(int index, int count) => index < count - 1;

    /// <summary>Next and Last: something newer exists.</summary>
    public static bool CanNewer(int index, int count) => index != 0 && count > 0;
}
