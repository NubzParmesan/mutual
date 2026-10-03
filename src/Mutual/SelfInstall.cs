using System.Diagnostics;
using System.Security.Cryptography;

namespace Mutual;

// no installer, it installs itself into Program Files (one admin prompt). Program Files matters:
// anything running as you can swap files in your own folders, and the ssh button asks windows to run
// mutual as admin, so the exe has to sit somewhere only an admin can change
// before installing or updating it checks itself against the checksum on the matching github release
static class SelfInstall
{
    public const string OfficialRepo = "NubzParmesan/mutual";
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Mutual");
    public static string Exe => Path.Combine(Dir, "Mutual.exe");
    // where 1.0.0 used to install itself, per user
    static string OldDir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mutual");

    public static bool RunningInstalled => Same(Environment.ProcessPath!, Exe);
    public static bool DevBuild => Environment.ProcessPath!.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar);
    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // true if this copy should keep running, false if it handed off to the installed one
    public static bool Offer(AppSettings settings)
    {
        var me = Environment.ProcessPath!;
        if (AppSettings.Profile != null || DevBuild) return true;
        if (RunningInstalled)
        {
            // first run from Program Files (put there by a script, say) makes the shortcuts once
            RepairShortcuts(createMissing: !settings.ShortcutsMade);
            if (!settings.ShortcutsMade) { settings.ShortcutsMade = true; settings.Save(); }
            return true;
        }

        var mine = Version(me);
        bool installed = File.Exists(Exe);
        var theirs = installed ? Version(Exe) : null;
        bool fromOld = Same(Path.GetDirectoryName(me)!, OldDir);
        string msg;
        if (!installed)
        {
            if (settings.DeclinedInstall && !fromOld) return true;
            msg = fromOld
                ? "Move Mutual to Program Files?\n\nIt's safer there: other programs can't swap it out, which matters because SSH runs it as admin. Windows asks for admin once."
                : "Install Mutual?\n\nIt goes in Program Files with Start menu and desktop shortcuts. Windows asks for admin once.\n\nNo runs it from here this time.";
        }
        else if (theirs != null && mine != null && mine > theirs)
            msg = $"Update the installed Mutual from {theirs} to {mine}? Windows asks for admin once.";
        else
        {
            // installed one is the same or newer so js open that
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
            return false;
        }

        // is this the real one
        var check = Verify(me, mine);
        if (check == Check.Mismatch)
        {
            MessageBox.Show($"This Mutual.exe doesn't match any official Mutual {mine} from github.com/{OfficialRepo}.\n\nIt might have been changed by someone. It won't be installed. Get it from the Releases page instead.",
                "Mutual", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (check == Check.Unknown) msg += "\n\n(Couldn't check this copy against the official release. Only say yes if you got it from github.com/" + OfficialRepo + ".)";

        if (MessageBox.Show(msg, "Mutual", MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
        {
            if (!installed && !fromOld) { settings.DeclinedInstall = true; settings.Save(); }
            return true;
        }
        try
        {
            // the copy into Program Files is the only part that runs as admin
            var p = Process.Start(new ProcessStartInfo(me, "--install-copy") { UseShellExecute = true, Verb = "runas" })!;
            p.WaitForExit();
            if (p.ExitCode != 0 || !File.Exists(Exe)) throw new IOException("the copy didnt finish");
            RepairShortcuts(createMissing: true);
            settings.ShortcutsMade = true; settings.Save();
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
            return false;
        }
        catch (System.ComponentModel.Win32Exception) { return true; }   // said no at the admin prompt
        catch (Exception e)
        {
            MessageBox.Show("Couldn't install: " + e.Message + "\n\nRunning from here instead.", "Mutual");
            return true;
        }
    }

    // the admin half: stop the old installed copy and put this one in Program Files. it only ever
    // copies itself, never a path its handed, so nothing can launch it to sneak some other exe in
    public static int CopyElevated()
    {
        var source = Environment.ProcessPath!;
        try
        {
            foreach (var p in Process.GetProcessesByName("Mutual"))
                try { if (p.Id != Environment.ProcessId && Same(p.MainModule!.FileName, Exe)) { p.Kill(); p.WaitForExit(3000); } } catch { }
            Directory.CreateDirectory(Dir);
            File.Copy(source, Exe, overwrite: true);
            return 0;
        }
        catch (Exception e) { Diag.Write("install copy failed: " + e); return 1; }
    }

    // start menu, desktop and startup shortcuts point at the installed exe. something rewriting
    // the startup one to run another program gets put back (and logged)
    public static void RepairShortcuts(bool createMissing = false)
    {
        var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mutual.lnk");
        var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mutual.lnk");
        var startup = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Mutual.lnk");
        foreach (var (path, args, create) in new[] { (startMenu, "", createMissing), (desktop, "", createMissing), (startup, "--tray", false) })
        {
            try
            {
                if (!File.Exists(path)) { if (create) Takeover.MakeShortcut(path, Exe, args); continue; }
                var target = Takeover.ShortcutTarget(path);
                if (target != null && Same(target, Exe)) continue;
                if (path == startup && target != null && !Same(Path.GetDirectoryName(target)!, OldDir))
                    Diag.Write("startup shortcut pointed at " + target + " instead of mutual, put it back");
                Takeover.MakeShortcut(path, Exe, args);
            }
            catch { }
        }
    }

    enum Check { Match, Mismatch, Unknown }

    // hashes this exe and compares it with Mutual.exe.sha256 on the release for its version
    static Check Verify(string path, Version? v)
    {
        if (v == null) return Check.Unknown;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(6) };
            var url = $"https://github.com/{OfficialRepo}/releases/download/v{v.Major}.{v.Minor}.{v.Build}/Mutual.exe.sha256";
            var resp = http.GetAsync(url).GetAwaiter().GetResult();
            // github answered but theres no such release: a copy claiming a version that was never published
            if (resp.StatusCode == System.Net.HttpStatusCode.NotFound) return Check.Mismatch;
            if (!resp.IsSuccessStatusCode) return Check.Unknown;
            var want = resp.Content.ReadAsStringAsync().GetAwaiter().GetResult().Trim().Split(' ')[0].ToLowerInvariant();
            using var f = File.OpenRead(path);
            var have = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
            return have == want ? Check.Match : Check.Mismatch;
        }
        catch { return Check.Unknown; }
    }

    static Version? Version(string path)
    {
        try { var v = FileVersionInfo.GetVersionInfo(path); return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart); }
        catch { return null; }
    }
}
