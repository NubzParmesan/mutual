using System.Diagnostics;

namespace Mutual;

// no installer, it installs itself. run from somewhere else (downloads, a usb) it offers to copy
// itself to %LOCALAPPDATA%\Programs\Mutual and make shortcuts. the firewall rules point at the
// exe so it needs to stay put. a newer copy replaces an older one
static class SelfInstall
{
    public static string Dir => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Mutual");
    public static string Exe => Path.Combine(Dir, "Mutual.exe");

    // true if this copy should keep running, false if it handed off to the installed one
    public static bool Offer(AppSettings settings)
    {
        var me = Environment.ProcessPath!;
        if (AppSettings.Profile != null || string.Equals(Path.GetFullPath(me), Path.GetFullPath(Exe), StringComparison.OrdinalIgnoreCase)) return true;
        // dev builds never ask
        if (me.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar)) return true;

        var mine = Version(me);
        bool installed = File.Exists(Exe);
        var theirs = installed ? Version(Exe) : null;
        string msg;
        if (!installed)
        {
            if (settings.DeclinedInstall) return true;
            msg = "Install Mutual?\n\nIt goes in your own programs folder (no admin needed) with Start menu and desktop shortcuts. Firewall rules point at it, so it works best from one fixed spot.\n\nNo runs it from here this time.";
        }
        else if (theirs != null && mine != null && mine > theirs)
            msg = $"Update the installed Mutual from {theirs} to {mine}?";
        else
        {
            // installed one is the same or newer so js open that
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
            return false;
        }
        var r = MessageBox.Show(msg, "Mutual", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
        if (r != DialogResult.Yes)
        {
            if (!installed) { settings.DeclinedInstall = true; settings.Save(); }
            return true;
        }
        try
        {
            foreach (var p in Process.GetProcessesByName("Mutual"))
                if (p.Id != Environment.ProcessId && string.Equals(p.MainModule?.FileName, Exe, StringComparison.OrdinalIgnoreCase)) { p.Kill(); p.WaitForExit(3000); }
            Directory.CreateDirectory(Dir);
            File.Copy(me, Exe, overwrite: true);
            var startMenu = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Mutual.lnk");
            var desktop = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Mutual.lnk");
            Takeover.MakeShortcut(startMenu, Exe, "");
            if (!File.Exists(desktop)) Takeover.MakeShortcut(desktop, Exe, "");
            Process.Start(new ProcessStartInfo(Exe) { UseShellExecute = true });
            return false;
        }
        catch (Exception e)
        {
            MessageBox.Show("Couldn't install: " + e.Message + "\n\nRunning from here instead.", "Mutual");
            return true;
        }
    }

    static Version? Version(string path)
    {
        try { var v = FileVersionInfo.GetVersionInfo(path); return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart, v.FilePrivatePart); }
        catch { return null; }
    }
}
