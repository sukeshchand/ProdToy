using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Flicker-free owner-drawn <see cref="TabControl"/>. Uses full <c>UserPaint</c>
/// (like the host's ThemedTabControl) rather than <c>DrawMode.OwnerDrawFixed</c> +
/// <c>DrawItem</c> — the latter is painted by the OS outside .NET's double-buffer,
/// so it flickers no matter what styles you set. The whole strip is painted in
/// one buffered <see cref="OnPaint"/>; each tab is rendered via <see cref="PaintTab"/>.
/// </summary>
sealed class OwnerDrawTabControl : TabControl
{
    private readonly PluginTheme _theme;

    /// <summary>Per-tab painter: (graphics, tabIndex, tabBounds, isSelected).</summary>
    public Action<Graphics, int, Rectangle, bool>? PaintTab;

    public OwnerDrawTabControl(PluginTheme theme)
    {
        _theme = theme;
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
            | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaintBackground(PaintEventArgs e) { /* painted in OnPaint */ }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.ClearTypeGridFit;

        using (var bg = new SolidBrush(_theme.BgDark))
            g.FillRectangle(bg, ClientRectangle);

        for (int i = 0; i < TabCount; i++)
        {
            Rectangle r;
            try { r = GetTabRect(i); } catch { continue; }
            PaintTab?.Invoke(g, i, r, SelectedIndex == i);
        }
    }

    protected override void OnSelectedIndexChanged(EventArgs e)
    {
        base.OnSelectedIndexChanged(e);
        Invalidate();
    }
}

/// <summary>Double-buffered <see cref="FlowLayoutPanel"/> for the shortcut-row
/// list, so row repaints don't flash the panel background.</summary>
sealed class BufferedFlowPanel : FlowLayoutPanel
{
    public BufferedFlowPanel() => DoubleBuffered = true;
}

/// <summary>
/// Tabbed console viewer for the Consolidated Launcher.
///
/// Outer tabs: one per shortcut (plus a "Launcher" tab for lifecycle/error
/// banners). Each outer tab holds an inner TabControl with two sub-tabs:
///   • Live   — every line the process emits, as it emits it.
///   • Filter — only lines matching the per-tab filter pattern (when enabled).
///
/// Every line that flows in is also appended to a per-tab <see cref="LogFileStore"/>
/// — the file is the session-scoped source of truth. Clear empties only the
/// visible RTB; Reload re-reads the file from scratch. This pair gives users
/// "trim my view" without losing history.
///
/// File-backed history is wiped at host start (<see cref="ShortCutManagerPlugin"/>),
/// so cross-session retention is not in scope.
/// </summary>
sealed class ConsolidatedLogTabs : UserControl
{
    public const string LauncherTabKey = "__launcher__";
    private const int LineCap = 5000;
    private const int LineTrimChunk = 500;
    private const int FilterDebounceMs = 200;

    private readonly PluginTheme _theme;
    private readonly Color _stdoutColor;
    private readonly Color _stderrColor;

    /// <summary>Session id — disambiguates per-tab log files when two
    /// consolidated-launcher windows are open for the same folder. Wired
    /// into the file name via <see cref="LogFileStore"/>.</summary>
    private readonly string _sessionId = Guid.NewGuid().ToString("N");

    private readonly OwnerDrawTabControl _tabs;
    private readonly Dictionary<string, TabContent> _contentByKey =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly Panel _toolbar;
    private readonly Button _autoScrollBtn;
    private readonly ContextMenuStrip _tabContextMenu;
    private TabPage? _contextTab;

    /// <summary>Compiled snapshot of the current highlight rules. Rebuilt
    /// when <see cref="ConsolidatedSettings.HighlightRulesChanged"/> fires.
    /// Snapshot is read on the hot path; never mutated after assignment.</summary>
    private CompiledHighlight[] _highlights = Array.Empty<CompiledHighlight>();

    /// <summary>Fired when the user picks Restart from a tab's right-click
    /// menu. Argument is the tab key (shortcut id). Launcher tab hides this.</summary>
    public event Action<string>? RestartRequested;

    public ConsolidatedLogTabs(PluginTheme theme)
    {
        _theme = theme;
        _stdoutColor = theme.TextPrimary;
        _stderrColor = theme.ErrorColor;
        BackColor = theme.BgDark;

        // Tabs fill — toolbar docks above. Order matters: Fill child first so
        // the docked Top child reserves space above it instead of being
        // painted under it (same rule as the launcher panel).
        _tabs = new OwnerDrawTabControl(theme)
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            ItemSize = new Size(120, 24),
            SizeMode = TabSizeMode.Fixed,
        };
        _tabs.PaintTab = DrawTabHeader;
        _tabs.MouseUp += OnTabsMouseUp;
        _tabs.SelectedIndexChanged += (_, _) => RefreshAutoScrollButton();
        Controls.Add(_tabs);

