using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Mutual;

// checks github for a newer release, downloads it, makes sure it matches the checksum on that release,
// then hands it to SelfInstall (one admin prompt, swaps the installed copy, opens the new one). so an
// update is one click instead of going to the releases page
static class Updater
{
    public sealed record Release(Version Version, string ExeUrl, string ShaUrl);

    // dev builds and --profile test copies never update themselves (MUTUAL_UPDATER_TEST=1 lets a dev build try it)
    public static bool Enabled => (AppSettings.Profile == null && !SelfInstall.DevBuild) || Environment.GetEnvironmentVariable("MUTUAL_UPDATER_TEST") == "1";

    public static Version Current
    {
        get
        {
            var v = FileVersionInfo.GetVersionInfo(Environment.ProcessPath!);
            return new Version(v.FileMajorPart, v.FileMinorPart, v.FileBuildPart);
        }
    }

    static HttpClient Http()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mutual/" + Current);   // github's api wants one
        return http;
    }

    // the newest release if its newer than this one, null if not (or github didnt answer)
    public static async Task<Release?> CheckAsync()
    {
        try
        {
            using var http = Http();
            using var doc = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{SelfInstall.OfficialRepo}/releases/latest"));
            var tag = doc.RootElement.GetProperty("tag_name").GetString() ?? "";
            if (!System.Version.TryParse(tag.TrimStart('v'), out var v)) return null;
            v = new Version(v.Major, v.Minor, Math.Max(0, v.Build));
            if (v <= Current) return null;
            string? exe = null, sha = null;
            // only files on our own release, so a weird api answer cant point the download somewhere else
            var prefix = $"https://github.com/{SelfInstall.OfficialRepo}/releases/download/{tag}/";
            foreach (var a in doc.RootElement.GetProperty("assets").EnumerateArray())
            {
                var url = a.GetProperty("browser_download_url").GetString() ?? "";
                if (!url.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var name = a.GetProperty("name").GetString();
                if (name == "Mutual.exe") exe = url;
                else if (name == "Mutual.exe.sha256") sha = url;
            }
            return exe != null && sha != null ? new Release(v, exe, sha) : null;
        }
        catch { return null; }
    }

    // downloads into temp and checks it. throws if the file doesnt match the release checksum
    public static async Task<string> DownloadAsync(Release r)
    {
        using var http = Http();
        http.Timeout = TimeSpan.FromMinutes(10);
        var want = (await http.GetStringAsync(r.ShaUrl)).Trim().Split(' ')[0].ToLowerInvariant();
        var dir = Path.Combine(Path.GetTempPath(), "Mutual-update");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "Mutual.exe");
        await using (var src = await http.GetStreamAsync(r.ExeUrl))
        await using (var dst = File.Create(path))
            await src.CopyToAsync(dst);
        string have;
        using (var f = File.OpenRead(path)) have = Convert.ToHexString(SHA256.HashData(f)).ToLowerInvariant();
        if (have != want)
        {
            try { File.Delete(path); } catch { }
            throw new InvalidDataException("the download didn't match the checksum on the release, so it wasn't used");
        }
        return path;
    }

    // the new copy does the install (it checks itself against the release again first)
    public static void Launch(string exe) => Process.Start(new ProcessStartInfo(exe, "--update") { UseShellExecute = true });
}
