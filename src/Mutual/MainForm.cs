using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Mutual.Core;
using Mutual.Stream;

namespace Mutual;

// the main window. who youre paired with, if theyre on, the buttons and whats happened
// closing it js hides it in the tray, quit is in the tray menu
sealed class MainForm : DarkForm
{
    readonly AppSettings settings;
    Pairing? pairing;
    string? pairingError;
    ActivityLog? log;
    LinkHub? hub;
    Rendezvous? rendezvous;
    readonly NotifyIcon tray;
    readonly Label friendName = new(), friendStatus = new(), pairInfo = new();
    readonly Avatar avatar = new();
    readonly Banner setupBar = new(), updateBar = new(), sessionBar = new(), versionBar = new();
    readonly Panel header = new(), tiles = new(), welcome = new(), feedHead = new(), feedHost = new();
    FlatButton? updateButton;
    readonly ToolStripMenuItem trayUpdate = new("Update Mutual…") { Visible = false };
    Updater.Release? update;
    readonly ActivityList activity = new();
    readonly CancellationTokenSource quit = new();
    readonly ToolStripMenuItem trayStatus = new() { Enabled = false };
    readonly ToolStripMenuItem trayLogin = new("Start with Windows") { CheckOnClick = true };
    Tile? streamButton, sshButton, sendButton, playButton;
    bool online, checking = true, quitting;
    readonly bool offline;

