using System.Drawing;

namespace ProdToy;

/// <summary>
/// Modal dialog that copies every file under <paramref name="source"/> into
/// <paramref name="dest"/> while showing a progress bar. Used by the Data
/// Sync settings tab when the user redirects (or resets) the data folder
/// so the switch "just works" on the next launch without the user having
/// to move files themselves.
///
/// Existing files at the destination are overwritten — this is intentional
/// for the sync scenario where the destination may already hold a prior
/// snapshot of the same logical data.
/// </summary>
sealed class DataMigrationDialog : Form
{
    private readonly ProgressBar _bar;
    private readonly Label _status;
    private readonly Button _cancelBtn;
    private readonly CancellationTokenSource _cts = new();
    private readonly string _source;
    private readonly string _dest;

    /// <summary>True when all files copied successfully.</summary>
    public bool Success { get; private set; }

    /// <summary>Non-null when the copy failed or was cancelled.</summary>
    public string? Error { get; private set; }

    public DataMigrationDialog(PopupTheme theme, string source, string dest)
    {
        _source = source;
        _dest = dest;

        Text = "Moving data folder";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterParent;
        MaximizeBox = false;
        MinimizeBox = false;
        ControlBox = false;
        ShowInTaskbar = false;
        Size = new Size(560, 180);
        BackColor = theme.BgDark;
        ForeColor = theme.TextPrimary;
        Font = new Font("Segoe UI", 9.5f);

        int pad = 18;

        var header = new Label
        {
            Text = "Copying data to the new folder...",
            Font = new Font("Segoe UI Semibold", 10.5f, FontStyle.Bold),
            ForeColor = theme.TextPrimary,
            AutoSize = true,
            Location = new Point(pad, pad),
            BackColor = Color.Transparent,
        };
        Controls.Add(header);

        _bar = new ProgressBar
        {
            Style = ProgressBarStyle.Continuous,
            Location = new Point(pad, pad + 32),
            Size = new Size(ClientSize.Width - pad * 2, 20),
            Minimum = 0,
            Maximum = 1,
            Value = 0,
        };
        Controls.Add(_bar);

        _status = new Label
        {
            Text = "Scanning files...",
            Font = new Font("Segoe UI", 9f),
            ForeColor = theme.TextSecondary,
            AutoEllipsis = true,
            Location = new Point(pad, pad + 60),
            Size = new Size(ClientSize.Width - pad * 2, 20),
            BackColor = Color.Transparent,
        };
        Controls.Add(_status);

        _cancelBtn = new Button
        {
            Text = "Cancel",
            Font = new Font("Segoe UI", 9f),
            Size = new Size(90, 28),
            Location = new Point(ClientSize.Width - pad - 90, pad + 90),
            FlatStyle = FlatStyle.Flat,
            BackColor = theme.BgHeader,
            ForeColor = theme.TextPrimary,
            Cursor = Cursors.Hand,
        };
        _cancelBtn.FlatAppearance.BorderSize = 0;
        _cancelBtn.Click += (_, _) =>
        {
            _cancelBtn.Enabled = false;
            _cancelBtn.Text = "Cancelling...";
            _cts.Cancel();
        };
        Controls.Add(_cancelBtn);
    }

    protected override async void OnShown(EventArgs e)
    {
        base.OnShown(e);

        try
        {
            Directory.CreateDirectory(_dest);

            // Enumerate on a background thread — on large trees this can take
            // noticeable time and we don't want to freeze the dialog paint.
            var files = await Task.Run(() =>
            {
                if (!Directory.Exists(_source)) return new List<string>();
                return Directory.EnumerateFiles(_source, "*", SearchOption.AllDirectories).ToList();
            }, _cts.Token);

            if (files.Count == 0)
            {
                Success = true;
                Close();
                return;
            }

            _bar.Maximum = files.Count;
            _bar.Value = 0;

            for (int i = 0; i < files.Count; i++)
            {
                _cts.Token.ThrowIfCancellationRequested();

                string src = files[i];
                string rel = Path.GetRelativePath(_source, src);
                string dst = Path.Combine(_dest, rel);

                string? dstDir = Path.GetDirectoryName(dst);
                if (!string.IsNullOrEmpty(dstDir))
                    Directory.CreateDirectory(dstDir);

                // Skip copying a file onto itself (can happen if the user picks
                // a destination that overlaps the source by symlink or case).
                if (!string.Equals(
                        Path.GetFullPath(src),
                        Path.GetFullPath(dst),
                        StringComparison.OrdinalIgnoreCase))
                {
                    await Task.Run(() => File.Copy(src, dst, overwrite: true), _cts.Token);
                }

                int done = i + 1;
                _bar.Value = done;
                _status.Text = $"Copying {done} / {files.Count}: {rel}";
            }

            Success = true;
        }
        catch (OperationCanceledException)
        {
            Error = "Cancelled before completion. Partial data may exist at the destination.";
        }
        catch (Exception ex)
        {
            Error = ex.Message;
        }
        finally
        {
            Close();
        }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        // Swallow the user clicking the X (we hid ControlBox but be safe) —
        // the Cancel button is the only interactive dismissal path.
        if (!Success && Error == null)
        {
            e.Cancel = true;
            return;
        }
        base.OnFormClosing(e);
    }
}
