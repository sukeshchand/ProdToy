using System.Drawing;
using ProdToy.Plugins.ClaudeIntegration;
using Xunit;

namespace ProdToy.Tests.Claude;

public class SnoozeMathTests
{
    private static readonly DateTime Now = new(2026, 11, 10, 15, 15, 0);

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-5, 1)]
    [InlineData(30, 30)]
    [InlineData(1440, 1440)]
    [InlineData(5000, 1440)]
    public void ClampKeepsOneMinuteToOneDay(int asked, int got) => Assert.Equal(got, SnoozeMath.Clamp(asked));

    [Theory]
    [InlineData(4, true, true, 240)]
    [InlineData(24, true, true, 1440)]   // 1 day is accepted
    [InlineData(25, true, false, 0)]     // 25 h is refused
    [InlineData(1.5, true, true, 90)]
    [InlineData(1441, false, false, 0)]
    [InlineData(0, false, false, 0)]
    [InlineData(1, false, true, 1)]
    [InlineData(2.5, false, false, 0)]
    public void TypedLengths(double value, bool hours, bool ok, int minutes)
    {
        var unit = hours ? SnoozeUnit.Hours : SnoozeUnit.Minutes;
        Assert.Equal(ok, SnoozeMath.TryMinutes((decimal)value, unit, out int got));
        Assert.Equal(minutes, got);
    }

    [Fact]
    public void TextThatIsNotANumberIsRefused()
    {
        Assert.False(SnoozeMath.TryParse("", SnoozeUnit.Minutes, out _));
        Assert.False(SnoozeMath.TryParse("abc", SnoozeUnit.Hours, out _));
        Assert.True(SnoozeMath.TryParse(" 8 ", SnoozeUnit.Hours, out int m));
        Assert.Equal(480, m);
    }

    [Fact]
    public void SnoozingAndWakingUp()
    {
        var until = SnoozeMath.Until(Now, 27);

        Assert.True(SnoozeMath.IsSnoozed(until, Now));
        Assert.Equal(27, SnoozeMath.MinutesLeft(until, Now));
        Assert.Equal("Snoozed 27 min", SnoozeMath.ButtonText(until, Now));
        Assert.Equal("Snoozed until 15:42 - Wake now", SnoozeMath.WakeText(until, Now));

        // Wake now clears it.
        Assert.False(SnoozeMath.IsSnoozed(SnoozeMath.Awake, Now));
        Assert.Equal("Snooze", SnoozeMath.ButtonText(SnoozeMath.Awake, Now));
        Assert.Null(SnoozeMath.WakeText(SnoozeMath.Awake, Now));
        Assert.Equal(0, SnoozeMath.MinutesLeft(SnoozeMath.Awake, Now));
    }

    [Fact]
    public void MinutesLeftRoundUp() => Assert.Equal(1, SnoozeMath.MinutesLeft(Now.AddSeconds(10), Now));

    [Theory]
    [InlineData(10, "10 min")]
    [InlineData(60, "1 h")]
    [InlineData(90, "1 h 30 min")]
    [InlineData(240, "4 h")]
    [InlineData(1440, "1 day")]
    public void Words(int minutes, string words) => Assert.Equal(words, SnoozeMath.Words(minutes));

    [Fact]
    public void TheLastCustomLengthIsOfferedOnlyOnceUsed()
    {
        Assert.Null(SnoozeMath.LastText(0));
        Assert.Null(SnoozeMath.LastText(30));   // already a preset
        Assert.Equal("Last: 4 h", SnoozeMath.LastText(240));
        Assert.Equal(new[] { 10, 20, 30, 60 }, SnoozeMath.Presets);
    }

    [Fact]
    public void TheFooterIsOneRowWithOkCentred()
    {
        var r = FooterLayout.Place(800, 56, new Size(110, 32), new Size(130, 36), new Size(40, 32));

        Assert.Equal(400, r.Ok.Left + r.Ok.Width / 2);   // OK in the middle
        Assert.True(r.Snooze.Right <= r.Ok.Left);
        Assert.True(r.More.Left >= r.Ok.Right);
        int middle = r.Ok.Top + r.Ok.Height / 2;           // one row: every control on the same middle line
        Assert.InRange(r.Snooze.Top + r.Snooze.Height / 2, middle - 1, middle + 1);
        Assert.InRange(r.More.Top + r.More.Height / 2, middle - 1, middle + 1);
        Assert.True(r.Ok.Bottom <= 56);
    }
}
