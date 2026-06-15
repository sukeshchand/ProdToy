using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy.Plugins.ShortCutManager;

/// <summary>
/// Edits the global list of log-line highlight rules. Each rule is a row:
/// enabled checkbox, pattern textbox, regex checkbox, colour swatch (click to
/// pick), reorder ▲/▼, and 🗑 delete. Live "Test against a line" field at the
/// bottom shows which rule (if any) would fire on a given line.
///
/// First-match-wins is the consumer policy, so order matters; rows can be
/// shuffled with the ▲/▼ buttons. Save returns the new list via
/// <see cref="Result"/>; Cancel returns null.
/// </summary>
sealed class LogHighlightRulesForm : Form
{
    private readonly PluginTheme _theme;
    private readonly FlowLayoutPanel _rowsPanel;
    private readonly TextBox _testBox;
    private readonly Label _testResultLabel;
    private readonly List<RuleRow> _rows = new();

    public List<HighlightRule>? Result { get; private set; }

    public LogHighlightRulesForm(PluginTheme theme, IEnumerable<HighlightRule> existing)
    {
        _theme = theme;
        Text = "Highlight rules";
        Icon = IconHelper.CreateAppIcon(theme.Primary);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(720, 540);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 14;

        var header = new Label
        {
            Text = "Highlight rules",
            Font = new Font("Segoe UI Semibold", 13f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, 10),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);

        var hint = new Label
        {
            Text = "First matching rule wins. New rules apply to new log lines only — restart a process to recolour old output.",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = false,
            Size = new Size(ClientSize.Width - pad * 2, 32),
            Location = new Point(pad, 40),
            BackColor = Color.Transparent,
        };
        Controls.Add(hint);

        // Column headers — pinned above the scroll panel.
        var colHeader = new Panel
        {
            Location = new Point(pad, 76),
            Size = new Size(ClientSize.Width - pad * 2, 22),
            BackColor = theme.BgHeader,
        };
        AddColLabel(colHeader, "On", 6);
        AddColLabel(colHeader, "Pattern", 36);
        AddColLabel(colHeader, "Regex", 360);
        AddColLabel(colHeader, "Colour", 420);
        AddColLabel(colHeader, "Order", 500);
        AddColLabel(colHeader, "", 580);
        Controls.Add(colHeader);

        _rowsPanel = new FlowLayoutPanel
        {
            Location = new Point(pad, 98),
            Size = new Size(ClientSize.Width - pad * 2, 320),
            FlowDirection = FlowDirection.TopDown,
            WrapContents = false,
            AutoScroll = true,
            BackColor = theme.BgDark,
            BorderStyle = BorderStyle.FixedSingle,
        };
        Controls.Add(_rowsPanel);

        foreach (var r in existing ?? Array.Empty<HighlightRule>())
            AddRow(r);

        var addBtn = new RoundedButton
        {
            Text = "+ Add rule",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(110, 28),
            Location = new Point(pad, 424),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        addBtn.FlatAppearance.BorderSize = 0;
        addBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        addBtn.Click += (_, _) =>
        {
            AddRow(new HighlightRule { ColorHex = "#7FE0FF", Enabled = true });
            _rowsPanel.ScrollControlIntoView(_rows[^1].Container);
        };
        Controls.Add(addBtn);

        // Test field — what rule (if any) fires on this line?
        var testLabel = new Label
        {
            Text = "Test against a line:",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, 462),
            BackColor = Color.Transparent,
        };
        Controls.Add(testLabel);

        _testBox = new TextBox
        {
            Font = new Font("Cascadia Mono", 9f),
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            BorderStyle = BorderStyle.FixedSingle,
            Location = new Point(pad, 481),
            Size = new Size(ClientSize.Width - pad * 2, 24),
            PlaceholderText = "Paste a log line here…",
        };
        _testBox.TextChanged += (_, _) => RefreshTestResult();
        Controls.Add(_testBox);

        _testResultLabel = new Label
        {
            Text = "→ matches: (none)",
            Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, 512),
            BackColor = Color.Transparent,
        };
        Controls.Add(_testResultLabel);

