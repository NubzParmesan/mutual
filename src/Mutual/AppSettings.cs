using System.Text.Json;

namespace Mutual;

// settings live in %APPDATA%\Mutual\settings.json
// --profile name gets its own folder under profiles so two copies can run at once (for testing)
sealed class AppSettings
{
    // old mutual ssh folder if theres one, used when theres no pairing of mutuals own
    public string PairingFolder { get; set; } = FindLegacyFolder();
    // the name that goes in your pairing code
    public string MyName { get; set; } = Environment.UserName;
    // handle pings ourselves instead of the old Notifier.exe
    public bool HandlePings { get; set; } = true;
    // host:port of a rendezvous server, empty if you dont need one
    public string Rendezvous { get; set; } = "";
    // let it open the port on the router with upnp
    public bool UseUpnp { get; set; } = true;
    public bool ShareSound { get; set; } = true;
    // off by default since it sends whatever you copy
    public bool ShareClipboard { get; set; }
    // said no to installing, dont keep asking
    public bool DeclinedInstall { get; set; }
    // hash of the friends key last time, so a pairing that changes without you re-pairing gets noticed
    public string? PeerFingerprint { get; set; }
    public bool ShortcutsMade { get; set; }
    // testing only, accepts everything without asking
    public bool AutoAccept { get => autoAccept && Profile != null; set => autoAccept = value; }
    bool autoAccept;

    // documents might be in onedrive or not so check both
    static string FindLegacyFolder()
    {
        var candidates = new[]
        {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "MutualSSH"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Documents", "MutualSSH"),
            Path.Combine(Environment.GetEnvironmentVariable("OneDrive") ?? "", "Documents", "MutualSSH"),
        };
        return candidates.FirstOrDefault(c => System.IO.File.Exists(Path.Combine(c, "config.json"))) ?? candidates[0];
    }

    public static string? Profile { get; set; }
    public static string Dir => Profile == null
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mutual")
        : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Mutual", "profiles", Profile);
    static string File => Path.Combine(Dir, "settings.json");

    public static AppSettings Load()
    {
        try { if (System.IO.File.Exists(File)) return JsonSerializer.Deserialize<AppSettings>(System.IO.File.ReadAllText(File)) ?? new(); }
        catch { }
        return new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Dir);
        System.IO.File.WriteAllText(File, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
    }
}
