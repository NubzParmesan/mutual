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
        ApplicationConfiguration.Initialize();
        Diag.HookCrashes();
        ActivityLog.Mirror = Diag.Write;
        var settings = AppSettings.Load();
        if (args.Contains("--ssh-session"))
        {
            var folder = AppSettings.Dir;
            var pairing = Pairing.SavedExists(folder) ? Pairing.Load(folder)
                : Pairing.LegacyFolderExists(settings.PairingFolder) ? Pairing.FromLegacyFolder(settings.PairingFolder) : null;
            if (pairing == null) { MessageBox.Show("Not paired yet.", "Mutual"); return 1; }
            Application.Run(new SshSession(pairing, sendPing: !args.Contains("--no-ping")));
            return 0;
        }

        if (!args.Contains("--snapshot") && !args.Contains("--snapshot-pair") && !SelfInstall.Offer(settings)) return 0;
        using var single = new Mutex(true, @"Local\Mutual-App" + (AppSettings.Profile == null ? "" : "-" + AppSettings.Profile), out bool first);
        if (!first) return 0;
        var snap = Arg("--snapshot");
        if (snap != null)
        {
            // draws the window to a png without showing it or doing any networking
            using var f = new MainForm(settings, offline: true);
            f.CreateControl();
            ForceHandles(f);
            f.PerformLayout();
            using var bmp = new Bitmap(f.Width, f.Height);
            f.DrawToBitmap(bmp, new Rectangle(0, 0, f.Width, f.Height));
            bmp.Save(snap);
            return 0;
        }
        var snapPair = Arg("--snapshot-pair");
        if (snapPair != null)
        {
            using var d = new PairDialog(settings, Path.Combine(Path.GetTempPath(), "mutual-snapshot-identity"));
            d.CreateControl(); ForceHandles(d); d.PerformLayout();
            using var bmp = new Bitmap(d.Width, d.Height);
            d.DrawToBitmap(bmp, new Rectangle(0, 0, d.Width, d.Height));
            bmp.Save(snapPair);
            return 0;
        }
        var form = new MainForm(settings);
        Automate(form, Arg("--do"), Arg("--report"));
        if (Arg("--stop-after") is { } stopAfter)
        {
            // tests: end the stream cleanly so both sides write their summary
            var st = new System.Windows.Forms.Timer { Interval = int.Parse(stopAfter) * 1000 };
            st.Tick += (_, _) => { st.Stop(); form.StopStreamForTests(); };
            st.Start();
        }
        if (args.Contains("--tray")) Application.Run();   // started at login, stay in the tray
        else Application.Run(form);
        return 0;
    }

    static void ForceHandles(Control c) { _ = c.Handle; foreach (Control k in c.Controls) ForceHandles(k); }

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