        var saveBtn = new RoundedButton
        {
            Text = "Save",
            Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
            Size = new Size(110, 30),
            Location = new Point(ClientSize.Width - pad - 110, 424),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.Primary,
            ForeColor = Color.White,
            Cursor = Cursors.Hand,
        };
        saveBtn.FlatAppearance.BorderSize = 0;
        saveBtn.FlatAppearance.MouseOverBackColor = theme.PrimaryLight;
        saveBtn.Click += (_, _) =>
        {
            Result = _rows.Select(r => r.ToRule()).Where(r => !string.IsNullOrEmpty(r.Pattern)).ToList();
            DialogResult = DialogResult.OK;
            Close();
        };
        Controls.Add(saveBtn);

        var cancelBtn = new RoundedButton
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(90, 30),
            Location = new Point(ClientSize.Width - pad - 110 - 100, 424),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        cancelBtn.FlatAppearance.BorderSize = 0;
        cancelBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(cancelBtn);
    }

    private void AddColLabel(Control parent, string text, int x)
    {
        parent.Controls.Add(new Label
        {
            Text = text,
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = _theme.TextSecondary,
            AutoSize = true,
            Location = new Point(x, 4),
            BackColor = Color.Transparent,
        });
    }

    private void AddRow(HighlightRule rule)
    {
        var row = new RuleRow(_theme, rule,
            onMoveUp: r => MoveRow(r, -1),
            onMoveDown: r => MoveRow(r, +1),
            onDelete: r => RemoveRow(r),
            onChanged: RefreshTestResult);
        _rows.Add(row);
        _rowsPanel.Controls.Add(row.Container);
        RefreshTestResult();
    }

    private void MoveRow(RuleRow row, int delta)
    {
        int idx = _rows.IndexOf(row);
        int target = idx + delta;
        if (idx < 0 || target < 0 || target >= _rows.Count) return;
        _rows.RemoveAt(idx);
        _rows.Insert(target, row);
        _rowsPanel.Controls.SetChildIndex(row.Container, target);
        RefreshTestResult();
    }

    private void RemoveRow(RuleRow row)
    {
        _rows.Remove(row);
        _rowsPanel.Controls.Remove(row.Container);
        row.Container.Dispose();
        RefreshTestResult();
    }

    private void RefreshTestResult()
    {
        // AddRow is called for each existing rule during the ctor — at which
        // point _testBox / _testResultLabel are still null (created later).
        // Guard so seeding doesn't NRE.
        if (_testBox is null || _testResultLabel is null) return;

        string line = _testBox.Text ?? "";
        if (string.IsNullOrEmpty(line))
        {
            _testResultLabel.Text = "→ matches: (none)";
            _testResultLabel.ForeColor = _theme.TextSecondary;
            return;
        }

        var compiled = LogHighlightCompiler.Compile(_rows.Select(r => r.ToRule()));
        var color = LogHighlightCompiler.FirstMatch(compiled, line);
        if (color is null)
        {
            _testResultLabel.Text = "→ matches: (none)";
            _testResultLabel.ForeColor = _theme.TextSecondary;
        }
        else
        {
            // Find the rule index that matched, so the user sees which entry won.
            var compiledEnabled = compiled.Where(c => c.Enabled).ToArray();
            int matchIdx = -1;
            for (int i = 0; i < compiledEnabled.Length; i++)
            {
                if (compiledEnabled[i].Pattern.IsMatch(line)) { matchIdx = i; break; }
            }
            _testResultLabel.Text = matchIdx >= 0
                ? $"→ matches rule #{matchIdx + 1}"
                : "→ matches: (none)";
            _testResultLabel.ForeColor = color.Value;
        }
    }

    /// <summary>One editable row in the rules list. Built as a single Panel
    /// so the FlowLayoutPanel above lays them out top-to-bottom and the
    /// reorder buttons can swap them by index.</summary>
    private sealed class RuleRow
    {
        public Panel Container { get; }
        private readonly CheckBox _enabled;
        private readonly TextBox _pattern;
        private readonly CheckBox _isRegex;
        private readonly Panel _swatch;
        private Color _color;

        public RuleRow(PluginTheme theme, HighlightRule rule,
            Action<RuleRow> onMoveUp, Action<RuleRow> onMoveDown,
            Action<RuleRow> onDelete, Action onChanged)
        {
            Container = new Panel
            {
                Size = new Size(680, 30),
                BackColor = theme.BgDark,
                Margin = new Padding(0, 2, 0, 2),
            };

            _enabled = new CheckBox
            {
                Checked = rule.Enabled,
                Location = new Point(8, 6),
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = theme.TextPrimary,
            };
            _enabled.CheckedChanged += (_, _) => onChanged();
            Container.Controls.Add(_enabled);

            _pattern = new TextBox
            {
                Text = rule.Pattern,
                Font = new Font("Cascadia Mono", 9f),
                BackColor = theme.BgHeader,
                ForeColor = theme.TextPrimary,
                BorderStyle = BorderStyle.FixedSingle,
                Location = new Point(34, 4),
                Size = new Size(320, 24),
            };
            _pattern.TextChanged += (_, _) => onChanged();
            Container.Controls.Add(_pattern);

            _isRegex = new CheckBox
            {
                Checked = rule.IsRegex,
                Location = new Point(362, 6),
                AutoSize = true,
                BackColor = Color.Transparent,
                ForeColor = theme.TextPrimary,
            };
            _isRegex.CheckedChanged += (_, _) => onChanged();
            Container.Controls.Add(_isRegex);

            _color = LogHighlightCompiler.ParseHex(rule.ColorHex);
            _swatch = new Panel
            {
                Location = new Point(420, 6),
                Size = new Size(48, 20),
                BackColor = _color,
                BorderStyle = BorderStyle.FixedSingle,
                Cursor = Cursors.Hand,
            };
            _swatch.Click += (_, _) =>
            {
                using var dlg = new ColorDialog
                {
                    Color = _color,
                    FullOpen = true,
                    AnyColor = true,
                };
                if (dlg.ShowDialog(Container.FindForm()) == DialogResult.OK)
                {
                    _color = dlg.Color;
                    _swatch.BackColor = _color;
                    onChanged();
                }
            };
            Container.Controls.Add(_swatch);

            var upBtn = MakeIconBtn(theme, "▲", 500, () => onMoveUp(this));
            var downBtn = MakeIconBtn(theme, "▼", 530, () => onMoveDown(this));
            var delBtn = MakeIconBtn(theme, "🗑", 580, () => onDelete(this));
            delBtn.ForeColor = theme.ErrorColor;
            Container.Controls.Add(upBtn);
            Container.Controls.Add(downBtn);
            Container.Controls.Add(delBtn);
        }

        public HighlightRule ToRule() => new()
        {
            Enabled = _enabled.Checked,
            Pattern = _pattern.Text ?? "",
            IsRegex = _isRegex.Checked,
            ColorHex = LogHighlightCompiler.ToHex(_color),
        };

        private static Button MakeIconBtn(PluginTheme theme, string glyph, int x, Action onClick)
        {
            var b = new Button
            {
                Text = glyph,
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Size = new Size(26, 22),
                Location = new Point(x, 4),
                FlatStyle = FlatStyle.Flat,
                BackColor = theme.PrimaryDim,
                ForeColor = theme.TextPrimary,
                Cursor = Cursors.Hand,
                TabStop = false,
            };
            b.FlatAppearance.BorderSize = 0;
            b.Click += (_, _) => onClick();
            return b;
        }
    }
}
