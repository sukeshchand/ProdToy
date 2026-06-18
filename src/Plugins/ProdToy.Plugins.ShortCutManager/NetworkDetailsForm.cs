using System.Drawing;
using System.Windows.Forms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

enum NetState { Pending, Done, Failed }

/// <summary>One network resource loaded by a preview pane during a navigation.</summary>
internal sealed class NetworkResource
{
    public string Id = "";
    public string Url = "";
    public string Method = "";
    public int Status;
    public string Type = "";
    public string Mime = "";
    public long Bytes;
    public bool FromCache;
    public double StartTs;
    public double EndTs;
    public NetState State;

    public double StartOffsetMs(double t0) => StartTs > 0 && t0 > 0 ? (StartTs - t0) * 1000 : 0;
    public double DurationMs => EndTs > StartTs ? (EndTs - StartTs) * 1000 : 0;
}

/// <summary>
/// Live, detailed breakdown of everything a preview pane loaded this session — one
/// row per request (status, type, size, start offset, duration, cache), refreshed
/// from the pane on a short timer. Read-only; closing it doesn't affect the pane.
/// </summary>
sealed class NetworkDetailsForm : Form
{
    private readonly PluginTheme _theme;
    private readonly ConsolidatedBrowserPane _pane;
    private readonly Label _summary;
    private readonly BufferedListView _list;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly Dictionary<string, ListViewItem> _rows = new();

    public NetworkDetailsForm(PluginTheme theme, ConsolidatedBrowserPane pane)
    {
        _theme = theme;
        _pane = pane;

        Text = "Network details";
        try { Icon = IconHelper.CreateAppIcon(theme.Primary); } catch { /* keep default */ }
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        Size = new Size(980, 620);
        MinimumSize = new Size(620, 320);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9F);

        _summary = new Label
        {
            Dock = DockStyle.Top,
            Height = 30,
            ForeColor = theme.TextPrimary,
            BackColor = theme.BgHeader,
            Font = new Font("Segoe UI Semibold", 9.5F, FontStyle.Bold),
            TextAlign = ContentAlignment.MiddleLeft,
            Padding = new Padding(10, 0, 10, 0),
            Text = "Loading…",
        };

        _list = new BufferedListView
        {
            Dock = DockStyle.Fill,
            View = View.Details,
            FullRowSelect = true,
            HideSelection = false,
            OwnerDraw = true,
            BackColor = theme.BgDark,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.None,
            Font = new Font("Segoe UI", 8.5F),
        };
        _list.Columns.Add("#", 44, HorizontalAlignment.Right);
        _list.Columns.Add("Method", 62);
        _list.Columns.Add("Status", 56);
        _list.Columns.Add("Type", 95);
        _list.Columns.Add("Size", 78, HorizontalAlignment.Right);
        _list.Columns.Add("Start", 72, HorizontalAlignment.Right);
        _list.Columns.Add("Time", 72, HorizontalAlignment.Right);
        _list.Columns.Add("Cache", 52);
        _list.Columns.Add("URL", 420);
        _list.DrawColumnHeader += DrawHeader;
        _list.DrawItem += (_, e) => { e.DrawDefault = false; };
        _list.DrawSubItem += DrawCell;

        Controls.Add(_list);
        Controls.Add(_summary);

