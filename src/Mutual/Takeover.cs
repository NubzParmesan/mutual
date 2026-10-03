using System.Diagnostics;
using Mutual.Core;

namespace Mutual;

// switching a pc over from the old notifier, once, when you say so
// firewall rules (one admin prompt), stop Notifier.exe, move its startup shortcut into the pairing
// folder so it can go back, start mutual at login. the old ssh scripts still work
static class Takeover
{
    const string OldShortcut = "Mutual SSH Notifier.lnk", NewShortcut = "Mutual.lnk";
    static string Startup => Environment.GetFolderPath(Environment.SpecialFolder.Startup);

    public static bool OldNotifierRunning() => Process.GetProcessesByName("Notifier").Length > 0;
    public static bool StartsAtLogin() => File.Exists(Path.Combine(Startup, NewShortcut));

    public static void SetStartsAtLogin(bool on)
    {
        var path = Path.Combine(Startup, NewShortcut);
        if (on && !File.Exists(path)) MakeShortcut(path, Application.ExecutablePath, "--tray");
        else if (!on && File.Exists(path)) File.Delete(path);
    }

    // whats left to do on this pc in plain words, empty when done
    public static List<string> Pending(Pairing p)
    {
        var todo = new List<string>();
        if (p.IsLegacy && (OldNotifierRunning() || File.Exists(Path.Combine(Startup, OldShortcut)))) todo.Add("stop the old Notifier and take over its requests");
        if (StreamPort.Missing(p).Count > 0) todo.Add("add firewall rules so " + p.PeerName + " can reach Mutual (only " + p.PeerName + "'s address, only Mutual)");
        if (!StartsAtLogin() && AppSettings.Profile == null) todo.Add("start Mutual in the tray when you log in");
        return todo;
    }

    // returns what didnt work, null if it all did
    public static string? Run(Pairing p, string folder)
    {
        var problems = new List<string>();
        var missing = StreamPort.Missing(p);
        if (missing.Count > 0 && !StreamPort.AddRules(missing, Application.ExecutablePath, p.BindAddress.ToString(), p.PeerAddress))
            problems.Add("the firewall rules weren't added (admin prompt declined?)");

        if (p.IsLegacy)
        {
            foreach (var proc in Process.GetProcessesByName("Notifier"))
                try { proc.Kill(); proc.WaitForExit(3000); } catch { problems.Add("couldn't stop Notifier.exe"); }
            var old = Path.Combine(Startup, OldShortcut);
            if (File.Exists(old))
                try { File.Move(old, Path.Combine(folder, OldShortcut + ".disabled"), overwrite: true); }
                catch (Exception e) { problems.Add("couldn't move the old startup shortcut: " + e.Message); }
        }

        if (!StartsAtLogin() && AppSettings.Profile == null)
            try { MakeShortcut(Path.Combine(Startup, NewShortcut), Application.ExecutablePath, "--tray"); }
            catch (Exception e) { problems.Add("couldn't add Mutual to startup: " + e.Message); }
        return problems.Count == 0 ? null : string.Join("; ", problems);
    }

    // where a .lnk points, or null if it cant be read
    public static string? ShortcutTarget(string path)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell");
        if (shellType == null) return null;
        dynamic shell = Activator.CreateInstance(shellType)!;
        try { return (string)shell.CreateShortcut(path).TargetPath; }
        catch { return null; }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }

    public static void MakeShortcut(string path, string target, string args)
    {
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("no WScript.Shell");
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = args;
            link.WorkingDirectory = Path.GetDirectoryName(target);
            link.Description = "Mutual";
            link.Save();
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
}
