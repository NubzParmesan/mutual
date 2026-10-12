using System.Runtime.InteropServices;
using Mutual.Stream;

namespace Mutual;

// the little bar at the top of your screen while youre sharing. says whether your friend can use
// your mouse and keyboard and lets you hand it over or take it back. Ctrl+Alt+End takes it back
// from anywhere. hidden from capture so it never shows up in the stream
sealed class ControlBar : Form
{
    const int HotkeyId = 0x4D55;
    readonly string friend;
    readonly bool wholeScreen;
    readonly Label text = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Font = Theme.Small, Padding = new Padding(30, 0, 8, 0), UseMnemonic = false };
    readonly FlatButton toggle, stop;
    StreamHost? host;
    string? hotkey;
    bool controlling;
    float scaled = 1;
    public event Action? StopClicked;

    public ControlBar(string friend, bool wholeScreen)
    {
        this.friend = friend; this.wholeScreen = wholeScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        DoubleBuffered = true;
        Size = new Size(600, 44);
        Padding = new Padding(6);
        toggle = new FlatButton { Text = "Let them control", Glyph = Theme.Glyph.Mouse, Kind = ButtonKind.Primary, Width = 156, Dock = DockStyle.Right };
        toggle.Click += (_, _) => Toggle();
        stop = new FlatButton { Text = "Stop sharing", Glyph = Theme.Glyph.Stop, Width = 120, Dock = DockStyle.Right };
        stop.Click += (_, _) => StopClicked?.Invoke();
        Controls.Add(text);
        Controls.Add(toggle);
        Controls.Add(new Panel { Dock = DockStyle.Right, Width = 6 });
        Controls.Add(stop);
        // drag it out of the way by the text
        text.MouseDown += (_, e) => { if (e.Button == MouseButtons.Left) { ReleaseCapture(); SendMessage(Handle, 0xA1, 2, 0); } };
        text.Cursor = Cursors.SizeAll;
        text.Paint += (_, e) =>
        {
            // live dot
            var c = controlling ? Theme.Warn : host == null ? Theme.Faint : Theme.Bad;
            float k = Theme.K(text);
            Theme.FillRound(e.Graphics, c, new RectangleF(12 * k, text.Height / 2f - 5 * k, 10 * k, 10 * k), 5 * k);
        };
        Refresh(false);
        Theme.ScaleUp(this);
        scaled = Theme.K(this);
        text.Padding = new Padding(this.S(30), 0, this.S(8), 0);
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top + this.S(8));
    }

    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams { get { var p = base.CreateParams; p.ExStyle |= 0x80 | 0x08000000; return p; } }   // tool window, no activate

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        SetWindowDisplayAffinity(Handle, 0x11);   // never in the stream
        // another program might already own one of these, so take the first that works and show that one
        foreach (var (mods, vk, name) in new[] { (0x1u | 0x2u, 0x23u, "Ctrl+Alt+End"), (0x1u | 0x2u, 0x7Bu, "Ctrl+Alt+F12"), (0x2u | 0x4u, 0x7Bu, "Ctrl+Shift+F12"), (0x1u | 0x2u, 0x13u, "Ctrl+Alt+Pause") })
            if (RegisterHotKey(Handle, HotkeyId, mods | 0x4000, vk)) { hotkey = name; break; }
        Refresh(host?.AllowInput == true);
    }

    // a new link (first one or after a reconnect) always starts as watch only
    public void Attach(StreamHost? h)
    {
        host = h;
        if (h != null) h.SetControl(false);
        Refresh(false);
    }

    void Toggle()
    {
        if (host == null) return;
        bool on = !host.AllowInput;
        if (on && wholeScreen && Prompt.Show(this, friend + " will be able to use everything on this screen, not just one window.\n\nYou can take it back any time" + (hotkey != null ? " with " + hotkey : " with the button on the bar") + ".", "Mutual",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        host.SetControl(on);
        Refresh(on);
    }

    public void TakeBack()
    {
        if (host is { AllowInput: true }) { host.SetControl(false); Refresh(false); }
    }

    void Refresh(bool on)
    {
        controlling = on;
        var back = hotkey ?? "the button";
        text.Text = host == null ? "Waiting for " + friend + " to join…" : on ? friend + " is using your mouse and keyboard.  " + back + " takes it back" : "Live  ·  " + friend + " is watching";
        BackColor = on ? Color.FromArgb(74, 48, 18) : Theme.Panel;
        toggle.Text = on ? "Take back control" : "Let them control";
        toggle.Kind = on ? ButtonKind.Secondary : ButtonKind.Primary;
        toggle.Width = (int)((on ? 168 : 156) * scaled);
        toggle.Enabled = host != null;
        Invalidate(true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        using var pen = new Pen(controlling ? Theme.Warn : Theme.Line);
        e.Graphics.DrawRectangle(pen, 0, 0, Width - 1, Height - 1);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x0312 && (int)m.WParam == HotkeyId) TakeBack();   // WM_HOTKEY
        base.WndProc(ref m);
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        UnregisterHotKey(Handle, HotkeyId);
        base.OnFormClosed(e);
    }

    [DllImport("user32.dll")] static extern bool SetWindowDisplayAffinity(nint h, uint a);
    [DllImport("user32.dll")] static extern bool RegisterHotKey(nint h, int id, uint mods, uint vk);
    [DllImport("user32.dll")] static extern bool UnregisterHotKey(nint h, int id);
    [DllImport("user32.dll")] static extern bool ReleaseCapture();
    [DllImport("user32.dll")] static extern nint SendMessage(nint h, int msg, nint w, nint l);
}
