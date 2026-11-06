namespace Mutual;

sealed class SettingsDialog : DarkForm
{
    readonly AppSettings s;
    readonly Field name = new(), server = new();
    readonly Switch upnp = new() { Text = "Open Mutual's port on the router (UPnP)", Description = "Only used with a rendezvous server. Lets your friend connect straight to you instead of thru the relay." };
    readonly Switch sound = new() { Text = "Share sound when streaming", Description = "Just the shared program's sound for a window or game. Discord is always left out." };
    readonly Switch clip = new() { Text = "Share copied text during streams", Description = "Anything you copy while a stream is up goes to your friend, and theirs comes to you." };
    readonly Switch login = new() { Text = "Start with Windows", Description = "Sits in the tray so your friend can reach you without you opening it." };
    readonly Switch updates = new() { Text = "Get updates ready on their own", Description = "Downloads new versions in the background so updating is one click. Nothing installs until you say yes." };

    public SettingsDialog(AppSettings settings)
    {
        s = settings;
        Dialog();
        Text = "Settings";
        ClientSize = new Size(480, 776);
        name.Box.Text = s.MyName; name.Box.MaxLength = 40;
        server.Box.Text = s.Rendezvous; server.Box.PlaceholderText = "host:port";
        upnp.Checked = s.UseUpnp; sound.Checked = s.ShareSound; clip.Checked = s.ShareClipboard; login.Checked = Takeover.StartsAtLogin(); updates.Checked = s.AutoDownload;
        // upnp means nothing without a server
        void SyncUpnp() => upnp.Enabled = server.Box.Text.Trim().Length > 0;
        server.Box.TextChanged += (_, _) => SyncUpnp();
        SyncUpnp();

        var ok = new FlatButton { Text = "Save", Kind = ButtonKind.Primary, Width = 96 };
        ok.Click += (_, _) => Save();
        var cancel = new FlatButton { Text = "Cancel", Width = 90 };
        cancel.Click += (_, _) => Close();
        AcceptButton = ok; CancelButton = cancel;

        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(20, 8, 20, 8), AutoScroll = true };
        // docked top so they go in backwards
        Section(body, "This PC", login, Gap(), updates);
        Section(body, "While streaming", sound, Gap(), clip);
        Section(body, "Connection", Theme.Caption("Rendezvous server, only if you're not on the same VPN"), server, Gap(14), upnp);
        Section(body, "You", Theme.Caption("Your name, it goes in your pairing code"), name);
        var version = new Label { Text = "Mutual " + Updater.Current + (AppSettings.Profile != null ? "  ·  profile " + AppSettings.Profile : ""), ForeColor = Theme.Faint, Font = Theme.Small, AutoSize = true, Dock = DockStyle.Left, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(0, 9, 0, 0) };
        var bar = ButtonBar(ok, cancel);
        bar.Controls.Add(version);
        Controls.Add(body);
        Controls.Add(bar);
        ActiveControl = name.Box;
    }

    static Panel Gap(int h = 10) => new() { Dock = DockStyle.Top, Height = h };

    // a heading and a card with the controls in it
    static void Section(Panel body, string title, params Control[] topDown)
    {
        var card = new Card { Dock = DockStyle.Top, Padding = new Padding(16, 12, 16, 12) };
        foreach (var c in topDown.Reverse()) { c.Dock = DockStyle.Top; card.Controls.Add(c); }
        foreach (var c in topDown.OfType<Switch>()) c.Height = string.IsNullOrEmpty(c.Description) ? 28 : 28 + Theme.MeasureHeight(c.Description, Theme.Small, 336) + 6;
        card.Height = card.Padding.Vertical + topDown.Sum(c => c.Height);
        body.Controls.Add(Gap(6));
        body.Controls.Add(card);
        body.Controls.Add(new Label { Text = title.ToUpper(), Font = Theme.SmallBold, ForeColor = Theme.Faint, Dock = DockStyle.Top, Height = 28, Padding = new Padding(2, 10, 0, 0) });
    }

    void Save()
    {
        s.MyName = string.IsNullOrWhiteSpace(name.Box.Text) ? Environment.UserName : name.Box.Text.Trim();
        s.Rendezvous = server.Box.Text.Trim();
        s.UseUpnp = upnp.Checked; s.ShareSound = sound.Checked; s.ShareClipboard = clip.Checked; s.AutoDownload = updates.Checked;
        try { Takeover.SetStartsAtLogin(login.Checked); }
        catch (Exception e) { Prompt.Show(this, "Couldn't change Start with Windows: " + e.Message, "Mutual"); }
        s.Save();
        DialogResult = DialogResult.OK;
        Close();
    }
}
