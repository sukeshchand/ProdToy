using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ClaudeIntegration;

/// <summary>Search the tags, tick the ones a node carries, make a new one (name + colour) or delete one for good.</summary>
sealed class TagDialog : Form
{
    private static readonly string[] Palette = ["#e57373", "#ffb74d", "#fff176", "#81c784", "#4dd0e1", "#64b5f6", "#b39ddb", "#f48fb1", "#a1887f", "#90a4ae"];

    private readonly TagStore _store;
    private readonly HashSet<string> _chosen;
    private readonly TextBox _search;
    private readonly CheckedListBox _list;
    private readonly Panel _swatches;
    private readonly PluginTheme _theme;
    private string _color = Palette[5];
    private bool _filling;

    /// <summary>The tags ticked when OK was pressed.</summary>
    public IReadOnlyCollection<string> Chosen => _chosen;

    public TagDialog(PluginTheme theme, TagStore store, IEnumerable<string> chosen, string title)
    {
        _theme = theme;
        _store = store;
        _chosen = new HashSet<string>(chosen);

        Text = title.Length > 60 ? title[..60] : title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(340, 380);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9f);

        _search = new TextBox
        {
            PlaceholderText = "Find a tag, or name a new one",
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Location = new Point(12, 12),
            Width = 316,
        };
        _search.TextChanged += (_, _) => Fill();

        _swatches = new Panel { Location = new Point(12, 42), Size = new Size(316, 26), BackColor = theme.BgDark };
        _swatches.Paint += PaintSwatches;
        _swatches.MouseDown += (_, e) =>
        {
            int at = e.X / 30;
            if (at < 0 || at >= Palette.Length) return;
            _color = Palette[at];
            _swatches.Invalidate();
        };

        var create = new Button { Text = "Create tag", Location = new Point(12, 74), Size = new Size(100, 26), FlatStyle = FlatStyle.Flat, ForeColor = theme.TextPrimary };
        create.Click += (_, _) => CreateTag();

        _list = new CheckedListBox
        {
            Location = new Point(12, 108),
            Size = new Size(316, 216),
            BorderStyle = BorderStyle.FixedSingle,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            CheckOnClick = true,
            IntegralHeight = false,
            DisplayMember = nameof(TreeTag.Name),
        };
        _list.ItemCheck += (_, e) =>
        {
            if (_filling || _list.Items[e.Index] is not TreeTag tag) return;
            if (e.NewValue == CheckState.Checked) _chosen.Add(tag.Id);
            else _chosen.Remove(tag.Id);
        };

        var delete = new Button { Text = "Delete tag", Location = new Point(12, 338), Size = new Size(100, 28), FlatStyle = FlatStyle.Flat, ForeColor = theme.TextSecondary };
        delete.Click += (_, _) => DeleteTag();

        var ok = new Button { Text = "OK", DialogResult = DialogResult.OK, Location = new Point(166, 338), Size = new Size(76, 28), FlatStyle = FlatStyle.Flat, ForeColor = theme.TextPrimary };
        var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Location = new Point(252, 338), Size = new Size(76, 28), FlatStyle = FlatStyle.Flat, ForeColor = theme.TextPrimary };
        AcceptButton = ok;
        CancelButton = cancel;

        Controls.AddRange(new Control[] { _search, _swatches, create, _list, delete, ok, cancel });
        Fill();
    }

    private void Fill()
    {
        _filling = true;
        string find = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var tag in _store.All.Where(t => find.Length == 0 || t.Name.Contains(find, StringComparison.CurrentCultureIgnoreCase)))
            _list.Items.Add(tag, _chosen.Contains(tag.Id));
        _filling = false;
    }

    private void CreateTag()
    {
        string name = _search.Text.Trim();
        if (name.Length == 0) return;

        var tag = _store.Create(name.Length > 40 ? name[..40] : name, _color);
        _chosen.Add(tag.Id);
        _search.Clear();
        Fill();
    }

    private void DeleteTag()
    {
        if (_list.SelectedItem is not TreeTag tag) return;
        if (MessageBox.Show(this, $"Delete the tag \"{tag.Name}\" from everything it is on?", "Delete tag", MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
            return;

        _store.Delete(tag.Id);
        _chosen.Remove(tag.Id);
        Fill();
    }

    private void PaintSwatches(object? sender, PaintEventArgs e)
    {
        for (int i = 0; i < Palette.Length; i++)
        {
            var box = new Rectangle(i * 30 + 2, 3, 20, 20);
            using var brush = new SolidBrush(ColorTranslator.FromHtml(Palette[i]));
            e.Graphics.FillRectangle(brush, box);
            if (Palette[i] != _color) continue;

            using var pen = new Pen(_theme.TextPrimary, 2);
            e.Graphics.DrawRectangle(pen, box);
        }
    }
}

/// <summary>The tags of the item shown, as small coloured chips in the right pane's header.</summary>
sealed class TagChipStrip : Control
{
    private const int Gap = 4;
    private IReadOnlyList<TreeTag> _tags = Array.Empty<TreeTag>();

    public TagChipStrip()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint, true);
        Font = new Font("Segoe UI", 7.5f);
    }

    public void SetTags(IReadOnlyList<TreeTag> tags)
    {
        _tags = tags;
        Width = tags.Sum(t => ChipWidth(t) + Gap);
        Visible = tags.Count > 0;
        Invalidate();
    }

    private int ChipWidth(TreeTag tag) => TextRenderer.MeasureText(tag.Name, Font, Size.Empty, TextFormatFlags.NoPadding).Width + 10;

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? BackColor);
        int x = 0;
        foreach (var tag in _tags)
        {
            int width = ChipWidth(tag);
            TagChip.Draw(e.Graphics, tag, Font, new Rectangle(x, 0, width, Height));
            x += width + Gap;
        }
    }
}

/// <summary>One coloured chip: the tag's colour behind dark or light words, whichever reads.</summary>
static class TagChip
{
    public static Color ColorOf(TreeTag tag)
    {
        try
        {
            return ColorTranslator.FromHtml(tag.Color);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException)
        {
            return Color.Gray;
        }
    }

    public static void Draw(Graphics g, TreeTag tag, Font font, Rectangle box)
    {
        var color = ColorOf(tag);
        using var brush = new SolidBrush(color);
        g.FillRectangle(brush, box);
        var words = color.GetBrightness() > 0.55f ? Color.Black : Color.White;
        TextRenderer.DrawText(g, tag.Name, font, box, words,
            TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
    }
}
