using Mutual.Core;
using Mutual.Stream;

namespace Mutual;

static class Program
{
    public static Icon AppIcon { get; } = LoadIcon();

    static Icon LoadIcon()
    {
        try { return Icon.ExtractAssociatedIcon(Environment.ProcessPath!) ?? SystemIcons.Application; }
        catch { return SystemIcons.Application; }
    }

    // tray icon gets a green dot when theyre online
    public static Icon TrayIcon(bool online)
    {
        using var bmp = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.DrawIcon(new Icon(AppIcon, 32, 32), new Rectangle(0, 0, 32, 32));
            if (online)
            {
                g.FillEllipse(Brushes.Black, 19, 19, 13, 13);
                using var b = new SolidBrush(Theme.Online);
                g.FillEllipse(b, 21, 21, 9, 9);
            }
        }
        return Icon.FromHandle(bmp.GetHicon());
    }

    [STAThread]
    static int Main(string[] args)
    {
        string? Arg(string name) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : null; }
        AppSettings.Profile = Arg("--profile");

        // the admin helpers, no tray and no single instance check
        if (args.Contains("--ssh-watchdog")) return SshSession.Watchdog(Arg("--ssh-watchdog")!);
        if (args.Contains("--install-copy")) return SelfInstall.CopyElevated();
        ApplicationConfiguration.Initialize();
        Diag.HookCrashes();
        ActivityLog.Mirror = Diag.Write;
        var settings = AppSettings.Load();
        if (args.Contains("--ssh-session"))
        {
            var folder = AppSettings.Dir;
            var pairing = Pairing.SavedExists(folder) ? Pairing.Load(folder)
                : Pairing.LegacyFolderExists(settings.PairingFolder) ? Pairing.FromLegacyFolder(settings.PairingFolder) : null;
            if (pairing == null) { Prompt.Show("Not paired yet.", "Mutual"); return 1; }
            Application.Run(new SshSession(pairing, sendPing: !args.Contains("--no-ping")));
            return 0;
        }

        if (!args.Any(a => a.StartsWith("--snapshot")) && !SelfInstall.Offer(settings, args.Contains("--update"))) return 0;
        var instance = @"Local\Mutual-App" + (AppSettings.Profile == null ? "" : "-" + AppSettings.Profile);
        using var single = new Mutex(true, instance, out bool first);
        if (!first)
        {
            // already running (usually hiding in the tray since login), so tell that one to show itself
            // instead of quitting with nothing on screen
            try { using var show = EventWaitHandle.OpenExisting(instance + "-show"); show.Set(); } catch { }
            return 0;
        }
        if (Arg("--snapshot-ui") is { } which) return Snapshots.Run(settings, which, args[Array.IndexOf(args, "--snapshot-ui") + 2]);
        if (Arg("--snapshot") is { } snap) return Snapshots.Run(settings, "main", snap);
        if (Arg("--snapshot-pair") is { } snapPair) return Snapshots.Run(settings, "pair", snapPair);
        var form = new MainForm(settings);
        var ui = SynchronizationContext.Current;
        var showMe = new EventWaitHandle(false, EventResetMode.AutoReset, instance + "-show");
        new Thread(() => { while (showMe.WaitOne()) ui?.Post(_ => form.ShowMe(), null); }) { IsBackground = true, Name = "mutual show me" }.Start();
        Automate(form, Arg("--do"), Arg("--report"));
        if (Arg("--stop-after") is { } stopAfter)
        {
            // tests: end the stream cleanly so both sides write their summary
            var st = new System.Windows.Forms.Timer { Interval = int.Parse(stopAfter) * 1000 };
            st.Tick += (_, _) => { st.Stop(); form.StopStreamForTests(); };
            st.Start();
        }
        if (args.Contains("--tray"))
        {
            // started at login, stay in the tray. quitting closes a form nobody showed, which on its own
            // left the process running invisibly (holding the ports, so opening mutual again did nothing)
            form.FormClosed += (_, _) => Application.ExitThread();
            Application.Run();
        }
        else Application.Run(form);
        return 0;
    }

    // lets tests drive it without clicking: --do "stream-box x y w h", "send-file path" etc
    // --report file writes a status line every second
    static void Automate(MainForm form, string? command, string? reportPath)
    {
        if (command != null)
        {
            var t = new System.Windows.Forms.Timer { Interval = 2500 };
            t.Tick += (_, _) =>
            {
                t.Stop();
                var parts = command.Split(' ', 2);
                switch (parts[0])
                {
                    case "stream-box":
                        var n = parts[1].Split(' ').Select(int.Parse).ToArray();
                        form.StartStream(new BoxSource(new Rectangle(n[0], n[1], n[2], n[3])));
                        break;
                    case "send-file": form.SendFile(parts[1]); break;
                    case "play-rimworld": form.PlayRimWorldNow(); break;
                    case "update-now": _ = form.UpdateNowForTests(); break;
                    case "quit": form.QuitForTests(); break;
                    case "sever-after":
                        // testing reconnect, the link js dies a few seconds in
                        var s2 = new System.Windows.Forms.Timer { Interval = int.Parse(parts[1]) * 1000 };
                        s2.Tick += (_, _) => { s2.Stop(); form.WatchingReceiver?.Sever(); };
                        s2.Start();
                        break;
                    case "stream-window":
                        var w = WindowSource.List().FirstOrDefault(x => x.Title.Contains(parts[1], StringComparison.OrdinalIgnoreCase));
                        if (w != null) form.StartStream(w);
                        break;
                }
            };
            t.Start();
        }
        if (reportPath != null)
        {
            var r = new System.Windows.Forms.Timer { Interval = 1000 };
            r.Tick += (_, _) => { try { File.WriteAllText(reportPath, form.Report()); } catch { } };
            r.Start();
        }
    }
}