        _toolbar = BuildToolbar(out _autoScrollBtn);
        Controls.Add(_toolbar);

        _tabContextMenu = BuildContextMenu();

        _highlights = LogHighlightCompiler.Compile(ConsolidatedSettings.GetHighlightRules());
        ConsolidatedSettings.HighlightRulesChanged += OnHighlightRulesChanged;

        AddOrGetTab(LauncherTabKey, "Launcher", theme.TextSecondary, canRestart: false);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            ConsolidatedSettings.HighlightRulesChanged -= OnHighlightRulesChanged;
            foreach (var c in _contentByKey.Values)
                c.Dispose();
            _contentByKey.Clear();
        }
        base.Dispose(disposing);
    }

    private void OnHighlightRulesChanged()
    {
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(new Action(OnHighlightRulesChanged)); return; }
        _highlights = LogHighlightCompiler.Compile(ConsolidatedSettings.GetHighlightRules());
    }

    // ---- toolbar -------------------------------------------------------------------

    private Panel BuildToolbar(out Button autoScrollBtn)
    {
        var bar = new Panel
        {
            Dock = DockStyle.Top,
            Height = 28,
            BackColor = _theme.BgHeader,
        };

        autoScrollBtn = MakeToolbarBtn("⏸ Auto-scroll: ON", 4, ToggleAutoScrollForActive);
        bar.Controls.Add(autoScrollBtn);

        var clearBtn = MakeToolbarBtn("🧹 Clear", 158, ClearActive);
        bar.Controls.Add(clearBtn);

        var reloadBtn = MakeToolbarBtn("🔄 Reload", 240, ReloadActive);
        bar.Controls.Add(reloadBtn);

        var rulesBtn = MakeToolbarBtn("🎨 Highlight rules…", 322, OpenRulesEditor);
        bar.Controls.Add(rulesBtn);

        return bar;
    }

    private Button MakeToolbarBtn(string text, int x, Action onClick)
    {
        var b = new Button
        {
            Text = text,
            Font = new Font("Segoe UI", 8.5f),
            Size = new Size(text.Length > 14 ? 150 : 78, 22),
            Location = new Point(x, 3),
            FlatStyle = FlatStyle.Flat,
            BackColor = _theme.PrimaryDim,
            ForeColor = _theme.TextPrimary,
            Cursor = Cursors.Hand,
            TabStop = false,
        };
        b.FlatAppearance.BorderSize = 0;
        b.Click += (_, _) => onClick();
        return b;
    }

    private void OpenRulesEditor()
    {
        // Try/catch surfaces any constructor failure as a MessageBox instead
        // of being swallowed by the button's click pump (where a thrown
        // exception just disappears with no UI feedback).
        try
        {
            using var dlg = new LogHighlightRulesForm(_theme, ConsolidatedSettings.GetHighlightRules());
            dlg.StartPosition = FormStartPosition.CenterParent;
            var owner = FindForm();
            var result = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog();
            if (result == DialogResult.OK && dlg.Result != null)
                ConsolidatedSettings.SetHighlightRules(dlg.Result);
        }
        catch (Exception ex)
        {
            MessageBox.Show(FindForm(),
                $"Couldn't open highlight rules:\n\n{ex.GetType().Name}: {ex.Message}",
                "Highlight rules", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }

    private void ToggleAutoScrollForActive()
    {
        var (content, isFilter) = ResolveActive();
        if (content == null) return;
        if (isFilter) content.AutoScrollFilter = !content.AutoScrollFilter;
        else content.AutoScrollLive = !content.AutoScrollLive;
        RefreshAutoScrollButton();
        if (((isFilter && content.AutoScrollFilter) || (!isFilter && content.AutoScrollLive)))
        {
            var rtb = isFilter ? content.FilterRtb : content.LiveRtb;
            if (!rtb.IsDisposed)
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.ScrollToCaret();
            }
        }
    }

    private void RefreshAutoScrollButton()
    {
        var (content, isFilter) = ResolveActive();
        bool on = content != null && (isFilter ? content.AutoScrollFilter : content.AutoScrollLive);
        _autoScrollBtn.Text = on ? "⏸ Auto-scroll: ON" : "▶ Auto-scroll: PAUSED";
        _autoScrollBtn.BackColor = on ? _theme.PrimaryDim : _theme.ErrorBg;
    }

    private void ClearActive()
    {
        var (content, isFilter) = ResolveActive();
        if (content == null) return;
        if (isFilter)
        {
            content.FilterRtb.Clear();
            content.FilterLineCount = 0;
        }
        else
        {
            content.LiveRtb.Clear();
            content.LiveLineCount = 0;
        }
    }

    private void ReloadActive()
    {
        var (content, isFilter) = ResolveActive();
        if (content == null) return;
        if (isFilter) RebuildFilterFromFile(content);
        else RebuildLiveFromFile(content);
    }

    // ---- tab construction ----------------------------------------------------------

    /// <summary>Currently selected outer tab's key (shortcut id), or null
    /// when no tab is selected. Inner sub-tab state is NOT considered here —
    /// see <see cref="ResolveActive"/> for the sub-tab.</summary>
    public string? SelectedKey
    {
        get
        {
            if (_tabs.SelectedTab is not { } page) return null;
            foreach (var kv in _contentByKey)
                if (kv.Value.OuterPage == page) return kv.Key;
            return null;
        }
    }

    /// <summary>Resolve the currently active (TabContent, isFilterSubTab)
    /// pair from the outer + inner tab selections.</summary>
    private (TabContent? content, bool isFilter) ResolveActive()
    {
        var key = SelectedKey;
        if (key == null || !_contentByKey.TryGetValue(key, out var c)) return (null, false);
        bool isFilter = c.InnerTabs.SelectedIndex == 1;
        return (c, isFilter);
    }

    /// <summary>
    /// Creates a tab for the given key if it doesn't exist. Subsequent calls
    /// with the same key are a no-op. <paramref name="canRestart"/> controls
    /// whether the right-click menu exposes Restart.
    /// </summary>
    public void AddOrGetTab(string key, string displayName, Color swatch, bool canRestart = true)
    {
        if (_contentByKey.ContainsKey(key)) return;

        var page = new TabPage(displayName)
        {
            BackColor = _theme.BgDark,
            Tag = canRestart,
        };

        var content = BuildTabContent(key, page, swatch);
        _contentByKey[key] = content;
        _tabs.TabPages.Add(page);

        // When the user switches between Live and Filter sub-tabs the toolbar
        // button must reflect the newly active sub-tab's auto-scroll state.
        content.InnerTabs.SelectedIndexChanged += (_, _) => RefreshAutoScrollButton();
    }

    private TabContent BuildTabContent(string key, TabPage outerPage, Color swatch)
    {
        var safeKey = SanitizeForFileName(key);
        var filePath = Path.Combine(LogFileStore.LogsDirectory, $"{_sessionId}-{safeKey}.log");
        var store = new LogFileStore(filePath);

        var inner = new OwnerDrawTabControl(_theme)
        {
            Dock = DockStyle.Fill,
            Appearance = TabAppearance.Normal,
            ItemSize = new Size(90, 22),
            SizeMode = TabSizeMode.Fixed,
        };

        var livePage = new TabPage("Live") { BackColor = _theme.BgDark };
        var filterPage = new TabPage("Filter") { BackColor = _theme.BgDark };
        inner.TabPages.Add(livePage);
        inner.TabPages.Add(filterPage);

        // Simple sub-tab painter — solid bg + bottom underline when selected.
        inner.PaintTab = (g, i, r, sel) =>
        {
            using var bg = new SolidBrush(sel ? _theme.BgHeader : _theme.BgDark);
            g.FillRectangle(bg, r);
            TextRenderer.DrawText(
                g, inner.TabPages[i].Text,
                new Font("Segoe UI", 9f, sel ? FontStyle.Bold : FontStyle.Regular),
                r,
                sel ? _theme.TextPrimary : _theme.TextSecondary,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            if (sel)
            {
                using var pen = new Pen(_theme.Primary, 2);
                g.DrawLine(pen, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
            }
        };

        var liveRtb = MakeLogRtb();
        livePage.Controls.Add(liveRtb);

        // Filter sub-tab: header strip + RTB filling the rest.
        var filterHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = 30,
            BackColor = _theme.BgHeader,
        };

        var patternBox = new TextBox
        {
            Font = new Font("Cascadia Mono", 9f),
            BackColor = _theme.BgDark,
            ForeColor = _theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(6, 4),
            Size = new Size(380, 22),
            PlaceholderText = "Filter pattern — regex if valid, otherwise case-insensitive substring",
        };
        filterHeader.Controls.Add(patternBox);

        var enabledBox = new CheckBox
        {
            Text = "Enabled",
            Font = new Font("Segoe UI", 9f),
            ForeColor = _theme.TextPrimary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(394, 6),
        };
        filterHeader.Controls.Add(enabledBox);

        var indicator = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = _theme.TextSecondary,
            BackColor = Color.Transparent,
            AutoSize = true,
            Location = new Point(478, 8),
        };
        filterHeader.Controls.Add(indicator);

        var filterRtb = MakeLogRtb();

        // Add in z-order: RTB (Fill) first, header second (docks Top above it).
        filterPage.Controls.Add(filterRtb);
        filterPage.Controls.Add(filterHeader);

        outerPage.Controls.Add(inner);

        var content = new TabContent
        {
            Key = key,
            OuterPage = outerPage,
            SwatchColor = swatch,
            InnerTabs = inner,
            LiveRtb = liveRtb,
            FilterRtb = filterRtb,
            PatternBox = patternBox,
            EnabledBox = enabledBox,
            Indicator = indicator,
            Store = store,
        };

        // Debounce timer for filter pattern changes: avoid recompiling +
        // rescanning the whole file on every keystroke.
        content.FilterDebounce = new System.Windows.Forms.Timer { Interval = FilterDebounceMs };
        content.FilterDebounce.Tick += (_, _) =>
        {
            content.FilterDebounce!.Stop();
            ApplyFilterPattern(content);
            RebuildFilterFromFile(content);
        };
        patternBox.TextChanged += (_, _) =>
        {
            content.FilterDebounce.Stop();
            content.FilterDebounce.Start();
        };
        enabledBox.CheckedChanged += (_, _) =>
        {
            ApplyFilterPattern(content);
            RebuildFilterFromFile(content);
        };

        // Implicit auto-scroll pause: scrolling away from the bottom pauses,
        // scrolling back resumes. Per sub-tab.
        WireVScrollAutoPause(liveRtb, isFilterRtb: false, content);
        WireVScrollAutoPause(filterRtb, isFilterRtb: true, content);

        ApplyFilterPattern(content);
        return content;
    }

    private RichTextBox MakeLogRtb() => new()
    {
        Dock = DockStyle.Fill,
        ReadOnly = true,
        WordWrap = false,
        BackColor = _theme.BgDark,
        ForeColor = _stdoutColor,
        Font = new Font("Cascadia Mono", 9F),
        BorderStyle = BorderStyle.None,
        HideSelection = false,
        DetectUrls = false,
        ScrollBars = RichTextBoxScrollBars.Both,
    };

    private void WireVScrollAutoPause(RichTextBox rtb, bool isFilterRtb, TabContent content)
    {
        rtb.VScroll += (_, _) =>
        {
            if (rtb.IsDisposed) return;
            // Skip during programmatic scroll-restore around an append.
            if (_suppressScrollHeuristic) return;
            int lastLine = rtb.GetLineFromCharIndex(rtb.TextLength);
            int lastVisible = rtb.GetLineFromCharIndex(rtb.GetCharIndexFromPosition(
                new Point(0, rtb.ClientSize.Height - 1)));
            bool atBottom = lastLine - lastVisible <= 3;

            if (isFilterRtb)
            {
                if (content.AutoScrollFilter == atBottom) return;
                content.AutoScrollFilter = atBottom;
            }
            else
            {
                if (content.AutoScrollLive == atBottom) return;
                content.AutoScrollLive = atBottom;
            }

            // Update toolbar only if this is the active sub-tab.
            var (active, isFilterActive) = ResolveActive();
            if (active == content && isFilterActive == isFilterRtb)
                RefreshAutoScrollButton();
        };
    }

    private static string SanitizeForFileName(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s)
            sb.Append(char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_');
        return sb.Length == 0 ? "_" : sb.ToString();
    }

    // ---- append (single + batch) ---------------------------------------------------

    /// <summary>
    /// Append a line to a tab. Must be called on the UI thread.
    /// <paramref name="isError"/> renders the line in the error colour.
    /// </summary>
    public void AppendLine(string key, string line, bool isError = false)
    {
        if (IsDisposed || Disposing) return;

        if (!_contentByKey.TryGetValue(key, out var content))
        {
            if (!_contentByKey.TryGetValue(LauncherTabKey, out content)) return;
        }
        if (content.LiveRtb.IsDisposed) return;

        // 1) Source of truth.
        content.Store.Append(line, isError);
        content.Store.Flush();

        // 2) Live RTB.
        var rules = _highlights;
        var color = LogHighlightCompiler.FirstMatch(rules, line)
                    ?? (isError ? _stderrColor : _stdoutColor);
        TrimIfNeededLive(content);
        try
        {
            AppendColoured(content.LiveRtb, line + Environment.NewLine, color, content.AutoScrollLive);
            content.LiveLineCount++;
        }
        catch (ObjectDisposedException) { return; }
        catch (InvalidOperationException) { return; }

        // 3) Filter RTB — only if enabled and pattern matches.
        if (content.FilterEnabled && FilterMatches(content, line))
        {
            TrimIfNeededFilter(content);
            try
            {
                AppendColoured(content.FilterRtb, line + Environment.NewLine, color, content.AutoScrollFilter);
                content.FilterLineCount++;
            }
            catch (ObjectDisposedException) { }
            catch (InvalidOperationException) { }
        }
    }

    /// <summary>
    /// Append many lines to a tab in one shot — the high-volume path used by
    /// the UI flush timer draining buffered process output. Must be called on
    /// the UI thread.
    /// </summary>
    public void AppendBatch(string key, IReadOnlyList<(string line, bool isError)> batch)
    {
        if (IsDisposed || Disposing || batch.Count == 0) return;

        if (!_contentByKey.TryGetValue(key, out var content))
        {
            if (!_contentByKey.TryGetValue(LauncherTabKey, out content)) return;
        }
        if (content.LiveRtb.IsDisposed) return;

        // 1) Source of truth — file first, so a crash mid-batch doesn't lose
        //    lines that the user saw rendered.
        for (int b = 0; b < batch.Count; b++)
            content.Store.Append(batch[b].line, batch[b].isError);
        content.Store.Flush();

        var rules = _highlights;

        // 2) Live RTB — trim, then coalesce consecutive same-colour appends.
        while (content.LiveLineCount + batch.Count > LineCap)
        {
            int before = content.LiveLineCount;
            TrimOldestChunk(content.LiveRtb, ref content.LiveLineCount);
            if (content.LiveLineCount >= before) break;
        }

        try
        {
            AppendBatchToRtb(content.LiveRtb, batch, rules, content.AutoScrollLive);
            content.LiveLineCount += batch.Count;
        }
        catch (ObjectDisposedException) { return; }
        catch (InvalidOperationException) { return; }

        // 3) Filter RTB — only matching lines.
        if (!content.FilterEnabled || content.FilterMatcher == null) return;

        var matching = new List<(string line, bool isError)>();
        for (int b = 0; b < batch.Count; b++)
        {
            if (FilterMatches(content, batch[b].line))
                matching.Add(batch[b]);
        }
        if (matching.Count == 0) return;

        while (content.FilterLineCount + matching.Count > LineCap)
        {
            int before = content.FilterLineCount;
            TrimOldestChunk(content.FilterRtb, ref content.FilterLineCount);
            if (content.FilterLineCount >= before) break;
        }
        try
        {
            AppendBatchToRtb(content.FilterRtb, matching, rules, content.AutoScrollFilter);
            content.FilterLineCount += matching.Count;
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void AppendBatchToRtb(
        RichTextBox rtb,
        IReadOnlyList<(string line, bool isError)> batch,
        CompiledHighlight[] rules,
        bool autoScroll)
    {
        bool restore = !autoScroll && rtb.IsHandleCreated;
        POINT saved = default;
        if (restore)
        {
            saved = GetScrollPos(rtb);
            _suppressScrollHeuristic = true;
            SendMessage(rtb.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }
        try
        {
            // Coalesce consecutive lines that share the same effective colour
            // (stream default OR a highlight rule match) into one AppendText.
            int i = 0;
            while (i < batch.Count)
            {
                Color firstColor = LogHighlightCompiler.FirstMatch(rules, batch[i].line)
                                   ?? (batch[i].isError ? _stderrColor : _stdoutColor);
                var sb = new StringBuilder();
                int j = i;
                while (j < batch.Count)
                {
                    Color c = LogHighlightCompiler.FirstMatch(rules, batch[j].line)
                              ?? (batch[j].isError ? _stderrColor : _stdoutColor);
                    if (c != firstColor) break;
                    sb.Append(batch[j].line).Append(Environment.NewLine);
                    j++;
                }
                rtb.SelectionStart = rtb.TextLength;
                rtb.SelectionLength = 0;
                rtb.SelectionColor = firstColor;
                rtb.AppendText(sb.ToString());
                i = j;
            }
            rtb.SelectionColor = rtb.ForeColor;

            if (restore) SetScrollPos(rtb, saved);
            else if (autoScroll)
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.ScrollToCaret();
            }
        }
        finally
        {
            if (restore)
            {
                SendMessage(rtb.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _suppressScrollHeuristic = false;
                rtb.Invalidate();
            }
        }
    }

    private void TrimIfNeededLive(TabContent c)
    {
        if (c.LiveLineCount >= LineCap)
            TrimOldestChunk(c.LiveRtb, ref c.LiveLineCount);
    }
    private void TrimIfNeededFilter(TabContent c)
    {
        if (c.FilterLineCount >= LineCap)
            TrimOldestChunk(c.FilterRtb, ref c.FilterLineCount);
    }

    // ---- filter --------------------------------------------------------------------

    /// <summary>Recompile the pattern + update the indicator label. Tries
    /// regex first; falls back to case-insensitive substring on parse failure.
    /// Sets <c>FilterMatcher</c> on the content so the hot path can call it
    /// without re-deciding regex vs substring per line.</summary>
    private void ApplyFilterPattern(TabContent content)
    {
        content.FilterEnabled = content.EnabledBox.Checked;
        string pattern = content.PatternBox.Text ?? "";

        if (string.IsNullOrEmpty(pattern))
        {
            content.FilterMatcher = null;
            content.Indicator.Text = content.FilterEnabled ? "(empty)" : "(off)";
            content.Indicator.ForeColor = _theme.TextSecondary;
            return;
        }

        try
        {
            var rx = new Regex(pattern,
                RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.CultureInvariant);
            content.FilterMatcher = line => rx.IsMatch(line);
            content.Indicator.Text = "regex ✓";
            content.Indicator.ForeColor = _theme.Primary;
        }
        catch
        {
            string lit = pattern;
            content.FilterMatcher = line => line.IndexOf(lit, StringComparison.OrdinalIgnoreCase) >= 0;
            content.Indicator.Text = "text";
            content.Indicator.ForeColor = _theme.TextSecondary;
        }
    }

    private static bool FilterMatches(TabContent c, string line)
        => c.FilterEnabled && c.FilterMatcher != null && c.FilterMatcher(line);

    /// <summary>Clear the Filter RTB and re-scan the entire file. Called
    /// when the pattern or enable toggle changes, or when Reload fires on
    /// the Filter sub-tab.</summary>
    private void RebuildFilterFromFile(TabContent content)
    {
        content.FilterRtb.Clear();
        content.FilterLineCount = 0;
        if (!content.FilterEnabled || content.FilterMatcher == null) return;

        var batch = new List<(string line, bool isError)>();
        foreach (var (line, isErr) in content.Store.EnumerateAll())
        {
            if (FilterMatches(content, line))
                batch.Add((line, isErr));
        }
        if (batch.Count == 0) return;

        // Re-use the batch path — it handles colour coalescing + trim cap.
        // We may exceed LineCap from a single rebuild; in that case keep only
        // the most recent LineCap lines so the freshest hits are visible.
        if (batch.Count > LineCap)
            batch = batch.GetRange(batch.Count - LineCap, LineCap);

        try
        {
            AppendBatchToRtb(content.FilterRtb, batch, _highlights, content.AutoScrollFilter);
            content.FilterLineCount = batch.Count;
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    private void RebuildLiveFromFile(TabContent content)
    {
        content.LiveRtb.Clear();
        content.LiveLineCount = 0;

        var batch = new List<(string line, bool isError)>();
        foreach (var entry in content.Store.EnumerateAll())
            batch.Add(entry);
        if (batch.Count == 0) return;

        if (batch.Count > LineCap)
            batch = batch.GetRange(batch.Count - LineCap, LineCap);

        try
        {
            AppendBatchToRtb(content.LiveRtb, batch, _highlights, content.AutoScrollLive);
            content.LiveLineCount = batch.Count;
        }
        catch (ObjectDisposedException) { }
        catch (InvalidOperationException) { }
    }

    // ---- public misc ---------------------------------------------------------------

    public void FocusTab(string key)
    {
        if (_contentByKey.TryGetValue(key, out var c))
            _tabs.SelectedTab = c.OuterPage;
    }

    /// <summary>Clear a tab's buffer. Targets the Live sub-tab — matches the
    /// pre-filter behaviour of the right-click "Clear" menu item.</summary>
    public void ClearTab(string key)
    {
        if (_contentByKey.TryGetValue(key, out var c))
        {
            c.LiveRtb.Clear();
            c.LiveLineCount = 0;
        }
    }

    // ---- owner draw (outer) --------------------------------------------------------

    private void DrawTabHeader(Graphics g, int index, Rectangle bounds, bool selected)
    {
        if (index < 0 || index >= _tabs.TabPages.Count) return;
        var page = _tabs.TabPages[index];

        var bg = selected ? _theme.BgHeader : _theme.BgDark;
        using (var brush = new SolidBrush(bg))
            g.FillRectangle(brush, bounds);

        Color swatchColor = _theme.TextSecondary;
        foreach (var c in _contentByKey.Values)
        {
            if (c.OuterPage == page) { swatchColor = c.SwatchColor; break; }
        }

        const int swatchSize = 8;
        var swatchY = bounds.Y + (bounds.Height - swatchSize) / 2;
        using (var swatchBrush = new SolidBrush(swatchColor))
            g.FillEllipse(swatchBrush, bounds.X + 8, swatchY, swatchSize, swatchSize);

        var textColor = selected ? _theme.TextPrimary : _theme.TextSecondary;
        using var font = new Font(selected ? "Segoe UI Semibold" : "Segoe UI", 9F);
        var textRect = new Rectangle(
            bounds.X + 8 + swatchSize + 6, bounds.Y,
            bounds.Width - (8 + swatchSize + 6) - 4, bounds.Height);
        TextRenderer.DrawText(g, page.Text, font, textRect, textColor,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis);

        if (selected)
        {
            using var pen = new Pen(swatchColor, 2);
            g.DrawLine(pen, bounds.X, bounds.Bottom - 1, bounds.Right, bounds.Bottom - 1);
        }
    }

    // ---- context menu --------------------------------------------------------------

    private ContextMenuStrip BuildContextMenu()
    {
        var menu = new ContextMenuStrip { BackColor = _theme.BgHeader, ForeColor = _theme.TextPrimary };
        var restartItem = new ToolStripMenuItem("Restart") { Name = "restart" };
        var clearItem = new ToolStripMenuItem("Clear") { Name = "clear" };
        var copyItem = new ToolStripMenuItem("Copy all") { Name = "copy" };

        restartItem.Click += (_, _) =>
        {
            if (_contextTab is { } page && KeyForPage(page) is { } key)
                RestartRequested?.Invoke(key);
        };
        clearItem.Click += (_, _) =>
        {
            if (_contextTab is { } page && KeyForPage(page) is { } key)
                ClearTab(key);
        };
        copyItem.Click += (_, _) =>
        {
            if (_contextTab is { } page && KeyForPage(page) is { } key
                && _contentByKey.TryGetValue(key, out var c)
                && c.LiveRtb.TextLength > 0)
            {
                try { Clipboard.SetText(c.LiveRtb.Text); } catch { }
            }
        };

        menu.Items.Add(restartItem);
        menu.Items.Add(clearItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(copyItem);

        menu.Opening += (_, _) => restartItem.Visible = _contextTab?.Tag is true;
        return menu;
    }

    private string? KeyForPage(TabPage page)
    {
        foreach (var kv in _contentByKey)
            if (kv.Value.OuterPage == page) return kv.Key;
        return null;
    }

    private void OnTabsMouseUp(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Right) return;
        for (int i = 0; i < _tabs.TabPages.Count; i++)
        {
            if (_tabs.GetTabRect(i).Contains(e.Location))
            {
                _contextTab = _tabs.TabPages[i];
                _tabContextMenu.Show(_tabs, e.Location);
                return;
            }
        }
    }

    // ---- low-level helpers ---------------------------------------------------------

    // Win32 EM_GETSCROLLPOS / EM_SETSCROLLPOS — used to save and restore the
    // RTB's actual scroll position around an append. Without this, paused
    // tabs visibly jump back to the bottom each time text is appended,
    // because EM_REPLACESEL (the message AppendText sends) scrolls the
    // caret into view whenever the selection point is at the end. The
    // restore is silent at the WinForms level but still raises a VScroll
    // event under the hood — we gate that handler with
    // <see cref="_suppressScrollHeuristic"/> so the auto-pause heuristic
    // can't flip Live back to ON during a restore.
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern int SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref POINT lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    private const int WM_USER = 0x0400;
    private const int EM_GETSCROLLPOS = WM_USER + 221;
    private const int EM_SETSCROLLPOS = WM_USER + 222;
    private const int WM_SETREDRAW = 0x000B;

    /// <summary>Set during a save/restore-scroll append. The VScroll handler
    /// reads this and skips the auto-pause heuristic so a programmatic
    /// scroll restore doesn't look like the user scrolling to the bottom.</summary>
    private bool _suppressScrollHeuristic;

    private static POINT GetScrollPos(RichTextBox rtb)
    {
        var p = new POINT();
        if (rtb.IsHandleCreated)
            SendMessage(rtb.Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref p);
        return p;
    }
    private static void SetScrollPos(RichTextBox rtb, POINT p)
    {
        if (rtb.IsHandleCreated)
            SendMessage(rtb.Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref p);
    }

    /// <summary>Append one coloured line. Caller indicates whether to honour
    /// auto-scroll; when off, the current scroll position is saved before
    /// the append and restored after so the user's view doesn't jump.
    /// During the bracket: VScroll heuristic is suppressed (so an
    /// EM_REPLACESEL auto-scroll inside AppendText can't flip the flag back
    /// to ON), and redraw is frozen (so the viewport doesn't visibly jump
    /// to the bottom and then back).</summary>
    private void AppendColoured(RichTextBox rtb, string text, Color color, bool autoScroll)
    {
        bool restore = !autoScroll && rtb.IsHandleCreated;
        POINT saved = default;
        if (restore)
        {
            saved = GetScrollPos(rtb);
            _suppressScrollHeuristic = true;
            SendMessage(rtb.Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        }
        try
        {
            rtb.SelectionStart = rtb.TextLength;
            rtb.SelectionLength = 0;
            rtb.SelectionColor = color;
            rtb.AppendText(text);
            rtb.SelectionColor = rtb.ForeColor;

            if (restore) SetScrollPos(rtb, saved);
            else if (autoScroll)
            {
                rtb.SelectionStart = rtb.TextLength;
                rtb.ScrollToCaret();
            }
        }
        finally
        {
            if (restore)
            {
                SendMessage(rtb.Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
                _suppressScrollHeuristic = false;
                rtb.Invalidate();
            }
        }
    }

    private static void TrimOldestChunk(RichTextBox rtb, ref int lineCount)
    {
        var text = rtb.Text;
        int idx = 0;
        int found = 0;
        while (found < LineTrimChunk && idx < text.Length)
        {
            var nl = text.IndexOf('\n', idx);
            if (nl < 0) break;
            idx = nl + 1;
            found++;
        }
        if (idx > 0)
        {
            rtb.Select(0, idx);
            rtb.SelectedText = string.Empty;
            lineCount = Math.Max(0, lineCount - found);
        }
    }

    /// <summary>Deterministic, readable colour for a shortcut from its id —
    /// used for the tab swatch so each shortcut keeps the same colour.</summary>
    public static Color StableColor(string seed)
    {
        const uint offset = 2166136261;
        const uint prime = 16777619;
        uint hash = offset;
        var s = seed ?? "";
        for (int i = 0; i < s.Length; i++) { hash ^= s[i]; hash *= prime; }
        double h = hash % 360;
        return FromHsv(h, 0.55, 0.95);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        int hi = (int)(h / 60) % 6;
        double f = h / 60 - Math.Floor(h / 60);
        v *= 255;
        int vi = (int)v;
        int p = (int)(v * (1 - s));
        int q = (int)(v * (1 - f * s));
        int t = (int)(v * (1 - (1 - f) * s));
        return hi switch
        {
            0 => Color.FromArgb(vi, t, p),
            1 => Color.FromArgb(q, vi, p),
            2 => Color.FromArgb(p, vi, t),
            3 => Color.FromArgb(p, q, vi),
            4 => Color.FromArgb(t, p, vi),
            _ => Color.FromArgb(vi, p, q),
        };
    }

    // ---- TabContent ----------------------------------------------------------------

    /// <summary>Per-shortcut tab state. Holds the inner two-tab control, both
    /// log RTBs, the filter UI, the file-backed log store, and a debounce
    /// timer so filter typing doesn't rescan the whole file on every keystroke.</summary>
    private sealed class TabContent : IDisposable
    {
        public required string Key { get; init; }
        public required TabPage OuterPage { get; init; }
        public required Color SwatchColor { get; init; }
        public required TabControl InnerTabs { get; init; }
        public required RichTextBox LiveRtb { get; init; }
        public required RichTextBox FilterRtb { get; init; }
        public required TextBox PatternBox { get; init; }
        public required CheckBox EnabledBox { get; init; }
        public required Label Indicator { get; init; }
        public required LogFileStore Store { get; init; }

        public int LiveLineCount;
        public int FilterLineCount;

        /// <summary>Per-sub-tab auto-scroll. Default ON; flipped by the
        /// toolbar button or by the VScroll auto-pause heuristic.</summary>
        public bool AutoScrollLive = true;
        public bool AutoScrollFilter = true;

        public bool FilterEnabled;
        public Func<string, bool>? FilterMatcher;

        public System.Windows.Forms.Timer? FilterDebounce;

        public void Dispose()
        {
            try { FilterDebounce?.Dispose(); } catch { }
            try { Store.Dispose(); } catch { }
        }
    }
}
