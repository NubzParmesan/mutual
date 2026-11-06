using System.Text.RegularExpressions;
using System.Xml.Linq;
using Microsoft.Win32;

namespace Mutual.Core;

public enum HostProblem { None, NoGame, NoMod }

// can this pc host the rimworld split. whoever clicks play is the host, so this runs before anything starts
// only says no when its sure. a config it cant read counts as fine, the wait banner catches the rest
public static class RimWorldHost
{
    const string ModId = "aiden.splitcolony";

    public static string ModsConfigPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        "AppData", "LocalLow", "Ludeon Studios", "RimWorld by Ludeon Studios", "Config", "ModsConfig.xml");

    public static HostProblem Check(bool gameRunning) => Check(SteamRoot(), ModsConfigPath, gameRunning);

    public static HostProblem Check(string? steamRoot, string modsConfig, bool gameRunning)
    {
        if (!gameRunning && !Installed(steamRoot)) return HostProblem.NoGame;
        return ModOff(modsConfig) ? HostProblem.NoMod : HostProblem.None;
    }

    static string? SteamRoot()
    {
        try { return Registry.GetValue(@"HKEY_CURRENT_USER\Software\Valve\Steam", "SteamPath", null) as string; }
        catch { return null; }
    }

    // the game can be in any steam library, libraryfolders.vdf lists them
    static bool Installed(string? steamRoot)
    {
        if (string.IsNullOrEmpty(steamRoot)) return false;
        var roots = new List<string> { steamRoot };
        try
        {
            var vdf = File.ReadAllText(Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf"));
            foreach (Match m in Regex.Matches(vdf, "\"path\"\\s+\"([^\"]+)\""))
                roots.Add(m.Groups[1].Value.Replace("\\\\", "\\"));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        return roots.Any(r => File.Exists(Path.Combine(r, "steamapps", "appmanifest_294100.acf")));
    }

    static bool ModOff(string modsConfig)
    {
        try
        {
            var active = XDocument.Load(modsConfig).Root?.Element("activeMods");
            if (active == null) return false;
            // a copy from the workshop next to a local one gets _steam on the end
            return !active.Elements("li").Select(l => l.Value.Trim().ToLowerInvariant()).Any(id => id == ModId || id == ModId + "_steam");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Xml.XmlException) { return false; }
    }
}