        _timer = new System.Windows.Forms.Timer { Interval = 400 };
        _timer.Tick += (_, _) => Refresh2();
        _timer.Start();
        Refresh2();
    }

    private void Refresh2()
    {
        if (_pane.IsDisposed)
        {
            _summary.Text = "Preview closed — telemetry stopped.";
            _timer.Stop();
            return;
        }

        var snapshot = _pane.NetworkSnapshot();
        double t0 = _pane.NavStartTs;

        // A new navigation clears the pane's list (fresh request ids) — detect that
        // and rebuild rather than leaving stale rows.
        var ids = new HashSet<string>();
        foreach (var r in snapshot) ids.Add(r.Id);
        bool reset = false;
        foreach (var k in _rows.Keys) if (!ids.Contains(k)) { reset = true; break; }
        if (reset) { _list.Items.Clear(); _rows.Clear(); }

        _list.BeginUpdate();
        try
        {
            int i = 0;
            foreach (var r in snapshot)
            {
                i++;
                if (!_rows.TryGetValue(r.Id, out var item))
                {
                    item = new ListViewItem(i.ToString());
                    for (int c = 0; c < 8; c++) item.SubItems.Add("");
                    item.UseItemStyleForSubItems = true;
                    _rows[r.Id] = item;
                    _list.Items.Add(item);
                }
                item.SubItems[0].Text = i.ToString();
                item.SubItems[1].Text = r.Method;
                item.SubItems[2].Text = r.Status > 0 ? r.Status.ToString() : (r.State == NetState.Failed ? "fail" : "…");
                item.SubItems[3].Text = ShortType(r);
                item.SubItems[4].Text = r.FromCache ? "(cache)" : (r.State == NetState.Pending ? "…" : ConsolidatedBrowserPane.FormatBytes(r.Bytes));
                item.SubItems[5].Text = r.StartTs > 0 ? $"{r.StartOffsetMs(t0):0} ms" : "";
                item.SubItems[6].Text = r.State == NetState.Pending ? "…" : $"{r.DurationMs:0} ms";
                item.SubItems[7].Text = r.FromCache ? "✓" : "";
                item.SubItems[8].Text = r.Url;
                item.ForeColor = r.State == NetState.Failed ? _theme.ErrorColor
                    : r.FromCache ? _theme.TextSecondary
                    : r.State == NetState.Pending ? Color.FromArgb(0xE6, 0xA5, 0x3A)
                    : _theme.TextPrimary;
            }
        }
        finally { _list.EndUpdate(); }

        _summary.Text = _pane.SummaryText();
    }

    private static string ShortType(NetworkResource r)
    {
        if (!string.IsNullOrEmpty(r.Type)) return r.Type;
        if (!string.IsNullOrEmpty(r.Mime)) return r.Mime;
        return "";
    }

    private void DrawHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        using var bg = new SolidBrush(_theme.BgHeader);
        e.Graphics.FillRectangle(bg, e.Bounds);
        using var pen = new Pen(_theme.Border);
        e.Graphics.DrawLine(pen, e.Bounds.Left, e.Bounds.Bottom - 1, e.Bounds.Right, e.Bounds.Bottom - 1);
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | ((e.Header!.TextAlign == HorizontalAlignment.Right) ? TextFormatFlags.Right : TextFormatFlags.Left);
        using var f = new Font("Segoe UI Semibold", 8.5F, FontStyle.Bold);
        TextRenderer.DrawText(e.Graphics, e.Header.Text, f, Rectangle.Inflate(e.Bounds, -6, 0), _theme.TextSecondary, flags);
    }

    private void DrawCell(object? sender, DrawListViewSubItemEventArgs e)
    {
        bool selected = e.Item!.Selected;
        using (var bg = new SolidBrush(selected ? _theme.PrimaryDim : _theme.BgDark))
            e.Graphics.FillRectangle(bg, e.Bounds);

        var col = _list.Columns[e.ColumnIndex];
        var flags = TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis
            | (col.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        Color fg = selected ? Color.White : e.Item.ForeColor;
        TextRenderer.DrawText(e.Graphics, e.SubItem!.Text, _list.Font, Rectangle.Inflate(e.Bounds, -6, 0), fg, flags);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        _timer.Stop();
        _timer.Dispose();
        base.OnFormClosed(e);
    }
}

/// <summary>Double-buffered ListView so the realtime row updates don't flicker.</summary>
sealed class BufferedListView : ListView
{
    public BufferedListView() => DoubleBuffered = true;
}
