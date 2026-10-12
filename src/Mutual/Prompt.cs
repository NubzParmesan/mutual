namespace Mutual;

// mutuals own message box so the yes/no prompts match everything else instead of a white windows one
// same shape as MessageBox.Show so it drops in
sealed class Prompt : DarkForm
{
    Prompt(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon)
    {
        Dialog();
        Text = title;
        ShowInTaskbar = true;
        // the first line is the headline when theres more than one paragraph
        var parts = text.Replace("\r\n", "\n").Split("\n\n", 2);
        string? head = parts.Length > 1 && parts[0].Length <= 120 ? parts[0] : null;
        string body = head == null ? text : parts[1];

        var (glyph, tint, fill) = icon switch
        {
            MessageBoxIcon.Warning => (Theme.Glyph.Warning, Theme.Warn, Theme.WarnSoft),
            MessageBoxIcon.Error => (Theme.Glyph.Error, Theme.Bad, Theme.BadSoft),
            MessageBoxIcon.Question => (Theme.Glyph.Info, Theme.Accent, Theme.AccentSoft),
            _ => (Theme.Glyph.Info, Theme.Dim, Theme.Raised),
        };
        const int width = 440, textX = 76, textW = width - textX - 24;
        int y = 24;
        var badge = new Label { Text = glyph, Font = Theme.Icons(16), ForeColor = tint, BackColor = fill, TextAlign = ContentAlignment.MiddleCenter, Bounds = new Rectangle(24, 24, 36, 36), UseMnemonic = false };
        badge.Paint += (_, e) => { e.Graphics.Clear(Theme.Back); Theme.FillRound(e.Graphics, fill, new RectangleF(0, 0, badge.Width - 1, badge.Height - 1), badge.Width / 2f); Theme.DrawGlyph(e.Graphics, glyph, 14, tint, badge.ClientRectangle); };
        Controls.Add(badge);
        if (head != null)
        {
            int h = Theme.MeasureHeight(head, Theme.Heading, textW);
            Controls.Add(new Label { Text = head, Font = Theme.Heading, ForeColor = Theme.Text, Bounds = new Rectangle(textX, y + 2, textW, h + 4), UseMnemonic = false });
            y += h + 12;
        }
        int bh = Theme.MeasureHeight(body, Theme.Body, textW);
        Controls.Add(new Label { Text = body, Font = Theme.Body, ForeColor = head == null ? Theme.Text : Theme.Dim, Bounds = new Rectangle(textX, y + (head == null ? 6 : 0), textW, bh + 6), UseMnemonic = false });
        y = Math.Max(y + bh + 30, 84);

        var choices = buttons switch
        {
            MessageBoxButtons.OKCancel => new[] { ("OK", DialogResult.OK), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.YesNo => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No) },
            MessageBoxButtons.YesNoCancel => new[] { ("Yes", DialogResult.Yes), ("No", DialogResult.No), ("Cancel", DialogResult.Cancel) },
            MessageBoxButtons.RetryCancel => new[] { ("Try again", DialogResult.Retry), ("Cancel", DialogResult.Cancel) },
            _ => new[] { ("OK", DialogResult.OK) },
        };
        var bar = new Panel { Dock = DockStyle.Bottom, Height = 62, BackColor = Theme.Panel, Padding = new Padding(20, 14, 20, 14) };
        for (int i = choices.Length - 1; i >= 0; i--)
        {
            var (label, result) = choices[i];
            var b = new FlatButton { Text = label, Kind = i == 0 ? ButtonKind.Primary : ButtonKind.Secondary, Dock = DockStyle.Right, Width = 92, DialogResult = result };
            bar.Controls.Add(b);
            if (i > 0) bar.Controls.Add(new Panel { Dock = DockStyle.Right, Width = 8 });
            if (i == 0) AcceptButton = b;
            if (result is DialogResult.Cancel or DialogResult.No && CancelButton == null) CancelButton = b;
        }
        if (CancelButton == null && choices.Length == 1) CancelButton = (IButtonControl)bar.Controls[0];
        Controls.Add(bar);
        ClientSize = new Size(width, y + bar.Height);
        Shown += (_, _) => { if (icon is MessageBoxIcon.Warning or MessageBoxIcon.Error) System.Media.SystemSounds.Exclamation.Play(); };
    }

    public static Form Make(string text, string title, MessageBoxButtons buttons, MessageBoxIcon icon) => new Prompt(text, title, buttons, icon);

    public static DialogResult Show(string text, string title = "Mutual", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None) => Show(null, text, title, buttons, icon);
    public static DialogResult Show(IWin32Window? owner, string text, string title = "Mutual", MessageBoxButtons buttons = MessageBoxButtons.OK, MessageBoxIcon icon = MessageBoxIcon.None)
    {
        using var a = new Prompt(text, title, buttons, icon);
        // a hidden owner (main window in the tray) would put this behind everything
        if (owner is Control c && !c.Visible) owner = null;
        if (owner == null) { a.StartPosition = FormStartPosition.CenterScreen; a.TopMost = true; }
        return a.ShowDialog(owner);
    }
}
