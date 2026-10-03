using System.Diagnostics;
using System.Net.Sockets;
using System.Security.Principal;
using System.Text.Json;
using Mutual.Core;

namespace Mutual;

// ssh, run as its own admin copy of mutual (the admin prompt is part of saying yes)
// same protocol, log lines and active.json as the old Connect.ps1 so it works with someone on the old
// scripts and OFF.cmd still works
//   wait for them -> both send 1 -> start sshd -> both send 2 -> open ssh window -> 3 every second -> sshd off
// a watchdog copy turns sshd back off if this one dies
sealed class SshSession : Form
{
    const string ControllerMutex = @"Global\MutualSSH-Controller";
    readonly Pairing p;
    readonly ActivityLog log;
    readonly bool ping;
    readonly Label status = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = Theme.Body, Padding = new Padding(16, 14, 16, 0) };
    readonly Button stopButton;
    readonly CancellationTokenSource stop = new();
    string StatePath => Path.Combine(p.Folder, "active.json");

    public SshSession(Pairing pairing, bool sendPing)
    {
        p = pairing; ping = sendPing;
        log = new ActivityLog(Path.Combine(p.Folder, "activity.jsonl"));
        Text = "Mutual SSH with " + p.PeerName;
        Icon = Program.AppIcon;
        BackColor = Theme.Back; ForeColor = Theme.Text; Font = Theme.Body;
        ClientSize = new Size(420, 150);
        FormBorderStyle = FormBorderStyle.FixedSingle; MaximizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        stopButton = Theme.Button("Disconnect", (_, _) => stop.Cancel(), primary: true);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 52, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(10), BackColor = Theme.Back };
        stopButton.Width = 120;
        bar.Controls.Add(stopButton);
        Controls.Add(status);
        Controls.Add(bar);
        Shown += async (_, _) => { await Run(); Close(); };
        FormClosing += (_, _) => stop.Cancel();
    }

    void Say(string s) { if (!IsDisposed) BeginInvoke(() => status.Text = s); }

    async Task Run()
    {
        if (!new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator))
        { MessageBox.Show(this, "SSH needs to run as administrator.", "Mutual"); return; }
        var sshClient = FindSshClient();
        if (sshClient == null) { MessageBox.Show(this, "The OpenSSH client (ssh.exe) isn't installed. Add \"OpenSSH Client\" in Settings > Apps > Optional features. SSH was not switched on.", "Mutual"); return; }
        if (!Sc("query sshd").ok) { MessageBox.Show(this, "OpenSSH Server isn't installed on this PC, so there's nothing for " + p.PeerName + " to connect to. Add \"OpenSSH Server\" in Settings > Apps > Optional features.", "Mutual"); return; }

        using var mutex = new Mutex(false, ControllerMutex);
        bool locked;
        try { locked = mutex.WaitOne(0); } catch (AbandonedMutexException) { locked = true; }
        if (!locked) { MessageBox.Show(this, "An SSH session is already running.", "Mutual"); return; }
        System.Net.Security.SslStream? link = null;
        Process? shell = null, watchdog = null;
        string phase = "Waiting"; DateTime? readyAt = null;
        try
        {
            StopManagedSsh();
            var me = Process.GetCurrentProcess();
            WriteState(me, null, "Waiting");
            watchdog = Process.Start(new ProcessStartInfo(Application.ExecutablePath, $"--ssh-watchdog \"{StatePath}\"") { UseShellExecute = false, CreateNoWindow = true });
            log.Write(ActivityResult.INFO, "Waiting for paired peer; SSH off");
            Say("SSH is off. Waiting up to 10 minutes for " + p.PeerName + "...\r\nClose this window to cancel.");
            // old notifier and mutual both know MSN1. it only asks, doesnt turn anything on
            if (ping) _ = Notify.SendAsync(p.PeerAddress, p.PeerPorts.Ping, p.Own, p.PeerCertRaw, new Request(RequestKind.Ssh, null), legacy: true);

            link = await PeerLink.OpenAsync(p.Role, p.Role == LinkRole.Server ? p.BindAddress : System.Net.IPAddress.Any, p.PeerAddress, p.PeerPorts.Consent,
                p.Own, p.PeerCertRaw, TimeSpan.FromMinutes(10), stop.Token);
            await PeerLink.ExchangeByteAsync(link, LegacyConsent.Present, stop.Token);
            if (watchdog.HasExited) throw new InvalidOperationException("Watchdog failed to start; SSH remains off.");
            if (!Sc("start sshd").ok && ServiceState() != "RUNNING") throw new InvalidOperationException("Windows SSH wouldn't start.");
            await PeerLink.ExchangeByteAsync(link, LegacyConsent.Enabled, stop.Token);
            log.Write(ActivityResult.OK, "Both peers ready; Windows SSH enabled");
            phase = "Ready"; readyAt = DateTime.UtcNow;
            var target = SshTarget();
            if (!Pairing.IsPlainAddress(target)) throw new InvalidOperationException("The saved address for " + p.PeerName + " doesn't look like an address; not passing it to ssh.");
            // -- so nothing after it can count as an ssh option
            shell = Process.Start(new ProcessStartInfo(sshClient) { UseShellExecute = false, ArgumentList = { "--", target } });
            WriteState(me, shell, "Ready");
            Say("Both ready. SSH is on and a login window opened.\r\nDisconnect here (or close this window) to turn it off.");
            while (File.Exists(StatePath) && !stop.IsCancellationRequested)
            {
                if (watchdog.HasExited) throw new InvalidOperationException("Watchdog stopped; closing SSH.");
                await PeerLink.ExchangeByteAsync(link, LegacyConsent.Alive, stop.Token);
                await Task.Delay(1000, stop.Token);
            }
            log.Write(ActivityResult.INFO, "Session ended from this PC");
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested)
        {
            log.Write(ActivityResult.INFO, phase == "Ready" ? "Session ended from this PC" : "Cancelled before " + p.PeerName + " answered; SSH stayed off");
        }
        catch (Exception e)
        {
            // same wording as the old controller so both lists read the same
            bool timedOut = false, io = false;
            for (var x = e; x != null; x = x.InnerException)
            {
                if (x is IOException) io = true;
                if (x is SocketException se) { io = true; if (se.SocketErrorCode == SocketError.TimedOut) timedOut = true; }
            }
            if (phase == "Ready" && (e.Message.Contains("Peer disconnected") || io && !timedOut))
                log.Write(ActivityResult.INFO, readyAt != null && (DateTime.UtcNow - readyAt.Value).TotalSeconds < 5
                    ? p.PeerName + " dropped the link immediately after connecting. That is usually a fault on their PC, not a normal disconnect; ask them to check their own activity list."
                    : "Session ended: " + p.PeerName + " disconnected");
            else if (phase == "Ready" && timedOut) log.Write(ActivityResult.INFO, "Session ended: lost contact with " + p.PeerName + " (their PC went offline or the network dropped)");
            else if (e is TimeoutException) log.Write(ActivityResult.INFO, "No answer from " + p.PeerName + " within 10 minutes; SSH stayed off");
            else { log.Write(ActivityResult.FAILED, "Controller error: " + e.Message); MessageBox.Show(this, "SSH stopped: " + e.Message, "Mutual"); }
        }
        finally
        {
            try { StopManagedSsh(); } catch (Exception e) { MessageBox.Show(this, "Couldn't confirm SSH is off: " + e.Message, "Mutual"); }
            try { if (shell is { HasExited: false }) shell.Kill(); } catch { }
            try { File.Delete(StatePath); } catch { }
            link?.Dispose();
            mutex.ReleaseMutex();
        }
    }

    // "ssh name" if ~/.ssh/config has them as a host, otherwise their address
    string SshTarget()
    {
        var cfg = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh", "config");
        try
        {
            if (File.Exists(cfg) && File.ReadLines(cfg).Any(l => l.Trim().StartsWith("Host ", StringComparison.OrdinalIgnoreCase) && l.Trim()[5..].Split(' ').Contains(p.PeerName)))
                return p.PeerName;
        }
        catch { }
        return p.PeerAddress;
    }

    static string? FindSshClient()
    {
        // mutual is 64 bit but check sysnative too in case
        foreach (var dir in new[] { "System32", "Sysnative" })
        {
            var f = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), dir, "OpenSSH", "ssh.exe");
            if (File.Exists(f)) return f;
        }
        // never off PATH: this runs as admin and a folder on PATH could be writable by anyone
        var pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "OpenSSH", "ssh.exe");
        return File.Exists(pf) ? pf : null;
    }

    void WriteState(Process parent, Process? shell, string phase)
    {
        // the old OFF.cmd and Watchdog.ps1 read these exact fields
        var state = new Dictionary<string, object>
        {
            ["ParentId"] = parent.Id, ["ParentTicks"] = parent.StartTime.ToUniversalTime().Ticks,
            ["ShellId"] = shell?.Id ?? 0, ["ShellTicks"] = shell?.StartTime.ToUniversalTime().Ticks ?? 0L, ["Phase"] = phase,
        };
        File.WriteAllText(StatePath, JsonSerializer.Serialize(state));
    }

    // sshd off, manual start, no sshd processes left, and checked
    void StopManagedSsh() => StopManagedSsh(log);

    public static void StopManagedSsh(ActivityLog log)
    {
        Sc("config sshd start= demand");
        Sc("stop sshd");
        foreach (var pr in Process.GetProcessesByName("sshd")) try { pr.Kill(); pr.WaitForExit(2000); } catch { }
        for (int i = 0; i < 20 && ServiceState() != "STOPPED"; i++) Thread.Sleep(250);
        if (ServiceState() != "STOPPED" || Process.GetProcessesByName("sshd").Length > 0 || !Sc("qc sshd").output.Contains("DEMAND_START"))
            throw new InvalidOperationException("SSH shutdown could not be verified.");
        log.Write(ActivityResult.OK, "Windows SSH stopped; startup manual");
    }

    static string ServiceState()
    {
        var o = Sc("query sshd").output;
        foreach (var s in new[] { "RUNNING", "STOPPED", "START_PENDING", "STOP_PENDING" }) if (o.Contains(s)) return s;
        return "UNKNOWN";
    }

    static (bool ok, string output) Sc(string args)
    {
        try
        {
            using var pr = Process.Start(new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "sc.exe"), args) { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            var o = pr.StandardOutput.ReadToEnd();
            pr.WaitForExit();
            return (pr.ExitCode == 0, o);
        }
        catch { return (false, ""); }
    }

    // watchdog, a second admin copy watching the controller in active.json
    // if the controller dies (crash, killed, whatever) it turns sshd off and closes the login window
    // same as the old Watchdog.ps1
    public static int Watchdog(string statePath)
    {
        var log = new ActivityLog(Path.Combine(Path.GetDirectoryName(statePath)!, "activity.jsonl"));
        JsonElement Read() { using var d = JsonDocument.Parse(File.ReadAllText(statePath)); return d.RootElement.Clone(); }
        if (!File.Exists(statePath)) return 0;
        var first = Read();
        int pid = first.GetProperty("ParentId").GetInt32(); long ticks = first.GetProperty("ParentTicks").GetInt64();
        while (File.Exists(statePath))
        {
            JsonElement s;
            try { s = Read(); } catch { Thread.Sleep(200); continue; }
            if (s.GetProperty("ParentId").GetInt32() != pid || s.GetProperty("ParentTicks").GetInt64() != ticks) return 0;
            bool alive;
            try { var pr = Process.GetProcessById(pid); alive = pr.StartTime.ToUniversalTime().Ticks == ticks; } catch { alive = false; }
            if (!alive)
            {
                try { StopManagedSsh(log); } catch { log.Write(ActivityResult.FAILED, "OFF failed; shutdown not verified"); }
                try
                {
                    int shellId = s.GetProperty("ShellId").GetInt32();
                    // active.json sits in a folder anyone logged in can write, so only ever kill an ssh window from it
                    if (shellId > 0) { var sh = Process.GetProcessById(shellId); if (sh.ProcessName.Equals("ssh", StringComparison.OrdinalIgnoreCase) && sh.StartTime.ToUniversalTime().Ticks == s.GetProperty("ShellTicks").GetInt64()) sh.Kill(); }
                }
                catch { }
                try { File.Delete(statePath); } catch { }
                return 0;
            }
            Thread.Sleep(1000);
        }
        return 0;
    }
}
