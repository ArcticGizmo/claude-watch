namespace ClaudeWatch;

/// <summary>
/// A small modal dialog for adding or editing a custom <see cref="QuickLink"/>: a display name and
/// the executable to launch (with a Browse… picker). Dark-themed to match the settings window.
/// On <see cref="DialogResult.OK"/> the chosen values are exposed via <see cref="LinkName"/> and
/// <see cref="LinkPath"/>; the caller decides whether they map onto a new or an existing link.
/// </summary>
internal sealed class QuickLinkDialog : Form
{
    private readonly TextBox _nameBox;
    private readonly TextBox _pathBox;

    public string LinkName => _nameBox.Text.Trim();
    public string LinkPath => _pathBox.Text.Trim();

    public QuickLinkDialog(QuickLink? existing)
    {
        Text            = existing == null ? "Add quick link" : "Edit quick link";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition   = FormStartPosition.CenterParent;
        MaximizeBox     = false;
        MinimizeBox     = false;
        ShowInTaskbar   = false;
        BackColor       = Theme.FormBg;
        ForeColor       = Theme.Fg;
        Font            = new Font("Segoe UI", 9f, FontStyle.Regular, GraphicsUnit.Point);
        ClientSize      = new Size(440, 196);

        const int pad = 16;
        int innerW = ClientSize.Width - pad * 2;

        var nameCaption = Caption("Name", pad, pad);
        _nameBox = MakeTextBox(existing?.Name ?? "");
        _nameBox.SetBounds(pad, nameCaption.Bottom + 4, innerW, _nameBox.Height);

        var pathCaption = Caption("Program (.exe)", pad, _nameBox.Bottom + 12);
        _pathBox = MakeTextBox(existing?.ExePath ?? "");
        const int browseW = 92, gap = 8;
        _pathBox.SetBounds(pad, pathCaption.Bottom + 4, innerW - browseW - gap, _pathBox.Height);

        var browse = MakeButton("Browse…");
        browse.SetBounds(_pathBox.Right + gap, _pathBox.Top - 1, browseW, _pathBox.Height + 2);
        browse.Click += (_, _) => Browse();

        var ok = MakeButton("Save");
        ok.DialogResult = DialogResult.OK;
        var cancel = MakeButton("Cancel");
        cancel.DialogResult = DialogResult.Cancel;

        const int btnW = 92, btnH = 30;
        int btnY = ClientSize.Height - pad - btnH;
        cancel.SetBounds(ClientSize.Width - pad - btnW, btnY, btnW, btnH);
        ok.SetBounds(cancel.Left - btnW - gap, btnY, btnW, btnH);

        // Guard against an empty name — the overlay needs something to label the icon's fallback.
        void Validate() => ok.Enabled = _nameBox.Text.Trim().Length > 0;
        _nameBox.TextChanged += (_, _) => Validate();
        Validate();

        Controls.AddRange([nameCaption, _nameBox, pathCaption, _pathBox, browse, ok, cancel]);
        AcceptButton = ok;
        CancelButton = cancel;
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        NativeMethods.UseDarkTitleBar(Handle);
    }

    private void Browse()
    {
        using var dlg = new OpenFileDialog
        {
            Title            = "Choose a program",
            Filter           = "Programs (*.exe)|*.exe|All files (*.*)|*.*",
            CheckFileExists  = true,
        };
        if (!string.IsNullOrWhiteSpace(_pathBox.Text))
        {
            try { dlg.InitialDirectory = Path.GetDirectoryName(_pathBox.Text); } catch { }
        }
        if (dlg.ShowDialog(this) == DialogResult.OK)
        {
            _pathBox.Text = dlg.FileName;
            // Offer a sensible default name from the file when the name field is still empty.
            if (_nameBox.Text.Trim().Length == 0)
                _nameBox.Text = Path.GetFileNameWithoutExtension(dlg.FileName);
        }
    }

    private static Label Caption(string text, int x, int y) => new()
    {
        Text      = text,
        AutoSize  = true,
        ForeColor = Theme.Muted,
        Location  = new Point(x, y),
    };

    private static TextBox MakeTextBox(string value) => new()
    {
        Text        = value,
        Height      = 26,
        BackColor   = Theme.ButtonBg,
        ForeColor   = Theme.Fg,
        BorderStyle = BorderStyle.FixedSingle,
        Font        = new Font("Segoe UI", 9.5f, FontStyle.Regular, GraphicsUnit.Point),
    };

    private static Button MakeButton(string text)
    {
        var b = new Button
        {
            Text      = text,
            FlatStyle = FlatStyle.Flat,
            ForeColor = Theme.Fg,
            BackColor = Theme.ButtonBg,
            UseVisualStyleBackColor = false,
        };
        b.FlatAppearance.BorderColor        = Theme.Border;
        b.FlatAppearance.MouseOverBackColor = Theme.ButtonHover;
        b.FlatAppearance.MouseDownBackColor = Theme.Border;
        return b;
    }
}
