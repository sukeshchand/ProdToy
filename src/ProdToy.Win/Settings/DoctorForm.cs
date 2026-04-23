using System.Drawing;
using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Progressive, single-screen diagnostics dialog.
///
/// Top half — CHECKS panel: as each source runs, its individual checks
/// appear beneath a source header. Each check gets a pass/fail glyph,
/// title, and detail line.
///
/// Bottom half — ISSUES panel: once everything has run, lists only the
/// failed checks with a fix checkbox and a "Fix Selected" button.
/// </summary>
class DoctorForm : Form
{
    private readonly PopupTheme _theme;
    private readonly List<HostDoctor.DoctorCheckSource> _sources;
    private readonly Panel _checksPanel;
    private readonly Panel _issuesPanel;
    private readonly Label _summaryLabel;
    private readonly RoundedButton _closeBtn;
    private RoundedButton? _fixBtn;

    private readonly Dictionary<string, SourceRow> _sourceRows = new();
    private readonly List<(DoctorCheck Check, CheckBox Box)> _issueRows = new();
    private readonly List<DoctorCheck> _allChecks = new();
    private int _nextChecksY;

    private sealed class SourceRow
    {
        public Label Header = null!;
        public Label Badge = null!;
        public int StartY;
        public int Height;
    }

    public DoctorForm(PopupTheme theme)
    {
        _theme = theme;
        _sources = HostDoctor.GetSources();

        Text = "ProdToy Doctor";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        Size = new Size(820, 760);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);
        AutoScaleMode = AutoScaleMode.Dpi;

        int pad = 20;

        var header = new Label
        {
            Text = "Running Diagnostics",
            Font = new Font("Segoe UI Semibold", 14f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, pad),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);

        _summaryLabel = new Label
        {
            Text = $"Checking {_sources.Count} source(s)...",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(pad, pad + 34),
            BackColor = Color.Transparent,
        };
        Controls.Add(_summaryLabel);

