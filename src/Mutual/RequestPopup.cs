using Mutual.Core;

namespace Mutual;

// the "wants to stream" popup in the corner. stays on top but never takes focus so your game keeps the mouse
// its a plain window not a toast because toasts get dropped when notifications are off
// nothing happens unless you hit accept
sealed class RequestPopup : Form
{
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get { var p = base.CreateParams; p.ExStyle |= 0x8 | 0x80 | 0x08000000; return p; }   // topmost, tool window, no activate
    }

    public RequestPopup(string friend, Request req, Action accept)
    {
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        DoubleBuffered = true;

        var (what, glyph) = req.Kind switch
        {
            RequestKind.Ssh => ("wants to open SSH", Theme.Glyph.Terminal),
            RequestKind.Stream => ("wants to stream to you", Theme.Glyph.Screen),
            RequestKind.Play => ("wants to play together", Theme.Glyph.Game),
            RequestKind.File => ("wants to send you a file", Theme.Glyph.Download),
            _ => ("wants to connect", Theme.Glyph.Link),
        };
        string? detail = req.Kind switch
        {
            RequestKind.Ssh => "SSH turns on for this session only, and back off when either of you closes it",
            _ => string.IsNullOrEmpty(req.Detail) ? null : req.Detail,
        };
        bool risky = req.Kind == RequestKind.File && FileTransfer.CanRun(req.Detail);

        const int width = 380, textX = 70, textW = width - textX - 18;
        var avatar = new Avatar { Who = friend, Ring = Theme.Accent, Size = new Size(40, 40), Location = new Point(18, 18) };
        var badge = new Label { Text = glyph, Font = Theme.Icons(9), ForeColor = Theme.Back, BackColor = Theme.Accent, Size = new Size(20, 20), Location = new Point(42, 42), TextAlign = ContentAlignment.MiddleCenter };
        badge.Paint += (_, e) => { e.Graphics.Clear(Theme.Panel); Theme.FillRound(e.Graphics, Theme.Accent, new RectangleF(0, 0, badge.Width - 1, badge.Height - 1), badge.Width / 2f); Theme.DrawGlyph(e.Graphics, glyph, 8, Theme.Back, badge.ClientRectangle); };
        Controls.Add(badge);
        Controls.Add(avatar);

        int y = 16;
        var head = new Label { Text = friend + " " + what, Font = Theme.Heading, ForeColor = Theme.Text, UseMnemonic = false, AutoEllipsis = true, Bounds = new Rectangle(textX, y, textW - 56, 22) };
        var when = new Label { Text = DateTime.Now.ToString("h:mm tt"), Font = Theme.Small, ForeColor = Theme.Faint, TextAlign = ContentAlignment.TopRight, Bounds = new Rectangle(width - 74, y + 2, 58, 18) };
        Controls.Add(head); Controls.Add(when);
        y += 24;
        if (detail != null)
        {
            int h = Math.Min(54, Theme.MeasureHeight(detail, Theme.Small, textW));
            Controls.Add(new Label { Text = detail, Font = Theme.Small, ForeColor = Theme.Dim, UseMnemonic = false, AutoEllipsis = true, Bounds = new Rectangle(textX, y, textW, h + 2) });
            y += h + 4;
        }
        if (risky)
        {
            Controls.Add(new Label { Text = Theme.Glyph.Warning, Font = Theme.Icons(9), ForeColor = Theme.Warn, Bounds = new Rectangle(textX, y + 2, 16, 18) });
            Controls.Add(new Label { Text = "This kind of file can run code. Only open it if you trust it.", Font = Theme.Small, ForeColor = Theme.Warn, Bounds = new Rectangle(textX + 18, y, textW - 18, 20) });
            y += 22;
        }
        y = Math.Max(y + 10, 70);

        shownAt = DateTime.UtcNow;
        this.accept = accept;
        // you might be fullscreen in a game and this never takes focus, so theres a key for it too
        hint = new Label { Text = "Ctrl+Alt+Y accepts", Font = Theme.Small, ForeColor = Theme.Faint, Bounds = new Rectangle(textX, y + 8, 110, 18) };
        Controls.Add(hint);
        var yes = new FlatButton { Text = "Accept", Glyph = Theme.Glyph.Check, Kind = ButtonKind.Primary, Bounds = new Rectangle(width - 18 - 100 - 8 - 86, y, 100, 32) };
        // a click in the first second is prolly someone mid game, not a real yes
        yes.Click += (_, _) => Accept();
        var no = new FlatButton { Text = "Ignore", Bounds = new Rectangle(width - 18 - 86, y, 86, 32) };
        no.Click += (_, _) => Close();
        Controls.Add(yes); Controls.Add(no);
        Size = new Size(width, y + 32 + 16);

        // grow to the screens scaling now so the corner spot is worked out from the real size
        Theme.ScaleUp(this);
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Right - Width - this.S(16), area.Bottom - Height - this.S(16));
        // their side stops waiting after 10 min so this goes away too
        var expire = new System.Windows.Forms.Timer { Interval = 600_000 };
        expire.Tick += (_, _) => Close();
        expire.Start();
        // slides up a bit while it fades in
        Opacity = 0;
        int finalY = Location.Y, step = 0;
        var fade = new System.Windows.Forms.Timer { Interval = 15 };
        fade.Tick += (_, _) =>
        {
            step++;
            float t = Math.Min(1, step / 14f), ease = 1 - (1 - t) * (1 - t);
            Opacity = ease;
            Top = finalY + (int)((1 - ease) * this.S(18));
            if (t >= 1) fade.Stop();
        };
        Shown += (_, _) => fade.Start();
        FormClosed += (_, _) => { fade.Dispose(); expire.Dispose(); };
        System.Media.SystemSounds.Asterisk.Play();
    }

    readonly Action accept;
    readonly Label hint;
    DateTime shownAt;
    bool hotkey;
    const int HotkeyId = 0x4D56;

    void Accept()
    {
        if ((DateTime.UtcNow - shownAt).TotalMilliseconds < 1000 || IsDisposed) return;
        Close();
        accept();
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        // only one popup can own the key, the others js dont show the hint
        hotkey = RegisterHotKey(Handle, HotkeyId, 0x1 | 0x2 | 0x4000, 0x59);   // ctrl+alt+y, no repeat
        hint.Visible = hotkey;
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && (int)m.WParam == HotkeyId) Accept();   // WM_HOTKEY
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        if (hotkey) UnregisterHotKey(Handle, HotkeyId);
        base.OnFormClosed(e);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [System.Runtime.InteropServices.DllImport("user32.dll")] static extern bool UnregisterHotKey(nint h, int id);

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
        using var stripe = new SolidBrush(Theme.Accent);
        e.Graphics.FillRectangle(stripe, 0, 0, this.S(3), Height);
    }
}
