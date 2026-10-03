namespace Mutual;

sealed class SettingsDialog : Form
{
    readonly AppSettings s;
    readonly TextBox name = new() { Dock = DockStyle.Top };
    readonly TextBox server = new() { Dock = DockStyle.Top, PlaceholderText = "host:port, leave empty if you're on the same vpn or network" };
    readonly CheckBox upnp = new() { Text = "let Mutual open its port on the router (upnp) when using a rendezvous server", Dock = DockStyle.Top, Height = 40 };
    readonly CheckBox sound = new() { Text = "share sound when streaming", Dock = DockStyle.Top, Height = 28 };
    readonly CheckBox clip = new() { Text = "share copied text with your friend during streams (sends whatever you copy)", Dock = DockStyle.Top, Height = 40 };
    readonly CheckBox login = new() { Text = "start with windows (in the tray)", Dock = DockStyle.Top, Height = 28 };

    public SettingsDialog(AppSettings settings)
    {
        s = settings;
        Text = "Mutual settings";
        Icon = Program.AppIcon;
        BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.Body;
        ClientSize = new Size(460, 380);
        FormBorderStyle = FormBorderStyle.FixedDialog; MaximizeBox = false; MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        foreach (var t in new[] { name, server }) { t.BackColor = Theme.Panel; t.ForeColor = Theme.Text; t.BorderStyle = BorderStyle.FixedSingle; }
        name.Text = s.MyName; server.Text = s.Rendezvous; upnp.Checked = s.UseUpnp; sound.Checked = s.ShareSound; clip.Checked = s.ShareClipboard; login.Checked = Takeover.StartsAtLogin();

        var ok = Theme.Button("Save", (_, _) => Save(), primary: true);
        var cancel = Theme.Button("Cancel", (_, _) => Close());
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 50, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
        ok.Width = 100; cancel.Width = 90;
        bar.Controls.Add(ok); bar.Controls.Add(cancel);
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(16, 10, 16, 0) };
        body.Controls.Add(login);
        body.Controls.Add(clip);
        body.Controls.Add(sound);
        body.Controls.Add(upnp);
        body.Controls.Add(server);
        body.Controls.Add(Caption("rendezvous server (for when you're not on the same vpn)"));
        body.Controls.Add(name);
        body.Controls.Add(Caption("your name (goes in your pairing code)"));
        Controls.Add(body);
        Controls.Add(bar);
    }

    static Label Caption(string t) => new() { Text = t, Dock = DockStyle.Top, Height = 26, Padding = new Padding(0, 8, 0, 0), ForeColor = Theme.Dim };

    void Save()
    {
        s.MyName = string.IsNullOrWhiteSpace(name.Text) ? Environment.UserName : name.Text.Trim();
        s.Rendezvous = server.Text.Trim();
        s.UseUpnp = upnp.Checked; s.ShareSound = sound.Checked; s.ShareClipboard = clip.Checked;
        try { Takeover.SetStartsAtLogin(login.Checked); } catch { }
        s.Save();
        DialogResult = DialogResult.OK;
        Close();
    }
}