        // --- CHECKS ---
        var checksHeader = new Label
        {
            Text = "CHECKS",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, pad + 66),
            BackColor = Color.Transparent,
        };
        Controls.Add(checksHeader);

        _checksPanel = new Panel
        {
            AutoScroll = true,
            Location = new Point(pad, pad + 92),
            Size = new Size(ClientSize.Width - pad * 2, 340),
            BackColor = theme.BgHeader,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _checksPanel.Layout += (_, _) =>
        {
            _checksPanel.HorizontalScroll.Maximum = 0;
            _checksPanel.HorizontalScroll.Visible = false;
            _checksPanel.AutoScroll = true;
        };
        Controls.Add(_checksPanel);

        // Pre-create a header row per source so the user sees the list up front.
        _nextChecksY = 8;
        foreach (var s in _sources)
        {
            var row = new SourceRow { StartY = _nextChecksY };
            row.Header = new Label
            {
                Text = s.Name.ToUpperInvariant(),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = theme.Primary,
                AutoSize = true,
                Location = new Point(10, _nextChecksY),
                BackColor = Color.Transparent,
            };
            _checksPanel.Controls.Add(row.Header);

            row.Badge = new Label
            {
                Text = "(pending)",
                Font = new Font("Segoe UI", 8.5f, FontStyle.Italic),
                ForeColor = theme.TextSecondary,
                AutoSize = true,
                Location = new Point(180, _nextChecksY + 2),
                BackColor = Color.Transparent,
            };
            _checksPanel.Controls.Add(row.Badge);

            _nextChecksY += 24;
            row.Height = 24;
            _sourceRows[s.Name] = row;
        }

        // --- ISSUES ---
        var issuesHeader = new Label
        {
            Text = "ISSUES",
            Font = new Font("Segoe UI Semibold", 8.5f, FontStyle.Bold),
            ForeColor = theme.Primary,
            AutoSize = true,
            Location = new Point(pad, pad + 92 + 340 + 14),
            BackColor = Color.Transparent,
        };
        Controls.Add(issuesHeader);

        _issuesPanel = new Panel
        {
            AutoScroll = true,
            Location = new Point(pad, pad + 92 + 340 + 40),
            Size = new Size(ClientSize.Width - pad * 2, ClientSize.Height - (pad + 92 + 340 + 40) - 70),
            BackColor = theme.BgHeader,
            BorderStyle = BorderStyle.FixedSingle,
        };
        _issuesPanel.Layout += (_, _) =>
        {
            _issuesPanel.HorizontalScroll.Maximum = 0;
            _issuesPanel.HorizontalScroll.Visible = false;
            _issuesPanel.AutoScroll = true;
        };
        Controls.Add(_issuesPanel);

        var waitingLabel = new Label
        {
            Text = "(waiting for checks to finish)",
            Font = new Font("Segoe UI", 9f, FontStyle.Italic),
            ForeColor = theme.TextSecondary,
            AutoSize = true,
            Location = new Point(10, 10),
            BackColor = Color.Transparent,
            Tag = "placeholder",
        };
        _issuesPanel.Controls.Add(waitingLabel);

        // --- Buttons ---
        int btnY = ClientSize.Height - 56 + 10;

        _closeBtn = new RoundedButton
        {
            Text = "Close",
            Font = new Font("Segoe UI", 9.5f),
            Size = new Size(100, 36),
            Location = new Point(ClientSize.Width - pad - 100, btnY),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.PrimaryDim,
            ForeColor = theme.TextSecondary,
            Cursor = Cursors.Hand,
        };
        _closeBtn.FlatAppearance.BorderSize = 0;
        _closeBtn.FlatAppearance.MouseOverBackColor = theme.Primary;
        _closeBtn.Click += (_, _) => { DialogResult = DialogResult.Cancel; Close(); };
        Controls.Add(_closeBtn);

        Shown += (_, _) => _ = RunChecksAsync();
    }

    private async Task RunChecksAsync()
    {
        int totalFailed = 0;

        foreach (var source in _sources)
        {
            var row = _sourceRows[source.Name];
            row.Badge.Text = "checking...";
            row.Badge.ForeColor = _theme.TextSecondary;

            // Run off the UI thread.
            var checks = await Task.Run(source.Run);

            _allChecks.AddRange(checks);
            InsertChecksUnderSource(source.Name, checks);

            int passed = checks.Count(c => c.Passed);
            int failed = checks.Count(c => !c.Passed);
            totalFailed += failed;

            if (failed == 0)
            {
                row.Badge.Text = $"✓ {passed} checks passed";
                row.Badge.ForeColor = Color.FromArgb(0x5f, 0xc7, 0x6e);
            }
            else
            {
                int errs = checks.Count(c => !c.Passed && c.Severity == DoctorSeverity.Error);
                row.Badge.Text = errs > 0
                    ? $"✗ {passed} passed, {failed} failed"
                    : $"⚠ {passed} passed, {failed} failed";
                row.Badge.ForeColor = errs > 0 ? _theme.ErrorColor : Color.Orange;
            }
        }

        FinalizeIssuesPanel(totalFailed);
    }

    /// <summary>
    /// Inserts detail rows under the source header for each individual check.
    /// Shifts every subsequent header down to make room.
    /// </summary>
    private void InsertChecksUnderSource(string sourceName, IReadOnlyList<DoctorCheck> checks)
    {
        if (!_sourceRows.TryGetValue(sourceName, out var row)) return;

        int insertAt = row.StartY + 24; // just below the header+badge row
        int inserted = 0;

        foreach (var c in checks)
        {
            var glyph = new Label
            {
                Text = c.Passed ? "✓" : (c.Severity == DoctorSeverity.Error ? "✗" : "⚠"),
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                ForeColor = c.Passed
                    ? Color.FromArgb(0x5f, 0xc7, 0x6e)
                    : (c.Severity == DoctorSeverity.Error ? _theme.ErrorColor : Color.Orange),
                AutoSize = false,
                TextAlign = ContentAlignment.MiddleCenter,
                Size = new Size(22, 18),
                Location = new Point(26, insertAt + inserted),
                BackColor = Color.Transparent,
            };
            _checksPanel.Controls.Add(glyph);

            var title = new Label
            {
                Text = c.Title,
                Font = new Font("Segoe UI", 9f),
                ForeColor = _theme.TextPrimary,
                AutoSize = true,
                Location = new Point(54, insertAt + inserted + 1),
                BackColor = Color.Transparent,
            };
            _checksPanel.Controls.Add(title);
            inserted += 18;

            if (!string.IsNullOrWhiteSpace(c.Details))
            {
                // One-line detail row beneath the title.
                var firstLine = c.Details.Split('\n', 2)[0];
                var det = new Label
                {
                    Text = firstLine,
                    Font = new Font("Consolas", 8f),
                    ForeColor = _theme.TextSecondary,
                    AutoSize = false,
                    AutoEllipsis = true,
                    Size = new Size(_checksPanel.ClientSize.Width - 80, 14),
                    Location = new Point(54, insertAt + inserted),
                    BackColor = Color.Transparent,
                };
                _checksPanel.Controls.Add(det);
                inserted += 16;
            }
            inserted += 4; // gap between checks
        }

        // Shift every later source row down.
        ShiftSourcesAfter(sourceName, inserted);
        _nextChecksY += inserted;
    }

    private void ShiftSourcesAfter(string sourceName, int delta)
    {
        bool past = false;
        foreach (var s in _sources)
        {
            if (past)
            {
                var r = _sourceRows[s.Name];
                r.Header.Location = new Point(r.Header.Location.X, r.Header.Location.Y + delta);
                r.Badge.Location  = new Point(r.Badge.Location.X,  r.Badge.Location.Y  + delta);
                r.StartY += delta;
            }
            if (s.Name == sourceName) past = true;
        }
    }

    private void FinalizeIssuesPanel(int totalFailed)
    {
        // Clear waiting placeholder.
        foreach (var c in _issuesPanel.Controls.OfType<Control>().ToList())
        {
            if ((c.Tag as string) == "placeholder") { _issuesPanel.Controls.Remove(c); c.Dispose(); }
        }

        int totalChecks = _allChecks.Count;
        int passedCount = totalChecks - totalFailed;
        _summaryLabel.Text = totalFailed == 0
            ? $"All healthy — {totalChecks} check(s) passed across {_sources.Count} source(s)."
            : $"{passedCount} of {totalChecks} check(s) passed — {totalFailed} issue(s) to review.";
        _summaryLabel.ForeColor = totalFailed == 0
            ? Color.FromArgb(0x5f, 0xc7, 0x6e)
            : _theme.TextSecondary;

        if (totalFailed == 0)
        {
            var healthyLabel = new Label
            {
                Text = "✓  Everything looks good.",
                Font = new Font("Segoe UI Semibold", 11f, FontStyle.Bold),
                ForeColor = Color.FromArgb(0x5f, 0xc7, 0x6e),
                AutoSize = true,
                Location = new Point(12, 12),
                BackColor = Color.Transparent,
            };
            _issuesPanel.Controls.Add(healthyLabel);
            _closeBtn.Text = "Close";
            return;
        }

        _closeBtn.Text = "Cancel";

        int ry = 8;
        foreach (var check in _allChecks.Where(c => !c.Passed))
        {
            bool fixable = check.Fix != null;
            var cb = new CheckBox
            {
                AutoSize = false,
                Size = new Size(22, 22),
                Location = new Point(10, ry + 2),
                Checked = fixable,
                Enabled = fixable,
                BackColor = Color.Transparent,
                ForeColor = _theme.TextPrimary,
            };
            _issuesPanel.Controls.Add(cb);
            _issueRows.Add((check, cb));

            var sevColor = check.Severity switch
            {
                DoctorSeverity.Error   => _theme.ErrorColor,
                DoctorSeverity.Warning => Color.Orange,
                _                      => _theme.TextSecondary,
            };
            var sevBadge = new Label
            {
                Text = check.Severity.ToString().ToUpperInvariant(),
                Font = new Font("Segoe UI Semibold", 7.5f, FontStyle.Bold),
                ForeColor = sevColor,
                AutoSize = true,
                Location = new Point(38, ry + 4),
                BackColor = Color.Transparent,
            };
            _issuesPanel.Controls.Add(sevBadge);

            var sourceLabel = new Label
            {
                Text = $"  [{check.Source}]",
                Font = new Font("Segoe UI", 8.5f),
                ForeColor = _theme.TextSecondary,
                AutoSize = true,
                Location = new Point(sevBadge.Location.X + 70, ry + 4),
                BackColor = Color.Transparent,
            };
            _issuesPanel.Controls.Add(sourceLabel);

            var titleLabel = new Label
            {
                Text = check.Title + (check.RequiresRestart ? "  (restart required)" : ""),
                Font = new Font("Segoe UI Semibold", 9.5f, FontStyle.Bold),
                ForeColor = fixable ? _theme.TextPrimary : _theme.TextSecondary,
                AutoSize = false,
                Size = new Size(_issuesPanel.ClientSize.Width - 50, 20),
                Location = new Point(38, ry + 22),
                BackColor = Color.Transparent,
            };
            _issuesPanel.Controls.Add(titleLabel);

            int rowH = 46;
            if (!string.IsNullOrEmpty(check.Details))
            {
                var detailsLabel = new Label
                {
                    Text = check.Details,
                    Font = new Font("Consolas", 8.5f),
                    ForeColor = _theme.TextSecondary,
                    AutoSize = false,
                    MaximumSize = new Size(_issuesPanel.ClientSize.Width - 50, 0),
                    Size = new Size(_issuesPanel.ClientSize.Width - 50, 18),
                    Location = new Point(38, ry + 42),
                    BackColor = Color.Transparent,
                };
                _issuesPanel.Controls.Add(detailsLabel);
                rowH += 20;
            }

            if (!fixable)
            {
                var infoHint = new Label
                {
                    Text = "(no automatic fix — manual action required)",
                    Font = new Font("Segoe UI", 8f, FontStyle.Italic),
                    ForeColor = _theme.TextSecondary,
                    AutoSize = true,
                    Location = new Point(38, ry + rowH - 14),
                    BackColor = Color.Transparent,
                };
                _issuesPanel.Controls.Add(infoHint);
                rowH += 4;
            }

            var sep = new Panel
            {
                BackColor = _theme.Border,
                Size = new Size(_issuesPanel.ClientSize.Width - 20, 1),
                Location = new Point(10, ry + rowH),
            };
            _issuesPanel.Controls.Add(sep);
            ry += rowH + 6;
        }

        if (_allChecks.Any(c => !c.Passed && c.Fix != null))
        {
            int btnY = ClientSize.Height - 56 + 10;
            _fixBtn = new RoundedButton
            {
                Text = "Fix Selected",
                Font = new Font("Segoe UI Semibold", 10f, FontStyle.Bold),
                Size = new Size(140, 36),
                Location = new Point(ClientSize.Width - 20 - 100 - 10 - 140, btnY),
                FlatStyle = FlatStyle.Flat,
                BackColor = _theme.Primary,
                ForeColor = Color.White,
                Cursor = Cursors.Hand,
            };
            _fixBtn.FlatAppearance.BorderSize = 0;
            _fixBtn.FlatAppearance.MouseOverBackColor = _theme.PrimaryLight;
            _fixBtn.Click += (_, _) => ApplyFixes();
            Controls.Add(_fixBtn);
        }
    }

    private void ApplyFixes()
    {
        var selected = _issueRows.Where(r => r.Box.Checked && r.Check.Fix != null).ToList();
        if (selected.Count == 0)
        {
            DialogResult = DialogResult.Cancel;
            Close();
            return;
        }

        int applied = 0;
        var errors = new List<string>();
        bool needsRestart = false;

        foreach (var (issue, _) in selected)
        {
            try
            {
                issue.Fix!();
                applied++;
                if (issue.RequiresRestart) needsRestart = true;
            }
            catch (Exception ex)
            {
                errors.Add($"• {issue.Title}: {ex.Message}");
            }
        }

        string summary = $"Applied {applied} fix(es).";
        if (errors.Count > 0)
            summary += $"\n\n{errors.Count} fix(es) failed:\n" + string.Join("\n", errors);

        if (needsRestart)
        {
            var res = MessageBox.Show(this,
                summary + "\n\nSome fixes require a restart. Restart ProdToy now?",
                "Fixes Applied",
                MessageBoxButtons.YesNo, MessageBoxIcon.Information, MessageBoxDefaultButton.Button1);
            if (res == DialogResult.Yes)
            {
                RestartApp();
                return;
            }
        }
        else
        {
            MessageBox.Show(this, summary, "Fixes Applied",
                MessageBoxButtons.OK, MessageBoxIcon.Information);
        }

        DialogResult = DialogResult.OK;
        Close();
    }

    private static void RestartApp()
    {
        try
        {
            var exe = AppPaths.ExePath;
            if (File.Exists(exe))
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = true,
                });
            }
        }
        catch { /* best-effort */ }
        Application.Exit();
    }
}
