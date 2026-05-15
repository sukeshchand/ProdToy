using System.Drawing;
using System.Drawing.Drawing2D;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Owner-drawn TabControl that paints its tab strip and page area with
/// plugin theme colors instead of the Windows system look. Mirrors the
/// host's <c>ThemedTabControl</c> in <c>SettingsForm</c>; copy lives here
/// because plugins can't reference host types directly.
/// </summary>
class ThemedTabControl : TabControl
{
    public PluginTheme Theme { get; set; }

    public ThemedTabControl(PluginTheme theme)
    {
        Theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.DoubleBuffer, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bgBrush = new SolidBrush(Theme.BgDark))
            g.FillRectangle(bgBrush, ClientRectangle);

        if (TabCount == 0) return;

        var pageRect = GetPageBounds();
        using (var borderPen = new Pen(Theme.Border, 1f))
            g.DrawRectangle(borderPen, pageRect);

        for (int i = 0; i < TabCount; i++)
        {
            var tabRect = GetTabRect(i);
            bool isActive = SelectedIndex == i;

            if (isActive)
            {
                using (var activeBrush = new SolidBrush(Theme.BgDark))
                    g.FillRectangle(activeBrush, tabRect);

                using (var accentPen = new Pen(Theme.Primary, 2.5f))
                    g.DrawLine(accentPen, tabRect.Left + 2, tabRect.Top + 1, tabRect.Right - 2, tabRect.Top + 1);

                using var borderPen = new Pen(Theme.Border, 1f);
                g.DrawLine(borderPen, tabRect.Left, tabRect.Top, tabRect.Left, tabRect.Bottom);
                g.DrawLine(borderPen, tabRect.Right, tabRect.Top, tabRect.Right, tabRect.Bottom);
                g.DrawLine(borderPen, tabRect.Left, tabRect.Top, tabRect.Right, tabRect.Top);

                using (var erasePen = new Pen(Theme.BgDark, 2f))
                    g.DrawLine(erasePen, tabRect.Left + 1, tabRect.Bottom, tabRect.Right - 1, tabRect.Bottom);
            }
            else
            {
                using (var inactiveBrush = new SolidBrush(Theme.BgHeader))
                    g.FillRectangle(inactiveBrush, tabRect.X, tabRect.Y + 2, tabRect.Width, tabRect.Height - 2);

                using var borderPen = new Pen(Theme.Border, 1f);
                g.DrawLine(borderPen, tabRect.Left, tabRect.Bottom, tabRect.Right, tabRect.Bottom);
            }

            var textColor = isActive ? Theme.TextPrimary : Theme.TextSecondary;
            using var textBrush = new SolidBrush(textColor);
            using var sf = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center };
            g.DrawString(TabPages[i].Text, Font, textBrush, tabRect, sf);
        }
    }

    private Rectangle GetPageBounds()
    {
        if (TabCount == 0) return ClientRectangle;
        var firstTab = GetTabRect(0);
        int tabStripHeight = firstTab.Bottom;
        return new Rectangle(0, tabStripHeight, Width - 1, Height - tabStripHeight - 1);
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }

    protected override void OnPaintBackground(PaintEventArgs pevent) { }
}
