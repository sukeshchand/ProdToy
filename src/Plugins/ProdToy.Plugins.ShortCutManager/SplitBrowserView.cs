using System.Drawing;
using System.Windows.Forms;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// One preview "tab" that can hold 1..5 browser panes side by side — each a full
/// <see cref="ConsolidatedBrowserPane"/> (its own address bar + nav controls + a ✕
/// close button), separated by mouse-draggable splitters.
/// </summary>
/// <remarks>
/// Implemented as a fixed skeleton of nested <see cref="SplitContainer"/>s. Each
/// pane lives in a fixed slot panel; splitting/closing just collapses or expands
/// the relevant Panel1/Panel2 (closing a pane collapses its own slot). Panes are
/// never reparented — that matters because moving a WebView2 to a new parent
/// blanks/recreates it. Any pane (even a middle one) can be closed; the slot is
/// reused by the next split.
/// </remarks>
sealed class SplitBrowserView : UserControl
{
    public const int MaxPanes = 5;

    private readonly PluginTheme _theme;
    private readonly SplitContainer[] _splits = new SplitContainer[MaxPanes - 1];   // 4 nested
    private readonly Panel[] _slots = new Panel[MaxPanes];                          // 5 slots
    private readonly ConsolidatedBrowserPane?[] _panes = new ConsolidatedBrowserPane[MaxPanes];
    private readonly bool[] _active = new bool[MaxPanes];

    public SplitBrowserView(PluginTheme theme, string url)
    {
        _theme = theme;
        BackColor = theme.BgDark;
        BuildSkeleton();

        ShowPane(0, string.IsNullOrWhiteSpace(url) ? "about:blank" : url);
        ApplyLayout();
    }

    private int ActiveCount
    {
        get { int c = 0; foreach (var a in _active) if (a) c++; return c; }
    }

    public bool CanSplit => ActiveCount < MaxPanes;

    private ConsolidatedBrowserPane NewPane(int slot)
    {
        var p = new ConsolidatedBrowserPane(_theme) { Dock = DockStyle.Fill };
        p.CloseRequested += () => ClosePane(slot);
        return p;
    }

    private void BuildSkeleton()
    {
        for (int i = 0; i < _splits.Length; i++)
            _splits[i] = new SplitContainer
            {
                Dock = DockStyle.Fill,
                Orientation = Orientation.Vertical,   // vertical splitter → left | right
                SplitterWidth = 6,
                BackColor = _theme.Border,
            };
        for (int i = 0; i < _slots.Length; i++)
            _slots[i] = new Panel { Dock = DockStyle.Fill, BackColor = _theme.BgDark };

        // SC0.Panel1=slot0, SC0.Panel2=SC1, … SC3.Panel1=slot3, SC3.Panel2=slot4.
        for (int i = 0; i < _splits.Length; i++)
        {
            _splits[i].Panel1.Controls.Add(_slots[i]);
            if (i < _splits.Length - 1)
                _splits[i].Panel2.Controls.Add(_splits[i + 1]);
            else
                _splits[i].Panel2.Controls.Add(_slots[i + 1]);
        }
        Controls.Add(_splits[0]);
    }

    private void ShowPane(int slot, string url)
    {
        if (_panes[slot] == null)
        {
            _panes[slot] = NewPane(slot);
            _slots[slot].Controls.Add(_panes[slot]);
            _ = _panes[slot]!.NavigateAsync(url);
        }
        _active[slot] = true;
    }

    /// <summary>Add a browser pane (split), up to <see cref="MaxPanes"/>. The new
    /// pane opens to <paramref name="url"/> (blank by default) so the user can type
    /// any URL into its own address bar. Fills the lowest free slot.</summary>
    public bool AddPane(string url = "about:blank")
    {
        if (ActiveCount >= MaxPanes) return false;
        int slot = Array.IndexOf(_active, false);
        if (slot < 0) return false;
        ShowPane(slot, string.IsNullOrWhiteSpace(url) ? "about:blank" : url);
        ApplyLayout();
        return true;
    }

    /// <summary>Close a specific pane (the ✕ on its toolbar). The last remaining
    /// pane can't be closed. The freed slot is reused by the next split.</summary>
    public void ClosePane(int slot)
    {
        if (slot < 0 || slot >= MaxPanes || !_active[slot]) return;
        if (ActiveCount <= 1) return;   // keep at least one pane
        _active[slot] = false;
        if (_panes[slot] != null)
        {
            try { _panes[slot]!.Dispose(); } catch { }
            _slots[slot].Controls.Clear();
            _panes[slot] = null;
        }
        ApplyLayout();
    }

    private bool AnyActiveAfter(int i)
    {
        for (int j = i + 1; j < MaxPanes; j++) if (_active[j]) return true;
        return false;
    }

    private int CountActiveAfter(int i)
    {
        int c = 0;
        for (int j = i + 1; j < MaxPanes; j++) if (_active[j]) c++;
        return c;
    }

    private void ApplyLayout()
    {
        // Collapse each split's slot side when that slot is closed, and its tail
        // side when nothing after it is active.
        for (int i = 0; i < _splits.Length; i++)
        {
            var sc = _splits[i];
            bool p1Collapse = !_active[i];                 // slot i closed
            bool p2Collapse = !AnyActiveAfter(i);          // nothing after i
            if (p1Collapse && p2Collapse) p2Collapse = false;   // can't collapse both; SC is hidden by an ancestor anyway

            // Set the panel we want VISIBLE first so we never momentarily request
            // both collapsed (which SplitContainer rejects).
            if (!p1Collapse) sc.Panel1Collapsed = false;
            if (!p2Collapse) sc.Panel2Collapsed = false;
            if (p1Collapse) sc.Panel1Collapsed = true;
            else if (p2Collapse) sc.Panel2Collapsed = true;
        }

        // Each pane shows its ✕ only when there's more than one pane.
        bool showClose = ActiveCount > 1;
        for (int i = 0; i < MaxPanes; i++)
            if (_active[i]) _panes[i]?.SetCloseButtonVisible(showClose);

        if (IsHandleCreated) BeginInvoke(new Action(RelayoutEven));
    }

    /// <summary>Give every visible pane an equal width. For each split that shows
    /// both sides, distance = width / panes-in-it cascades to an even distribution.
    /// Only runs when the split set changes — user drags are preserved afterward.</summary>
    private void RelayoutEven()
    {
        for (int i = 0; i < _splits.Length; i++)
        {
            var sc = _splits[i];
            if (sc.Panel1Collapsed || sc.Panel2Collapsed) continue;   // no visible splitter here
            int w = sc.Width;
            if (w <= 0) continue;
            int panes = 1 + CountActiveAfter(i);   // slot i + the active panes in Panel2
            int dist = w / panes;
            int min = sc.Panel1MinSize;
            int max = w - sc.Panel2MinSize - sc.SplitterWidth;
            if (max <= min) continue;
            try { sc.SplitterDistance = Math.Min(Math.Max(dist, min), max); } catch { /* mid-layout */ }
        }
    }
}