    public MainForm(AppSettings s, bool offline = false)
    {
        settings = s; this.offline = offline;
        Text = AppSettings.Profile == null ? "Mutual" : "Mutual (" + AppSettings.Profile + ")";
        ClientSize = new Size(480, 640);
        MinimumSize = new Size(440, 540);
        StartPosition = FormStartPosition.CenterScreen;

        tray = new NotifyIcon { Icon = Program.TrayIcon(false), Text = "Mutual", Visible = !offline };
        var menu = Theme.Menu();
        menu.Items.Add(trayStatus);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Open Mutual", null, (_, _) => ShowMe());
        menu.Items.Add("Stream…", null, (_, _) => { ShowMe(); StartStream(); });
        menu.Items.Add("Send file…", null, (_, _) => { ShowMe(); SendFile(); });
        menu.Items.Add("SSH", null, (_, _) => StartSsh());
        menu.Items.Add("Play RimWorld together", null, (_, _) => { ShowMe(); _ = PlayRimWorld(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(trayLogin);
        menu.Items.Add("Pair with someone new…", null, (_, _) => { ShowMe(); Pair(); });
        menu.Items.Add("Settings…", null, (_, _) => { ShowMe(); OpenSettings(); });
        menu.Items.Add("Clear activity history…", null, (_, _) => ClearHistory());
        menu.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add("Report a problem…", null, (_, _) => { ShowMe(); ReportProblem(); });
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(trayUpdate);
        trayUpdate.Click += (_, _) => { ShowMe(); _ = UpdateNow(); };
        if (Updater.Enabled) menu.Items.Add("Check for updates", null, (_, _) => _ = CheckForUpdateLoud());
        menu.Items.Add("Quit", null, (_, _) => Quit());
        menu.Opening += (_, _) => trayLogin.Checked = Takeover.StartsAtLogin();
        trayLogin.Click += (_, _) => { try { Takeover.SetStartsAtLogin(trayLogin.Checked); } catch (Exception e) { Prompt.Show(this, e.Message, "Mutual"); } };
        tray.ContextMenuStrip = menu;
        tray.MouseClick += (_, e) => { if (e.Button == MouseButtons.Left) ShowMe(); };

        BuildLayout();
        LoadPairing();
        if (offline) return;
        // started at login it sits in the tray and never gets shown, so no window handle, and every
        // BeginInvoke from the network threads threw. your friends invites just vanished until you
        // opened the window once. a hidden handle fixes that and shows nothing
        CreateHandle();
        NoteIfJustUpdated();
        StartServices();
        if (Updater.Enabled) _ = UpdateLoop(quit.Token);
        var refresh = new System.Windows.Forms.Timer { Interval = 3000 };
        refresh.Tick += (_, _) => RefreshActivity();
        refresh.Start();
    }

    void BuildLayout()
    {
        // who youre paired with
        header.Dock = DockStyle.Top; header.Height = 100;
        avatar.Location = new Point(20, 22);
        friendName.SetBounds(86, 18, 290, 32); friendName.Font = Theme.Hero; friendName.ForeColor = Theme.Text; friendName.UseMnemonic = false; friendName.AutoEllipsis = true;
        friendName.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        friendStatus.SetBounds(102, 51, 280, 20); friendStatus.Font = Theme.Body; friendStatus.ForeColor = Theme.Dim; friendStatus.UseMnemonic = false;
        pairInfo.SetBounds(86, 72, 370, 18); pairInfo.ForeColor = Theme.Faint; pairInfo.Font = Theme.Small; pairInfo.AutoEllipsis = true; pairInfo.UseMnemonic = false;
        pairInfo.Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right;
        var more = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Theme.Glyph.More, Size = new Size(36, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right, TabStop = false };
        var gear = new FlatButton { Kind = ButtonKind.Ghost, Glyph = Theme.Glyph.Settings, Size = new Size(36, 34), Anchor = AnchorStyles.Top | AnchorStyles.Right, TabStop = false };
        header.Width = ClientSize.Width;
        more.Location = new Point(header.Width - 16 - 36, 20);
        gear.Location = new Point(more.Left - 38, 20);
        gear.Click += (_, _) => OpenSettings();
        more.Click += (_, _) => MoreMenu().Show(more, new Point(more.Width, more.Height + 2), ToolStripDropDownDirection.BelowLeft);
        var tip = new ToolTip();
        tip.SetToolTip(gear, "Settings"); tip.SetToolTip(more, "More");
        header.Controls.AddRange(new Control[] { avatar, friendName, friendStatus, pairInfo, gear, more });
        header.Paint += (_, e) =>
        {
            // status dot in front of the status text
            if (pairing == null) return;
            var c = checking ? Theme.Faint : online ? Theme.Online : Theme.Offline;
            float k = Theme.K(header);
            Theme.FillRound(e.Graphics, c, new RectangleF(88 * k, 57 * k, 9 * k, 9 * k), 4.5f * k);
        };

        // things that need you, only one or two show at a time
        setupBar.Style(Theme.Glyph.Shield, Theme.Warn, Theme.WarnSoft);
        setupBar.AddButton("Set up…", RunSetup);
        updateBar.Style(Theme.Glyph.Update, Theme.Online, Theme.OnlineSoft);
        updateButton = updateBar.AddButton("Update", () => _ = UpdateNow());
        versionBar.Style(Theme.Glyph.Info, Theme.Accent, Theme.AccentSoft);
        versionBar.AddButton("Copy a note", CopyUpdateNote, ButtonKind.Secondary);

        // the four things you can do
        tiles.Dock = DockStyle.Top; tiles.Height = 76 * 2 + 8 + 12; tiles.Padding = new Padding(16, 0, 16, 12);
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 2, Margin = new Padding(0) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        streamButton = new Tile { Text = "Stream", Subtitle = "Share your screen, a window or a box", Glyph = Theme.Glyph.Screen, Kind = ButtonKind.Primary };
        sendButton = new Tile { Text = "Send a file", Subtitle = "Or drop one on this window", Glyph = Theme.Glyph.Send, GlyphColor = Color.FromArgb(110, 170, 255) };
        playButton = new Tile { Text = "Play RimWorld", Subtitle = "You host, they get the right half", Glyph = Theme.Glyph.Game, GlyphColor = Color.FromArgb(190, 130, 255) };
        sshButton = new Tile { Text = "SSH", Subtitle = "A terminal on their PC, if they say yes", Glyph = Theme.Glyph.Terminal, GlyphColor = Theme.Online };
        streamButton.Click += (_, _) => StartStream();
        sendButton.Click += (_, _) => SendFile();
        playButton.Click += (_, _) => _ = PlayRimWorld();
        sshButton.Click += (_, _) => StartSsh();
        foreach (var t in new[] { streamButton, sendButton, playButton, sshButton })
        {
            t.Dock = DockStyle.Fill; t.Margin = new Padding(4);
            grid.Controls.Add(t);
        }
        tiles.Controls.Add(grid);

        // shown instead of the tiles until youre paired
        welcome.Dock = DockStyle.Top; welcome.Height = 300; welcome.Padding = new Padding(16, 0, 16, 12);
        var hero = new Card { Dock = DockStyle.Fill, Padding = new Padding(22, 20, 22, 18) };
        var heroTitle = new Label { Text = "Pair with a friend to get started", Font = Theme.Title, ForeColor = Theme.Text, Dock = DockStyle.Top, Height = 34 };
        var heroText = new Label
        {
            Text = "Mutual links your PC with one friend's. Stream to each other, send files, play RimWorld split screen or open SSH. Nothing happens unless the other person clicks yes.",
            Font = Theme.Body, ForeColor = Theme.Dim, Dock = DockStyle.Top, Height = 64,
        };
        var steps = new StepList(new[]
        {
            "You each copy your pairing code and send it (Discord, text, anything)",
            "Paste theirs in",
            "Read the safety code to each other out loud",
        }) { Dock = DockStyle.Top, Height = 86 };
        var pairNow = new FlatButton { Text = "Pair with a friend", Glyph = Theme.Glyph.People, Kind = ButtonKind.Primary, Dock = DockStyle.Left, Width = 190 };
        pairNow.Click += (_, _) => Pair();
        var pairRow = new Panel { Dock = DockStyle.Top, Height = 46, Padding = new Padding(0, 8, 0, 0) };
        pairRow.Controls.Add(pairNow);
        hero.Controls.Add(pairRow);
        hero.Controls.Add(steps);
        hero.Controls.Add(heroText);
        hero.Controls.Add(heroTitle);
        welcome.Controls.Add(hero);

        // whats happened
        feedHead.Dock = DockStyle.Top; feedHead.Height = 28;
        feedHead.Controls.Add(new Label { Text = "ACTIVITY", Font = Theme.SmallBold, ForeColor = Theme.Faint, Location = new Point(21, 6), AutoSize = true });
        feedHost.Dock = DockStyle.Fill; feedHost.Padding = new Padding(16, 0, 16, 16);
        var feedCard = new Card { Dock = DockStyle.Fill, Padding = new Padding(2, 8, 2, 8) };
        activity.Dock = DockStyle.Fill;
        feedCard.Controls.Add(activity);
        feedHost.Controls.Add(feedCard);

        Controls.Add(feedHost);
        Controls.Add(feedHead);
        Controls.Add(welcome);
        Controls.Add(tiles);
        Controls.Add(Slot(sessionBar));
        Controls.Add(Slot(setupBar));
        Controls.Add(Slot(versionBar));
        Controls.Add(Slot(updateBar));
        Controls.Add(header);

        // drop a file anywhere on the window to send it
        AllowDropEverywhere(this);
    }

    // banners get a holder for the gap under them
    static Panel Slot(Banner b)
    {
        var holder = new Panel { Dock = DockStyle.Top, Height = b.Height + 10, Padding = new Padding(16, 0, 16, 10), Visible = false };
        holder.Controls.Add(b);
        b.Dock = DockStyle.Fill;
        return holder;
    }

    ContextMenuStrip MoreMenu()
    {
        var m = Theme.Menu();
        m.Items.Add(pairing == null ? "Pair with a friend…" : "Pair with someone new…", null, (_, _) => Pair());
        m.Items.Add("Settings…", null, (_, _) => OpenSettings());
        if (Updater.Enabled) m.Items.Add("Check for updates", null, (_, _) => _ = CheckForUpdateLoud());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Open log folder", null, (_, _) => OpenLogFolder());
        m.Items.Add("Report a problem…", null, (_, _) => ReportProblem());
        m.Items.Add("Clear activity history…", null, (_, _) => ClearHistory());
        m.Items.Add(new ToolStripSeparator());
        m.Items.Add("Quit Mutual", null, (_, _) => Quit());
        m.Closed += (_, _) => BeginInvoke(m.Dispose);
        return m;
    }

    void OpenLogFolder() => Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + AppSettings.Dir + "\"") { UseShellExecute = true });

    void Quit() { quitting = true; Close(); }

    // windows messages already end in a period, which read as ".." inside the brackets
    static string Why(Exception e) => e.Message.Trim().TrimEnd('.');

    static void OpenPage(string url) { try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { } }

    // copies what someone helping you would ask for first. mutual.log has your friends name and address
    // in it, so it says so instead of sending anything anywhere itself
    void ReportProblem()
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Mutual " + Updater.Current + " on " + Environment.OSVersion.VersionString + (AppSettings.Profile != null ? " (profile " + AppSettings.Profile + ")" : ""));
        sb.AppendLine("installed: " + SelfInstall.RunningInstalled + ", paired: " + (pairing != null) + ", friend online: " + online + ", friend on: " + (peerVersion?.ToString() ?? (online ? "older than 1.4" : "unknown")));
        sb.AppendLine("--- last lines of mutual.log");
        try { foreach (var l in File.ReadLines(Diag.PathName).TakeLast(60)) sb.AppendLine(l); } catch { sb.AppendLine("(no log yet)"); }
        try { Clipboard.SetText(sb.ToString()); } catch { }
        if (Prompt.Show(this, "Copied what's needed to look into it (your version, Windows version and the end of Mutual's log).\n\nOpen GitHub to write it up and paste it in? The log has your friend's name and address in it, cut those out if you'd rather.",
                "Report a problem", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK)
            OpenPage("https://github.com/" + SelfInstall.OfficialRepo + "/issues/new");
    }

    void AllowDropEverywhere(Control c)
    {
        c.AllowDrop = true;
        c.DragEnter += (_, e) =>
        {
            e.Effect = pairing != null && e.Data?.GetDataPresent(DataFormats.FileDrop) == true ? DragDropEffects.Copy : DragDropEffects.None;
            if (sendButton != null && sendButton.Highlight != (e.Effect == DragDropEffects.Copy)) { sendButton.Highlight = e.Effect == DragDropEffects.Copy; sendButton.Invalidate(); }
        };
        c.DragLeave += (_, _) => { if (sendButton is { Highlight: true }) { sendButton.Highlight = false; sendButton.Invalidate(); } };
        c.DragDrop += (_, e) =>
        {
            if (sendButton != null) { sendButton.Highlight = false; sendButton.Invalidate(); }
            if (e.Data?.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
            var f = files.FirstOrDefault(File.Exists);
            if (f == null) { Prompt.Show(this, "Mutual sends one file at a time, not folders. Zip the folder and drop the zip.", "Mutual"); return; }
            if (files.Length > 1) log?.Write(ActivityResult.INFO, "Mutual: dropped " + files.Length + " things, sending just " + Path.GetFileName(f));
            Activate();
            SendFile(f);
        };
        foreach (Control k in c.Controls) AllowDropEverywhere(k);
    }

    // pairing

    void LoadPairing()
    {
        pairing = null; pairingError = null;
        try
        {
            if (ignoreSaved) { }
            else if (Pairing.SavedExists(AppSettings.Dir)) pairing = Pairing.Load(AppSettings.Dir);
            else if (AppSettings.Profile == null && Pairing.LegacyFolderExists(settings.PairingFolder)) pairing = Pairing.FromLegacyFolder(settings.PairingFolder);
        }
        catch (Exception e) { pairingError = e.Message; }

        if (pairing == null)
        {
            friendName.Text = "Not paired yet";
            friendStatus.Text = "You both do this once";
            pairInfo.Text = pairingError ?? "";
            pairInfo.ForeColor = pairingError == null ? Theme.Faint : Theme.Bad;
            log = new ActivityLog(Path.Combine(AppSettings.Dir, "activity.jsonl"));
            Directory.CreateDirectory(AppSettings.Dir);
        }
        else
        {
            log = new ActivityLog(Path.Combine(pairing.Folder, "activity.jsonl"));
            CheckPairingUnchanged(pairing);
            friendName.Text = pairing.PeerName;
            pairInfo.Text = PairText(pairing);
            pairInfo.ForeColor = Theme.Faint;
            checking = true;
            friendStatus.Text = "Checking…";
        }
        avatar.Who = activity.PeerName = pairing?.PeerName ?? "";
        // no dot to sit behind when theres nobody
        friendStatus.Left = (int)((pairing == null ? 86 : 102) * ScaledBy);
        avatar.Ring = Theme.Line;
        avatar.Invalidate();
        header.Invalidate();
        welcome.Visible = pairing == null;
        tiles.Visible = pairing != null;
        activity.Empty = pairing == null ? "Once you're paired, streams, files and SSH sessions show up here." : "Nothing yet. Stream, send a file or start SSH and it shows up here.";
        trayStatus.Text = pairing == null ? "Not paired" : pairing.PeerName + ": checking…";
        RefreshActivity();
        RefreshSetup();
    }

    string PairText(Pairing p) => p.PeerAddress + "   ·   " + (p.IsLegacy ? "paired through Mutual SSH" : "safety code " + p.SafetyCode) + (peerVersion != null ? "   ·   they're on " + peerVersion : "");

    // remembers the friends key. if its different next time and you didnt re-pair, say so loudly
    void CheckPairingUnchanged(Pairing p)
    {
        var fp = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(p.PeerCertRaw));
        if (settings.PeerFingerprint != null && settings.PeerFingerprint != fp && !justPaired)
        {
            log?.Write(ActivityResult.FAILED, "Mutual: the pairing with " + p.PeerName + " changed without re-pairing (new safety code " + p.SafetyCode + ")");
            if (!offline) Prompt.Show(this, "The key Mutual trusts for " + p.PeerName + " changed, but you didn't pair again.\n\nNew safety code: " + p.SafetyCode + "\n\nIf neither of you re-paired, something on this PC changed it. Pair again and compare the safety code out loud.",
                "Mutual", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        settings.PeerFingerprint = fp;
        justPaired = false;
        if (!offline) settings.Save();
    }
    bool justPaired;

    void Pair()
    {
        using var d = new PairDialog(settings, AppSettings.Dir);
        if (d.ShowDialog(this) != DialogResult.OK || d.Result == null) return;
        StopServices();
        justPaired = true;
        LoadPairing();
        log?.Write(ActivityResult.OK, "Mutual: paired with " + d.Result.PeerName + " (safety code " + d.Result.SafetyCode + ")");
        if (!offline) StartServices();
    }

    void OpenSettings()
    {
        using var d = new SettingsDialog(settings);
        if (d.ShowDialog(this) != DialogResult.OK) return;
        // new rendezvous server only kicks in after restarting the background stuff
        StopServices();
        if (!offline) StartServices();
    }

    CancellationTokenSource? services;

    void StartServices()
    {
        if (pairing == null) return;
        services = CancellationTokenSource.CreateLinkedTokenSource(quit.Token);
        hub = new LinkHub(pairing) { PeerConfirms = peerVersion >= LinkConfirmSince };
        hub.Note += n => log?.Write(ActivityResult.INFO, "Mutual: " + n);
        if (!string.IsNullOrWhiteSpace(settings.Rendezvous))
        {
            rendezvous = new Rendezvous(settings.Rendezvous, pairing);
            hub.RelayDial = rendezvous.RelayAsync;
            _ = rendezvous.AnnounceLoop(settings.UseUpnp, services.Token);
            rendezvous.Request += req => BeginInvoke(() => OnRequest(req));
            rendezvous.Found += addr => BeginInvoke(() =>
            {
                // if the paired address isnt answering use the one the rendezvous gave us
                if (!online && addr != pairing.PeerAddress && Pairing.IsPlainAddress(addr)) { pairing.PeerAddress = addr; log?.Write(ActivityResult.INFO, "Mutual: found " + pairing.PeerName + " at a new address through the rendezvous"); }
            });
        }
        _ = PresenceLoop(services.Token);
        if (settings.HandlePings) _ = ListenForPings(services.Token);
    }

    // looks for a new release now and then. a new one gets downloaded and checked in the background
    // (setting on by default), so updating is one click and a few seconds. nothing installs without a yes
    async Task UpdateLoop(CancellationToken ct)
    {
        try { await Task.Delay(TimeSpan.FromSeconds(20), ct); } catch { return; }
        while (!ct.IsCancellationRequested)
        {
            await CheckForUpdate();
            OfferUpdateIfQuiet();
            try { await Task.Delay(TimeSpan.FromHours(1), ct); } catch { return; }
        }
    }

    // true if theres a newer one (and the bar is showing it)
    async Task<bool> CheckForUpdate()
    {
        var found = await Updater.CheckAsync();
        if (found == null) return update != null;
        if (update == null || found.Version > update.Version)
        {
            update = found; readyExe = null;
            updateBar.Message = $"Mutual {found.Version} is out. You have {Updater.Current}.";
            updateBar.Style(Theme.Glyph.Update, Theme.Online, Theme.OnlineSoft);
            updateBar.On = true;
            trayUpdate.Text = $"Update Mutual to {found.Version}";
            trayUpdate.Visible = true;
            log?.Write(ActivityResult.INFO, $"Mutual: {found.Version} is out");
            if (settings.AutoDownload) _ = GetUpdateReady(found);
        }
        return true;
    }

    string? readyExe;
    bool fetching;

    // downloads and checks it now so the click later is instant
    async Task GetUpdateReady(Updater.Release r)
    {
        if (fetching || readyExe != null) return;
        fetching = true;
        try
        {
            var exe = await Updater.DownloadAsync(r);
            if (update != r) return;
            readyExe = exe;
            updateBar.Message = $"Mutual {r.Version} is ready. Updating takes a few seconds and Mutual opens again by itself.";
            OfferUpdateIfQuiet();
        }
        catch (Exception e) { Diag.Write("background update download failed: " + e.Message); }   // the button still downloads it
        finally { fetching = false; }
    }

    // anything that a restart would cut off
    bool Busy => sharing != null || waitingForViewer != null || Watching != null || transfers > 0 || share == ShareState.Rimworld;
    int transfers;

    // a corner popup once per version, only while nothing is going on and you're not in a fullscreen game.
    // "later" leaves the banner and tray item, it doesnt ask again for that version
    void OfferUpdateIfQuiet()
    {
        if (update == null || readyExe == null || Busy || settings.SnoozedVersion == update.Version.ToString() || offerShowing || Visible) return;
        if (SHQueryUserNotificationState(out int state) == 0 && state is 2 or 3 or 4) return;   // busy, d3d fullscreen, presenting
        var r = update;
        offerShowing = true;
        var popup = new RequestPopup("Mutual", $"Mutual {r.Version} is ready", "Takes a few seconds, then Mutual opens again. Windows asks for admin once.", Theme.Glyph.Update,
            "Update", () => { offerShowing = false; ShowMe(); _ = UpdateNow(); },
            "Later", () => { offerShowing = false; settings.SnoozedVersion = r.Version.ToString(); settings.Save(); });
        popup.FormClosed += (_, _) => offerShowing = false;
        popup.Show();
    }
    bool offerShowing;

    [System.Runtime.InteropServices.DllImport("shell32.dll")] static extern int SHQueryUserNotificationState(out int state);

    // first start after an update says so, with a link to whats new
    void NoteIfJustUpdated()
    {
        var now = Updater.Current.ToString();
        var before = settings.LastVersion;
        settings.LastVersion = now;
        if (before == now) return;
        settings.Save();
        if (before == null) return;
        log?.Write(ActivityResult.OK, $"Mutual: updated from {before} to {now}");
        updateBar.Style(Theme.Glyph.Check, Theme.Online, Theme.OnlineSoft);
        updateBar.Message = $"Updated to Mutual {now}.";
        updateBar.ClearButtons();
        updateBar.AddButton("What's new", () => OpenPage($"https://github.com/{SelfInstall.OfficialRepo}/releases/tag/v{now}"), ButtonKind.Secondary);
        updateBar.AddButton("Got it", () => { updateBar.On = false; ResetUpdateBar(); }, ButtonKind.Secondary);
        updateBar.On = true;
    }

    void ResetUpdateBar()
    {
        updateBar.ClearButtons();
        updateBar.Style(Theme.Glyph.Update, Theme.Online, Theme.OnlineSoft);
        updateButton = updateBar.AddButton("Update", () => _ = UpdateNow());
    }

    // from the menus, says something either way
    async Task CheckForUpdateLoud()
    {
        if (!await CheckForUpdate()) Prompt.Show(this, $"You're on the newest Mutual ({Updater.Current}).", "Mutual");
        else ShowMe();
    }

    // for --do update-now
    public async Task UpdateNowForTests()
    {
        update = await Updater.CheckAsync();
        log?.Write(ActivityResult.INFO, "Mutual: update check found " + (update == null ? "nothing newer" : update.Version.ToString()));
        await UpdateNow();
    }

    async Task UpdateNow()
    {
        if (update == null) return;
        if (updateButton is { Enabled: false } && readyExe == null) return;
        if (Busy) { Prompt.Show(this, "Finish what's going on first (stream, file or game). Updating restarts Mutual for a few seconds.", "Mutual"); return; }
        if (updateButton != null) updateButton.Enabled = false;
        trayUpdate.Enabled = false;
        try
        {
            var exe = readyExe;
            if (exe == null || !File.Exists(exe))
            {
                updateBar.Message = $"Downloading {update.Version}…";
                exe = await Updater.DownloadAsync(update);
            }
            updateBar.Message = $"Installing {update.Version}. Say yes to the admin prompt.";
            Updater.Launch(exe);
            // the new copy swaps the installed one and opens itself, this one gets out of the way
            Quit();
        }
        catch (Exception e)
        {
            Diag.Write("update failed: " + e);
            readyExe = null;
            updateBar.Message = "Update failed: " + e.Message;
            updateBar.Style(Theme.Glyph.Error, Theme.Bad, Theme.BadSoft);
            if (updateButton != null) updateButton.Enabled = true;
            trayUpdate.Enabled = true;
        }
    }

    // their version, from their hello. mutual before 1.4 never says hello
    static readonly Version LinkConfirmSince = new(1, 4, 0);
    Version? peerVersion;
    bool helloSent, loginSent;
    int onlineWithoutHello;

    void SendHello()
    {
        if (pairing == null) return;
        helloSent = true;
        _ = Ask(new Request(RequestKind.Hello, Updater.Current.ToString()));
    }

    void OnHello(string? detail)
    {
        if (!System.Version.TryParse(detail, out var v)) return;
        v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
        bool changed = peerVersion != v;
        peerVersion = v;
        if (hub != null) hub.PeerConfirms = v >= LinkConfirmSince;
        if (!helloSent) SendHello();
        if (!loginSent && v >= SshLogin.Since) { loginSent = true; _ = Ask(new Request(RequestKind.Login, Environment.UserName)); }
        if (!changed) return;
        if (pairing != null) pairInfo.Text = PairText(pairing);
        RefreshVersionBar();
        // theyre on something newer, no need to wait an hour to find out
        if (v > Updater.Current && Updater.Enabled) _ = CheckForUpdate();
    }

    void RefreshVersionBar()
    {
        if (pairing == null) { versionBar.On = false; return; }
        var me = Updater.Current;
        var peer = pairing.PeerName;
        if (peerVersion != null && peerVersion < me)
            versionBar.Message = $"{peer} is on Mutual {peerVersion}, you're on {me}. Ask them to update.";
        else if (peerVersion == null && online && onlineWithoutHello >= 3)
            versionBar.Message = $"{peer} is on an older Mutual. Ask them to update.";
        else { versionBar.On = false; return; }
        versionBar.On = true;
    }

    void CopyUpdateNote()
    {
        try
        {
            Clipboard.SetText($"hey, can you update Mutual? right click its tray icon (two rings, bottom right) and pick Check for updates. or grab it from https://github.com/{SelfInstall.OfficialRepo}/releases/latest");
            versionBar.Message = "Copied. Paste it to " + (pairing?.PeerName ?? "them") + " anywhere.";
        }
        catch { }
    }

    void StopServices()
    {
        services?.Cancel(); services = null;
        hub?.Dispose(); hub = null;
        rendezvous?.Dispose(); rendezvous = null;
    }

    async Task PresenceLoop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && pairing != null)
        {
            // a tcp connect is one round trip, close enough to a ping to say how good the link is
            var clock = Stopwatch.StartNew();
            bool direct = await Presence.IsOnlineAsync(pairing.PeerAddress, pairing.PeerPorts.Ping);
            long ms = clock.ElapsedMilliseconds;
            bool up = direct || rendezvous?.PeerOnline == true;
            friendStatus.Text = !up ? "Offline" : !direct ? "Online, thru the relay" : "Online  ·  " + Math.Max(1, ms) + " ms";
            friendStatus.ForeColor = up && direct && ms > 150 ? Theme.Warn : Theme.Dim;
            if (up && !online) { helloSent = false; loginSent = false; onlineWithoutHello = 0; }
            if (!up && online && hub != null) hub.PeerConfirms = false;   // whatever comes back might be a different version
            if (up && !helloSent) SendHello();
            if (up && peerVersion == null && ++onlineWithoutHello == 3) RefreshVersionBar();
            if (up != online || checking)
            {
                online = up; checking = false;
                trayStatus.Text = pairing.PeerName + ": " + (up ? "online" : "offline");
                tray.Icon = Program.TrayIcon(up);
                tray.Text = "Mutual · " + pairing.PeerName + " " + (up ? "online" : "offline");
                avatar.Ring = up ? Theme.Online : Theme.Line;
                avatar.Invalidate();
                header.Invalidate();
            }
            try { await Task.Delay(online ? 15000 : 8000, ct); } catch { break; }
        }
    }

    async Task ListenForPings(CancellationToken ct)
    {
        var p = pairing!;
        bool said = false;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Notify.ListenAsync(IPAddress.Any, p.MyPorts.Ping, LoopbackFriend(p) ? null : p.PeerAddress, () => p.Own, p.PeerCertRaw,
                    req => BeginInvoke(() => OnRequest(req)), ct);
            }
            catch (SocketException)
            {
                // old Notifier.exe still has the port, it handles pings for now. keep checking tho: once it
                // closes nothing would be listening and your friend would see you offline for good
                if (!said) { log?.Write(ActivityResult.INFO, "Mutual: old notifier owns the ping port, leaving pings to it"); BeginInvoke(RefreshSetup); said = true; }
                try { await Task.Delay(TimeSpan.FromSeconds(30), ct); } catch { return; }
                continue;
            }
            catch (Exception) { try { await Task.Delay(5000, ct); } catch { return; } }
        }
    }

    // thru the relay or on the same pc they come from a different address than the paired one
    bool LoopbackFriend(Pairing p) => p.PeerAddress.StartsWith("127.") || rendezvous != null;

    // tries them directly first, then thru the rendezvous server
    async Task<bool> Ask(Request req)
    {
        var p = pairing!;
        if (await Notify.SendAsync(p.PeerAddress, p.PeerPorts.Ping, p.Own, p.PeerCertRaw, req)) return true;
        return rendezvous?.SendRequest(req) == true;
    }

    // first time setup

    void RefreshSetup()
    {
        if (pairing == null) { setupBar.On = false; return; }
        var todo = Takeover.Pending(pairing);
        setupBar.On = todo.Count > 0;
        setupBar.Message = todo.Count == 0 ? "" : pairing.IsLegacy && Takeover.OldNotifierRunning()
            ? "Mutual isn't fully set up on this PC yet. Until it is, " + pairing.PeerName + "'s requests go to the old Notifier and you won't see stream invites."
            : "Finish setting up Mutual on this PC so " + pairing.PeerName + " can reach it.";
    }

    void RunSetup()
    {
        if (pairing == null) return;
        var todo = Takeover.Pending(pairing);
        if (todo.Count == 0) { RefreshSetup(); return; }
        var msg = "Finish setting up Mutual on this PC?\n\n• " + string.Join("\n• ", todo) + (pairing.IsLegacy ? "\n\nThe old startup shortcut is kept in " + pairing.Folder + " so this can be undone." : "") + "\n\nWindows asks for admin once.";
        if (Prompt.Show(this, msg, "Set up Mutual", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        var problem = Takeover.Run(pairing, pairing.Folder);
        log?.Write(problem == null ? ActivityResult.OK : ActivityResult.FAILED, "Mutual: setup " + (problem == null ? "done" : "incomplete: " + problem));
        if (problem != null) Prompt.Show(this, "Not everything worked: " + problem, "Mutual");
        RefreshSetup();
        if (settings.HandlePings && !Takeover.OldNotifierRunning() && services != null) _ = ListenForPings(services.Token);
    }

    bool EnsureFirewall()
    {
        // test profiles run a dev build thats already let thru, and nobodys there to click the prompt
        if (settings.AutoAccept) return true;
        var missing = StreamPort.Missing(pairing!);
        if (missing.Count == 0) return true;
        var ok = Prompt.Show(this, "This needs firewall rules on this PC: only " + pairing!.PeerName + "'s address, only Mutual, only its ports.\n\nWindows will ask for admin once.",
            "Mutual", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
        if (!ok) return false;
        if (StreamPort.AddRules(missing, Application.ExecutablePath, pairing.BindAddress.ToString(), pairing.PeerAddress)) { RefreshSetup(); return true; }
        Prompt.Show(this, "The firewall rules weren't added, so " + pairing.PeerName + " can't connect yet.", "Mutual");
        return false;
    }

    // requests from them

    readonly List<DateTime> unanswered = new();
    DateTime mutedUntil;

    void OnRequest(Request req)
    {
        if (pairing == null) return;
        if (req.Kind == RequestKind.Hello) { OnHello(req.Detail); return; }
        if (req.Kind == RequestKind.Decline) { OnDeclined(req.Detail); return; }
        if (req.Kind == RequestKind.Login)
        {
            if (SshLogin.IsUsableName(req.Detail) && settings.PeerLogin != req.Detail) { settings.PeerLogin = req.Detail; settings.Save(); }
            return;
        }
        // the offer that card promised just showed up, or isnt coming anymore
        if (req.Kind == RequestKind.Stream || (req.Kind == RequestKind.Play && req.Detail == PlayNote.Stopped)) playCard?.Close();
        if (req.Kind == RequestKind.Play && req.Detail == PlayNote.Stopped)
        {
            log?.Write(ActivityResult.INFO, "Mutual: " + pairing.PeerName + " stopped hosting RimWorld");
            return;
        }
        // 3 ignored invites in 10 min and the popups go quiet for 10 min, so nobody can spam you into a misclick
        var now = DateTime.UtcNow;
        unanswered.RemoveAll(t => now - t > TimeSpan.FromMinutes(10));
        if (now < mutedUntil)
        {
            log?.Write(ActivityResult.INFO, $"Mutual: {pairing.PeerName} asked again for {Asked(req.Kind)} (popups paused for a bit)");
            return;
        }
        if (unanswered.Count >= 3)
        {
            mutedUntil = now.AddMinutes(10);
            log?.Write(ActivityResult.INFO, $"Mutual: {pairing.PeerName} keeps asking, pausing their popups for 10 minutes");
            return;
        }
        if (req.Kind == RequestKind.Play && req.Detail == PlayNote.Starting)
        {
            log?.Write(ActivityResult.INFO, "Mutual: " + pairing.PeerName + " is hosting RimWorld, a stream offer comes when their colony is up");
            if (settings.AutoAccept) return;
            unanswered.Add(now);
            playCard?.Close();
            var card = new RequestPopup(pairing.PeerName, PlayCardHead(pairing.PeerName), PlayCardBody, Theme.Glyph.Game, unanswered.Clear);
            card.FormClosed += (_, _) => { if (playCard == card) playCard = null; };
            playCard = card;
            card.Show();
            return;
        }
        // the only other play note there is. the text comes from here, never from whats on the wire
        if (req.Kind == RequestKind.Play && req.Detail != PlayNote.AskHost) return;
        string detail = req.Kind == RequestKind.Play ? PlayAskBody
            : req.Kind == RequestKind.File && FileTransfer.TryParse(req.Detail, out var fn, out var fs) ? fn + " (" + TransferWindow.Size(fs) + ")" : req.Detail ?? "";
        log?.Write(ActivityResult.INFO, $"Mutual: {pairing.PeerName} asked for {Asked(req.Kind)}" + (detail.Length > 0 ? ": " + detail : ""));
        if (settings.AutoAccept) { Accept(req); return; }
        unanswered.Add(now);
        var popup = new RequestPopup(pairing.PeerName, req.Kind is RequestKind.File or RequestKind.Play ? req with { Detail = detail } : req, () => { unanswered.Clear(); Accept(req); },
            // tells them, so they're not left on "waiting for you to accept" for ten minutes
            () => { if (req.Kind is RequestKind.Stream or RequestKind.File) _ = Ask(new Request(RequestKind.Decline, ((byte)req.Kind).ToString())); });
        popup.Show();
    }

    // they hit ignore on your invite
    void OnDeclined(string? detail)
    {
        if (pairing == null || !byte.TryParse(detail, out var k)) return;
        if ((RequestKind)k == RequestKind.Stream && share == ShareState.Waiting)
        {
            log?.Write(ActivityResult.INFO, "Mutual: " + pairing.PeerName + " said not now to the stream");
            StopSharing();
        }
        else if ((RequestKind)k == RequestKind.File && offeredFiles.Count > 0)
        {
            var w = offeredFiles[0];
            w.Declined = true;
            w.Cancel.Cancel();
        }
    }

    static string Asked(RequestKind k) => k switch { RequestKind.Stream => "a stream", RequestKind.File => "a file", RequestKind.Ssh => "SSH", RequestKind.Play => "a game", _ => k.ToString() };

    void Accept(Request req)
    {
        switch (req.Kind)
        {
            case RequestKind.Ssh: StartSsh(answering: true); break;
            case RequestKind.Stream: _ = WatchStream(req.Detail); break;
            case RequestKind.File: _ = ReceiveFile(req.Detail); break;
            case RequestKind.Play: _ = PlayRimWorld(askedToHost: true); break;
            default: Prompt.Show(this, req.Kind + " isn't built yet.", "Mutual"); break;
        }
    }

    // streaming

    StreamHost? sharing;
    BoxOverlay? boxFrame;
    ControlBar? controlBar;
    CancellationTokenSource? waitingForViewer;
    public ViewerForm? Watching { get; private set; }
    public StreamReceiver? WatchingReceiver { get; private set; }
    public StreamHost? Sharing => sharing;

    // udp for video. whoever is the tls server listens, the other one sends first
    Func<byte[], UdpVideo> UdpLane(bool isHost) => key => pairing!.Role == LinkRole.Server
        ? UdpVideo.Listen(IPAddress.Any, pairing.MyPorts.Stream, key, isHost)
        : UdpVideo.Connect(new IPEndPoint(IPAddress.TryParse(pairing.PeerAddress, out var ip) ? ip : Dns.GetHostAddresses(pairing.PeerAddress).First(a => a.AddressFamily == AddressFamily.InterNetwork), pairing.PeerPorts.Stream), key, isHost);

    // duocursor throws away injected mouse input, which is how their mouse gets here
    // so offer to close it
    bool DuoCursorOutOfTheWay()
    {
        var duo = Process.GetProcessesByName("DuoCursor");
        if (duo.Length == 0 || settings.AutoAccept) return true;
        var r = Prompt.Show(this, "DuoCursor is running and blocks " + pairing!.PeerName + "'s mouse from reaching this PC. Close it for now?\n\n(Mutual does its job; you can start it again any time.)",
            "Mutual", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) foreach (var p in duo) try { p.Kill(); } catch { }
        return true;
    }

    public void StartStream(StreamSource? preset = null)
    {
        if (pairing == null || hub == null) { if (pairing == null) Pair(); return; }
        if (sharing != null || waitingForViewer != null) { StopSharing(); return; }
        if (share == ShareState.Rimworld) { rimCancelled = true; return; }
        var source = preset;
        if (source == null)
        {
            using var picker = new SourcePicker();
            if (picker.ShowDialog(this) != DialogResult.OK || picker.Chosen == null) return;
            source = picker.Chosen;
        }
        if (!EnsureFirewall() || !DuoCursorOutOfTheWay()) return;
        _ = ShareAsync(source);
    }

    bool stopRequested;

    // offers the stream then keeps it going. if the link drops and they didnt close it on purpose
    // it waits a minute for them to come back (they retry on their end too)
    async Task ShareAsync(StreamSource source)
    {
        var p = pairing!;
        stopRequested = false;
        if (source is BoxSource box) { boxFrame = new BoxOverlay(box); boxFrame.Show(); }
        shareSource = source.Describe();
        inviteMissed = false;
        SetShare(ShareState.Waiting);
        log?.Write(ActivityResult.INFO, "Mutual: offered to stream " + source.Describe());
        var asked = await Ask(new Request(RequestKind.Stream, source.Describe()));
        inviteMissed = !asked;
        if (!asked) { log?.Write(ActivityResult.INFO, "Mutual: couldn't reach " + p.PeerName + "'s Mutual; waiting anyway in case they open it"); RenderSession(); }
        var wait = TimeSpan.FromMinutes(10);
        bool first = true;
        var tally = new SessionTally();
        // rimworld split sends their input to the mod, not your mouse, and you clicked play for exactly that, so its on from the start
        bool controlFromStart = source is SplitColonySource;
        controlBar = new ControlBar(p.PeerName, source is ScreenSource);
        controlBar.StopClicked += () => StopSharing();
        controlBar.Show();
        // links that die right after starting get backed off and logged once
        int quick = 0;
        while (!stopRequested)
        {
            waitingForViewer = new CancellationTokenSource();
            StreamHost host;
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnStatus(string s)
            {
                Diag.Write("host: " + s);
                if (s == "viewer left" || s.StartsWith("stream stopped")) ended.TrySetResult(s.StartsWith("stream stopped"));
                else if (s.StartsWith("video on") || s.StartsWith("udp unavailable") || s.StartsWith("paused") || s.StartsWith("resumed") || s.StartsWith("sound:"))
                    try { BeginInvoke(() => log?.Write(ActivityResult.INFO, "Mutual: " + s)); } catch { }
            }
            try
            {
                var link = await hub!.OpenAsync(Channel.Stream, wait, waitingForViewer.Token);
                link.ReadTimeout = 15000;
                host = new StreamHost(new Stream.Wire(link), source, allowInput: false, openUdp: UdpLane(isHost: true), shareSound: settings.ShareSound, onStatus: OnStatus) { Tally = tally };
            }
            catch (Exception e)
            {
                if (!stopRequested) log?.Write(ActivityResult.INFO, "Mutual: " + (first ? "stream not started (" + Why(e) + ")" : p.PeerName + " didn't come back (" + Why(e) + ")"));
                break;
            }
            finally { waitingForViewer?.Dispose(); waitingForViewer = null; }

            sharing = host;
            controlBar?.Attach(host);
            if (controlFromStart) host.SetControl(true);
            using var clip = settings.ShareClipboard ? new ClipboardSync(this, host.SendClipboard) : null;
            if (clip != null) host.ClipboardReceived += clip.Apply;
            // an older mutual on their end hangs up on us until they click accept, so it only counts as
            // live once their viewer actually says something
            var lived = Stopwatch.StartNew();
            while (!ended.Task.IsCompleted && !host.HeardFromViewer) await Task.WhenAny(ended.Task, Task.Delay(100));
            if (host.HeardFromViewer)
            {
                if (quick < 2) log?.Write(ActivityResult.OK, "Mutual: " + (first ? "streaming " + source.Describe() + " to " + p.PeerName : p.PeerName + " is back, streaming again"));
                SetShare(ShareState.Live);
            }
            bool onPurpose = await ended.Task || host.EndedByViewer;
            controlBar?.Attach(null);
            host.Dispose();
            if (sharing == host) sharing = null;
            if (stopRequested || onPurpose) { if (!stopRequested) log?.Write(ActivityResult.INFO, "Mutual: " + p.PeerName + " closed the stream"); break; }
            if (!host.HeardFromViewer)
            {
                // nobody on the other end yet, so not a drop. try again quietly
                for (int ms = 500; ms > 0 && !stopRequested; ms -= 100) await Task.Delay(100);
                continue;
            }
            quick = lived.ElapsedMilliseconds < 3000 ? quick + 1 : 0;
            if (quick < 2) log?.Write(ActivityResult.INFO, "Mutual: the link to " + p.PeerName + " dropped, waiting up to a minute for it to come back");
            else if (quick == 3) log?.Write(ActivityResult.FAILED, "Mutual: the stream keeps dropping as soon as it starts, slowing down the retries" + (host.Failure != null ? " (" + Why(host.Failure) + ")" : "") + ". their activity should say why");
            SetShare(ShareState.Reconnecting);
            wait = TimeSpan.FromMinutes(1);
            first = false;
            // 1, 2, 4 then 8 s, in small steps so stop still works right away
            if (quick >= 3) for (int ms = Math.Min(8000, 1000 << Math.Min(quick - 3, 3)); ms > 0 && !stopRequested; ms -= 250) await Task.Delay(250);
        }
        if (tally.Reports > 0) log?.Write(ActivityResult.OK, "Mutual: streamed to " + p.PeerName + ": " + tally.Describe());
        EndShare();
    }

    public void StopStreamForTests() => StopSharing();
    public void QuitForTests() => Quit();

    void StopSharing(string? why = null)
    {
        stopRequested = true;
        waitingForViewer?.Cancel();
        sharing?.Dispose(); sharing = null;
        if (why != null) log?.Write(ActivityResult.INFO, "Mutual: stream ended (" + why + ")");
        EndShare();
    }

    void EndShare()
    {
        boxFrame?.Close(); boxFrame = null;
        controlBar?.Close(); controlBar = null;
        SetShare(ShareState.None);
    }

    // joins their stream and rejoins on its own for a minute if the link drops
    async Task WatchStream(string? what)
    {
        if (pairing == null || hub == null) return;
        if (!EnsureFirewall()) return;
        var title = pairing.PeerName + "'s " + (what ?? "screen");
        var wait = TimeSpan.FromMinutes(2);
        Rectangle? lastBounds = null;
        var tally = new SessionTally();
        bool summarized = false;
        int soundChunks = 0;
        // says whether any sound came thru, so "no sound" can be told apart from "sound never left their pc"
        void Summary() { if (summarized || tally.Reports == 0) return; summarized = true; log?.Write(ActivityResult.OK, "Mutual: watched " + pairing!.PeerName + ": " + tally.Describe() + (soundChunks > 0 ? $", sound {soundChunks / 100}s" : ", no sound came thru")); }
        // same as the sharing side, a stream that dies right as it starts gets backed off and its reason logged once
        int quick = 0;
        string? lastWhy = null;
        for (int attempt = 0; ; attempt++)
        {
            StreamReceiver rx;
            try
            {
                var link = await hub.OpenAsync(Channel.Stream, wait);
                link.ReadTimeout = 15000;
                rx = new StreamReceiver(new Stream.Wire(link), UdpLane(isHost: false)) { Tally = tally };
                var counted = rx;
                rx.Ended += _ => soundChunks += counted.SoundChunks;
            }
            catch (Exception e)
            {
                log?.Write(ActivityResult.INFO, "Mutual: " + (attempt == 0 ? "couldn't join the stream" : "couldn't get back into the stream") + " (" + Why(e) + ")");
                if (attempt == 0 && !settings.AutoAccept) Prompt.Show(this, "Couldn't join the stream: " + e.Message, "Mutual");
                Summary();
                return;
            }
            var view = new ViewerForm(rx, title) { Icon = Program.AppIcon };
            var clip = settings.ShareClipboard ? new ClipboardSync(this, rx.SendClipboard) : null;
            if (clip != null) { rx.ClipboardReceived += clip.Apply; view.FormClosed += (_, _) => clip.Dispose(); }
            if (lastBounds is { } b) { view.StartPosition = FormStartPosition.Manual; view.Bounds = b; }
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool userClosed = true;
            bool handled = false;
            void OnEnded(string why)
            {
                if (handled) return;
                handled = true;
                Diag.Write("viewer: " + why);
                lastWhy = why;
                // they stopped sharing, the window stays open on the last picture but the session is over
                if (rx.EndedByHost) { Summary(); return; }
                // link broke, close the window ourselves and try again
                userClosed = false;
                if (!view.IsDisposed) { lastBounds = view.Bounds; view.Close(); }
            }
            rx.Ended += why => { try { BeginInvoke(() => OnEnded(why)); } catch { } };
            view.FormClosed += (_, _) => { rx.Dispose(); Watching = null; closed.TrySetResult(true); };
            Watching = view; WatchingReceiver = rx;
            watchingWhat = what ?? "screen";
            RenderSession();
            view.Show();
            // it can end before Ended got hooked up, or before the window had a handle to close
            if (rx.EndReason is { } early) OnEnded(early);
            if (quick < 2) log?.Write(ActivityResult.OK, "Mutual: " + (attempt == 0 ? "watching " + pairing.PeerName + "'s stream" : "back in " + pairing.PeerName + "'s stream"));
            var lived = Stopwatch.StartNew();
            await closed.Task;
            watchingWhat = null;
            RenderSession();
            if (userClosed || rx.EndedByHost) { Summary(); return; }
            quick = lived.ElapsedMilliseconds < 3000 ? quick + 1 : 0;
            if (quick < 2) log?.Write(ActivityResult.INFO, "Mutual: lost the stream (" + (lastWhy ?? "no reason") + "), trying to get back in");
            else if (quick == 3) log?.Write(ActivityResult.FAILED, "Mutual: the stream keeps dropping as soon as it starts (" + (lastWhy ?? "no reason") + "), slowing down the retries");
            wait = TimeSpan.FromMinutes(1);
            if (quick >= 3) await Task.Delay(Math.Min(8000, 1000 << Math.Min(quick - 3, 3)));
        }
    }

    // rimworld

    // for --do play-rimworld
    public void PlayRimWorldNow() => _ = PlayRimWorld();

    public static string PlayCardHead(string peer) => peer + " is hosting RimWorld";
    public const string PlayCardBody = "Their game is loading. You'll get a stream offer once a colony is up, accept that and the right half is yours.";
    public const string PlayAskBody = "RimWorld or the SplitColony mod isn't set up on their PC. Accept to host on yours, they get the right half.";

    void TellFriendAboutPlay(string note)
    {
        if (peerVersion >= PlayNote.Since) _ = Ask(new Request(RequestKind.Play, note));
    }

    // whoever clicks play hosts, so if this pc cant, say why and point at the other one
    async Task CantHost(HostProblem problem, bool askedToHost)
    {
        var peer = pairing!.PeerName;
        var text = "This PC can't host RimWorld\n\n"
            + (problem == HostProblem.NoGame ? "RimWorld isn't installed thru Steam here." : "The SplitColony mod isn't turned on in RimWorld here.")
            + " Whoever clicks Play hosts the game and the other person gets the right half as a stream.";
        if (askedToHost) { Prompt.Show(this, text, "Mutual"); return; }
        if (!(peerVersion >= PlayNote.Since)) { Prompt.Show(this, text + "\n\nAsk " + peer + " to click Play RimWorld on their PC.", "Mutual"); return; }
        if (Prompt.Show(this, text + "\n\nAsk " + peer + " to host instead?", "Mutual", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
        if (await Ask(new Request(RequestKind.Play, PlayNote.AskHost))) log?.Write(ActivityResult.INFO, "Mutual: asked " + peer + " to host RimWorld");
        else Prompt.Show(this, "Couldn't reach " + peer + "'s Mutual.", "Mutual");
    }

    // starts rimworld thru steam if its not open, turns the split on and streams them the right half
    // their mouse and keys go to the mod, you keep the left half
    async Task PlayRimWorld(bool askedToHost = false)
    {
        if (pairing == null || hub == null) { if (pairing == null) Pair(); return; }
        if (sharing != null || waitingForViewer != null) { Prompt.Show(this, "Stop the current stream first.", "Mutual"); return; }
        var problem = RimWorldHost.Check(SplitColonySource.GameRunning());
        if (problem != HostProblem.None) { await CantHost(problem, askedToHost); return; }
        if (!EnsureFirewall()) return;
        foreach (var d in Process.GetProcessesByName("DuoCursor")) try { d.Kill(); d.WaitForExit(2000); } catch { }
        if (!SplitColonySource.GameRunning())
        {
            log?.Write(ActivityResult.INFO, "Mutual: starting RimWorld");
            try { Process.Start(new ProcessStartInfo("steam://rungameid/294100") { UseShellExecute = true }); }
            catch (Exception e) { Prompt.Show(this, "Couldn't start RimWorld thru Steam: " + e.Message, "Mutual"); return; }
        }
        SplitColonySource src;
        try { src = new SplitColonySource(); }
        catch (SocketException) { Prompt.Show(this, "Something else is already talking to the SplitColony mod.", "Mutual"); return; }
        rimCancelled = false;
        rimStage = RimStage(0);
        SetShare(ShareState.Rimworld);
        TellFriendAboutPlay(PlayNote.Starting);
        // the mod only answers once a colony is loaded and big mod lists take a while
        for (int i = 0; i < 600 && !src.Active && !rimCancelled; i++)
        {
            await Task.Delay(500);
            // game is up but no split yet, flip it
            if (i == 16 && SplitColonySource.GameRunning() && !src.Active) src.Toggle();
            if (i % 40 == 39 && !src.Active && SplitColonySource.GameRunning()) src.Toggle();
            var stage = RimStage(i);
            if (stage != rimStage) { rimStage = stage; RenderSession(); }
        }
        if (!src.Active)
        {
            bool started = SplitColonySource.GameRunning();
            src.Dispose(); SetShare(ShareState.None);
            TellFriendAboutPlay(PlayNote.Stopped);
            if (rimCancelled) { log?.Write(ActivityResult.INFO, "Mutual: stopped waiting for RimWorld"); return; }
            Prompt.Show(this, started
                ? "RimWorld never turned the split on. Is a colony loaded? You can press Pause/Break in game to flip it by hand."
                : "RimWorld never started. Steam might be waiting on an update or a prompt, or the game didn't launch.", "Mutual");
            return;
        }
        log?.Write(ActivityResult.OK, "Mutual: RimWorld split is on");
        _ = ShareAsync(src);
    }

    // what the banner says while it waits. steam launching looks the same as steam doing nothing, so after a minute it says so
    string RimStage(int halfSeconds)
    {
        var peer = pairing?.PeerName ?? "your friend";
        if (SplitColonySource.GameRunning()) return "RimWorld is up. Load a colony and the split turns on by itself, then " + peer + " gets the stream offer.";
        return halfSeconds < 120 ? "Starting RimWorld thru Steam on this PC. You're hosting, " + peer + " gets the right half."
            : "RimWorld still hasn't started. Check Steam, it might be waiting on an update or a prompt.";
    }

    // whats going on rn, shown in the banner under your friends name and on the stream tile

    enum ShareState { None, Waiting, Live, Reconnecting, Rimworld }
    ShareState share;
    string shareSource = "";
    string? watchingWhat;
    bool rimCancelled;
    string rimStage = "";
    RequestPopup? playCard;
    // the invite never got to them (offline, or mutual closed), so "waiting for them to accept" would be a lie
    bool inviteMissed;

    void SetShare(ShareState s) { share = s; RenderSession(); }

    // "window: Foo" reads better as just Foo in a sentence
    static string Nice(string describe) => describe.StartsWith("window: ") ? describe[8..] : "your " + describe;

    void RenderSession()
    {
        if (IsDisposed || streamButton == null) return;
        var peer = pairing?.PeerName ?? "your friend";
        sessionBar.ClearButtons();
        switch (share)
        {
            case ShareState.Waiting:
                sessionBar.Style(Theme.Glyph.Screen, Theme.Accent, Theme.AccentSoft);
                sessionBar.Message = inviteMissed ? "Couldn't reach " + peer + ". Waiting in case they open Mutual" : "Waiting for " + peer + " to accept " + Nice(shareSource);
                sessionBar.AddButton("Cancel", () => StopSharing(), ButtonKind.Secondary);
                break;
            case ShareState.Live:
                sessionBar.Style(Theme.Glyph.Screen, Theme.Online, Theme.OnlineSoft);
                sessionBar.Message = "Streaming " + Nice(shareSource) + " to " + peer;
                sessionBar.AddButton("Stop", () => StopSharing(), ButtonKind.Danger);
                break;
            case ShareState.Reconnecting:
                sessionBar.Style(Theme.Glyph.Warning, Theme.Warn, Theme.WarnSoft);
                sessionBar.Message = "Lost the link to " + peer + ". Waiting up to a minute for it to come back.";
                sessionBar.AddButton("Stop", () => StopSharing(), ButtonKind.Secondary);
                break;
            case ShareState.Rimworld:
                sessionBar.Style(Theme.Glyph.Game, Theme.Accent, Theme.AccentSoft);
                sessionBar.Message = rimStage;
                sessionBar.AddButton("Cancel", () => rimCancelled = true, ButtonKind.Secondary);
                break;
            default:
                if (watchingWhat != null)
                {
                    sessionBar.Style(Theme.Glyph.Screen, Theme.Online, Theme.OnlineSoft);
                    sessionBar.Message = "Watching " + peer + "'s " + (watchingWhat.StartsWith("window: ") ? watchingWhat[8..] : watchingWhat);
                    sessionBar.AddButton("Show", () => { Watching?.Activate(); }, ButtonKind.Secondary);
                    sessionBar.AddButton("Leave", () => Watching?.Close(), ButtonKind.Secondary);
                }
                break;
        }
        sessionBar.On = share != ShareState.None || watchingWhat != null;
        (streamButton.Text, streamButton.Subtitle, streamButton.Highlight) = share switch
        {
            ShareState.Waiting => ("Waiting for " + peer + "…", "Click to cancel", true),
            ShareState.Live => ("Streaming", "Click to stop sharing", true),
            ShareState.Reconnecting => ("Reconnecting…", "Click to stop", true),
            ShareState.Rimworld => ("Starting RimWorld…", "Waiting for the split", true),
            _ => ("Stream", "Share your screen, a window or a box", false),
        };
        streamButton.Invalidate();
    }

    // files

    public void SendFile(string? path = null)
    {
        if (pairing == null || hub == null) { if (pairing == null) Pair(); return; }
        if (path == null)
        {
            using var pick = new OpenFileDialog { Title = "Send a file to " + pairing.PeerName };
            if (pick.ShowDialog(this) != DialogResult.OK) return;
            path = pick.FileName;
        }
        if (!EnsureFirewall()) return;
        _ = SendFileAsync(path);
    }

    async Task SendFileAsync(string path)
    {
        var p = pairing!;
        long size = new FileInfo(path).Length;
        var win = new TransferWindow(Path.GetFileName(path), size, sending: true, "Waiting for " + p.PeerName + " to accept…");
        win.Show();
        log?.Write(ActivityResult.INFO, "Mutual: offered " + Path.GetFileName(path) + " (" + TransferWindow.Size(size) + ")");
        transfers++;
        offeredFiles.Add(win);
        try
        {
            if (!await Ask(new Request(RequestKind.File, FileTransfer.Describe(path))))
                win.Status = "Couldn't reach " + p.PeerName + "'s Mutual. Still waiting in case they open it.";
            using var link = await hub!.OpenAsync(Channel.File, TimeSpan.FromMinutes(10), win.Cancel.Token);
            offeredFiles.Remove(win);
            link.ReadTimeout = link.WriteTimeout = 30000;
            win.Status = "Sending to " + p.PeerName;
            await FileTransfer.SendAsync(link, path, win.Progress, win.Cancel.Token);
            log?.Write(ActivityResult.OK, "Mutual: sent " + Path.GetFileName(path) + ", verified by " + p.PeerName);
        }
        catch (Exception e) when (!win.Cancel.IsCancellationRequested) { log?.Write(ActivityResult.FAILED, "Mutual: sending " + Path.GetFileName(path) + " failed (" + Why(e) + ")"); }
        catch (Exception) { log?.Write(ActivityResult.INFO, "Mutual: " + (win.Declined ? p.PeerName + " said no to " + Path.GetFileName(path) : "sending " + Path.GetFileName(path) + " cancelled")); }
        finally { transfers--; offeredFiles.Remove(win); if (!win.IsDisposed) win.Close(); }
    }

    readonly List<TransferWindow> offeredFiles = new();

    async Task ReceiveFile(string? detail)
    {
        if (pairing == null || hub == null || !FileTransfer.TryParse(detail, out var name, out var size)) return;
        if (!EnsureFirewall()) return;
        var win = new TransferWindow(name, size, sending: false, "From " + pairing.PeerName + ", saving to Downloads");
        win.Show();
        transfers++;
        try
        {
            using var link = await hub.OpenAsync(Channel.File, TimeSpan.FromMinutes(2), win.Cancel.Token);
            link.ReadTimeout = link.WriteTimeout = 30000;
            var saved = await FileTransfer.ReceiveAsync(link, FileTransfer.DownloadsFolder(), name, size, win.Progress, win.Cancel.Token);
            log?.Write(ActivityResult.OK, "Mutual: received " + Path.GetFileName(saved) + " from " + pairing.PeerName + " (checked)");
            if (!settings.AutoAccept) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + saved + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (!win.Cancel.IsCancellationRequested) { log?.Write(ActivityResult.FAILED, "Mutual: receiving " + name + " failed (" + Why(e) + ")"); }
        catch (Exception) { log?.Write(ActivityResult.INFO, "Mutual: receiving " + name + " cancelled"); }
        finally { transfers--; if (!win.IsDisposed) win.Close(); }
    }

    // ssh

    // opens the ssh window as an admin copy of mutual, the admin prompt is part of saying yes
    // talks the old protocol so it still works if theyre on mutual ssh
    void StartSsh(bool answering = false)
    {
        if (pairing == null) { Prompt.Show(this, pairingError ?? "Not paired.", "Mutual"); return; }
        if (!SelfInstall.RunningInstalled && !SelfInstall.DevBuild)
        {
            Prompt.Show(this, "SSH runs as admin, so it only runs from Mutual in Program Files (where nothing else can swap it out). Close Mutual and open it again to install it there.", "Mutual");
            return;
        }
        var args = "--ssh-session" + (answering ? " --no-ping" : "") + (AppSettings.Profile != null ? " --profile \"" + AppSettings.Profile + "\"" : "");
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true, Verb = "runas" });
            log?.Write(ActivityResult.INFO, answering ? "Mutual: answering SSH request" : "Mutual: starting SSH");
        }
        catch (System.ComponentModel.Win32Exception) { log?.Write(ActivityResult.INFO, "Mutual: SSH cancelled at the admin prompt"); }
        catch (Exception e) { Prompt.Show(this, "Couldn't start SSH: " + e.Message, "Mutual"); }
    }

    // activity list

    void RefreshActivity()
    {
        if (log == null) return;
        var rows = log.ReadRecent(80);
        rows.Reverse();
        activity.SetRows(rows);
    }

    // wipes the activity list and mutual.log on this pc (who you talked to and when)
    void ClearHistory()
    {
        if (Prompt.Show(this, "Clear the activity list and Mutual's log on this PC? This can't be undone.", "Mutual", MessageBoxButtons.OKCancel, MessageBoxIcon.Question) != DialogResult.OK) return;
        foreach (var f in new[] { log?.Path, log?.Path + ".previous", Diag.PathName, Diag.PathName + ".old" })
            try { if (f != null && File.Exists(f)) File.Delete(f); } catch { }
        activity.SetRows(new());
    }

    // for tests, what this copy is doing rn as one json line
    public string Report() => JsonSerializer.Serialize(new
    {
        paired = pairing != null, online, peerVersion = peerVersion?.ToString(), confirms = hub?.PeerConfirms ?? false,
        sharing = sharing != null, framesSent = sharing?.FramesSent ?? 0, hostTransport = sharing?.Transport,
        watching = Watching != null, presented = Watching?.Presented ?? 0, viewerStats = sharing?.LastStats,
    });

    // fake states for --snapshot-ui
    public MainForm Demo(bool online = false, bool live = false, bool unpaired = false, bool behind = false, bool rimworld = false)
    {
        if (unpaired) { ignoreSaved = true; LoadPairing(); activity.SetRows(new()); return this; }
        // readme pictures use made up names, never a real pairing
        friendName.Text = avatar.Who = activity.PeerName = "alex";
        Text = "Mutual";
        pairInfo.Text = "25.14.88.203   ·   safety code 7D4F-9B98-8960";
        var t = DateTime.UtcNow.Date.AddHours(DateTime.UtcNow.Hour);
        activity.SetRows(new()
        {
            new(t.AddMinutes(-2), "", ActivityResult.OK, "Mutual: streamed to alex: 41m, 60 fps, ping 18 ms (worst 31), 0.1% loss, 14.2 mbit/s, udp"),
            new(t.AddMinutes(-43), "", ActivityResult.INFO, "Mutual: video on udp"),
            new(t.AddMinutes(-43), "", ActivityResult.OK, "Mutual: streaming window: RimWorld by Ludeon Studios to alex"),
            new(t.AddMinutes(-44), "", ActivityResult.OK, "Mutual: received colony.rws from alex (checked)"),
            new(t.AddMinutes(-50), "", ActivityResult.INFO, "Mutual: alex asked for a file: colony.rws (39.3 MB)"),
            new(t.AddDays(-1).AddMinutes(-200), "", ActivityResult.OK, "Mutual: watched alex: 1h 12m, 59 fps, ping 21 ms (worst 40), 0.0% loss, udp"),
            new(t.AddDays(-1).AddMinutes(-275), "", ActivityResult.FAILED, "Mutual: sending build.zip failed (alex cancelled)"),
            new(t.AddDays(-1).AddMinutes(-300), "", ActivityResult.OK, "Mutual: paired with alex (safety code 7D4F-9B98-8960)"),
        });
        if (online) { this.online = true; checking = false; friendStatus.Text = "Online  ·  18 ms"; avatar.Ring = Theme.Online; }
        if (live) { shareSource = "window: RimWorld by Ludeon Studios"; SetShare(ShareState.Live); }
        if (rimworld) { rimStage = "RimWorld is up. Load a colony and the split turns on by itself, then alex gets the stream offer."; SetShare(ShareState.Rimworld); }
        if (behind)
        {
            pairInfo.Text = "25.14.88.203   ·   safety code 7D4F-9B98-8960   ·   they're on 1.3.3";
            versionBar.Message = "alex is on Mutual 1.3.3, you're on 1.4.0. Ask them to update.";
            versionBar.On = true;
            updateBar.Message = "Mutual 1.4.1 is ready. Updating takes a few seconds and Mutual opens again by itself.";
            updateBar.On = true;
        }
        return this;
    }
    bool ignoreSaved;

    public void ShowMe() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true; Hide();
            // first time only, so closing the window doesnt look like it quit
            if (!settings.TrayHintShown && !offline)
            {
                tray.ShowBalloonTip(5000, "Mutual is still running", "It stays in the tray so " + (pairing?.PeerName ?? "your friend") + " can reach you. Right click the tray icon to quit.", ToolTipIcon.None);
                settings.TrayHintShown = true;
                settings.Save();
            }
            return;
        }
        StopSharing();
        StopServices();
        quit.Cancel();
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}
