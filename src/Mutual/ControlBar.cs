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
    readonly Label text = new() { AutoSize = false, Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, ForeColor = Theme.Text, Font = Theme.Small, Padding = new Padding(10, 0, 0, 0) };
    readonly Button toggle, stop;
    StreamHost? host;
    string? hotkey;
    public event Action? StopClicked;

    public ControlBar(string friend, bool wholeScreen)
    {
        this.friend = friend; this.wholeScreen = wholeScreen;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        StartPosition = FormStartPosition.Manual;
        BackColor = Theme.Panel;
        Size = new Size(520, 34);
        var area = Screen.PrimaryScreen!.WorkingArea;
        Location = new Point(area.Left + (area.Width - Width) / 2, area.Top);
        toggle = Theme.Button("Let them control", (_, _) => Toggle(), primary: true);
        stop = Theme.Button("Stop sharing", (_, _) => StopClicked?.Invoke());
        foreach (var b in new[] { stop, toggle }) { b.Dock = DockStyle.Right; b.Height = 34; b.Width = b == toggle ? 150 : 110; Controls.Add(b); }
        Controls.Add(text);
        text.BringToFront();
        Refresh(false);
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
        if (on && wholeScreen && MessageBox.Show(this, friend + " will be able to use everything on this screen, not just one window.\n\nYou can take it back any time" + (hotkey != null ? " with " + hotkey : " with the button on the bar") + ".", "Mutual",
                MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK) return;
        host.SetControl(on);
        Refresh(on);
    }

    public void TakeBack()
    {
        if (host is { AllowInput: true }) { host.SetControl(false); Refresh(false); }
    }

    void Refresh(bool controlling)
    {
        var back = hotkey != null ? "(" + hotkey + " takes it back)" : "(no take back key free, use this button)";
        text.Text = host == null ? "waiting for " + friend + "…" : controlling ? friend + " is using your mouse and keyboard   " + back : "sharing with " + friend + ", they can only watch";
        BackColor = controlling ? Color.FromArgb(120, 60, 20) : Theme.Panel;
        toggle.Text = controlling ? "Take back control" : "Let them control";
        toggle.Enabled = host != null;
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
}
