using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Mutual.Core;
using Mutual.Stream;

namespace Mutual;

// the main window. who youre paired with, if theyre on, the buttons and whats happened
// closing it js hides it in the tray, quit is in the tray menu
sealed class MainForm : Form
{
    readonly AppSettings settings;
    Pairing? pairing;
    string? pairingError;
    ActivityLog? log;
    LinkHub? hub;
    Rendezvous? rendezvous;
    readonly NotifyIcon tray;
    readonly Label friendName = new(), friendStatus = new(), pairInfo = new();
    readonly Panel statusDot = new();
    readonly Panel setupBar = new();
    readonly Label setupText = new();
    readonly ListView activity = new();
    readonly CancellationTokenSource quit = new();
    readonly ToolStripMenuItem trayStatus = new() { Enabled = false };
    readonly ToolStripMenuItem trayLogin = new("Start with Windows") { CheckOnClick = true };
    Button? streamButton, sshButton, sendButton;
    bool online, quitting;
    readonly bool offline;

    public MainForm(AppSettings s, bool offline = false)
    {
        settings = s; this.offline = offline;
        Text = AppSettings.Profile == null ? "Mutual" : "Mutual (" + AppSettings.Profile + ")";
        BackColor = Theme.Back;
        ForeColor = Theme.Text;
        Font = Theme.Body;
        ClientSize = new Size(460, 560);
        MinimumSize = new Size(420, 420);
        StartPosition = FormStartPosition.CenterScreen;
        Icon = Program.AppIcon;

        tray = new NotifyIcon { Icon = Program.TrayIcon(false), Text = "Mutual", Visible = !offline };
        var menu = new ContextMenuStrip();
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
        menu.Items.Add("Open log folder", null, (_, _) => Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "\"" + AppSettings.Dir + "\"") { UseShellExecute = true }));
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => { quitting = true; Close(); });
        menu.Opening += (_, _) => trayLogin.Checked = Takeover.StartsAtLogin();
        trayLogin.Click += (_, _) => { try { Takeover.SetStartsAtLogin(trayLogin.Checked); } catch (Exception e) { MessageBox.Show(this, e.Message, "Mutual"); } };
        tray.ContextMenuStrip = menu;
        tray.DoubleClick += (_, _) => ShowMe();

        BuildLayout();
        LoadPairing();
        if (offline) return;
        StartServices();
        var refresh = new System.Windows.Forms.Timer { Interval = 3000 };
        refresh.Tick += (_, _) => RefreshActivity();
        refresh.Start();
    }

    void BuildLayout()
    {
        var card = new Panel { BackColor = Theme.Panel, Dock = DockStyle.Top, Height = 92, Padding = new Padding(16) };
        statusDot.SetBounds(18, 26, 12, 12);
        statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var b = new SolidBrush(online ? Theme.Online : Theme.Offline);
            e.Graphics.FillEllipse(b, 0, 0, 11, 11);
        };
        friendName.SetBounds(38, 16, 380, 28); friendName.Font = Theme.Title; friendName.ForeColor = Theme.Text;
        friendStatus.SetBounds(38, 46, 380, 20); friendStatus.ForeColor = Theme.Dim;
        pairInfo.SetBounds(38, 66, 400, 18); pairInfo.ForeColor = Theme.Dim; pairInfo.Font = Theme.Small;
        var gear = new LinkLabel { Text = "settings", AutoSize = true, LinkColor = Theme.Dim, ActiveLinkColor = Theme.Accent, Font = Theme.Small, Anchor = AnchorStyles.Top | AnchorStyles.Right };
        gear.Location = new Point(card.Width - 70, 10);
        gear.LinkClicked += (_, _) => OpenSettings();
        card.Controls.AddRange(new Control[] { statusDot, friendName, friendStatus, pairInfo, gear });

        // shows until the firewall rules are in (and the old notifier is gone if you came from mutual ssh)
        setupBar.Dock = DockStyle.Top; setupBar.Height = 64; setupBar.BackColor = Color.FromArgb(58, 44, 20); setupBar.Padding = new Padding(16, 8, 12, 8); setupBar.Visible = false;
        setupText.Dock = DockStyle.Fill; setupText.ForeColor = Theme.Text; setupText.Font = Theme.Small;
        var setupButton = Theme.Button("Set up…", (_, _) => RunSetup(), primary: true);
        setupButton.Dock = DockStyle.Right; setupButton.Width = 96;
        setupBar.Controls.Add(setupText);
        setupBar.Controls.Add(setupButton);

        var actions = new TableLayoutPanel { Dock = DockStyle.Top, Height = 58, ColumnCount = 3, Padding = new Padding(12, 12, 12, 6), BackColor = Theme.Back };
        for (int i = 0; i < 3; i++) actions.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 33.3f));
        streamButton = Theme.Button("Stream…", (_, _) => { if (pairing == null) Pair(); else StartStream(); }, primary: true);
        sshButton = Theme.Button("SSH", (_, _) => StartSsh());
        sendButton = Theme.Button("Send file…", (_, _) => SendFile());
        foreach (var b in new[] { streamButton, sshButton, sendButton }) { b.Dock = DockStyle.Fill; b.Margin = new Padding(4); actions.Controls.Add(b); }

        var header = new Label { Text = "Recent activity", Dock = DockStyle.Top, Height = 28, Padding = new Padding(16, 8, 0, 0), ForeColor = Theme.Dim };
        activity.Dock = DockStyle.Fill;
        activity.View = View.Details;
        activity.HeaderStyle = ColumnHeaderStyle.None;
        activity.FullRowSelect = true;
        activity.BorderStyle = BorderStyle.None;
        activity.BackColor = Theme.Back;
        activity.ForeColor = Theme.Text;
        activity.Columns.Add("when", 90);
        activity.Columns.Add("what", 330);
        var listHost = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 0, 12, 12) };
        listHost.Controls.Add(activity);

        Controls.Add(listHost);
        Controls.Add(header);
        Controls.Add(actions);
        Controls.Add(setupBar);
        Controls.Add(card);
        void FitColumns() => activity.Columns[1].Width = Math.Max(120, activity.ClientSize.Width - activity.Columns[0].Width - SystemInformation.VerticalScrollBarWidth);
        Resize += (_, _) => FitColumns();
        Load += (_, _) => FitColumns();
    }

    // pairing

    void LoadPairing()
    {
        pairing = null; pairingError = null;
        try
        {
            if (Pairing.SavedExists(AppSettings.Dir)) pairing = Pairing.Load(AppSettings.Dir);
            else if (AppSettings.Profile == null && Pairing.LegacyFolderExists(settings.PairingFolder)) pairing = Pairing.FromLegacyFolder(settings.PairingFolder);
        }
        catch (Exception e) { pairingError = e.Message; }

        if (pairing == null)
        {
            friendName.Text = "Not paired yet";
            friendStatus.Text = pairingError ?? "Swap pairing codes with a friend to get started.";
            pairInfo.Text = "";
            if (streamButton != null) streamButton.Text = "Pair with a friend…";
            log = new ActivityLog(Path.Combine(AppSettings.Dir, "activity.jsonl"));
            Directory.CreateDirectory(AppSettings.Dir);
        }
        else
        {
            log = new ActivityLog(Path.Combine(pairing.Folder, "activity.jsonl"));
            friendName.Text = pairing.PeerName;
            pairInfo.Text = pairing.PeerAddress + "  ·  " + (pairing.IsLegacy ? "paired through Mutual SSH" : "safety code " + pairing.SafetyCode);
            friendStatus.Text = "checking…";
            if (streamButton != null) streamButton.Text = "Stream…";
        }
        trayStatus.Text = pairing == null ? "Not paired" : pairing.PeerName + ": checking…";
        RefreshActivity();
        RefreshSetup();
    }

    void Pair()
    {
        using var d = new PairDialog(settings, AppSettings.Dir);
        if (d.ShowDialog(this) != DialogResult.OK || d.Result == null) return;
        StopServices();
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
        hub = new LinkHub(pairing);
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
            bool up = await Presence.IsOnlineAsync(pairing.PeerAddress, pairing.PeerPorts.Ping) || rendezvous?.PeerOnline == true;
            if (up != online || friendStatus.Text == "checking…")
            {
                online = up;
                friendStatus.Text = up ? "online" : "offline";
                trayStatus.Text = pairing.PeerName + ": " + friendStatus.Text;
                tray.Icon = Program.TrayIcon(up);
                tray.Text = "Mutual · " + pairing.PeerName + " " + friendStatus.Text;
                statusDot.Invalidate();
            }
            try { await Task.Delay(online ? 15000 : 8000, ct); } catch { break; }
        }
    }

    async Task ListenForPings(CancellationToken ct)
    {
        var p = pairing!;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Notify.ListenAsync(IPAddress.Any, p.MyPorts.Ping, LoopbackFriend(p) ? null : p.PeerAddress, () => p.Own, p.PeerCertRaw,
                    req => BeginInvoke(() => OnRequest(req)), ct);
            }
            catch (SocketException)
            {
                // old Notifier.exe still has the port, it keeps handling pings until you hit set up
                log?.Write(ActivityResult.INFO, "Mutual: old notifier owns the ping port, leaving pings to it");
                BeginInvoke(RefreshSetup);
                return;
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
        if (pairing == null) { setupBar.Visible = false; return; }
        var todo = Takeover.Pending(pairing);
        setupBar.Visible = todo.Count > 0;
        setupText.Text = todo.Count == 0 ? "" : pairing.IsLegacy && Takeover.OldNotifierRunning()
            ? "Mutual isn't fully set up on this PC yet. Until it is, " + pairing.PeerName + "'s requests go to the old Notifier and you won't see stream invites."
            : "Finish setting up Mutual on this PC so " + pairing.PeerName + " can reach it.";
    }

    void RunSetup()
    {
        if (pairing == null) return;
        var todo = Takeover.Pending(pairing);
        if (todo.Count == 0) { RefreshSetup(); return; }
        var msg = "Mutual will:\n\n• " + string.Join("\n• ", todo) + (pairing.IsLegacy ? "\n\nThe old startup shortcut is kept in " + pairing.Folder + " so this can be undone." : "") + "\n\nWindows asks for admin once.";
        if (MessageBox.Show(this, msg, "Set up Mutual", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) != DialogResult.OK) return;
        var problem = Takeover.Run(pairing, pairing.Folder);
        log?.Write(problem == null ? ActivityResult.OK : ActivityResult.FAILED, "Mutual: setup " + (problem == null ? "done" : "incomplete: " + problem));
        if (problem != null) MessageBox.Show(this, "Not everything worked: " + problem, "Mutual");
        RefreshSetup();
        if (settings.HandlePings && !Takeover.OldNotifierRunning() && services != null) _ = ListenForPings(services.Token);
    }

    bool EnsureFirewall()
    {
        var missing = StreamPort.Missing(pairing!);
        if (missing.Count == 0) return true;
        var ok = MessageBox.Show(this, "This needs firewall rules on this PC: only " + pairing!.PeerName + "'s address, only Mutual, only its ports.\n\nWindows will ask for admin once.",
            "Mutual", MessageBoxButtons.OKCancel, MessageBoxIcon.Information) == DialogResult.OK;
        if (!ok) return false;
        if (StreamPort.AddRules(missing, Application.ExecutablePath, pairing.BindAddress.ToString(), pairing.PeerAddress)) { RefreshSetup(); return true; }
        MessageBox.Show(this, "The firewall rules weren't added, so " + pairing.PeerName + " can't connect yet.", "Mutual");
        return false;
    }

    // requests from them

    void OnRequest(Request req)
    {
        if (pairing == null) return;
        string detail = req.Kind == RequestKind.File && FileTransfer.TryParse(req.Detail, out var fn, out var fs) ? fn + " (" + TransferWindow.Size(fs) + ")" : req.Detail ?? "";
        log?.Write(ActivityResult.INFO, $"Mutual: {pairing.PeerName} asked for {req.Kind}" + (detail.Length > 0 ? ": " + detail : ""));
        if (settings.AutoAccept) { Accept(req); return; }
        new RequestPopup(pairing.PeerName, req.Kind == RequestKind.File ? req with { Detail = detail } : req, () => Accept(req)).Show();
    }

    void Accept(Request req)
    {
        switch (req.Kind)
        {
            case RequestKind.Ssh: StartSsh(answering: true); break;
            case RequestKind.Stream: _ = WatchStream(req.Detail); break;
            case RequestKind.File: _ = ReceiveFile(req.Detail); break;
            default: MessageBox.Show(this, req.Kind + " isn't built yet.", "Mutual"); break;
        }
    }

    // streaming

    StreamHost? sharing;
    BoxOverlay? boxFrame;
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
        var r = MessageBox.Show(this, "DuoCursor is running and blocks " + pairing!.PeerName + "'s mouse from reaching this PC. Close it for now?\n\n(Mutual does its job; you can start it again any time.)",
            "Mutual", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Question);
        if (r == DialogResult.Cancel) return false;
        if (r == DialogResult.Yes) foreach (var p in duo) try { p.Kill(); } catch { }
        return true;
    }

    public void StartStream(StreamSource? preset = null)
    {
        if (pairing == null || hub == null) { if (pairing == null) Pair(); return; }
        if (sharing != null || waitingForViewer != null) { StopSharing(); return; }
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
        SetShareButton("Waiting for " + p.PeerName + "… (click to cancel)");
        log?.Write(ActivityResult.INFO, "Mutual: offered to stream " + source.Describe());
        var asked = await Ask(new Request(RequestKind.Stream, source.Describe()));
        if (!asked) log?.Write(ActivityResult.INFO, "Mutual: couldn't reach " + p.PeerName + "'s Mutual; waiting anyway in case they open it");
        var wait = TimeSpan.FromMinutes(10);
        bool first = true;
        var tally = new SessionTally();
        while (!stopRequested)
        {
            waitingForViewer = new CancellationTokenSource();
            StreamHost host;
            try
            {
                var link = await hub!.OpenAsync(Channel.Stream, wait, waitingForViewer.Token);
                link.ReadTimeout = 15000;
                host = new StreamHost(new Stream.Wire(link), source, openUdp: UdpLane(isHost: true), shareSound: settings.ShareSound) { Tally = tally };
            }
            catch (Exception e)
            {
                if (!stopRequested) log?.Write(ActivityResult.INFO, "Mutual: " + (first ? "stream not started" : p.PeerName + " didn't come back") + " (" + e.Message + ")");
                break;
            }
            finally { waitingForViewer?.Dispose(); waitingForViewer = null; }

            sharing = host;
            using var clip = settings.ShareClipboard ? new ClipboardSync(this, host.SendClipboard) : null;
            if (clip != null) host.ClipboardReceived += clip.Apply;
            var ended = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            host.Status += s => Diag.Write("host: " + s);
            host.Status += s =>
            {
                if (s == "viewer left" || s.StartsWith("stream stopped")) ended.TrySetResult(host.EndedByViewer || s.StartsWith("stream stopped"));
                else if (s.StartsWith("video on") || s.StartsWith("udp unavailable") || s.StartsWith("paused") || s.StartsWith("resumed"))
                    BeginInvoke(() => log?.Write(ActivityResult.INFO, "Mutual: " + s));
            };
            log?.Write(ActivityResult.OK, "Mutual: " + (first ? "streaming " + source.Describe() + " to " + p.PeerName : p.PeerName + " is back, streaming again"));
            SetShareButton("Stop sharing");
            bool onPurpose = await ended.Task;
            host.Dispose();
            if (sharing == host) sharing = null;
            if (stopRequested || onPurpose) { if (!stopRequested) log?.Write(ActivityResult.INFO, "Mutual: " + p.PeerName + " closed the stream"); break; }
            log?.Write(ActivityResult.INFO, "Mutual: the link to " + p.PeerName + " dropped, waiting up to a minute for it to come back");
            SetShareButton("Reconnecting… (click to stop)");
            wait = TimeSpan.FromMinutes(1);
            first = false;
        }
        if (tally.Reports > 0) log?.Write(ActivityResult.OK, "Mutual: streamed to " + p.PeerName + ": " + tally.Describe());
        EndShare();
    }

    public void StopStreamForTests() => StopSharing();

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
        SetShareButton("Stream…");
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
        void Summary() { if (summarized || tally.Reports == 0) return; summarized = true; log?.Write(ActivityResult.OK, "Mutual: watched " + pairing!.PeerName + ": " + tally.Describe()); }
        for (int attempt = 0; ; attempt++)
        {
            StreamReceiver rx;
            try
            {
                var link = await hub.OpenAsync(Channel.Stream, wait);
                link.ReadTimeout = 15000;
                rx = new StreamReceiver(new Stream.Wire(link), UdpLane(isHost: false)) { Tally = tally };
            }
            catch (Exception e)
            {
                log?.Write(ActivityResult.INFO, "Mutual: " + (attempt == 0 ? "couldn't join the stream" : "couldn't get back into the stream") + " (" + e.Message + ")");
                if (attempt == 0 && !settings.AutoAccept) MessageBox.Show(this, "Couldn't join the stream: " + e.Message, "Mutual");
                Summary();
                return;
            }
            var view = new ViewerForm(rx, title) { Icon = Program.AppIcon };
            var clip = settings.ShareClipboard ? new ClipboardSync(this, rx.SendClipboard) : null;
            if (clip != null) { rx.ClipboardReceived += clip.Apply; view.FormClosed += (_, _) => clip.Dispose(); }
            if (lastBounds is { } b) { view.StartPosition = FormStartPosition.Manual; view.Bounds = b; }
            var closed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            bool userClosed = true;
            rx.Ended += why =>
            {
                Diag.Write("viewer: " + why);
                // they stopped sharing, the window stays open on the last picture but the session is over
                if (rx.EndedByHost) { try { BeginInvoke(Summary); } catch { } return; }
                // link broke, close the window ourselves and try again
                userClosed = false;
                try { view.BeginInvoke(() => { lastBounds = view.Bounds; view.Close(); }); } catch { }
            };
            view.FormClosed += (_, _) => { rx.Dispose(); Watching = null; closed.TrySetResult(true); };
            Watching = view; WatchingReceiver = rx;
            view.Show();
            log?.Write(ActivityResult.OK, "Mutual: " + (attempt == 0 ? "watching " + pairing.PeerName + "'s stream" : "back in " + pairing.PeerName + "'s stream"));
            await closed.Task;
            if (userClosed || rx.EndedByHost) { Summary(); return; }
            log?.Write(ActivityResult.INFO, "Mutual: lost the stream, trying to get back in");
            wait = TimeSpan.FromMinutes(1);
        }
    }

    // rimworld

    // starts rimworld thru steam if its not open, turns the split on and streams them the right half
    // their mouse and keys go to the mod, you keep the left half
    async Task PlayRimWorld()
    {
        if (pairing == null || hub == null) { if (pairing == null) Pair(); return; }
        if (sharing != null || waitingForViewer != null) { MessageBox.Show(this, "Stop the current stream first.", "Mutual"); return; }
        if (!EnsureFirewall()) return;
        foreach (var d in Process.GetProcessesByName("DuoCursor")) try { d.Kill(); d.WaitForExit(2000); } catch { }
        if (!SplitColonySource.GameRunning())
        {
            log?.Write(ActivityResult.INFO, "Mutual: starting RimWorld");
            try { Process.Start(new ProcessStartInfo("steam://rungameid/294100") { UseShellExecute = true }); }
            catch (Exception e) { MessageBox.Show(this, "Couldn't start RimWorld thru Steam: " + e.Message, "Mutual"); return; }
        }
        SplitColonySource src;
        try { src = new SplitColonySource(); }
        catch (SocketException) { MessageBox.Show(this, "Something else is already talking to the SplitColony mod.", "Mutual"); return; }
        SetShareButton("Waiting for RimWorld…");
        // the mod only answers once a colony is loaded and big mod lists take a while
        for (int i = 0; i < 600 && !src.Active; i++)
        {
            await Task.Delay(500);
            // game is up but no split yet, flip it
            if (i == 16 && SplitColonySource.GameRunning() && !src.Active) src.Toggle();
            if (i % 40 == 39 && !src.Active && SplitColonySource.GameRunning()) src.Toggle();
        }
        if (!src.Active)
        {
            src.Dispose(); SetShareButton("Stream…");
            MessageBox.Show(this, "RimWorld never turned the split on. Is the SplitColony mod enabled, and is a colony loaded?", "Mutual");
            return;
        }
        log?.Write(ActivityResult.OK, "Mutual: RimWorld split is on");
        _ = ShareAsync(src);
    }

    void SetShareButton(string text) { if (streamButton != null) streamButton.Text = text; }

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
        var win = new TransferWindow("Waiting for " + p.PeerName + " to accept " + Path.GetFileName(path) + "…", size);
        win.Show();
        log?.Write(ActivityResult.INFO, "Mutual: offered " + Path.GetFileName(path) + " (" + TransferWindow.Size(size) + ")");
        try
        {
            await Ask(new Request(RequestKind.File, FileTransfer.Describe(path)));
            using var link = await hub!.OpenAsync(Channel.File, TimeSpan.FromMinutes(10), win.Cancel.Token);
            link.ReadTimeout = link.WriteTimeout = 30000;
            win.Text = "Sending " + Path.GetFileName(path);
            await FileTransfer.SendAsync(link, path, win.Progress, win.Cancel.Token);
            log?.Write(ActivityResult.OK, "Mutual: sent " + Path.GetFileName(path) + ", verified by " + p.PeerName);
        }
        catch (Exception e) when (!win.Cancel.IsCancellationRequested) { log?.Write(ActivityResult.FAILED, "Mutual: sending " + Path.GetFileName(path) + " failed (" + e.Message + ")"); }
        catch (Exception) { log?.Write(ActivityResult.INFO, "Mutual: sending " + Path.GetFileName(path) + " cancelled"); }
        finally { if (!win.IsDisposed) win.Close(); }
    }

    async Task ReceiveFile(string? detail)
    {
        if (pairing == null || hub == null || !FileTransfer.TryParse(detail, out var name, out var size)) return;
        if (!EnsureFirewall()) return;
        var win = new TransferWindow("Receiving " + name + " from " + pairing.PeerName, size);
        win.Show();
        try
        {
            using var link = await hub.OpenAsync(Channel.File, TimeSpan.FromMinutes(2), win.Cancel.Token);
            link.ReadTimeout = link.WriteTimeout = 30000;
            var saved = await FileTransfer.ReceiveAsync(link, FileTransfer.DownloadsFolder(), name, size, win.Progress, win.Cancel.Token);
            log?.Write(ActivityResult.OK, "Mutual: received " + Path.GetFileName(saved) + " from " + pairing.PeerName + " (checked)");
            if (!settings.AutoAccept) Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), "/select,\"" + saved + "\"") { UseShellExecute = true });
        }
        catch (Exception e) when (!win.Cancel.IsCancellationRequested) { log?.Write(ActivityResult.FAILED, "Mutual: receiving " + name + " failed (" + e.Message + ")"); }
        catch (Exception) { log?.Write(ActivityResult.INFO, "Mutual: receiving " + name + " cancelled"); }
        finally { if (!win.IsDisposed) win.Close(); }
    }

    // ssh

    // opens the ssh window as an admin copy of mutual, the admin prompt is part of saying yes
    // talks the old protocol so it still works if theyre on mutual ssh
    void StartSsh(bool answering = false)
    {
        if (pairing == null) { MessageBox.Show(this, pairingError ?? "Not paired.", "Mutual"); return; }
        var args = "--ssh-session" + (answering ? " --no-ping" : "") + (AppSettings.Profile != null ? " --profile \"" + AppSettings.Profile + "\"" : "");
        try
        {
            Process.Start(new ProcessStartInfo(Application.ExecutablePath, args) { UseShellExecute = true, Verb = "runas" });
            log?.Write(ActivityResult.INFO, answering ? "Mutual: answering SSH request" : "Mutual: starting SSH");
        }
        catch (System.ComponentModel.Win32Exception) { log?.Write(ActivityResult.INFO, "Mutual: SSH cancelled at the admin prompt"); }
        catch (Exception e) { MessageBox.Show(this, "Couldn't start SSH: " + e.Message, "Mutual"); }
    }

    // activity list

    void RefreshActivity()
    {
        if (log == null) return;
        var rows = log.ReadRecent(60);
        rows.Reverse();
        activity.BeginUpdate();
        activity.Items.Clear();
        foreach (var r in rows)
        {
            var local = r.Time.ToLocalTime();
            string when = local.Date == DateTime.Today ? local.ToString("h:mm tt") : local.ToString("MMM d");
            // mutuals own rows start with "Mutual: " for the old viewer, no point showing it here
            var item = new ListViewItem(new[] { when, r.Action.StartsWith("Mutual: ") ? r.Action[8..] : r.Action })
            {
                ForeColor = r.Result switch { ActivityResult.FAILED => Theme.Bad, ActivityResult.OK => Theme.Online, _ => Theme.Text },
            };
            activity.Items.Add(item);
        }
        activity.EndUpdate();
    }

    // for tests, what this copy is doing rn as one json line
    public string Report() => JsonSerializer.Serialize(new
    {
        paired = pairing != null, online,
        sharing = sharing != null, framesSent = sharing?.FramesSent ?? 0, hostTransport = sharing?.Transport,
        watching = Watching != null, presented = Watching?.Presented ?? 0, viewerStats = sharing?.LastStats,
    });

    void ShowMe() { Show(); WindowState = FormWindowState.Normal; Activate(); }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (!quitting && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }
        StopSharing();
        StopServices();
        quit.Cancel();
        tray.Visible = false;
        base.OnFormClosing(e);
    }
}
